using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;

namespace DocumentHealth
{
    /// <summary>
    /// Provides shared diagnostic data for both inline adornments and glyph margin indicators.
    /// This class uses both IErrorTag from the editor (for fast response) and the Error List 
    /// (for complete diagnostic data), ensuring responsive UI with accurate information.
    /// </summary>
    internal sealed class DiagnosticDataProvider : IDisposable
    {
        // Compiled regex for extracting diagnostic codes (e.g., CS0168, CA1000)
        private static readonly Regex DiagnosticCodeRegex = new Regex(
            @"^([A-Z]{2,4}\d{4,5})\s*:",
            RegexOptions.Compiled);

        private readonly ITextView _view;
        private readonly JoinableTaskFactory _joinableTaskFactory;
        private readonly General _options;
        private readonly ITableManager _errorTableManager;
        private readonly IErrorList _errorList;
        private readonly ITagAggregator<IErrorTag> _errorTagAggregator;

        private volatile bool _isDisposed;
        private readonly object _updateGate = new object();
        private CancellationTokenSource _debounceCts;

        // Tracks the last known error tag count so we can detect removals quickly
        private volatile int _lastErrorTagCount;

        // Cached diagnostic data per line number
        private Dictionary<int, LineDiagnostic> _diagnosticsByLine = new Dictionary<int, LineDiagnostic>();

        private volatile bool _isSubscribedToErrorList;
        private int _initialErrorListRetryCount;

        private const int MaxInitialErrorListRetries = 6;

        /// <summary>
        /// Raised when diagnostic data has been updated. Subscribers should re-render.
        /// </summary>
        public event EventHandler DiagnosticsUpdated;

        /// <summary>
        /// Gets the current diagnostics by line number.
        /// </summary>
        public IReadOnlyDictionary<int, LineDiagnostic> DiagnosticsByLine => _diagnosticsByLine;

        /// <summary>
        /// Gets or creates a shared DiagnosticDataProvider for the specified text view.
        /// </summary>
        public static DiagnosticDataProvider GetOrCreate(
            ITextView textView,
            JoinableTaskFactory joinableTaskFactory,
            General options,
            ITableManagerProvider tableManagerProvider,
            SVsServiceProvider serviceProvider,
            IViewTagAggregatorFactoryService viewTagAggregatorFactoryService)
        {
            return textView.Properties.GetOrCreateSingletonProperty(
                typeof(DiagnosticDataProvider),
                () =>
                {
                    ITableManager errorTableManager = tableManagerProvider.GetTableManager(StandardTables.ErrorsTable);

                    IErrorList errorList = null;
                    try
                    {
                        errorList = serviceProvider.GetService(typeof(SVsErrorList)) as IErrorList;
                    }
                    catch (InvalidOperationException ex)
                    {
                        // Service may not be available during shutdown or in certain contexts
                        ex.Log();
                    }

                    return new DiagnosticDataProvider(textView, joinableTaskFactory, options, errorTableManager, errorList, viewTagAggregatorFactoryService);
                });
        }

        public DiagnosticDataProvider(
            ITextView view,
            JoinableTaskFactory joinableTaskFactory,
            General options,
            ITableManager errorTableManager,
            IErrorList errorList,
            IViewTagAggregatorFactoryService viewTagAggregatorFactoryService)
        {
            _view = view;
            _joinableTaskFactory = joinableTaskFactory;
            _options = options;
            _errorTableManager = errorTableManager;
            _errorList = errorList;

            // Create tag aggregator for fast error tag detection
            _errorTagAggregator = viewTagAggregatorFactoryService?.CreateTagAggregator<IErrorTag>(view);

            _view.Closed += OnViewClosed;
            _view.TextBuffer.Changed += OnTextBufferChanged;

            // Subscribe to error tag changes for immediate feedback
            if (_errorTagAggregator != null)
            {
                _errorTagAggregator.BatchedTagsChanged += OnErrorTagsChanged;
            }

            // Subscribe to error list changes for complete diagnostic data
            SubscribeToErrorListChanges();

            ScheduleUpdate(immediate: true);
        }

        /// <summary>
        /// Subscribes to error list changes to detect new/changed diagnostics.
        /// </summary>
        private void SubscribeToErrorListChanges()
        {
            if (_isSubscribedToErrorList || _errorList?.TableControl == null)
            {
                return;
            }

            _isSubscribedToErrorList = true;
            _errorList.TableControl.EntriesChanged += OnErrorListEntriesChanged;
        }

        /// <summary>
        /// Unsubscribes from error list changes.
        /// </summary>
        private void UnsubscribeFromErrorListChanges()
        {
            if (!_isSubscribedToErrorList || _errorList?.TableControl == null)
            {
                return;
            }

            _isSubscribedToErrorList = false;
            _errorList.TableControl.EntriesChanged -= OnErrorListEntriesChanged;
        }

        /// <summary>
        /// Called when error list entries change. Schedules an update.
        /// </summary>
        private void OnErrorListEntriesChanged(object sender, EntriesChangedEventArgs e)
        {
            if (_isDisposed)
            {
                return;
            }

            // Schedule an update to re-fetch diagnostics from the error list
            ScheduleUpdate();
        }

        /// <summary>
        /// Called when error tags change in the editor. Updates diagnostics immediately for fast feedback.
        /// </summary>
        private void OnErrorTagsChanged(object sender, BatchedTagsChangedEventArgs e)
        {
            if (_isDisposed)
            {
                return;
            }

            // Process incremental tag changes
            UpdateForTagChanges(e);

            // Also schedule full update in case error list needs refresh
            ScheduleUpdate();
        }

        private void UpdateForTagChanges(BatchedTagsChangedEventArgs e)
        {
            try
            {
                ITextSnapshot snapshot = _view.TextSnapshot;
                List<SnapshotSpan> changedLineSpans = new List<SnapshotSpan>();

                foreach (IMappingSpan mappingSpan in e.Spans)
                {
                    NormalizedSnapshotSpanCollection spans = mappingSpan.GetSpans(snapshot);
                    foreach (SnapshotSpan span in spans)
                    {
                        int startLine = snapshot.GetLineNumberFromPosition(span.Start.Position);
                        int endLine = snapshot.GetLineNumberFromPosition(span.End.Position);

                        for (int line = startLine; line <= endLine; line++)
                        {
                            ITextSnapshotLine snapshotLine = snapshot.GetLineFromLineNumber(line);
                            changedLineSpans.Add(snapshotLine.ExtentIncludingLineBreak);
                        }
                    }
                }

                NormalizedSnapshotSpanCollection processSpans = new NormalizedSnapshotSpanCollection(changedLineSpans);

                // Clear diagnostics for affected lines
                HashSet<int> affectedLines = new HashSet<int>();
                foreach (SnapshotSpan span in processSpans)
                {
                    int startLine = snapshot.GetLineNumberFromPosition(span.Start.Position);
                    int endLine = snapshot.GetLineNumberFromPosition(span.End.Position);
                    for (int line = startLine; line <= endLine; line++)
                    {
                        affectedLines.Add(line);
                    }
                }

                lock (_updateGate)
                {
                    foreach (int line in affectedLines)
                    {
                        _diagnosticsByLine.Remove(line);
                    }
                }

                // Recollect for affected spans
                Dictionary<int, LineDiagnostic> newDiags = CollectDiagnosticsFromErrorTags(processSpans);

                // Merge into cache
                lock (_updateGate)
                {
                    foreach (var kvp in newDiags)
                    {
                        _diagnosticsByLine[kvp.Key] = kvp.Value;
                    }
                }

                // Update count
                _lastErrorTagCount = _diagnosticsByLine.Values.Sum(d => d.Count);

                DiagnosticsUpdated?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        private void OnTextBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (_isDisposed || _diagnosticsByLine.Count == 0)
            {
                return;
            }

            foreach (ITextChange change in e.Changes)
            {
                int newlinesBefore = CountNewlines(change.OldText);
                int newlinesAfter = CountNewlines(change.NewText);

                if (newlinesBefore != newlinesAfter)
                {
                    // Line count changed; clear stale diagnostics immediately
                    _diagnosticsByLine = new Dictionary<int, LineDiagnostic>();
                    DiagnosticsUpdated?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }
        }

        /// <summary>
        /// Counts the number of newline sequences in a string.
        /// </summary>
        internal static int CountNewlines(string text)
        {
            int count = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\r')
                {
                    count++;

                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }
                }
                else if (text[i] == '\n')
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Schedules a diagnostic data update. Call this to trigger a refresh.
        /// </summary>
        public void ScheduleUpdate(bool immediate = false)
        {
            CancellationTokenSource nextCts;

            lock (_updateGate)
            {
                if (_isDisposed)
                {
                    return;
                }

                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
                _debounceCts = new CancellationTokenSource();
                nextCts = _debounceCts;
            }

            int delay = immediate ? 0 : Math.Max(0, _options.UpdateDelayMilliseconds);
            UpdateWithDebounceAsync(nextCts.Token, delay).FireAndForget();
        }

        private async System.Threading.Tasks.Task UpdateWithDebounceAsync(CancellationToken token, int delay)
        {
            try
            {
                if (delay > 0)
                {
                    await System.Threading.Tasks.Task.Delay(delay, token).ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();

                // Collect diagnostics from error tags on a background thread.
                // ITagAggregator.GetTags is thread-safe and does not require the UI thread.
                Dictionary<int, LineDiagnostic> diagnostics = CollectDiagnosticsFromErrorTags();

                if (_isDisposed || token.IsCancellationRequested)
                {
                    return;
                }

                // Pre-fetch document path off the UI thread where possible
                string documentPath = null;
                try
                {
                    documentPath = GetDocumentPathOffThread();
                }
                catch
                {
                    // Fall back to UI thread version
                }

                // Switch to UI thread for Error List access (IWpfTableControl.Entries requires it)
                // and to notify subscribers (they update WPF adornments)
                await _joinableTaskFactory.SwitchToMainThreadAsync(token);

                if (_isDisposed || token.IsCancellationRequested)
                {
                    return;
                }

                // Error List services can be initialized after this provider is created
                // during solution load. Retry event hookup when we're on the UI thread.
                SubscribeToErrorListChanges();

                // Enhance with Error List data - pass document path to optimize filtering
                if (string.IsNullOrEmpty(documentPath))
                {
                    documentPath = GetDocumentPath();
                }

                Dictionary<int, LineDiagnostic> errorListDiagnostics = CollectDiagnosticsFromErrorList(documentPath);
                MergeDiagnostics(diagnostics, errorListDiagnostics);

                // During solution/open-file startup there is a race where tags exist before
                // Error List entries for the same document are materialized. Retry a few times
                // so we can hydrate full messages/codes without requiring a close/reopen.
                if (diagnostics.Count > 0 &&
                    errorListDiagnostics.Count == 0 &&
                    _initialErrorListRetryCount < MaxInitialErrorListRetries)
                {
                    _initialErrorListRetryCount++;
                    ScheduleUpdate();
                }
                else if (errorListDiagnostics.Count > 0)
                {
                    _initialErrorListRetryCount = MaxInitialErrorListRetries;
                }

                if (_isDisposed || token.IsCancellationRequested)
                {
                    return;
                }

                _diagnosticsByLine = diagnostics;

                // Update tag count for heuristic in OnErrorTagsChanged
                int totalCount = 0;
                foreach (LineDiagnostic diag in diagnostics.Values)
                {
                    totalCount += diag.Count;
                }
                _lastErrorTagCount = totalCount;

                DiagnosticsUpdated?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                // Debounce cancelled
            }
            catch (ObjectDisposedException)
            {
                // View disposed
            }
        }

        /// <summary>
        /// Merges Error List diagnostics into tag-based diagnostics.
        /// Error List data is authoritative for messages and diagnostic codes.
        /// </summary>
        private static void MergeDiagnostics(Dictionary<int, LineDiagnostic> target, Dictionary<int, LineDiagnostic> source)
        {
            foreach (KeyValuePair<int, LineDiagnostic> kvp in source)
            {
                int line = kvp.Key;
                LineDiagnostic sourceDiag = kvp.Value;

                if (target.TryGetValue(line, out LineDiagnostic targetDiag))
                {
                    // Error List data has complete message and diagnostic code - prefer it
                    if (!string.IsNullOrEmpty(sourceDiag.PrimaryMessage))
                    {
                        targetDiag.PrimaryMessage = sourceDiag.PrimaryMessage;
                    }

                    if (!string.IsNullOrEmpty(sourceDiag.DiagnosticCode))
                    {
                        targetDiag.DiagnosticCode = sourceDiag.DiagnosticCode;
                    }

                    if (!string.IsNullOrEmpty(sourceDiag.Source))
                    {
                        targetDiag.Source = sourceDiag.Source;
                    }

                    // Keep the highest severity
                    if (sourceDiag.Severity > targetDiag.Severity)
                    {
                        targetDiag.Severity = sourceDiag.Severity;
                    }

                    // Use the higher count
                    if (sourceDiag.Count > targetDiag.Count)
                    {
                        targetDiag.Count = sourceDiag.Count;
                    }

                    // Merge entries; prefer source (Error List) entries when they cover
                    // at least as many diagnostics as the target already has, so we don't
                    // discard entries that the Error List path didn't match.
                    if (sourceDiag.Entries.Count >= targetDiag.Entries.Count)
                    {
                        targetDiag.Entries.Clear();
                        targetDiag.Entries.AddRange(sourceDiag.Entries);
                    }
                }
                else
                {
                    // Error List has entries not found in tags - add them
                    target[line] = sourceDiag;
                }
            }
        }

        /// <summary>
        /// Collects diagnostics from IErrorTag instances in the editor.
        /// This provides fast feedback as tags are generated immediately by the compiler/analyzer.
        /// </summary>
        private Dictionary<int, LineDiagnostic> CollectDiagnosticsFromErrorTags(NormalizedSnapshotSpanCollection processSpans = null)
        {
            var result = new Dictionary<int, LineDiagnostic>();

            if (_errorTagAggregator == null || _view.IsClosed)
            {
                return result;
            }

            try
            {
                ITextSnapshot snapshot = _view.TextSnapshot;
                NormalizedSnapshotSpanCollection spansToProcess = processSpans ?? new NormalizedSnapshotSpanCollection(new SnapshotSpan(snapshot, 0, snapshot.Length));

                foreach (IMappingTagSpan<IErrorTag> tagSpan in _errorTagAggregator.GetTags(spansToProcess))
                {
                    try
                    {
                        IErrorTag errorTag = tagSpan.Tag;
                        if (errorTag == null)
                        {
                            continue;
                        }

                        // Get the severity from the error type
                        DiagnosticSeverity severity = GetSeverityFromErrorType(errorTag.ErrorType);

                        if (!IsSeverityEnabled(severity))
                        {
                            continue;
                        }

                        // Get the line number from the tag span
                        NormalizedSnapshotSpanCollection spans = tagSpan.Span.GetSpans(snapshot);
                        if (spans.Count == 0)
                        {
                            continue;
                        }

                        int lineNumber = snapshot.GetLineNumberFromPosition(spans[0].Start.Position);

                        // Extract message from tooltip content
                        string message = ExtractToolTipText(errorTag.ToolTipContent);

                        // Try to extract diagnostic code from message
                        string errorCode = null;
                        if (!string.IsNullOrEmpty(message))
                        {
                            errorCode = ExtractDiagnosticCode(message);
                            if (!string.IsNullOrEmpty(errorCode))
                            {
                                message = StripCodePrefix(message, errorCode);
                            }
                        }

                        // Add or update the diagnostic for this line
                        var entry = new DiagnosticEntry
                        {
                            Severity = severity,
                            Message = message,
                            DiagnosticCode = errorCode,
                        };

                        if (result.TryGetValue(lineNumber, out LineDiagnostic existing))
                        {
                            // Keep the highest severity
                            if (severity > existing.Severity)
                            {
                                existing.Severity = severity;
                                if (!string.IsNullOrEmpty(message))
                                {
                                    existing.PrimaryMessage = message;
                                    existing.DiagnosticCode = errorCode;
                                }
                            }
                            // Update message if we don't have one yet
                            else if (string.IsNullOrEmpty(existing.PrimaryMessage) && !string.IsNullOrEmpty(message))
                            {
                                existing.PrimaryMessage = message;
                                existing.DiagnosticCode = errorCode;
                            }

                            existing.Count++;
                            existing.Entries.Add(entry);
                        }
                        else
                        {
                            var diag = new LineDiagnostic
                            {
                                Severity = severity,
                                PrimaryMessage = message,
                                DiagnosticCode = errorCode,
                                Source = "ErrorTag",
                                Count = 1,
                            };
                            diag.Entries.Add(entry);
                            result[lineNumber] = diag;
                        }
                    }
                    catch (ArgumentException ex)
                    {
                        // Tag span may be invalid
                        ex.Log();
                    }
                }
            }
            catch (ObjectDisposedException ex)
            {
                // Aggregator or view disposed
                ex.Log();
            }
            catch (InvalidOperationException ex)
            {
                // Collection modified during enumeration
                ex.Log();
            }

            return result;
        }

        /// <summary>
        /// Collects diagnostics from the Error List for the current document.
        /// Must be called on the UI thread (IWpfTableControl.Entries requires it).
        /// </summary>
        /// <param name="documentPath">Optional pre-computed document path to avoid redundant lookups</param>
        private Dictionary<int, LineDiagnostic> CollectDiagnosticsFromErrorList(string documentPath = null)
        {
            var result = new Dictionary<int, LineDiagnostic>();

            try
            {
                if (string.IsNullOrEmpty(documentPath))
                {
                    documentPath = GetDocumentPath();
                }

                if (string.IsNullOrEmpty(documentPath))
                {
                    return result;
                }

                // Normalize once for all comparisons
                string normalizedCurrentPath = NormalizeDocumentPath(documentPath);

                // Use IErrorList.TableControl.Entries for reliable access to Error List data
                if (_errorList?.TableControl != null)
                {
                    try
                    {
                        // OPTIMIZATION: Early filtering to reduce UI thread time
                        // Process entries in batches to avoid blocking UI thread for too long
                        const int batchSize = 100;
                        int processedCount = 0;
                        int matchedCount = 0;

                        foreach (ITableEntryHandle entry in _errorList.TableControl.Entries)
                        {
                            processedCount++;

                            // Early exit if we've processed too many entries without matches
                            // This prevents freezing when there are thousands of errors in other files
                            if (processedCount > batchSize && matchedCount == 0)
                            {
                                // No matches in first batch - likely wrong file, abort early
                                break;
                            }

                            if (ProcessErrorListEntry(result, entry, normalizedCurrentPath))
                            {
                                matchedCount++;
                            }

                            // If we've found entries and processed many more without additional matches, exit
                            if (matchedCount > 0 && processedCount > matchedCount + batchSize)
                            {
                                break;
                            }
                        }

                        if (result.Count > 0)
                        {
                            return result;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // May fail during update
                    }
                }

                // Fallback: iterate through sources using thread-safe snapshot APIs
                if (_errorTableManager != null)
                {
                    foreach (ITableDataSource source in _errorTableManager.Sources)
                    {
                        try
                        {
                            var collector = new SnapshotFactoryCollector();
                            IDisposable subscription = source.Subscribe(collector);

                            try
                            {
                                collector.ForEach(factory =>
                                {
                                    ITableEntriesSnapshot snapshot = factory.GetCurrentSnapshot();

                                    if (snapshot == null)
                                    {
                                        return;
                                    }

                                    ProcessSnapshotEntries(result, snapshot, normalizedCurrentPath);
                                });
                            }
                            finally
                            {
                                subscription?.Dispose();
                            }
                        }
                        catch (ObjectDisposedException ex)
                        {
                            // Source was disposed
                            ex.Log();
                        }
                    }
                }
            }
            catch (ObjectDisposedException ex)
            {
                // Table manager was disposed
                ex.Log();
            }
            catch (InvalidOperationException ex)
            {
                // Collection was modified during enumeration
                ex.Log();
            }

            return result;
        }

        /// <summary>
        /// Processes a single Error List entry and adds it to the result dictionary.
        /// </summary>
        /// <returns>True if the entry matched the current document, false otherwise</returns>
        private bool ProcessErrorListEntry(Dictionary<int, LineDiagnostic> result, ITableEntryHandle entry, string normalizedCurrentPath)
        {
            try
            {
                // Get the document name for this entry
                if (!entry.TryGetValue(StandardTableKeyNames.DocumentName, out object docNameObj) ||
                    !(docNameObj is string entryDocPath))
                {
                    return false;
                }

                // OPTIMIZATION: Fast path comparison before normalization
                // Compare document identity robustly. Some language services report only file names,
                // URI-style paths, or transient monikers before the file is saved.
                string normalizedEntryPath = NormalizeDocumentPath(entryDocPath);
                if (!IsNormalizedPathMatch(normalizedEntryPath, normalizedCurrentPath))
                {
                    return false;
                }

                // Get the line number (0-based in the Error List API)
                if (!entry.TryGetValue(StandardTableKeyNames.Line, out object lineObj) ||
                    !(lineObj is int lineNumber))
                {
                    return false;
                }

                // Get the severity
                DiagnosticSeverity severity = DiagnosticSeverity.Message;
                if (entry.TryGetValue(StandardTableKeyNames.ErrorSeverity, out object severityObj) &&
                    severityObj is __VSERRORCATEGORY category)
                {
                    switch (category)
                    {
                        case __VSERRORCATEGORY.EC_ERROR:
                            severity = DiagnosticSeverity.Error;
                            break;
                        case __VSERRORCATEGORY.EC_WARNING:
                            severity = DiagnosticSeverity.Warning;
                            break;
                        case __VSERRORCATEGORY.EC_MESSAGE:
                            severity = DiagnosticSeverity.Message;
                            break;
                    }
                }

                if (!IsSeverityEnabled(severity))
                {
                    return true; // Matched document but filtered by severity
                }

                // Get the message text
                if (!entry.TryGetValue(StandardTableKeyNames.Text, out object textObj) ||
                    !(textObj is string message) ||
                    string.IsNullOrWhiteSpace(message))
                {
                    return true; // Matched document but no message
                }

                // Get the error code
                string errorCode = null;
                if (entry.TryGetValue(StandardTableKeyNames.ErrorCode, out object errorCodeObj) &&
                    errorCodeObj is string code &&
                    !string.IsNullOrWhiteSpace(code))
                {
                    errorCode = code;
                }

                // Try to extract diagnostic code from the message if not in ErrorCode field
                string extractedCode = ExtractDiagnosticCode(message);
                if (!string.IsNullOrEmpty(extractedCode))
                {
                    message = StripCodePrefix(message, extractedCode);
                    if (string.IsNullOrEmpty(errorCode))
                    {
                        errorCode = extractedCode;
                    }
                }

                AddOrUpdateDiagnostic(result, lineNumber, severity, message, errorCode);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                // Entry may have been removed during enumeration
                ex.Log();
                return false;
            }
            catch (ArgumentException ex)
            {
                // Entry access failed due to invalid state
                ex.Log();
                return false;
            }
        }

        /// <summary>
        /// Processes entries from a snapshot and adds them to the result dictionary.
        /// </summary>
        private void ProcessSnapshotEntries(Dictionary<int, LineDiagnostic> result, ITableEntriesSnapshot snapshot, string normalizedCurrentPath)
        {
            try
            {
                int count = snapshot.Count;

                // OPTIMIZATION: Limit processing to avoid UI thread blocking
                // If there are too many entries, they're likely not all for this document
                const int maxEntriesToProcess = 1000;
                int processLimit = Math.Min(count, maxEntriesToProcess);

                for (int i = 0; i < processLimit; i++)
                {
                    // Get the document name for this entry
                    if (!snapshot.TryGetValue(i, StandardTableKeyNames.DocumentName, out object docNameObj) ||
                        !(docNameObj is string entryDocPath))
                    {
                        continue;
                    }

                    // OPTIMIZATION: Fast path comparison
                    // Compare document identity robustly. Some language services report only file names,
                    // URI-style paths, or transient monikers before the file is saved.
                    string normalizedEntryPath = NormalizeDocumentPath(entryDocPath);
                    if (!IsNormalizedPathMatch(normalizedEntryPath, normalizedCurrentPath))
                    {
                        continue;
                    }

                    // Get the line number (0-based in the Error List API)
                    if (!snapshot.TryGetValue(i, StandardTableKeyNames.Line, out object lineObj) ||
                        !(lineObj is int lineNumber))
                    {
                        continue;
                    }

                    // Get the severity
                    DiagnosticSeverity severity = GetSeverityFromSnapshot(snapshot, i);

                    if (!IsSeverityEnabled(severity))
                    {
                        continue;
                    }

                    // Get the message text
                    if (!snapshot.TryGetValue(i, StandardTableKeyNames.Text, out object textObj) ||
                        !(textObj is string message) ||
                        string.IsNullOrWhiteSpace(message))
                    {
                        continue;
                    }

                    // Get the error code
                    string errorCode = null;
                    if (snapshot.TryGetValue(i, StandardTableKeyNames.ErrorCode, out object errorCodeObj) &&
                        errorCodeObj is string code &&
                        !string.IsNullOrWhiteSpace(code))
                    {
                        errorCode = code;
                    }

                    // Try to extract diagnostic code from the message if not in ErrorCode field
                    string extractedCode = ExtractDiagnosticCode(message);
                    if (!string.IsNullOrEmpty(extractedCode))
                    {
                        message = StripCodePrefix(message, extractedCode);
                        if (string.IsNullOrEmpty(errorCode))
                        {
                            errorCode = extractedCode;
                        }
                    }

                    AddOrUpdateDiagnostic(result, lineNumber, severity, message, errorCode);
                }

                // Log warning if we hit the limit
                if (count > maxEntriesToProcess)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"DocumentHealth: Snapshot has {count} entries, processed only {maxEntriesToProcess} to avoid UI freeze");
                }
            }
            catch (InvalidOperationException ex)
            {
                // Snapshot may have changed during enumeration
                ex.Log();
            }
            catch (ArgumentOutOfRangeException ex)
            {
                // Index became invalid during enumeration
                ex.Log();
            }
        }

        /// <summary>
        /// Adds or updates a diagnostic entry in the result dictionary.
        /// </summary>
        private static void AddOrUpdateDiagnostic(Dictionary<int, LineDiagnostic> result, int lineNumber, DiagnosticSeverity severity, string message, string errorCode)
        {
            var entry = new DiagnosticEntry
            {
                Severity = severity,
                Message = message,
                DiagnosticCode = errorCode,
            };

            if (result.TryGetValue(lineNumber, out LineDiagnostic existing))
            {
                // Keep the highest severity
                if (severity > existing.Severity)
                {
                    existing.Severity = severity;
                    existing.PrimaryMessage = message;
                    existing.DiagnosticCode = errorCode;
                }

                existing.Count++;
                existing.Entries.Add(entry);
            }
            else
            {
                var diag = new LineDiagnostic
                {
                    Severity = severity,
                    PrimaryMessage = message,
                    DiagnosticCode = errorCode,
                    Source = null,
                    Count = 1,
                };
                diag.Entries.Add(entry);
                result[lineNumber] = diag;
            }
        }

        private bool IsSeverityEnabled(DiagnosticSeverity severity)
        {
            switch (severity)
            {
                case DiagnosticSeverity.Error: return _options.ShowErrors;
                case DiagnosticSeverity.Warning: return _options.ShowWarnings;
                case DiagnosticSeverity.Message: return _options.ShowSuggestions;
                default: return true;
            }
        }

        private static DiagnosticSeverity GetSeverityFromSnapshot(ITableEntriesSnapshot snapshot, int index)
        {
            if (snapshot.TryGetValue(index, StandardTableKeyNames.ErrorSeverity, out object severityObj))
            {
                if (severityObj is __VSERRORCATEGORY category)
                {
                    switch (category)
                    {
                        case __VSERRORCATEGORY.EC_ERROR:
                            return DiagnosticSeverity.Error;
                        case __VSERRORCATEGORY.EC_WARNING:
                            return DiagnosticSeverity.Warning;
                        case __VSERRORCATEGORY.EC_MESSAGE:
                            return DiagnosticSeverity.Message;
                    }
                }
            }

            return DiagnosticSeverity.Message;
        }

        /// <summary>
        /// Maps an IErrorTag.ErrorType string to DiagnosticSeverity.
        /// Uses PredefinedErrorTypeNames constants for matching.
        /// </summary>
        private static DiagnosticSeverity GetSeverityFromErrorType(string errorType)
        {
            if (string.IsNullOrEmpty(errorType))
            {
                return DiagnosticSeverity.Message;
            }

            // Match against PredefinedErrorTypeNames (case-insensitive for robustness)
            if (string.Equals(errorType, PredefinedErrorTypeNames.SyntaxError, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(errorType, PredefinedErrorTypeNames.CompilerError, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(errorType, "error", StringComparison.OrdinalIgnoreCase))
            {
                return DiagnosticSeverity.Error;
            }

            if (string.Equals(errorType, PredefinedErrorTypeNames.Warning, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(errorType, "compiler warning", StringComparison.OrdinalIgnoreCase))
            {
                return DiagnosticSeverity.Warning;
            }

            if (string.Equals(errorType, PredefinedErrorTypeNames.Suggestion, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(errorType, PredefinedErrorTypeNames.HintedSuggestion, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(errorType, "suggestion", StringComparison.OrdinalIgnoreCase))
            {
                return DiagnosticSeverity.Message;
            }

            // Default to message for unknown types
            return DiagnosticSeverity.Message;
        }

        /// <summary>
        /// Extracts text from IErrorTag.ToolTipContent.
        /// Handles string content directly, and attempts to extract text from WPF elements.
        /// </summary>
        private static string ExtractToolTipText(object toolTipContent)
        {
            if (toolTipContent == null)
            {
                return null;
            }

            // Direct string content
            if (toolTipContent is string text)
            {
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            // Handle ContainerElement (used by Roslyn and other analyzers)
            if (toolTipContent is ContainerElement container)
            {
                return ExtractTextFromContainerElement(container);
            }

            // Handle ClassifiedTextElement
            if (toolTipContent is ClassifiedTextElement classifiedText)
            {
                return ExtractTextFromClassifiedTextElement(classifiedText);
            }

            // Handle ImageElement with text (less common)
            if (toolTipContent is ImageElement imageElement && !string.IsNullOrWhiteSpace(imageElement.AutomationName))
            {
                return imageElement.AutomationName;
            }

            // Fallback: try ToString() for unknown types
            string strValue = toolTipContent.ToString();
            if (!string.IsNullOrWhiteSpace(strValue) && strValue != toolTipContent.GetType().FullName)
            {
                return strValue;
            }

            return null;
        }

        /// <summary>
        /// Determines whether an Error List document moniker refers to the current document.
        /// </summary>
        private static bool IsCurrentDocument(string entryDocumentPath, string currentDocumentPath)
        {
            if (string.IsNullOrWhiteSpace(entryDocumentPath) || string.IsNullOrWhiteSpace(currentDocumentPath))
            {
                return false;
            }

            string normalizedEntryPath = NormalizeDocumentPath(entryDocumentPath);
            string normalizedCurrentPath = NormalizeDocumentPath(currentDocumentPath);

            return IsNormalizedPathMatch(normalizedEntryPath, normalizedCurrentPath);
        }

        /// <summary>
        /// Fast path comparison of already-normalized document paths.
        /// Optimized version to avoid repeated normalization when paths are pre-normalized.
        /// </summary>
        private static bool IsNormalizedPathMatch(string normalizedEntryPath, string normalizedCurrentPath)
        {
            if (string.Equals(normalizedEntryPath, normalizedCurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Some diagnostics only report a file name before the document is saved.
            // Fall back to file name matching in that case.
            string entryFileName = Path.GetFileName(normalizedEntryPath);
            string currentFileName = Path.GetFileName(normalizedCurrentPath);

            return !string.IsNullOrEmpty(entryFileName)
                && string.Equals(entryFileName, currentFileName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Normalizes common Error List document moniker formats for reliable comparisons.
        /// </summary>
        private static string NormalizeDocumentPath(string path)
        {
            string normalized = path.Trim();

            if (normalized.EndsWith("*", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(0, normalized.Length - 1).TrimEnd();
            }

            if (Uri.TryCreate(normalized, UriKind.Absolute, out Uri uri) && uri.IsFile)
            {
                normalized = uri.LocalPath;
            }

            return normalized;
        }

        /// <summary>
        /// Extracts text from a ContainerElement by recursively processing its elements.
        /// </summary>
        private static string ExtractTextFromContainerElement(ContainerElement container)
        {
            if (container?.Elements == null)
            {
                return null;
            }

            var textParts = new List<string>();

            foreach (object element in container.Elements)
            {
                string extracted = null;

                if (element is string str)
                {
                    extracted = str;
                }
                else if (element is ClassifiedTextElement classifiedText)
                {
                    extracted = ExtractTextFromClassifiedTextElement(classifiedText);
                }
                else if (element is ContainerElement nestedContainer)
                {
                    extracted = ExtractTextFromContainerElement(nestedContainer);
                }
                else if (element is ImageElement imageElement && !string.IsNullOrWhiteSpace(imageElement.AutomationName))
                {
                    extracted = imageElement.AutomationName;
                }

                if (!string.IsNullOrWhiteSpace(extracted))
                {
                    textParts.Add(extracted);
                }
            }

            return textParts.Count > 0 ? string.Join(" ", textParts) : null;
        }

        /// <summary>
        /// Extracts text from a ClassifiedTextElement by concatenating its runs.
        /// </summary>
        private static string ExtractTextFromClassifiedTextElement(ClassifiedTextElement classifiedText)
        {
            if (classifiedText?.Runs == null)
            {
                return null;
            }

            var textParts = new List<string>();

            foreach (ClassifiedTextRun run in classifiedText.Runs)
            {
                if (!string.IsNullOrEmpty(run.Text))
                {
                    textParts.Add(run.Text);
                }
            }

            return textParts.Count > 0 ? string.Concat(textParts) : null;
        }

        /// <summary>
        /// Attempts to extract a diagnostic code (like CS0168) from the beginning of a message string.
        /// </summary>
        internal static string ExtractDiagnosticCode(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            // Match a leading diagnostic code pattern like CS0168: or CA1000:
            Match match = DiagnosticCodeRegex.Match(message);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Strips the diagnostic code prefix (e.g. CS0161: ) from the message text.
        /// </summary>
        internal static string StripCodePrefix(string message, string code)
        {
            if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(code))
            {
                return message;
            }

            // Check if message starts with the code
            if (!message.StartsWith(code, StringComparison.Ordinal))
            {
                return message;
            }

            // Find the position after the code and any following ": " pattern
            int pos = code.Length;

            // Skip whitespace
            while (pos < message.Length && char.IsWhiteSpace(message[pos]))
            {
                pos++;
            }

            // Skip colon if present
            if (pos < message.Length && message[pos] == ':')
            {
                pos++;

                // Skip whitespace after colon
                while (pos < message.Length && char.IsWhiteSpace(message[pos]))
                {
                    pos++;
                }
            }

            return message.Substring(pos);
        }

        /// <summary>
        /// Gets the document path for the current text view from the ITextDocument property.
        /// </summary>
        private string GetDocumentPath()
        {
            try
            {
                if (_view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document))
                {
                    return document.FilePath;
                }
            }
            catch (ObjectDisposedException ex)
            {
                // View or buffer may be disposed
                ex.Log();
            }
            catch (InvalidOperationException ex)
            {
                // Property access may fail during disposal
                ex.Log();
            }

            return null;
        }

        /// <summary>
        /// Attempts to get the document path without requiring the UI thread.
        /// This is best-effort; may throw if the property access requires marshalling.
        /// </summary>
        private string GetDocumentPathOffThread()
        {
            if (_view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document))
            {
                return document.FilePath;
            }

            return null;
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            Dispose();
        }

        public void Dispose()
        {
            lock (_updateGate)
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
                _debounceCts = null;
            }

            // Unsubscribe from events after setting disposed flag and cancelling pending work.
            // Safe outside the lock because _isDisposed prevents new work from being scheduled.
            _view.Closed -= OnViewClosed;
            _view.TextBuffer.Changed -= OnTextBufferChanged;

            if (_errorTagAggregator != null)
            {
                _errorTagAggregator.BatchedTagsChanged -= OnErrorTagsChanged;
                _errorTagAggregator.Dispose();
            }

            UnsubscribeFromErrorListChanges();
        }
    }

    internal enum DiagnosticSeverity
    {
        Message = 0,
        Warning = 1,
        Error = 2,
    }

    /// <summary>
    /// Represents a single diagnostic entry on a line.
    /// </summary>
    internal sealed class DiagnosticEntry
    {
        public DiagnosticSeverity Severity { get; set; }
        public string Message { get; set; }
        public string DiagnosticCode { get; set; }
    }

    internal class LineDiagnostic
    {
        public DiagnosticSeverity Severity { get; set; }
        public string PrimaryMessage { get; set; }
        public string DiagnosticCode { get; set; }
        public string Source { get; set; }
        public int Count { get; set; }

        /// <summary>
        /// All individual diagnostic entries on this line, used for tooltip display.
        /// </summary>
        public List<DiagnosticEntry> Entries { get; } = new List<DiagnosticEntry>();
    }

    /// <summary>
    /// A lightweight ITableDataSink implementation used to collect snapshot factories from an ITableDataSource.
    /// This is thread-safe and designed for temporary subscription to gather current data.
    /// </summary>
    internal sealed class SnapshotFactoryCollector : ITableDataSink
    {
        private readonly List<ITableEntriesSnapshotFactory> _factories = new List<ITableEntriesSnapshotFactory>();
        private readonly object _lock = new object();

        /// <summary>
        /// Gets the number of collected factories.
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _factories.Count;
                }
            }
        }

        /// <summary>
        /// Executes an action for each factory under the lock.
        /// This avoids allocating a copy of the list.
        /// </summary>
        public void ForEach(Action<ITableEntriesSnapshotFactory> action)
        {
            lock (_lock)
            {
                foreach (ITableEntriesSnapshotFactory factory in _factories)
                {
                    action(factory);
                }
            }
        }

        public bool IsStable { get; set; } = true;

        public void AddEntries(IReadOnlyList<ITableEntry> newEntries, bool removeAllEntries = false) { }

        public void AddFactory(ITableEntriesSnapshotFactory newFactory, bool removeAllFactories = false)
        {
            if (newFactory == null) return;
            lock (_lock)
            {
                if (removeAllFactories) _factories.Clear();
                _factories.Add(newFactory);
            }
        }

        public void AddSnapshot(ITableEntriesSnapshot newSnapshot, bool removeAllSnapshots = false) { }
        public void FactorySnapshotChanged(ITableEntriesSnapshotFactory factory) { }
        public void RemoveAllEntries() { }
        public void RemoveAllFactories() { lock (_lock) { _factories.Clear(); } }
        public void RemoveAllSnapshots() { }
        public void RemoveEntries(IReadOnlyList<ITableEntry> oldEntries) { }

        public void RemoveFactory(ITableEntriesSnapshotFactory oldFactory)
        {
            if (oldFactory == null) return;
            lock (_lock) { _factories.Remove(oldFactory); }
        }

        public void RemoveSnapshot(ITableEntriesSnapshot oldSnapshot) { }
        public void ReplaceEntries(IReadOnlyList<ITableEntry> oldEntries, IReadOnlyList<ITableEntry> newEntries) { }

        public void ReplaceFactory(ITableEntriesSnapshotFactory oldFactory, ITableEntriesSnapshotFactory newFactory)
        {
            if (oldFactory == null || newFactory == null) return;
            lock (_lock)
            {
                int index = _factories.IndexOf(oldFactory);
                if (index >= 0) _factories[index] = newFactory;
                else _factories.Add(newFactory);
            }
        }

        public void ReplaceSnapshot(ITableEntriesSnapshot oldSnapshot, ITableEntriesSnapshot newSnapshot) { }
    }
}
