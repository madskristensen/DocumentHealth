using System.ComponentModel;

namespace DocumentHealth
{
    public class General : BaseOptionModel<General>, IRatingConfig
    {
        [Category("Behavior")]
        [DisplayName("Update delay (ms)")]
        [Description("Delay in milliseconds before updating the health indicator after changes. Higher values improve performance during rapid typing.")]
        [DefaultValue(250)]
        public int UpdateDelayMilliseconds { get; set; } = 250;

        [Category("Behavior")]
        [DisplayName("Show messages count")]
        [Description("Include suggestions and informational messages in the tooltip count.")]
        [DefaultValue(true)]
        public bool ShowMessages { get; set; } = true;

        [Category("Behavior")]
        [DisplayName("Replace built-in indicator")]
        [Description("Disable Visual Studio's built-in file health indicator and use this extension's indicator instead.")]
        [DefaultValue(true)]
        public bool ReplaceBuiltInIndicator { get; set; } = true;

        [Category("Inline Diagnostics")]
        [DisplayName("Show inline messages")]
        [Description("Display diagnostic messages inline at the end of lines containing errors or warnings.")]
        [DefaultValue(true)]
        public bool ShowInlineMessages { get; set; } = true;

        [Category("Inline Diagnostics")]
        [DisplayName("Highlight lines")]
        [Description("Highlight the background of lines containing errors or warnings with a severity-colored tint.")]
        [DefaultValue(true)]
        public bool HighlightLines { get; set; } = true;

        [Category("Inline Diagnostics")]
        [DisplayName("Highlight messages")]
        [Description("Also highlight lines that only contain low-severity informational messages. When disabled, only errors and warnings are highlighted.")]
        [DefaultValue(false)]
        public bool HighlightMessages { get; set; } = false;

        [Browsable(false)]
        public int RatingRequests { get; set; }
    }
}
