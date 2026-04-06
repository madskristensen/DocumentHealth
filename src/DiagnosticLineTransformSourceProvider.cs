using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using ITextDocument = Microsoft.VisualStudio.Text.ITextDocument;

namespace DocumentHealth
{
    /// <summary>
    /// MEF-exported provider that creates a <see cref="DiagnosticLineTransformSource"/>
    /// for each text view, enabling above-line or below-line diagnostic message rendering.
    /// </summary>
    [Export(typeof(ILineTransformSourceProvider))]
    [ContentType(StandardContentTypeNames.Text)]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    internal sealed class DiagnosticLineTransformSourceProvider : ILineTransformSourceProvider
    {
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

        public ILineTransformSource Create(IWpfTextView textView)
        {
            if (textView.Roles.Contains(DifferenceViewerRoles.DiffTextViewRole))
            {
                return null;
            }

            General options = General.Instance;

            if (!options.ShowInlineMessages)
            {
                return null;
            }

            if (TextDocumentFactoryService.TryGetTextDocument(textView.TextBuffer, out ITextDocument textDocument)
                && options.IsFileExtensionIgnored(textDocument.FilePath))
            {
                return null;
            }

            DiagnosticDataProvider dataProvider = DiagnosticDataProvider.GetOrCreate(
                textView, JoinableTaskContext.Factory, options, TableManagerProvider, ServiceProvider, ViewTagAggregatorFactoryService);

            return textView.Properties.GetOrCreateSingletonProperty(
                typeof(DiagnosticLineTransformSource),
                () => new DiagnosticLineTransformSource(textView, options, dataProvider));
        }
    }
}
