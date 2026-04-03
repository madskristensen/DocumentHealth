using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace DocumentHealth
{
    /// <summary>
    /// Defines editor format entries for diagnostic colors that appear under
    /// Tools > Options > Environment > Fonts and Colors (Text Editor category).
    /// Each entry exposes foreground (inline text), background (line highlight),
    /// and Bold / Italic in the Fonts and Colors dialog.
    /// </summary>
    internal static class DiagnosticFormatDefinitions
    {
        internal const string ErrorFormat = "Document Health - Error";
        internal const string WarningFormat = "Document Health - Warning";
        internal const string MessageFormat = "Document Health - Message";

        [Export(typeof(EditorFormatDefinition))]
        [Name(ErrorFormat)]
        [UserVisible(true)]
        [Order(Before = Priority.Default)]
        internal sealed class Error : ClassificationFormatDefinition
        {
            public Error()
            {
                DisplayName = ErrorFormat;
                ForegroundColor = Color.FromRgb(0xE4, 0x54, 0x54);
                BackgroundColor = Color.FromRgb(0xE4, 0x54, 0x54);
                IsItalic = true;
            }
        }

        [Export(typeof(EditorFormatDefinition))]
        [Name(WarningFormat)]
        [UserVisible(true)]
        [Order(Before = Priority.Default)]
        internal sealed class Warning : ClassificationFormatDefinition
        {
            public Warning()
            {
                DisplayName = WarningFormat;
                ForegroundColor = Color.FromRgb(0xFF, 0x94, 0x2F);
                BackgroundColor = Color.FromRgb(0xFF, 0x94, 0x2F);
                IsItalic = true;
            }
        }

        [Export(typeof(EditorFormatDefinition))]
        [Name(MessageFormat)]
        [UserVisible(true)]
        [Order(Before = Priority.Default)]
        internal sealed class Message : ClassificationFormatDefinition
        {
            public Message()
            {
                DisplayName = MessageFormat;
                ForegroundColor = Color.FromRgb(0x00, 0xB7, 0xE4);
                BackgroundColor = Color.FromRgb(0x00, 0xB7, 0xE4);
                IsItalic = true;
            }
        }
    }
}
