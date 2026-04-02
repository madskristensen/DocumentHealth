using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
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
            General options)
        {
            _view = view;
            _layer = view.GetAdornmentLayer(InlineDiagnosticsAdornmentProvider.AdornmentLayerName);
            _aggregator = aggregator;
            _joinableTaskFactory = joinableTaskFactory;
            _options = options;

            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnViewClosed;
            _aggregator.BatchedTagsChanged += OnBatchedTagsChanged;

            ScheduleUpdate(immediate: true);
        }

        private void OnBatchedTagsChanged(object sender, BatchedTagsChangedEventArgs e)
        {
            ScheduleUpdate();
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

                Dictionary<int, LineDiagnostic> diagnostics = await System.Threading.Tasks.Task.Run(
                    () => CollectDiagnostics(token), token).ConfigureAwait(false);

                await _joinableTaskFactory.SwitchToMainThreadAsync(token);

                if (_isDisposed || token.IsCancellationRequested)
                {
                    return;
                }

                _diagnosticsByLine = diagnostics;
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

        private Dictionary<int, LineDiagnostic> CollectDiagnostics(CancellationToken token)
        {
            var result = new Dictionary<int, LineDiagnostic>();

            try
            {
                ITextSnapshot snapshot = _view.TextSnapshot;
                SnapshotSpan fullSpan = new SnapshotSpan(snapshot, 0, snapshot.Length);

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

                    if (string.IsNullOrWhiteSpace(message))
                    {
                        continue;
                    }

                    // Try to extract and strip the diagnostic code prefix from the message
                    string diagnosticCode = ExtractDiagnosticCode(message);
                    if (!string.IsNullOrEmpty(diagnosticCode))
                    {
                        message = StripCodePrefix(message, diagnosticCode);
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

            return result;
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
                case HighlightSeverity.All:
                    return true;
                case HighlightSeverity.ErrorsAndWarnings:
                    return severity >= DiagnosticSeverity.Warning;
                case HighlightSeverity.Errors:
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
            switch (errorType)
            {
                case PredefinedErrorTypeNames.CompilerError:
                case PredefinedErrorTypeNames.OtherError:
                case PredefinedErrorTypeNames.SyntaxError:
                    return DiagnosticSeverity.Error;
                case PredefinedErrorTypeNames.Warning:
                    return DiagnosticSeverity.Warning;
                default:
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

        private void OnViewClosed(object sender, EventArgs e)
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                _view.LayoutChanged -= OnLayoutChanged;
                _view.Closed -= OnViewClosed;
                _aggregator.BatchedTagsChanged -= OnBatchedTagsChanged;

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
}
