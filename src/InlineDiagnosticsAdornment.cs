using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
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
        private readonly General _options;
        private readonly DiagnosticDataProvider _dataProvider;

        private static readonly Brush _errorBackground;
        private static readonly Brush _warningBackground;
        private static readonly Brush _messageBackground;
        private static readonly Brush _errorForeground;
        private static readonly Brush _warningForeground;
        private static readonly Brush _messageForeground;

        private volatile bool _isDisposed;

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
            General options,
            DiagnosticDataProvider dataProvider)
        {
            _view = view;
            _layer = view.GetAdornmentLayer(InlineDiagnosticsAdornmentProvider.AdornmentLayerName);
            _options = options;
            _dataProvider = dataProvider;

            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnViewClosed;
            _view.TextBuffer.Changed += OnTextBufferChanged;
            _dataProvider.DiagnosticsUpdated += OnDiagnosticsUpdated;

            // Initial render
            RenderAdornments();
        }

        private void OnDiagnosticsUpdated(object sender, EventArgs e)
        {
            RenderAdornments();
        }

        private void OnTextBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (_isDisposed || _dataProvider.DiagnosticsByLine.Count == 0)
            {
                return;
            }

            foreach (ITextChange change in e.Changes)
            {
                int newlinesBefore = CountNewlines(change.OldText);
                int newlinesAfter = CountNewlines(change.NewText);

                if (newlinesBefore != newlinesAfter)
                {
                    // Line count changed; clear adornments immediately
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

        private void RenderAdornments()
        {
            if (_isDisposed || _view.IsClosed || _view.InLayout)
            {
                return;
            }

            _layer.RemoveAllAdornments();

            IReadOnlyDictionary<int, LineDiagnostic> diagnosticsByLine = _dataProvider.DiagnosticsByLine;
            if (diagnosticsByLine.Count == 0)
            {
                return;
            }

            ITextSnapshot snapshot = _view.TextSnapshot;

            foreach (ITextViewLine viewLine in _view.TextViewLines)
            {
                int lineNumber = snapshot.GetLineNumberFromPosition(viewLine.Start.Position);

                if (!diagnosticsByLine.TryGetValue(lineNumber, out LineDiagnostic diagnostic))
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
                _view.TextBuffer.Changed -= OnTextBufferChanged;
                _dataProvider.DiagnosticsUpdated -= OnDiagnosticsUpdated;
            }
        }
    }
}
