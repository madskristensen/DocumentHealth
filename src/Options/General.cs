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
        [DisplayName("Show gutter icons")]
        [Description("Display severity icons in the editor gutter for lines containing diagnostics.")]
        [DefaultValue(true)]
        public bool ShowGutterIcons { get; set; } = true;

        [Category("Inline Diagnostics")]
        [DisplayName("Show errors")]
        [Description("Include error diagnostics in the inline messages and line highlights.")]
        [DefaultValue(true)]
        public bool ShowErrors { get; set; } = true;

        [Category("Inline Diagnostics")]
        [DisplayName("Show warnings")]
        [Description("Include warning diagnostics in the inline messages and line highlights.")]
        [DefaultValue(true)]
        public bool ShowWarnings { get; set; } = true;

        [Category("Inline Diagnostics")]
        [DisplayName("Show suggestions")]
        [Description("Include informational and suggestion diagnostics in the inline messages and line highlights.")]
        [DefaultValue(false)]
        public bool ShowSuggestions { get; set; } = false;

        [Category("Inline Diagnostics")]
        [DisplayName("Highlight lines")]
        [Description("Controls which severity levels get line background highlighting.")]
        [DefaultValue(HighlightSeverity.ErrorsAndWarnings)]
        public HighlightSeverity HighlightLines { get; set; } = HighlightSeverity.ErrorsAndWarnings;

        [Category("Inline Diagnostics")]
        [DisplayName("Message template")]
        [Description("Customize the format of inline diagnostic messages. Supported placeholders: {message} (diagnostic text), {code} (diagnostic ID), {severity} (Error/Warning/Info), {source} (analyzer name).")]
        [DefaultValue("{message}")]
        public string MessageTemplate { get; set; } = "{message}";

        [Browsable(false)]
        public int RatingRequests { get; set; }
    }

    public enum HighlightSeverity
    {
        None,
        Errors,
        ErrorsAndWarnings,
        All,
    }
}
