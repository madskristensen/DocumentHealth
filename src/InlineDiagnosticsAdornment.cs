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
    internal sealed class InlineDiagnosticsAdornment : IDisposable
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
        private readonly List<TextBlock> _inlineMessageAdornments = new List<TextBlock>();

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
            _view.VisualElement.LayoutUpdated += OnVisualLayoutUpdated;
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
                int newlinesBefore = DiagnosticDataProvider.CountNewlines(change.OldText);
                int newlinesAfter = DiagnosticDataProvider.CountNewlines(change.NewText);

                if (newlinesBefore != newlinesAfter)
                {
                    // Line count changed; clear adornments immediately
                    _layer.RemoveAllAdornments();
                    return;
                }
            }
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
            _inlineMessageAdornments.Clear();

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

            ContextMenu contextMenu = null;

            textBlock.MouseRightButtonDown += (s, e) =>
            {
                e.Handled = true;
            };

            textBlock.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;

                // Lazily create context menu on first use
                if (contextMenu == null)
                {
                    contextMenu = DiagnosticContextMenu.Create(diagnostic);
                }

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

            _inlineMessageAdornments.Add(textBlock);
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

        /// <summary>
        /// Called when the WPF visual tree updates layout. Checks whether any of our inline
        /// message adornments overlap with elements on sibling adornment layers (e.g. Copilot
        /// ghost text) and hides them to avoid visual clutter.
        /// </summary>
        private void OnVisualLayoutUpdated(object sender, EventArgs e)
        {
            if (_isDisposed || _view.IsClosed || _inlineMessageAdornments.Count == 0)
            {
                return;
            }

            try
            {
                UpdateAdornmentVisibility();
            }
            catch
            {
                // Guard against unexpected visual tree states
            }
        }

        /// <summary>
        /// For each inline message TextBlock, checks all sibling adornment layer canvases
        /// for overlapping child elements. Hides the TextBlock if overlap is detected;
        /// restores it when the overlap clears.
        /// </summary>
        private void UpdateAdornmentVisibility()
        {
            // Walk up from any TextBlock to find the parent panel containing all adornment layers
            if (_inlineMessageAdornments.Count == 0)
            {
                return;
            }

            TextBlock firstAdornment = _inlineMessageAdornments[0];
            DependencyObject layerCanvas = VisualTreeHelper.GetParent(firstAdornment);
            if (layerCanvas == null)
            {
                return;
            }

            DependencyObject layerPanel = VisualTreeHelper.GetParent(layerCanvas);
            if (layerPanel == null)
            {
                return;
            }

            // Collect sibling canvases (other adornment layers)
            int siblingCount = VisualTreeHelper.GetChildrenCount(layerPanel);
            var siblingCanvases = new List<Canvas>(siblingCount);
            for (int i = 0; i < siblingCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(layerPanel, i);
                if (child is Canvas canvas && !ReferenceEquals(canvas, layerCanvas))
                {
                    siblingCanvases.Add(canvas);
                }
            }

            if (siblingCanvases.Count == 0)
            {
                return;
            }

            foreach (TextBlock adornment in _inlineMessageAdornments)
            {
                double adornmentLeft = Canvas.GetLeft(adornment);
                double adornmentTop = Canvas.GetTop(adornment);
                double adornmentHeight = adornment.ActualHeight > 0 ? adornment.ActualHeight : adornment.DesiredSize.Height;

                if (adornmentHeight <= 0)
                {
                    continue;
                }

                bool hasOverlap = false;

                foreach (Canvas sibling in siblingCanvases)
                {
                    int childCount = VisualTreeHelper.GetChildrenCount(sibling);
                    for (int i = 0; i < childCount; i++)
                    {
                        DependencyObject sibChild = VisualTreeHelper.GetChild(sibling, i);
                        if (!(sibChild is UIElement sibElement) || sibElement.Visibility != Visibility.Visible)
                        {
                            continue;
                        }

                        double sibTop = Canvas.GetTop(sibElement);
                        double sibLeft = Canvas.GetLeft(sibElement);
                        if (double.IsNaN(sibTop) || double.IsNaN(sibLeft))
                        {
                            continue;
                        }

                        FrameworkElement sibFE = sibElement as FrameworkElement;
                        double sibHeight = sibFE != null ? (sibFE.ActualHeight > 0 ? sibFE.ActualHeight : sibFE.DesiredSize.Height) : 0;

                        if (sibHeight <= 0)
                        {
                            continue;
                        }

                        // Check vertical overlap (same line)
                        bool verticalOverlap = adornmentTop < sibTop + sibHeight && adornmentTop + adornmentHeight > sibTop;

                        // Check horizontal overlap (sibling starts at or after line text end, overlapping our adornment)
                        bool horizontalOverlap = sibLeft >= adornmentLeft - 20;

                        if (verticalOverlap && horizontalOverlap)
                        {
                            hasOverlap = true;
                            break;
                        }
                    }

                    if (hasOverlap)
                    {
                        break;
                    }
                }

                adornment.Visibility = hasOverlap ? Visibility.Collapsed : Visibility.Visible;
            }
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
                _view.LayoutChanged -= OnLayoutChanged;
                _view.Closed -= OnViewClosed;
                _view.TextBuffer.Changed -= OnTextBufferChanged;
                _view.VisualElement.LayoutUpdated -= OnVisualLayoutUpdated;
                _dataProvider.DiagnosticsUpdated -= OnDiagnosticsUpdated;
            }
        }
    }
}
