using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace DocumentHealth
{
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(nameof(HealthMargin))]
    [MarginContainer(PredefinedMarginNames.RightControl)]
    [Order(After = "SplitterControl")]
    [ContentType(StandardContentTypeNames.Text)]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    internal class HealthMarginProvider : IWpfTextViewMarginProvider
    {
        private static readonly RatingPrompt _rating = new("MadsKristensen.DocumentHealth", Vsix.Name, General.Instance, 5);

        [Import]
        internal IViewTagAggregatorFactoryService ViewTagAggregatorFactoryService = null;

        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
        {
            // Disable File Health Indicator from showing up in the bottom left editor margin
            wpfTextViewHost.TextView.Options.SetOptionValue(DefaultTextViewHostOptions.EnableFileHealthIndicatorOptionId, false);

            // Register usage of the extension for showing a rating prompt
            _rating.RegisterSuccessfulUsage();

            ITagAggregator<IErrorTag> aggregator = ViewTagAggregatorFactoryService.CreateTagAggregator<IErrorTag>(wpfTextViewHost.TextView, (TagAggregatorOptions)TagAggregatorOptions2.DeferTaggerCreation);
            
            return new HealthMargin(wpfTextViewHost.TextView, aggregator);
        }
    }
}
