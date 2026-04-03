using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;

namespace DocumentHealth
{
    /// <summary>
    /// Renders inline diagnostic messages and line background highlights on the editor surface.
    /// </summary>
    internal sealed class InlineDiagnosticsAdornment
    {
        private readonly IWpfTextView _view;
        private readonly IAdornmentLayer _layer;
        private readonly ITagAggregator<IErrorTag> _aggregator;
        private readonly JoinableTaskFactory _joinableTaskFactory;
        private readonly General _options;
        private readonly ITableManager _errorTableManager;
        private readonly IErrorList _errorList;

        private static readonly Brush _errorBackground;
        private static readonly Brush _warningBackground;
        private static readonly Brush _messageBackground;
        private static readonly Brush _errorForeground;
        private static readonly Brush _warningForeground;
        private static readonly Brush _messageForeground;

        private volatile bool _isDisposed;
        private readonly object _updateGate = new object();
        private CancellationTokenSource _debounceCts;

        // Cached diagnostic data per line number
        private Dictionary<int, LineDiagnostic> _diagnosticsByLine = new Dictionary<int, LineDiagnostic>();

        // Tracks whether we have diagnostics that need error list data (missing tooltip text)
        private volatile bool _hasPendingErrorListLookups;
        private volatile bool _isSubscribedToErrorList;

        static InlineDiagnosticsAdornment()
        {
            _errorBackground = new SolidColorBrush(Color.FromArgb(0x26, 0xE4, 0x54, 0x54));
            _errorBackground.Freeze();
            _warningBackground = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0x94, 0x2F));
            _warningBackground.Freeze();
            _messageBackground = new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0xB7, 0xE4));
            _messageBackground.Freeze();

            _errorForeground = new SolidColorBrush(Color.FromArgb(0xCC, 0xE4, 0x54, 0x54));
            _errorForeground.Freeze();
            _warningForeground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0x94, 0x2F));
            _warningForeground.Freeze();
            _messageForeground = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0xB7, 0xE4));
            _messageForeground.Freeze();
        }

        public InlineDiagnosticsAdornment(
            IWpfTextView view,
            ITagAggregator<IErrorTag> aggregator,
            JoinableTaskFactory joinableTaskFactory,
            General options,
            ITableManager errorTableManager,
            IErrorList errorList)
        {
            _view = view;
            _layer = view.GetAdornmentLayer(InlineDiagnosticsAdornmentProvider.AdornmentLayerName);
            _aggregator = aggregator;
            _joinableTaskFactory = joinableTaskFactory;
            _options = options;
            _errorTableManager = errorTableManager;
            _errorList = errorList;

            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnViewClosed;
            _view.TextBuffer.Changed += OnTextBufferChanged;
            _aggregator.BatchedTagsChanged += OnBatchedTagsChanged;

            ScheduleUpdate(immediate: true);
        }

        private void OnBatchedTagsChanged(object sender, BatchedTagsChangedEventArgs e)
        {
            ScheduleUpdate();
        }

        /// <summary>
        /// Subscribes to error list changes when we have tags without tooltip text.
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
        /// Called when error list entries change. Schedules an update if we have pending lookups.
        /// </summary>
        private void OnErrorListEntriesChanged(object sender, EntriesChangedEventArgs e)
        {
            if (_isDisposed || !_hasPendingErrorListLookups)
            {
                return;
            }

            // Schedule an update to re-fetch messages from the now-populated error list
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
                    // Line count changed; clear stale diagnostics and adornments immediately
                    _diagnosticsByLine = new Dictionary<int, LineDiagnostic>();
                    _layer.RemoveAllAdornments();
                    return;
                }
            }
        }

        private static int CountNewlines(string text)
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

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (e.NewOrReformattedLines.Count > 0 || e.VerticalTranslation)
            {
                RenderAdornments();
            }
        }

        private void ScheduleUpdate(bool immediate = false)
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

                // Switch to UI thread to build error list cache (IWpfTableControl.Entries requires UI thread)
                await _joinableTaskFactory.SwitchToMainThreadAsync(token);

                if (_isDisposed || token.IsCancellationRequested)
                {
                    return;
                }

                // Build error list cache on UI thread
                Dictionary<ErrorListKey, ErrorListData> errorListCache = BuildErrorListCache();

                // Now run diagnostic collection on background thread with the cache
                var collectResult = await System.Threading.Tasks.Task.Run(
                    () => CollectDiagnostics(token, errorListCache), token).ConfigureAwait(false);

                await _joinableTaskFactory.SwitchToMainThreadAsync(token);

                if (_isDisposed || token.IsCancellationRequested)
                {
                    return;
                }

                _diagnosticsByLine = collectResult.Diagnostics;
                _hasPendingErrorListLookups = collectResult.HasPendingErrorListLookups;

                // Subscribe to error list changes if we have pending lookups
                if (_hasPendingErrorListLookups)
                {
                    SubscribeToErrorListChanges();
                }
                else
                {
                    UnsubscribeFromErrorListChanges();
                }

                RenderAdornments();
            }
            catch (OperationCanceledException)
            {
                // Debounce cancelled
            }
            catch (ObjectDisposedException)
            {
                // View or aggregator disposed
            }
        }

        private (Dictionary<int, LineDiagnostic> Diagnostics, bool HasPendingErrorListLookups) CollectDiagnostics(CancellationToken token, Dictionary<ErrorListKey, ErrorListData> errorListCache)
        {
            var result = new Dictionary<int, LineDiagnostic>();
            bool hasPendingLookups = false;

            try
            {
                ITextSnapshot snapshot = _view.TextSnapshot;
                SnapshotSpan fullSpan = new SnapshotSpan(snapshot, 0, snapshot.Length);

                // Get the document path for error list lookup
                string documentPath = GetDocumentPath();

                foreach (IMappingTagSpan<IErrorTag> tagSpan in _aggregator.GetTags(fullSpan))
                {
                    token.ThrowIfCancellationRequested();

                    NormalizedSnapshotSpanCollection spans = tagSpan.Span.GetSpans(snapshot);
                    if (spans.Count == 0)
                    {
                        continue;
                    }

                    SnapshotSpan span = spans[0];
                    int lineNumber = snapshot.GetLineNumberFromPosition(span.Start.Position);
                    DiagnosticSeverity severity = GetSeverity(tagSpan.Tag.ErrorType);

                    if (!IsSeverityEnabled(severity))
                    {
                        continue;
                    }

                    string message = ExtractTooltipText(tagSpan.Tag.ToolTipContent);
                    bool neededErrorListLookup = false;
                    ErrorListData errorListData = null;

                    if (string.IsNullOrWhiteSpace(message))
                    {
                        message = ExtractDescriptionViaReflection(tagSpan.Tag);
                    }

                    // Fallback to querying the Error List cache for the message
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        neededErrorListLookup = true;
                        errorListData = GetDataFromErrorListCache(errorListCache, documentPath, lineNumber, severity);
                        message = errorListData?.Message;
                    }

                    // If we still don't have a message and needed error list lookup,
                    // mark as pending and skip this diagnostic (don't show fallback)
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        if (neededErrorListLookup)
                        {
                            hasPendingLookups = true;
                        }

                        continue;
                    }

                    // Try to extract and strip the diagnostic code prefix from the message
                    string diagnosticCode = ExtractDiagnosticCode(message);
                    if (!string.IsNullOrEmpty(diagnosticCode))
                    {
                        message = StripCodePrefix(message, diagnosticCode);
                    }

                    // If we couldn't extract code from the message but have it from error list, use that
                    if (string.IsNullOrEmpty(diagnosticCode) && errorListData != null)
                    {
                        diagnosticCode = errorListData.ErrorCode;
                    }

                    string source = null;

                    if (result.TryGetValue(lineNumber, out LineDiagnostic existing))
                    {
                        // Keep the highest severity and collect messages
                        if (severity > existing.Severity)
                        {
                            existing.Severity = severity;
                            existing.PrimaryMessage = message;
                            existing.DiagnosticCode = diagnosticCode;
                            existing.Source = source;
                        }

                        existing.Count++;
                    }
                    else
                    {
                        result[lineNumber] = new LineDiagnostic
                        {
                            Severity = severity,
                            PrimaryMessage = message,
                            DiagnosticCode = diagnosticCode,
                            Source = source,
                            Count = 1,
                        };
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }

            return (result, hasPendingLookups);
        }

        private void RenderAdornments()
        {
            if (_isDisposed || _view.IsClosed || _view.InLayout)
            {
                return;
            }

            _layer.RemoveAllAdornments();

            if (_diagnosticsByLine.Count == 0)
            {
                return;
            }

            ITextSnapshot snapshot = _view.TextSnapshot;

            foreach (ITextViewLine viewLine in _view.TextViewLines)
            {
                int lineNumber = snapshot.GetLineNumberFromPosition(viewLine.Start.Position);

                if (!_diagnosticsByLine.TryGetValue(lineNumber, out LineDiagnostic diagnostic))
                {
                    continue;
                }

                if (ShouldHighlight(diagnostic.Severity))
                {
                    RenderLineHighlight(viewLine, diagnostic);
                }

                if (_options.ShowInlineMessages)
                {
                    RenderInlineMessage(viewLine, diagnostic);
                }
            }
        }

        private void RenderLineHighlight(ITextViewLine viewLine, LineDiagnostic diagnostic)
        {
            Brush background = GetBackgroundBrush(diagnostic.Severity);

            var highlight = new Border
            {
                Background = background,
                Width = Math.Max(_view.ViewportWidth, viewLine.Width),
                Height = viewLine.Height,
                IsHitTestVisible = false,
            };

            Canvas.SetLeft(highlight, _view.ViewportLeft);
            Canvas.SetTop(highlight, viewLine.Top);

            _layer.AddAdornment(
                AdornmentPositioningBehavior.TextRelative,
                viewLine.Extent,
                tag: null,
                adornment: highlight,
                removedCallback: null);
        }

        private void RenderInlineMessage(ITextViewLine viewLine, LineDiagnostic diagnostic)
        {
            // Apply the message template
            string displayMessage = ApplyMessageTemplate(diagnostic);

            if (diagnostic.Count > 1)
            {
                displayMessage += $" (+{diagnostic.Count - 1} more)";
            }

            displayMessage = "  " + displayMessage;

            // Truncate long messages
            if (displayMessage.Length > 200)
            {
                displayMessage = displayMessage.Substring(0, 197) + "...";
            }

            // Replace line breaks with a return symbol
            displayMessage = displayMessage.Replace("\r\n", " \u23CE ").Replace("\n", " \u23CE ").Replace("\r", " \u23CE ");

            Brush foreground = GetForegroundBrush(diagnostic.Severity);

            var textBlock = new TextBlock
            {
                Text = displayMessage,
                Foreground = foreground,
                FontSize = _view.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize * 0.9,
                FontFamily = _view.FormattedLineSource.DefaultTextProperties.Typeface.FontFamily,
                FontStyle = FontStyles.Italic,
                IsHitTestVisible = true,
                Opacity = 0.8,
                Tag = diagnostic,
                Cursor = System.Windows.Input.Cursors.Arrow,
            };

            ContextMenu contextMenu = DiagnosticContextMenu.Create(diagnostic);

            textBlock.MouseRightButtonDown += (s, e) =>
            {
                e.Handled = true;
            };

            textBlock.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                contextMenu.PlacementTarget = textBlock;
                contextMenu.IsOpen = true;
            };

            double left = viewLine.TextRight + 20;

            Canvas.SetLeft(textBlock, left);
            Canvas.SetTop(textBlock, viewLine.TextTop);

            _layer.AddAdornment(
                AdornmentPositioningBehavior.TextRelative,
                viewLine.Extent,
                tag: null,
                adornment: textBlock,
                removedCallback: null);
        }

        /// <summary>
        /// Applies the user's message template to format the diagnostic information.
        /// </summary>
        private string ApplyMessageTemplate(LineDiagnostic diagnostic)

        {
            string template = _options.MessageTemplate ?? "{message}";

            // Replace placeholders with actual values
            string result = template
                .Replace("{message}", diagnostic.PrimaryMessage ?? "")
                .Replace("{code}", diagnostic.DiagnosticCode ?? "")
                .Replace("{severity}", GetSeverityLabel(diagnostic.Severity))
                .Replace("{source}", diagnostic.Source ?? "");

            return result;
        }

        /// <summary>
        /// Gets the human-readable severity label for template substitution.
        /// </summary>
        private static string GetSeverityLabel(DiagnosticSeverity severity)
        {
            switch (severity)
            {
                case DiagnosticSeverity.Error: return "Error";
                case DiagnosticSeverity.Warning: return "Warning";
                case DiagnosticSeverity.Message: return "Info";
                default: return "";
            }
        }

        /// <summary>
        /// Extracts plain text from a tooltip content object, which may be a string,
        /// a <see cref="ContainerElement"/>, a <see cref="ClassifiedTextElement"/>,
        /// or a <see cref="ClassifiedTextRun"/>.
        /// </summary>
        private static string ExtractTooltipText(object content)
        {
            switch (content)
            {
                case string s:
                    return s;

                case ClassifiedTextRun run:
                    return run.Text ?? "";

                case ClassifiedTextElement textElement:
                    return string.Join("", textElement.Runs.Select(r => r.Text));

                case ContainerElement container:
                    foreach (object element in container.Elements)
                    {
                        string text = ExtractTooltipText(element);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text;
                        }
                    }
                    return "";

                case null:
                    return "";

                default:
                    return content.ToString();
            }
        }

        /// <summary>
        /// Some IErrorTag implementations (e.g. JsonErrorTag) store the message in a
        /// "Description" property that is not part of the IErrorTag interface.
        /// </summary>
        private static string ExtractDescriptionViaReflection(IErrorTag tag)
        {
            try
            {
                PropertyInfo prop = tag.GetType().GetProperty("Description", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (prop != null && prop.PropertyType == typeof(string))
                {
                    return prop.GetValue(tag) as string;
                }
            }
            catch
            {
                // Reflection may fail for security or access reasons; fall through
            }

            return null;
        }

        /// <summary>
        /// Attempts to extract a diagnostic code (like "CS0168") from the beginning of a message string.
        /// </summary>
        private static string ExtractDiagnosticCode(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            // Match a leading diagnostic code pattern like "CS0168:" or "CA1000:"
            var match = System.Text.RegularExpressions.Regex.Match(message, @"^([A-Z]{2,4}\d{4,5})\s*:");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Strips the diagnostic code prefix (e.g. "CS0161: ") from the message text.
        /// </summary>
        private static string StripCodePrefix(string message, string code)
        {
            // Remove patterns like "CS0161: " or "CS0161 : "
            var match = System.Text.RegularExpressions.Regex.Match(message, @"^" + System.Text.RegularExpressions.Regex.Escape(code) + @"\s*:\s*");
            if (match.Success)
            {
                return message.Substring(match.Length);
            }

            return message;
        }

        private bool ShouldHighlight(DiagnosticSeverity severity)
        {
            switch (_options.HighlightLines)
            {
                case SeverityFilter.All:
                    return true;
                case SeverityFilter.ErrorsAndWarnings:
                    return severity >= DiagnosticSeverity.Warning;
                case SeverityFilter.Errors:
                    return severity >= DiagnosticSeverity.Error;
                default:
                    return false;
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

        private static DiagnosticSeverity GetSeverity(string errorType)
        {
            if (string.IsNullOrEmpty(errorType))
            {
                return DiagnosticSeverity.Message;
            }

            switch (errorType)
            {
                case PredefinedErrorTypeNames.CompilerError:
                case PredefinedErrorTypeNames.OtherError:
                case PredefinedErrorTypeNames.SyntaxError:
                    return DiagnosticSeverity.Error;
                case PredefinedErrorTypeNames.Warning:
                    return DiagnosticSeverity.Warning;
                case PredefinedErrorTypeNames.Suggestion:
                case PredefinedErrorTypeNames.HintedSuggestion:
                    return DiagnosticSeverity.Message;
                default:
                    if (errorType.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return DiagnosticSeverity.Error;
                    }

                    if (errorType.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return DiagnosticSeverity.Warning;
                    }

                    return DiagnosticSeverity.Message;
            }
        }

        private static Brush GetBackgroundBrush(DiagnosticSeverity severity)
        {
            switch (severity)
            {
                case DiagnosticSeverity.Error: return _errorBackground;
                case DiagnosticSeverity.Warning: return _warningBackground;
                default: return _messageBackground;
            }
        }

        private static Brush GetForegroundBrush(DiagnosticSeverity severity)
        {
            switch (severity)
            {
                case DiagnosticSeverity.Error: return _errorForeground;
                case DiagnosticSeverity.Warning: return _warningForeground;
                default: return _messageForeground;
            }
        }

        /// <summary>
        /// Gets the document path for the current text view from the ITextDocument property.
        /// </summary>
        private string GetDocumentPath()
        {
            try
            {
                if (_view.TextBuffer.Properties.TryGetProperty(typeof(Microsoft.VisualStudio.Text.ITextDocument), out Microsoft.VisualStudio.Text.ITextDocument document))
                {
                    return document.FilePath;
                }
            }
            catch
            {
                // Property access may fail in some edge cases
            }

            return null;
        }

        /// <summary>
        /// Builds a cache of error list entries for the current document.
        /// Must be called on the UI thread.
        /// </summary>
        private Dictionary<ErrorListKey, ErrorListData> BuildErrorListCache()
        {
            var cache = new Dictionary<ErrorListKey, ErrorListData>();

            try
            {
                string documentPath = GetDocumentPath();

                if (string.IsNullOrEmpty(documentPath))
                {
                    return cache;
                }

                // Prefer IErrorList.TableControl.Entries for direct access (requires UI thread)
                if (_errorList?.TableControl != null)
                {
                    try
                    {
                        foreach (ITableEntryHandle entry in _errorList.TableControl.Entries)
                        {
                            AddEntryToCache(cache, entry, documentPath);
                        }

                        // If we got entries from the table control, we're done
                        if (cache.Count > 0)
                        {
                            return cache;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // May fail if called from wrong thread or during update
                    }
                }

                // Fallback: iterate through sources (may not always work due to async subscription)
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
                                foreach (ITableEntriesSnapshotFactory factory in collector.Factories.ToArray())
                                {
                                    ITableEntriesSnapshot snapshot = factory.GetCurrentSnapshot();

                                    if (snapshot == null)
                                    {
                                        continue;
                                    }

                                    AddSnapshotEntriesToCache(cache, snapshot, documentPath);
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

            return cache;
        }

        /// <summary>
        /// Adds a single entry (ITableEntryHandle) to the cache if it matches the document path.
        /// </summary>
        private static void AddEntryToCache(Dictionary<ErrorListKey, ErrorListData> cache, ITableEntryHandle entry, string documentPath)
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

                // Get the message text
                if (entry.TryGetValue(StandardTableKeyNames.Text, out object textObj) &&
                    textObj is string message &&
                    !string.IsNullOrWhiteSpace(message))
                {
                    var key = new ErrorListKey(documentPath, lineNumber, severity);

                    // Only add if not already present (first entry wins)
                    if (!cache.ContainsKey(key))
                    {
                        // Also try to get the error code
                        string errorCode = null;
                        if (entry.TryGetValue(StandardTableKeyNames.ErrorCode, out object errorCodeObj) &&
                            errorCodeObj is string code &&
                            !string.IsNullOrWhiteSpace(code))
                        {
                            errorCode = code;
                        }

                        cache[key] = new ErrorListData(message, errorCode);
                    }
                }
            }
            catch
            {
                // Entry access failed
            }
        }

        /// <summary>
        /// Adds entries from a snapshot to the cache for matching document path.
        /// </summary>
        private static void AddSnapshotEntriesToCache(Dictionary<ErrorListKey, ErrorListData> cache, ITableEntriesSnapshot snapshot, string documentPath)
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
                    DiagnosticSeverity severity = GetSeverityFromErrorListEntry(snapshot, i);

                    // Get the message text
                    if (snapshot.TryGetValue(i, StandardTableKeyNames.Text, out object textObj) &&
                        textObj is string message &&
                        !string.IsNullOrWhiteSpace(message))
                    {
                        var key = new ErrorListKey(documentPath, lineNumber, severity);

                        // Only add if not already present (first entry wins)
                        if (!cache.ContainsKey(key))
                        {
                            // Also try to get the error code
                            string errorCode = null;
                            if (snapshot.TryGetValue(i, StandardTableKeyNames.ErrorCode, out object errorCodeObj) &&
                                errorCodeObj is string code &&
                                !string.IsNullOrWhiteSpace(code))
                            {
                                errorCode = code;
                            }

                            cache[key] = new ErrorListData(message, errorCode);
                        }
                    }
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                // Snapshot count changed during iteration
            }
        }

        /// <summary>
        /// Gets data from the pre-built error list cache.
        /// Thread-safe - can be called from any thread.
        /// </summary>
        private static ErrorListData GetDataFromErrorListCache(
            Dictionary<ErrorListKey, ErrorListData> cache,
            string documentPath,
            int lineNumber,
            DiagnosticSeverity severity)
        {
            if (cache == null || string.IsNullOrEmpty(documentPath))
            {
                return null;
            }

            var key = new ErrorListKey(documentPath, lineNumber, severity);

            return cache.TryGetValue(key, out ErrorListData data) ? data : null;
        }

        /// <summary>
        /// Gets the severity from an Error List entry.
        /// </summary>
        private static DiagnosticSeverity GetSeverityFromErrorListEntry(ITableEntriesSnapshot snapshot, int index)
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

        private void OnViewClosed(object sender, EventArgs e)
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                _view.LayoutChanged -= OnLayoutChanged;
                _view.Closed -= OnViewClosed;
                _view.TextBuffer.Changed -= OnTextBufferChanged;
                _aggregator.BatchedTagsChanged -= OnBatchedTagsChanged;

                // Unsubscribe from error list changes
                UnsubscribeFromErrorListChanges();

                lock (_updateGate)
                {
                    _debounceCts?.Cancel();
                    _debounceCts?.Dispose();
                    _debounceCts = null;
                }

                _aggregator.Dispose();
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
    /// A key for the error list cache, combining document path, line number, and severity.
    /// </summary>
    internal readonly struct ErrorListKey : IEquatable<ErrorListKey>
    {
        public string DocumentPath { get; }
        public int LineNumber { get; }
        public DiagnosticSeverity Severity { get; }

        public ErrorListKey(string documentPath, int lineNumber, DiagnosticSeverity severity)
        {
            // Normalize the document path for consistent comparison
            DocumentPath = documentPath?.ToUpperInvariant() ?? string.Empty;
            LineNumber = lineNumber;
            Severity = severity;
        }

        public bool Equals(ErrorListKey other)
        {
            return LineNumber == other.LineNumber &&
                   Severity == other.Severity &&
                   string.Equals(DocumentPath, other.DocumentPath, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is ErrorListKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (DocumentPath?.GetHashCode() ?? 0);
                hash = hash * 31 + LineNumber;
                hash = hash * 31 + (int)Severity;
                return hash;
            }
        }
    }

    /// <summary>
    /// Data retrieved from the Error List for a diagnostic entry.
    /// </summary>
    internal sealed class ErrorListData
    {
        public string Message { get; }
        public string ErrorCode { get; }

        public ErrorListData(string message, string errorCode)
        {
            Message = message;
            ErrorCode = errorCode;
        }
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

        public void AddEntries(IReadOnlyList<ITableEntry> newEntries, bool removeAllEntries = false)
        {
            // Not used for snapshot collection
        }

        public void AddFactory(ITableEntriesSnapshotFactory newFactory, bool removeAllFactories = false)
        {
            if (newFactory == null)
            {
                return;
            }

            lock (_lock)
            {
                if (removeAllFactories)
                {
                    _factories.Clear();
                }

                _factories.Add(newFactory);
            }
        }

        public void AddSnapshot(ITableEntriesSnapshot newSnapshot, bool removeAllSnapshots = false)
        {
            // Not used for snapshot collection
        }

        public void FactorySnapshotChanged(ITableEntriesSnapshotFactory factory)
        {
            // Not used for snapshot collection
        }

        public void RemoveAllEntries()
        {
            // Not used for snapshot collection
        }

        public void RemoveAllFactories()
        {
            lock (_lock)
            {
                _factories.Clear();
            }
        }

        public void RemoveAllSnapshots()
        {
            // Not used for snapshot collection
        }

        public void RemoveEntries(IReadOnlyList<ITableEntry> oldEntries)
        {
            // Not used for snapshot collection
        }

        public void RemoveFactory(ITableEntriesSnapshotFactory oldFactory)
        {
            if (oldFactory == null)
            {
                return;
            }

            lock (_lock)
            {
                _factories.Remove(oldFactory);
            }
        }

        public void RemoveSnapshot(ITableEntriesSnapshot oldSnapshot)
        {
            // Not used for snapshot collection
        }

        public void ReplaceEntries(IReadOnlyList<ITableEntry> oldEntries, IReadOnlyList<ITableEntry> newEntries)
        {
            // Not used for snapshot collection
        }

        public void ReplaceFactory(ITableEntriesSnapshotFactory oldFactory, ITableEntriesSnapshotFactory newFactory)
        {
            if (oldFactory == null || newFactory == null)
            {
                return;
            }

            lock (_lock)
            {
                int index = _factories.IndexOf(oldFactory);

                if (index >= 0)
                {
                    _factories[index] = newFactory;
                }
                else
                {
                    _factories.Add(newFactory);
                }
            }
        }

        public void ReplaceSnapshot(ITableEntriesSnapshot oldSnapshot, ITableEntriesSnapshot newSnapshot)
        {
            // Not used for snapshot collection
        }
    }
}
