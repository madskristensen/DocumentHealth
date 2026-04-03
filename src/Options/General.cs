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
        [DisplayName("Update mode")]
        [Description("Controls when inline diagnostics are refreshed. 'Continuous' updates as you type (default). 'OnSave' only updates after the file is saved.")]
        [DefaultValue(UpdateMode.OnSave)]
        public UpdateMode UpdateMode { get; set; } = UpdateMode.OnSave;

        [Category("Inline Diagnostics")]
        [DisplayName("Show inline messages")]
        [Description("Display diagnostic messages inline at the end of lines containing errors or warnings.")]
        [DefaultValue(true)]
        public bool ShowInlineMessages { get; set; } = true;

        [Category("Inline Diagnostics")]
        [DisplayName("Show gutter icons")]
        [Description("Controls which severity levels get gutter icons in the editor for lines containing diagnostics.")]
        [DefaultValue(SeverityFilter.ErrorsAndWarnings)]
        public SeverityFilter ShowGutterIcons { get; set; } = SeverityFilter.ErrorsAndWarnings;

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
        [DefaultValue(SeverityFilter.ErrorsAndWarnings)]
        public SeverityFilter HighlightLines { get; set; } = SeverityFilter.ErrorsAndWarnings;

        [Category("Inline Diagnostics")]
        [DisplayName("Message placement")]
        [Description("Controls where inline diagnostic messages are rendered relative to the affected line. 'Inline' places them at the end of the line (default). 'Above' renders them on a separate line above the code. 'Below' renders them on a separate line below the code.")]
        [DefaultValue(MessagePosition.Inline)]
        public MessagePosition MessagePlacement { get; set; } = MessagePosition.Inline;

        [Category("Inline Diagnostics")]
        [DisplayName("Message template")]
        [Description("Customize the format of inline diagnostic messages. Supported placeholders: {message} (diagnostic text), {code} (diagnostic ID), {severity} (Error/Warning/Info), {source} (analyzer name).")]
        [DefaultValue("{message}")]
        public string MessageTemplate { get; set; } = "{message}";

        [Browsable(false)]
        public int RatingRequests { get; set; }
    }

    public enum SeverityFilter
    {
        None,
        Errors,
        ErrorsAndWarnings,
        All,
    }

    public enum UpdateMode
    {
        Continuous,
        OnSave,
    }

    public enum MessagePosition
    {
        Inline,
        Above,
        Below,
    }
}
