using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace DocumentHealth
{
    [Export(typeof(IGlyphFactoryProvider))]
    [Name("DocumentHealthDiagnosticGlyph")]
    [Order(After = "VsTextMarker")]
    [ContentType(StandardContentTypeNames.Text)]
    [TagType(typeof(DiagnosticGlyphTag))]
    internal sealed class DiagnosticGlyphFactoryProvider : IGlyphFactoryProvider
    {
        public IGlyphFactory GetGlyphFactory(IWpfTextView view, IWpfTextViewMargin margin)
        {
            return new DiagnosticGlyphFactory();
        }
    }

    internal sealed class DiagnosticGlyphFactory : IGlyphFactory
    {
        public UIElement GenerateGlyph(IWpfTextViewLine line, IGlyphTag tag)
        {
            if (tag is not DiagnosticGlyphTag glyphTag)
            {
                return null;
            }

            ImageMoniker moniker = GetMoniker(glyphTag.Diagnostic.Severity);

            var image = new CrispImage
            {
                Moniker = moniker,
                Width = 14,
                Height = 14,
                Cursor = Cursors.Arrow,
                Tag = glyphTag.Diagnostic, // Store diagnostic for lazy menu creation
            };

            ContextMenu contextMenu = null;

            image.MouseRightButtonDown += (s, e) =>
            {
                e.Handled = true;
            };

            image.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;

                // Lazily create context menu on first use
                if (contextMenu == null)
                {
                    contextMenu = DiagnosticContextMenu.Create(glyphTag.Diagnostic);
                }

                contextMenu.PlacementTarget = image;
                contextMenu.IsOpen = true;
            };

            return image;
        }

        private static ImageMoniker GetMoniker(DiagnosticSeverity severity)
        {
            switch (severity)
            {
                case DiagnosticSeverity.Error:
                    return KnownMonikers.StatusError;
                case DiagnosticSeverity.Warning:
                    return KnownMonikers.StatusWarning;
                default:
                    return KnownMonikers.StatusInformation;
            }
        }
    }
}
