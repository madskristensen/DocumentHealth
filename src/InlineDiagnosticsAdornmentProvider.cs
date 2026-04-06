using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using ITextDocument = Microsoft.VisualStudio.Text.ITextDocument;

namespace DocumentHealth
{
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType(StandardContentTypeNames.Text)]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    internal sealed class InlineDiagnosticsAdornmentProvider : IWpfTextViewCreationListener
    {
        internal const string AdornmentLayerName = "DocumentHealthInlineDiagnostics";

#pragma warning disable CS0169 // Field is never assigned to
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(AdornmentLayerName)]
        [Order(After = PredefinedAdornmentLayers.Text)]
        private AdornmentLayerDefinition _editorAdornmentLayer;
#pragma warning restore CS0169

        [Import]
        internal JoinableTaskContext JoinableTaskContext = null;

        [Import]
        internal ITableManagerProvider TableManagerProvider = null;

        [Import]
        internal SVsServiceProvider ServiceProvider = null;

        [Import]
        internal IViewTagAggregatorFactoryService ViewTagAggregatorFactoryService = null;

        [Import]
        internal ITextDocumentFactoryService TextDocumentFactoryService = null;

        [Import]
        internal IEditorFormatMapService EditorFormatMapService = null;

        public void TextViewCreated(IWpfTextView textView)
        {
            if (textView.Roles.Contains(DifferenceViewerRoles.DiffTextViewRole))
            {
                return;
            }

            General options = General.Instance;

            if (!options.ShowInlineMessages && options.HighlightLines == SeverityFilter.None)
            {
                return;
            }

            TextDocumentFactoryService.TryGetTextDocument(textView.TextBuffer, out ITextDocument textDocument);

            if (textDocument != null && options.IsFileExtensionIgnored(textDocument.FilePath))
            {
                return;
            }

            DiagnosticDataProvider dataProvider = DiagnosticDataProvider.GetOrCreate(
                textView, JoinableTaskContext.Factory, options, TableManagerProvider, ServiceProvider, ViewTagAggregatorFactoryService);

            IEditorFormatMap formatMap = EditorFormatMapService.GetEditorFormatMap(textView);

            textView.Properties.GetOrCreateSingletonProperty(
                typeof(InlineDiagnosticsAdornment),
                () => new InlineDiagnosticsAdornment(textView, options, dataProvider, textDocument, formatMap));
        }
    }
}
