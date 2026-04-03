using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;

namespace DocumentHealth
{
    /// <summary>
    /// Provides shared diagnostic data for both inline adornments and glyph margin indicators.
    /// This class uses the Error List (ITableManager) as the authoritative source of diagnostics,
    /// ensuring consistent data across all file types including those without squiggle taggers.
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

        private volatile bool _isDisposed;
        private readonly object _updateGate = new object();
        private CancellationTokenSource _debounceCts;

        // Cached diagnostic data per line number
        private Dictionary<int, LineDiagnostic> _diagnosticsByLine = new Dictionary<int, LineDiagnostic>();

        private volatile bool _isSubscribedToErrorList;

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
            SVsServiceProvider serviceProvider)
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
                    catch (InvalidOperationException)
                    {
                        // Service may not be available during shutdown or in certain contexts
                    }

                    return new DiagnosticDataProvider(textView, joinableTaskFactory, options, errorTableManager, errorList);
                });
        }

        public DiagnosticDataProvider(
            ITextView view,
            JoinableTaskFactory joinableTaskFactory,
            General options,
            ITableManager errorTableManager,
            IErrorList errorList)
        {
            _view = view;
            _joinableTaskFactory = joinableTaskFactory;
            _options = options;
            _errorTableManager = errorTableManager;
            _errorList = errorList;

            _view.Closed += OnViewClosed;
            _view.TextBuffer.Changed += OnTextBufferChanged;

            // Subscribe to error list changes immediately
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

                // Switch to UI thread to read Error List data (IWpfTableControl.Entries requires UI thread)
                await _joinableTaskFactory.SwitchToMainThreadAsync(token);

                if (_isDisposed || token.IsCancellationRequested)
                {
                    return;
                }

                // Collect diagnostics directly from the Error List
                Dictionary<int, LineDiagnostic> diagnostics = CollectDiagnosticsFromErrorList();

                if (_isDisposed || token.IsCancellationRequested)
                {
                    return;
                }

                _diagnosticsByLine = diagnostics;

                // Notify subscribers that data has been updated
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
        /// Collects diagnostics from the Error List for the current document.
        /// Must be called on the UI thread.
        /// </summary>
        private Dictionary<int, LineDiagnostic> CollectDiagnosticsFromErrorList()
        {
            var result = new Dictionary<int, LineDiagnostic>();

            try
            {
                string documentPath = GetDocumentPath();
                if (string.IsNullOrEmpty(documentPath))
                {
                    return result;
                }

                // Prefer IErrorList.TableControl.Entries for direct access
                if (_errorList?.TableControl != null)
                {
                    try
                    {
                        foreach (ITableEntryHandle entry in _errorList.TableControl.Entries)
                        {
                            ProcessErrorListEntry(result, entry, documentPath);
                        }

                        // If we got entries from the table control, we are done
                        if (result.Count > 0)
                        {
                            return result;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // May fail if called from wrong thread or during update
                    }
                }

                // Fallback: iterate through sources
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
                                foreach (ITableEntriesSnapshotFactory factory in collector.Factories)
                                {
                                    ITableEntriesSnapshot snapshot = factory.GetCurrentSnapshot();

                                    if (snapshot == null)
                                    {
                                        continue;
                                    }

                                    ProcessSnapshotEntries(result, snapshot, documentPath);
                                }
                            }
                            finally
                            {
                                subscription?.Dispose();
                            }
                        }
                        catch (ObjectDisposedException)
                        {
                            // Source was disposed
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Table manager was disposed
            }
            catch (InvalidOperationException)
            {
                // Collection was modified during enumeration
            }

            return result;
        }

        /// <summary>
        /// Processes a single Error List entry and adds it to the result dictionary.
        /// </summary>
        private void ProcessErrorListEntry(Dictionary<int, LineDiagnostic> result, ITableEntryHandle entry, string documentPath)
        {
            try
            {
                // Get the document name for this entry
                if (!entry.TryGetValue(StandardTableKeyNames.DocumentName, out object docNameObj) ||
                    !(docNameObj is string entryDocPath))
                {
                    return;
                }

                // Compare document paths (case-insensitive on Windows)
                if (!string.Equals(entryDocPath, documentPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // Get the line number (0-based in the Error List API)
                if (!entry.TryGetValue(StandardTableKeyNames.Line, out object lineObj) ||
                    !(lineObj is int lineNumber))
                {
                    return;
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
                    return;
                }

                // Get the message text
                if (!entry.TryGetValue(StandardTableKeyNames.Text, out object textObj) ||
                    !(textObj is string message) ||
                    string.IsNullOrWhiteSpace(message))
                {
                    return;
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
            }
            catch (InvalidOperationException)
            {
                // Entry may have been removed during enumeration
            }
            catch (ArgumentException)
            {
                // Entry access failed due to invalid state
            }
        }

        /// <summary>
        /// Processes entries from a snapshot and adds them to the result dictionary.
        /// </summary>
        private void ProcessSnapshotEntries(Dictionary<int, LineDiagnostic> result, ITableEntriesSnapshot snapshot, string documentPath)
        {
            try
            {
                int count = snapshot.Count;

                for (int i = 0; i < count; i++)
                {
                    // Get the document name for this entry
                    if (!snapshot.TryGetValue(i, StandardTableKeyNames.DocumentName, out object docNameObj) ||
                        !(docNameObj is string entryDocPath))
                    {
                        continue;
                    }

                    // Compare document paths (case-insensitive on Windows)
                    if (!string.Equals(entryDocPath, documentPath, StringComparison.OrdinalIgnoreCase))
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
            }
            catch (InvalidOperationException)
            {
                // Snapshot may have changed during enumeration
            }
            catch (ArgumentOutOfRangeException)
            {
                // Index became invalid during enumeration
            }
        }

        /// <summary>
        /// Adds or updates a diagnostic entry in the result dictionary.
        /// </summary>
        private static void AddOrUpdateDiagnostic(Dictionary<int, LineDiagnostic> result, int lineNumber, DiagnosticSeverity severity, string message, string errorCode)
        {
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
            }
            else
            {
                result[lineNumber] = new LineDiagnostic
                {
                    Severity = severity,
                    PrimaryMessage = message,
                    DiagnosticCode = errorCode,
                    Source = null,
                    Count = 1,
                };
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
            catch (ObjectDisposedException)
            {
                // View or buffer may be disposed
            }
            catch (InvalidOperationException)
            {
                // Property access may fail during disposal
            }

            return null;
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            Dispose();
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                _view.Closed -= OnViewClosed;
                _view.TextBuffer.Changed -= OnTextBufferChanged;

                // Unsubscribe from error list changes
                UnsubscribeFromErrorListChanges();

                lock (_updateGate)
                {
                    _debounceCts?.Cancel();
                    _debounceCts?.Dispose();
                    _debounceCts = null;
                }
            }
        }
    }

    internal enum DiagnosticSeverity
    {
        Message = 0,
        Warning = 1,
        Error = 2,
    }

    internal class LineDiagnostic
    {
        public DiagnosticSeverity Severity { get; set; }
        public string PrimaryMessage { get; set; }
        public string DiagnosticCode { get; set; }
        public string Source { get; set; }
        public int Count { get; set; }
    }

    /// <summary>
    /// A lightweight ITableDataSink implementation used to collect snapshot factories from an ITableDataSource.
    /// This is thread-safe and designed for temporary subscription to gather current data.
    /// </summary>
    internal sealed class SnapshotFactoryCollector : ITableDataSink
    {
        private readonly List<ITableEntriesSnapshotFactory> _factories = new List<ITableEntriesSnapshotFactory>();
        private readonly object _lock = new object();

        public IReadOnlyList<ITableEntriesSnapshotFactory> Factories
        {
            get
            {
                lock (_lock)
                {
                    return _factories.ToArray();
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
