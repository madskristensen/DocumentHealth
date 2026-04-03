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

            // Get or create the shared DiagnosticDataProvider
            DiagnosticDataProvider dataProvider = GetOrCreateDataProvider(textView, options);

            textView.Properties.GetOrCreateSingletonProperty(
                typeof(InlineDiagnosticsAdornment),
                () => new InlineDiagnosticsAdornment(textView, options, dataProvider));
        }

        internal DiagnosticDataProvider GetOrCreateDataProvider(IWpfTextView textView, General options)
        {
            return textView.Properties.GetOrCreateSingletonProperty(
                typeof(DiagnosticDataProvider),
                () =>
                {
                    ITableManager errorTableManager = TableManagerProvider.GetTableManager(StandardTables.ErrorsTable);

                    // Get the IErrorList service for direct access to the table control
                    IErrorList errorList = null;
                    try
                    {
                        errorList = ServiceProvider.GetService(typeof(SVsErrorList)) as IErrorList;
                    }
                    catch
                    {
                        // Service may not be available
                    }

                    return new DiagnosticDataProvider(textView, JoinableTaskContext.Factory, options, errorTableManager, errorList);
                });
        }
    }
}
