using System.ComponentModel.Composition;
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
        [Order(Before = PredefinedAdornmentLayers.Selection)]
        private AdornmentLayerDefinition _editorAdornmentLayer;

        [Import]
        internal IViewTagAggregatorFactoryService ViewTagAggregatorFactoryService = null;

        [Import]
        internal JoinableTaskContext JoinableTaskContext = null;

        public void TextViewCreated(IWpfTextView textView)
        {
            General options = General.Instance;

            if (!options.ShowInlineMessages && options.HighlightLines == HighlightSeverity.None)
            {
                return;
            }

            ITagAggregator<IErrorTag> aggregator = ViewTagAggregatorFactoryService.CreateTagAggregator<IErrorTag>(
                textView,
                (TagAggregatorOptions)TagAggregatorOptions2.DeferTaggerCreation);

            textView.Properties.GetOrCreateSingletonProperty(
                () => new InlineDiagnosticsAdornment(textView, aggregator, JoinableTaskContext.Factory, options));
        }
    }
}
