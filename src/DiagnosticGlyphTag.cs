using Microsoft.VisualStudio.Text.Editor;

namespace DocumentHealth
{
    /// <summary>
    /// A glyph tag that carries diagnostic information for rendering severity icons in the editor gutter.
    /// </summary>
    internal sealed class DiagnosticGlyphTag : IGlyphTag
    {
        public DiagnosticGlyphTag(LineDiagnostic diagnostic)
        {
            Diagnostic = diagnostic;
        }

        public LineDiagnostic Diagnostic { get; }
    }
}
