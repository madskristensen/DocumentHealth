using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;

namespace DocumentHealth
{
    internal class HealthStatusControl : DockPanel
    {
        private int _currentErrors = -1;
        private int _currentWarnings = -1;
        private int _currentMessages = -1;
        private readonly CrispImage _image = new()
        {
            Width = 12,
            Height = 12,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Moniker = KnownMonikers.StatusNotStarted,
        };

        private const string NoIssuesText = "No errors or warnings";
        private const string ErrorsText = "{0} error(s)";
        private const string WarningsText = "{0} warning(s)";
        private const string MessagesText = "{0} message(s)";

        private ToolTip _tooltip;
        private Label _errorLabel;
        private Label _warningLabel;
        private Label _messageLabel;

        public HealthStatusControl()
        {
            SetResourceReference(BackgroundProperty, EnvironmentColors.ScrollBarBackgroundBrushKey);
            Height = 16;
            System.Windows.Automation.AutomationProperties.SetName(_image, NoIssuesText);
            InitializeToolTip();
            Children.Add(_image);
        }

        private void InitializeToolTip()
        {
            _tooltip = new ToolTip
            {
                Padding = new Thickness(0),
                Placement = System.Windows.Controls.Primitives.PlacementMode.Left,
            };
            _tooltip.SetResourceReference(BackgroundProperty, EnvironmentColors.ScrollBarBackgroundBrushKey);

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(5, 0, 0, 0),
            };

            panel.Children.Add(new CrispImage
            {
                Moniker = KnownMonikers.StatusError,
                Width = 14,
                Height = 14,
            });

            _errorLabel = new Label();
            _errorLabel.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.CommandBarTextHoverBrushKey);
            panel.Children.Add(_errorLabel);

            panel.Children.Add(new CrispImage
            {
                Moniker = KnownMonikers.StatusWarning,
                Width = 14,
                Height = 14,
                Margin = new Thickness(10, 0, 0, 0),
            });

            _warningLabel = new Label();
            _warningLabel.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.CommandBarTextHoverBrushKey);
            panel.Children.Add(_warningLabel);

            panel.Children.Add(new CrispImage
            {
                Moniker = KnownMonikers.StatusInformation,
                Width = 14,
                Height = 14,
                Margin = new Thickness(10, 0, 0, 0),
            });

            _messageLabel = new Label();
            _messageLabel.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.CommandBarTextHoverBrushKey);
            panel.Children.Add(_messageLabel);

            _tooltip.Content = panel;
            ToolTip = _tooltip;
        }

        public void Update(int errors, int warnings, int messages)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_currentErrors == errors && _currentWarnings == warnings && _currentMessages == messages)
            {
                return;
            }

            _image.Moniker = GetMoniker(errors, warnings);
            System.Windows.Automation.AutomationProperties.SetName(_image, GetAccessibleText(errors, warnings, messages));

            _currentErrors = errors;
            _currentWarnings = warnings;
            _currentMessages = messages;
        }

        private static string GetAccessibleText(int errors, int warnings, int messages)
        {
            if (errors == 0 && warnings == 0 && messages == 0)
            {
                return NoIssuesText;
            }

            var parts = new System.Collections.Generic.List<string>(3);
            if (errors > 0)
            {
                parts.Add(string.Format(ErrorsText, errors));
            }
            if (warnings > 0)
            {
                parts.Add(string.Format(WarningsText, warnings));
            }
            if (messages > 0)
            {
                parts.Add(string.Format(MessagesText, messages));
            }
            return string.Join(", ", parts);
        }

        private ImageMoniker GetMoniker(int errors, int warnings)
        {
            if (errors > 0)
            {
                return KnownMonikers.StatusError;
            }
            else if (warnings > 0)
            {
                return KnownMonikers.StatusWarning;
            }

            return KnownMonikers.StatusOK;
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            VS.Commands.ExecuteAsync("View.NextError").FireAndForget();
        }

        protected override void OnToolTipOpening(ToolTipEventArgs e)
        {
            _errorLabel.Content = _currentErrors;
            _warningLabel.Content = _currentWarnings;
            _messageLabel.Content = _currentMessages;
        }
    }
}
