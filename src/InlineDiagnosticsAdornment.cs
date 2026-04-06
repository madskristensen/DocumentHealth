using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Tagging;

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
        private readonly ITextDocument _textDocument;
        private readonly IEditorFormatMap _formatMap;

        /// <summary>
        /// Foreground opacity applied to inline message text brushes.
        /// </summary>
        private const double ForegroundOpacity = 0.8;

        /// <summary>
        /// Background opacity applied to line highlight brushes.
        /// </summary>
        private const double BackgroundOpacity = 0.15;

        // Instance-level brushes loaded from Fonts & Colors settings.
        private Brush _errorBackground;
        private Brush _warningBackground;
        private Brush _messageBackground;
        private Brush _errorForeground;
        private Brush _warningForeground;
        private Brush _messageForeground;

        // Font weight and style from Fonts & Colors (per severity).
        private FontWeight _errorFontWeight;
        private FontWeight _warningFontWeight;
        private FontWeight _messageFontWeight;
        private System.Windows.FontStyle _errorFontStyle;
        private System.Windows.FontStyle _warningFontStyle;
        private System.Windows.FontStyle _messageFontStyle;

        private volatile bool _isDisposed;
        private readonly List<TextBlock> _inlineMessageAdornments = new List<TextBlock>();
        private readonly List<TextBlock> _textBlockPool = new List<TextBlock>();

        // Cached visual tree references for visibility checking to avoid repeated tree walks
        private WeakReference<DependencyObject> _cachedLayerCanvas;
        private WeakReference<DependencyObject> _cachedLayerPanel;

        /// <summary>
        /// Tracks whether a diagnostic refresh is pending. Initialized to true so that
        /// the first diagnostic update after a file is opened is rendered even in OnSave mode.
        /// </summary>
        private volatile bool _pendingSaveRender = true;

        private const int OnSaveContinuousGraceMilliseconds = 1500;
        private const int InitialLoadContinuousGraceMilliseconds = 10000;
        private DateTime _onSaveContinuousUntilUtc = DateTime.MinValue;

        // In OnSave mode, this stores the set of line numbers currently allowed to render.
        // New diagnostics are only added on save; resolved diagnostics are removed immediately.
        private readonly HashSet<int> _visibleLineNumbersOnSave = new HashSet<int>();

        public InlineDiagnosticsAdornment(
            IWpfTextView view,
            General options,
            DiagnosticDataProvider dataProvider,
            ITextDocument textDocument,
            IEditorFormatMap formatMap)
        {
            _view = view;
            _layer = view.GetAdornmentLayer(InlineDiagnosticsAdornmentProvider.AdornmentLayerName);
            _options = options;
            _dataProvider = dataProvider;
            _textDocument = textDocument;
            _formatMap = formatMap;

            // Load colors from Fonts & Colors immediately.
            RefreshBrushesFromFormatMap();

            // Subscribe to static event first to enable cleanup on failure.
            // This ensures we can always unsubscribe in Dispose even if later subscriptions fail.
            General.Saved += OnOptionsSaved;

            try
            {
                _view.LayoutChanged += OnLayoutChanged;
                _view.Closed += OnViewClosed;
                _view.TextBuffer.Changed += OnTextBufferChanged;
                _view.VisualElement.LayoutUpdated += OnVisualLayoutUpdated;
                _dataProvider.DiagnosticsUpdated += OnDiagnosticsUpdated;
                _formatMap.FormatMappingChanged += OnFormatMappingChanged;

                // During solution restore, diagnostics can arrive shortly after the first empty refresh.
                // Keep a short startup grace window so late Roslyn diagnostics can still be added.
                _onSaveContinuousUntilUtc = DateTime.UtcNow.AddMilliseconds(InitialLoadContinuousGraceMilliseconds);

                if (_textDocument != null)
                {
                    _textDocument.FileActionOccurred += OnFileActionOccurred;
                }
            }
            catch
            {
                // If any subscription fails, clean up and rethrow
                Dispose();
                throw;
            }
        }

        private void OnOptionsSaved(General options)
        {
            if (_isDisposed || _view.IsClosed)
            {
                return;
            }

            RenderAdornments();
        }

        /// <summary>
        /// Called when the user changes any Fonts and Colors entry that we use.
        /// </summary>
        private void OnFormatMappingChanged(object sender, FormatItemsEventArgs e)
        {
            if (_isDisposed)
            {
                return;
            }

            if (e.ChangedItems.Contains(DiagnosticFormatDefinitions.ErrorFormat) ||
                e.ChangedItems.Contains(DiagnosticFormatDefinitions.WarningFormat) ||
                e.ChangedItems.Contains(DiagnosticFormatDefinitions.MessageFormat))
            {
                RefreshBrushesFromFormatMap();
                RenderAdornments();
            }
        }

        /// <summary>
        /// Reads colors and font styles from the editor format map.
        /// Each severity has a single format entry whose foreground is used for
        /// inline text and whose background is used for line highlights.
        /// </summary>
        private void RefreshBrushesFromFormatMap()
        {
            ReadFormat(DiagnosticFormatDefinitions.ErrorFormat,
                out _errorForeground, out _errorBackground, out _errorFontWeight, out _errorFontStyle);
            ReadFormat(DiagnosticFormatDefinitions.WarningFormat,
                out _warningForeground, out _warningBackground, out _warningFontWeight, out _warningFontStyle);
            ReadFormat(DiagnosticFormatDefinitions.MessageFormat,
                out _messageForeground, out _messageBackground, out _messageFontWeight, out _messageFontStyle);
        }

        /// <summary>
        /// Reads foreground (inline text), background (line highlight), and font
        /// style from a single Fonts and Colors entry.
        /// </summary>
        private void ReadFormat(string formatName,
            out Brush foreground, out Brush background,
            out FontWeight fontWeight, out System.Windows.FontStyle fontStyle)
        {
            System.Windows.ResourceDictionary props = _formatMap.GetProperties(formatName);

            // Foreground → inline text color
            if (props.Contains(EditorFormatDefinition.ForegroundBrushId) &&
                props[EditorFormatDefinition.ForegroundBrushId] is SolidColorBrush fgBrush)
            {
                foreground = CreateFrozenBrush(fgBrush.Color, ForegroundOpacity);
            }
            else if (props.Contains(EditorFormatDefinition.ForegroundColorId) &&
                     props[EditorFormatDefinition.ForegroundColorId] is Color fgColor)
            {
                foreground = CreateFrozenBrush(fgColor, ForegroundOpacity);
            }
            else
            {
                foreground = CreateFrozenBrush(Colors.Gray, ForegroundOpacity);
            }

            // Background → line highlight color
            if (props.Contains(EditorFormatDefinition.BackgroundBrushId) &&
                props[EditorFormatDefinition.BackgroundBrushId] is SolidColorBrush bgBrush)
            {
                background = CreateFrozenBrush(bgBrush.Color, BackgroundOpacity);
            }
            else if (props.Contains(EditorFormatDefinition.BackgroundColorId) &&
                     props[EditorFormatDefinition.BackgroundColorId] is Color bgColor)
            {
                background = CreateFrozenBrush(bgColor, BackgroundOpacity);
            }
            else
            {
                background = CreateFrozenBrush(Colors.Gray, BackgroundOpacity);
            }

            fontWeight = props.Contains("IsBold") && props["IsBold"] is bool bold && bold
                ? FontWeights.Bold
                : FontWeights.Normal;

            fontStyle = props.Contains("IsItalic") && props["IsItalic"] is bool italic && italic
                ? FontStyles.Italic
                : FontStyles.Normal;
        }

        private static Brush CreateFrozenBrush(Color color, double opacity)
        {
            var brush = new SolidColorBrush(color) { Opacity = opacity };
            brush.Freeze();
            return brush;
        }

        private void OnFileActionOccurred(object sender, TextDocumentFileActionEventArgs e)
        {
            if (e.FileActionType == FileActionTypes.ContentSavedToDisk)
            {
                _pendingSaveRender = true;
                _onSaveContinuousUntilUtc = DateTime.UtcNow.AddMilliseconds(OnSaveContinuousGraceMilliseconds);
                _dataProvider.ScheduleUpdate(immediate: true);
            }
        }

        private void OnDiagnosticsUpdated(object sender, EventArgs e)
        {
            // Clear the suppress flag when diagnostics are updated
            _suppressRenderUntilDiagnosticUpdate = false;

            if (_options.UpdateMode == UpdateMode.OnSave)
            {
                IReadOnlyDictionary<int, LineDiagnostic> diagnosticsByLine = _dataProvider.DiagnosticsByLine;
                bool shouldRender;

                if (_pendingSaveRender)
                {
                    // Don't consume the flag until diagnostics are actually renderable.
                    // For C# files, early updates can contain line diagnostics without
                    // hydrated message text yet. Consuming the flag too early prevents
                    // the later hydration update from rendering the message.
                    if (diagnosticsByLine.Count > 0 && !HasRenderableDiagnostics())
                    {
                        return;
                    }

                    _pendingSaveRender = false;
                    shouldRender = ReplaceVisibleLineNumbers(diagnosticsByLine);
                }
                else if (IsWithinOnSaveContinuousGracePeriod())
                {
                    shouldRender = ReplaceVisibleLineNumbers(diagnosticsByLine);
                }
                else
                {
                    shouldRender = RemoveResolvedLineNumbers(diagnosticsByLine);
                }

                if (!shouldRender)
                {
                    return;
                }
            }

            // For Above/Below placement, force the view to re-format lines so
            // the line transform source is re-queried with updated diagnostics.
            // This causes OnLayoutChanged → RenderAdornments to run with the
            // correct expanded line heights.
            if (_options.MessagePlacement != MessagePosition.Inline &&
                _options.ShowInlineMessages &&
                !_view.IsClosed && !_view.InLayout &&
                _view.TextViewLines != null &&
                _view.TextViewLines.FirstVisibleLine != null)
            {
                ITextViewLine firstVisible = _view.TextViewLines.FirstVisibleLine;
                _view.DisplayTextLineContainingBufferPosition(
                    firstVisible.Start,
                    firstVisible.Top - _view.ViewportTop,
                    ViewRelativePosition.Top);

                // Render immediately as a fallback in case the forced reformat does not
                // produce a LayoutChanged payload that triggers rendering.
                RenderAdornments();
                return;
            }

            RenderAdornments();
        }

        /// <summary>
        /// Returns true when the current diagnostics are ready for user-visible rendering.
        /// In OnSave mode we keep waiting until this is true so we don't lock in highlight-only output.
        /// </summary>
        private bool HasRenderableDiagnostics()
        {
            IReadOnlyDictionary<int, LineDiagnostic> diagnosticsByLine = _dataProvider.DiagnosticsByLine;
            if (diagnosticsByLine.Count == 0)
            {
                return false;
            }

            if (!_options.ShowInlineMessages)
            {
                return true;
            }

            foreach (LineDiagnostic diagnostic in diagnosticsByLine.Values)
            {
                string displayMessage = ApplyMessageTemplate(diagnostic);
                if (!string.IsNullOrWhiteSpace(displayMessage))
                {
                    return true;
                }
            }

            return false;
        }

        private volatile bool _suppressRenderUntilDiagnosticUpdate;

        private void OnTextBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (_isDisposed || _dataProvider.DiagnosticsByLine.Count == 0)
            {
                return;
            }

            // In on-save mode, clear adornments immediately when any edit occurs
            // so stale diagnostics don't remain at wrong positions
            if (_options.UpdateMode == UpdateMode.OnSave)
            {
                _layer.RemoveAllAdornments();
                _inlineMessageAdornments.Clear();
                _visibleLineNumbersOnSave.Clear();
                return;
            }

            foreach (ITextChange change in e.Changes)
            {
                int newlinesBefore = DiagnosticDataProvider.CountNewlines(change.OldText);
                int newlinesAfter = DiagnosticDataProvider.CountNewlines(change.NewText);

                if (newlinesBefore != newlinesAfter)
                {
                    // Line count changed; clear adornments immediately and suppress re-render
                    // until diagnostics are updated to prevent blinking
                    _layer.RemoveAllAdornments();
                    _inlineMessageAdornments.Clear();
                    _suppressRenderUntilDiagnosticUpdate = true;
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
            if (_isDisposed || _view.IsClosed || _view.InLayout || _suppressRenderUntilDiagnosticUpdate)
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

            bool filterByOnSaveLines = _options.UpdateMode == UpdateMode.OnSave;
            if (filterByOnSaveLines && _visibleLineNumbersOnSave.Count == 0)
            {
                return;
            }

            ITextSnapshot snapshot = _view.TextSnapshot;

            foreach (ITextViewLine viewLine in _view.TextViewLines)
            {
                int lineNumber = snapshot.GetLineNumberFromPosition(viewLine.Start.Position);

                if (filterByOnSaveLines && !_visibleLineNumbersOnSave.Contains(lineNumber))
                {
                    continue;
                }

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

        private bool ReplaceVisibleLineNumbers(IReadOnlyDictionary<int, LineDiagnostic> diagnosticsByLine)
        {
            if (_visibleLineNumbersOnSave.Count == diagnosticsByLine.Count)
            {
                bool hasDifference = false;

                foreach (KeyValuePair<int, LineDiagnostic> diagnostic in diagnosticsByLine)
                {
                    if (!_visibleLineNumbersOnSave.Contains(diagnostic.Key))
                    {
                        hasDifference = true;
                        break;
                    }
                }

                if (!hasDifference)
                {
                    return false;
                }
            }

            _visibleLineNumbersOnSave.Clear();

            foreach (KeyValuePair<int, LineDiagnostic> diagnostic in diagnosticsByLine)
            {
                _visibleLineNumbersOnSave.Add(diagnostic.Key);
            }

            return true;
        }

        private bool RemoveResolvedLineNumbers(IReadOnlyDictionary<int, LineDiagnostic> diagnosticsByLine)
        {
            if (_visibleLineNumbersOnSave.Count == 0)
            {
                return false;
            }

            var resolvedLineNumbers = new List<int>();

            foreach (int lineNumber in _visibleLineNumbersOnSave)
            {
                if (!diagnosticsByLine.ContainsKey(lineNumber))
                {
                    resolvedLineNumbers.Add(lineNumber);
                }
            }

            if (resolvedLineNumbers.Count == 0)
            {
                return false;
            }

            foreach (int lineNumber in resolvedLineNumbers)
            {
                _visibleLineNumbersOnSave.Remove(lineNumber);
            }

            return true;
        }

        private bool IsWithinOnSaveContinuousGracePeriod()
        {
            return DateTime.UtcNow <= _onSaveContinuousUntilUtc;
        }

        private void RenderLineHighlight(ITextViewLine viewLine, LineDiagnostic diagnostic)
        {
            Brush background = GetBackgroundBrush(diagnostic.Severity);

            double fontSize = _view.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize;
            double messageBandHeight = (fontSize * 0.9) + 4;
            double highlightTop = viewLine.TextTop;
            double highlightHeight = viewLine.TextHeight;

            if (_options.MessagePlacement == MessagePosition.Above)
            {
                highlightTop = viewLine.TextTop - messageBandHeight;
                highlightHeight = messageBandHeight;
            }
            else if (_options.MessagePlacement == MessagePosition.Below)
            {
                highlightTop = viewLine.TextBottom;
                highlightHeight = messageBandHeight;
            }

            var highlight = new Border
            {
                Background = background,
                Width = Math.Max(_view.ViewportWidth, viewLine.Width),
                Height = highlightHeight,
                IsHitTestVisible = false,
            };

            Canvas.SetLeft(highlight, _view.ViewportLeft);
            Canvas.SetTop(highlight, highlightTop);

            _layer.AddAdornment(
                AdornmentPositioningBehavior.TextRelative,
                viewLine.Extent,
                tag: null,
                adornment: highlight,
                removedCallback: null);
        }

        private void RenderInlineMessage(ITextViewLine viewLine, LineDiagnostic diagnostic)
        {
            // Build the display message using StringBuilder to minimize allocations
            string displayMessage = BuildDisplayMessage(diagnostic);

            Brush foreground = GetForegroundBrush(diagnostic.Severity);

            TextBlock textBlock = _textBlockPool.Count > 0 ? _textBlockPool[_textBlockPool.Count - 1] : new TextBlock();
            if (_textBlockPool.Count > 0) _textBlockPool.RemoveAt(_textBlockPool.Count - 1);

            textBlock.Text = displayMessage;
            textBlock.Foreground = foreground;
            textBlock.FontSize = _view.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize * 0.9;
            textBlock.FontFamily = _view.FormattedLineSource.DefaultTextProperties.Typeface.FontFamily;
            textBlock.FontStyle = GetFontStyle(diagnostic.Severity);
            textBlock.FontWeight = GetFontWeight(diagnostic.Severity);
            textBlock.IsHitTestVisible = true;
            textBlock.Tag = diagnostic;
            textBlock.Cursor = System.Windows.Input.Cursors.Arrow;
            textBlock.ToolTip = BuildDiagnosticToolTip(diagnostic);

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

            double left;
            double top;

            MessagePosition placement = _options.MessagePlacement;

            if (placement == MessagePosition.Above)
            {
                // Align with the first non-whitespace character on the line
                left = GetIndentLeft(viewLine);
                double fontSize = _view.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize;
                double messageHeight = fontSize * 0.9;
                top = viewLine.TextTop - messageHeight - 2;
            }
            else if (placement == MessagePosition.Below)
            {
                left = GetIndentLeft(viewLine);
                top = viewLine.TextBottom + 2;
            }
            else
            {
                // Inline: position at end of text
                left = viewLine.TextRight + 20;
                top = viewLine.TextTop;
            }

            Canvas.SetLeft(textBlock, left);
            Canvas.SetTop(textBlock, top);

            _layer.AddAdornment(
                AdornmentPositioningBehavior.TextRelative,
                viewLine.Extent,
                tag: null,
                adornment: textBlock,
                removedCallback: null);

            _inlineMessageAdornments.Add(textBlock);
        }

        /// <summary>
        /// Gets the horizontal position of the first non-whitespace character on the
        /// view line, so above/below messages align with the code indentation.
        /// </summary>
        private double GetIndentLeft(ITextViewLine viewLine)
        {
            ITextSnapshotLine snapshotLine = _view.TextSnapshot.GetLineFromPosition(viewLine.Start.Position);
            string lineText = snapshotLine.GetText();
            int firstNonWhitespace = 0;

            while (firstNonWhitespace < lineText.Length && char.IsWhiteSpace(lineText[firstNonWhitespace]))
            {
                firstNonWhitespace++;
            }

            if (firstNonWhitespace >= snapshotLine.Length)
            {
                return viewLine.TextLeft;
            }

            SnapshotPoint indentPoint = snapshotLine.Start + firstNonWhitespace;

            if (indentPoint >= viewLine.Start && indentPoint <= viewLine.End)
            {
                return viewLine.GetCharacterBounds(indentPoint).Left;
            }

            return viewLine.TextLeft;
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
        /// Builds the final display message for a diagnostic, applying template, count suffix,
        /// positioning prefix, truncation, and line break normalization.
        /// Uses StringBuilder to minimize allocations in this hot path.
        /// </summary>
        private string BuildDisplayMessage(LineDiagnostic diagnostic)
        {
            string baseMessage = ApplyMessageTemplate(diagnostic);

            // Calculate approximate capacity to avoid reallocations
            int capacity = baseMessage.Length + 20;
            if (_options.MessagePlacement == MessagePosition.Inline)
            {
                capacity += 2;
            }

            var sb = new StringBuilder(capacity);

            // Add inline prefix if needed
            if (_options.MessagePlacement == MessagePosition.Inline)
            {
                sb.Append("  ");
            }

            // Add the base message, replacing line breaks inline
            for (int i = 0; i < baseMessage.Length; i++)
            {
                char c = baseMessage[i];
                if (c == '\r')
                {
                    // Check for \r\n
                    if (i + 1 < baseMessage.Length && baseMessage[i + 1] == '\n')
                    {
                        i++; // Skip the \n
                    }
                    sb.Append(" \u23CE ");
                }
                else if (c == '\n')
                {
                    sb.Append(" \u23CE ");
                }
                else
                {
                    sb.Append(c);
                }

                // Check for truncation (account for suffix we may add)
                if (sb.Length >= 197)
                {
                    sb.Length = 197;
                    sb.Append("...");
                    return sb.ToString();
                }
            }

            // Add count suffix if multiple diagnostics on the line
            if (diagnostic.Count > 1)
            {
                sb.Append(" (+");
                sb.Append(diagnostic.Count - 1);
                sb.Append(" more)");
            }

            // Final truncation check
            if (sb.Length > 200)
            {
                sb.Length = 197;
                sb.Append("...");
            }

            return sb.ToString();
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
        /// Internal helper for testing message template formatting.
        /// </summary>
        internal static string FormatMessage(string template, string message, string code, DiagnosticSeverity severity, string source)
        {
            return (template ?? "{message}")
                .Replace("{message}", message ?? "")
                .Replace("{code}", code ?? "")
                .Replace("{severity}", GetSeverityLabel(severity))
                .Replace("{source}", source ?? "");
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

        private Brush GetBackgroundBrush(DiagnosticSeverity severity)
        {
            switch (severity)
            {
                case DiagnosticSeverity.Error: return _errorBackground;
                case DiagnosticSeverity.Warning: return _warningBackground;
                default: return _messageBackground;
            }
        }

        private Brush GetForegroundBrush(DiagnosticSeverity severity)
        {
            switch (severity)
            {
                case DiagnosticSeverity.Error: return _errorForeground;
                case DiagnosticSeverity.Warning: return _warningForeground;
                default: return _messageForeground;
            }
        }

        private FontWeight GetFontWeight(DiagnosticSeverity severity)
        {
            switch (severity)
            {
                case DiagnosticSeverity.Error: return _errorFontWeight;
                case DiagnosticSeverity.Warning: return _warningFontWeight;
                default: return _messageFontWeight;
            }
        }

        private System.Windows.FontStyle GetFontStyle(DiagnosticSeverity severity)
        {
            switch (severity)
            {
                case DiagnosticSeverity.Error: return _errorFontStyle;
                case DiagnosticSeverity.Warning: return _warningFontStyle;
                default: return _messageFontStyle;
            }
        }

        /// <summary>
        /// Builds a formatted WPF ToolTip showing all diagnostic entries on a line.
        /// Error codes are displayed in bold, with blank lines separating multiple entries.
        /// </summary>
        internal static ToolTip BuildDiagnosticToolTip(LineDiagnostic diagnostic)
        {
            List<DiagnosticEntry> entries = diagnostic.Entries;

            // Fall back to primary message if no entries are available
            if (entries == null || entries.Count == 0)
            {
                if (string.IsNullOrEmpty(diagnostic.PrimaryMessage))
                {
                    return null;
                }

                entries = new List<DiagnosticEntry>
                {
                    new DiagnosticEntry
                    {
                        Severity = diagnostic.Severity,
                        Message = diagnostic.PrimaryMessage,
                        DiagnosticCode = diagnostic.DiagnosticCode,
                    }
                };
            }

            var textBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 600,
            };
            textBlock.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolTipTextBrushKey);

            for (int i = 0; i < entries.Count; i++)
            {
                DiagnosticEntry entry = entries[i];

                // Add blank line separator between entries
                if (i > 0)
                {
                    textBlock.Inlines.Add(new System.Windows.Documents.LineBreak());
                    textBlock.Inlines.Add(new System.Windows.Documents.LineBreak());
                }

                // Bold error code
                if (!string.IsNullOrEmpty(entry.DiagnosticCode))
                {
                    textBlock.Inlines.Add(new System.Windows.Documents.Bold(
                        new System.Windows.Documents.Run(entry.DiagnosticCode + ": ")));
                }

                // Message text
                string message = entry.Message ?? "";
                textBlock.Inlines.Add(new System.Windows.Documents.Run(message));
            }

            var toolTip = new ToolTip
            {
                Content = textBlock,
                Padding = new Thickness(8, 6, 8, 6),
            };
            toolTip.SetResourceReference(ToolTip.BackgroundProperty, EnvironmentColors.ToolTipBrushKey);
            toolTip.SetResourceReference(ToolTip.BorderBrushProperty, EnvironmentColors.ToolTipBorderBrushKey);

            return toolTip;
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

            if (_options.MessagePlacement != MessagePosition.Inline)
            {
                foreach (TextBlock adornment in _inlineMessageAdornments)
                {
                    adornment.Visibility = Visibility.Visible;
                }

                return;
            }

            try
            {
                UpdateAdornmentVisibility();
            }
            catch (Exception ex)
            {
                // Guard against unexpected visual tree states
                ex.Log();
            }
        }

        /// <summary>
        /// For each inline message TextBlock, checks all sibling adornment layer canvases
        /// for overlapping child elements. Hides the TextBlock if overlap is detected;
        /// restores it when the overlap clears.
        /// 
        /// Optimized to cache visual tree references and exit early when possible.
        /// </summary>
        private void UpdateAdornmentVisibility()
        {
            if (_inlineMessageAdornments.Count == 0)
            {
                return;
            }

            // Try to use cached references first
            DependencyObject layerCanvas = null;
            DependencyObject layerPanel = null;

            if (_cachedLayerCanvas != null && _cachedLayerCanvas.TryGetTarget(out layerCanvas) &&
                _cachedLayerPanel != null && _cachedLayerPanel.TryGetTarget(out layerPanel))
            {
                // Verify the cached references are still valid by checking
                // that our adornment is still a child of the cached canvas
                TextBlock firstAdornment = _inlineMessageAdornments[0];
                DependencyObject currentParent = VisualTreeHelper.GetParent(firstAdornment);
                if (!ReferenceEquals(currentParent, layerCanvas))
                {
                    layerCanvas = null;
                    layerPanel = null;
                }
            }

            // Walk up the visual tree if we don't have valid cached references
            if (layerCanvas == null)
            {
                TextBlock firstAdornment = _inlineMessageAdornments[0];
                layerCanvas = VisualTreeHelper.GetParent(firstAdornment);
                if (layerCanvas == null)
                {
                    return;
                }

                layerPanel = VisualTreeHelper.GetParent(layerCanvas);
                if (layerPanel == null)
                {
                    return;
                }

                // Cache for next time
                _cachedLayerCanvas = new WeakReference<DependencyObject>(layerCanvas);
                _cachedLayerPanel = new WeakReference<DependencyObject>(layerPanel);
            }

            // Collect sibling canvases that have visible children (skip empty ones early)
            int siblingCount = VisualTreeHelper.GetChildrenCount(layerPanel);
            var siblingCanvases = new List<Canvas>(siblingCount);
            int totalSiblingChildren = 0;

            for (int i = 0; i < siblingCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(layerPanel, i);
                if (child is Canvas canvas && !ReferenceEquals(canvas, layerCanvas))
                {
                    int canvasChildCount = VisualTreeHelper.GetChildrenCount(canvas);
                    if (canvasChildCount > 0)
                    {
                        siblingCanvases.Add(canvas);
                        totalSiblingChildren += canvasChildCount;
                    }
                }
            }

            // Early exit: if no siblings have children, all adornments are visible
            if (siblingCanvases.Count == 0 || totalSiblingChildren == 0)
            {
                foreach (TextBlock adornment in _inlineMessageAdornments)
                {
                    if (adornment.Visibility != Visibility.Visible)
                    {
                        adornment.Visibility = Visibility.Visible;
                    }
                }
                return;
            }

            // Check each adornment for overlap
            foreach (TextBlock adornment in _inlineMessageAdornments)
            {
                double adornmentLeft = Canvas.GetLeft(adornment);
                double adornmentTop = Canvas.GetTop(adornment);
                double adornmentHeight = adornment.ActualHeight > 0 ? adornment.ActualHeight : adornment.DesiredSize.Height;

                if (adornmentHeight <= 0)
                {
                    continue;
                }

                double adornmentBottom = adornmentTop + adornmentHeight;
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

                        // Check vertical overlap (same line) first - cheaper check
                        double sibBottom = sibTop + sibHeight;
                        bool verticalOverlap = adornmentTop < sibBottom && adornmentBottom > sibTop;
                        if (!verticalOverlap)
                        {
                            continue;
                        }

                        // Check horizontal overlap only if vertical overlap exists
                        bool horizontalOverlap;
                        if (_options.MessagePlacement == MessagePosition.Inline)
                        {
                            // Inline: sibling starts at or after line text end and overlaps our message.
                            horizontalOverlap = sibLeft >= adornmentLeft - 20;
                        }
                        else
                        {
                            double adornmentWidth = adornment.ActualWidth > 0 ? adornment.ActualWidth : adornment.DesiredSize.Width;
                            double sibWidth = sibFE != null ? (sibFE.ActualWidth > 0 ? sibFE.ActualWidth : sibFE.DesiredSize.Width) : 0;
                            if (adornmentWidth <= 0 || sibWidth <= 0)
                            {
                                continue;
                            }

                            // Above/Below: hide on actual bounding-box intersection.
                            horizontalOverlap = adornmentLeft < sibLeft + sibWidth && adornmentLeft + adornmentWidth > sibLeft;
                        }

                        if (horizontalOverlap)
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

                Visibility newVisibility = hasOverlap ? Visibility.Collapsed : Visibility.Visible;
                if (adornment.Visibility != newVisibility)
                {
                    adornment.Visibility = newVisibility;
                }
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
                _formatMap.FormatMappingChanged -= OnFormatMappingChanged;
                General.Saved -= OnOptionsSaved;

                if (_textDocument != null)
                {
                    _textDocument.FileActionOccurred -= OnFileActionOccurred;
                }
            }
        }
    }
}
