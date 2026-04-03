using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
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
        internal IViewTagAggregatorFactoryService ViewTagAggregatorFactoryService = null;

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

            ITagAggregator<IErrorTag> aggregator = ViewTagAggregatorFactoryService.CreateTagAggregator<IErrorTag>(
                textView,
                (TagAggregatorOptions)TagAggregatorOptions2.DeferTaggerCreation);

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

            textView.Properties.GetOrCreateSingletonProperty(
                () => new InlineDiagnosticsAdornment(textView, aggregator, JoinableTaskContext.Factory, options, errorTableManager, errorList));
        }
    }
}
