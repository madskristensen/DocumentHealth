using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace DocumentHealth
{
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType(StandardContentTypeNames.Text)]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    internal sealed class InlineDiagnosticsAdornmentProvider : IWpfTextViewCreationListener
    {
        internal const string AdornmentLayerName = "DocumentHealthInlineDiagnostics";

        [Export(typeof(AdornmentLayerDefinition))]
        [Name(AdornmentLayerName)]
        [Order(After = PredefinedAdornmentLayers.Text)]
        private AdornmentLayerDefinition _editorAdornmentLayer;

        [Import]
        internal JoinableTaskContext JoinableTaskContext = null;

        [Import]
        internal ITableManagerProvider TableManagerProvider = null;

        [Import]
        internal SVsServiceProvider ServiceProvider = null;

        public void TextViewCreated(IWpfTextView textView)
        {
            General options = General.Instance;

            if (!options.ShowInlineMessages && options.HighlightLines == SeverityFilter.None)
            {
                return;
            }

            DiagnosticDataProvider dataProvider = DiagnosticDataProvider.GetOrCreate(
                textView, JoinableTaskContext.Factory, options, TableManagerProvider, ServiceProvider);

            textView.Properties.GetOrCreateSingletonProperty(
                typeof(InlineDiagnosticsAdornment),
                () => new InlineDiagnosticsAdornment(textView, options, dataProvider));
        }
    }
}
