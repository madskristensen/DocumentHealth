using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;

namespace DocumentHealth
{
    /// <summary>
    /// Expands the height of editor lines that have diagnostics to make room for
    /// above-line or below-line message rendering.
    /// </summary>
    internal sealed class DiagnosticLineTransformSource : ILineTransformSource
    {
        private readonly IWpfTextView _view;
        private readonly General _options;
        private readonly DiagnosticDataProvider _dataProvider;

        /// <summary>
        /// Extra vertical space (in pixels) added to lines with diagnostics.
        /// Computed from the editor's default font size.
        /// </summary>
        private double _extraLineHeight;

        public DiagnosticLineTransformSource(
            IWpfTextView view,
            General options,
            DiagnosticDataProvider dataProvider)
        {
            _view = view;
            _options = options;
            _dataProvider = dataProvider;

            UpdateExtraLineHeight();
        }

        /// <summary>
        /// Gets the extra vertical space added per diagnostic line.
        /// </summary>
        internal double ExtraLineHeight => _extraLineHeight;

        /// <summary>
        /// Recalculates the extra height based on the current editor font size.
        /// </summary>
        internal void UpdateExtraLineHeight()
        {
            double fontSize = 13.0; // fallback

            if (_view.FormattedLineSource != null)
            {
                fontSize = _view.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize;
            }

            // Use 90% of font size (matching inline message scaling) plus some padding
            _extraLineHeight = (fontSize * 0.9) + 4;
        }

        public LineTransform GetLineTransform(ITextViewLine line, double yPosition, ViewRelativePosition placement)
        {
            MessagePosition messagePosition = _options.MessagePlacement;

            if (messagePosition == MessagePosition.Inline)
            {
                return line.DefaultLineTransform;
            }

            if (!_options.ShowInlineMessages)
            {
                return line.DefaultLineTransform;
            }

            IReadOnlyDictionary<int, LineDiagnostic> diagnosticsByLine = _dataProvider.DiagnosticsByLine;

            if (diagnosticsByLine.Count == 0)
            {
                return line.DefaultLineTransform;
            }

            int lineNumber = _view.TextSnapshot.GetLineNumberFromPosition(line.Start.Position);

            if (!diagnosticsByLine.ContainsKey(lineNumber))
            {
                return line.DefaultLineTransform;
            }

            double topSpace = line.DefaultLineTransform.TopSpace;
            double bottomSpace = line.DefaultLineTransform.BottomSpace;

            if (messagePosition == MessagePosition.Above)
            {
                topSpace += _extraLineHeight;
            }
            else if (messagePosition == MessagePosition.Below)
            {
                bottomSpace += _extraLineHeight;
            }

            return new LineTransform(topSpace, bottomSpace, line.DefaultLineTransform.VerticalScale);
        }
    }
}
