using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
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
        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
        {
            // Disable File Health Indicator from showing up in the bottom left editor margin
            wpfTextViewHost.TextView.Options.SetOptionValue(DefaultTextViewHostOptions.EnableFileHealthIndicatorOptionId, false);

            IErrorList errorList = VS.GetRequiredService<SVsErrorList, IErrorList>();
            return new HealthMargin(wpfTextViewHost.TextView, errorList);
        }
    }
}
