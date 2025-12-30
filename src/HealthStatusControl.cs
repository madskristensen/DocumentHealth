using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        private const string _noIssuesText = "No errors or warnings";
        private const string _errorsText = "{0} error(s)";
        private const string _warningsText = "{0} warning(s)";
        private const string _messagesText = "{0} message(s)";

        private ToolTip _tooltip;
        private Label _errorLabel;
        private Label _warningLabel;
        private Label _messageLabel;

        public HealthStatusControl()
        {
            SetResourceReference(BackgroundProperty, EnvironmentColors.ScrollBarBackgroundBrushKey);
            Height = 16;
            System.Windows.Automation.AutomationProperties.SetName(_image, _noIssuesText);
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
                return _noIssuesText;
            }

            var parts = new System.Collections.Generic.List<string>(3);
            if (errors > 0)
            {
                parts.Add(string.Format(_errorsText, errors));
            }
            if (warnings > 0)
            {
                parts.Add(string.Format(_warningsText, warnings));
            }
            if (messages > 0)
            {
                parts.Add(string.Format(_messagesText, messages));
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
            if (e.ChangedButton == MouseButton.Left)
            {
                VS.Commands.ExecuteAsync("View.NextError").FireAndForget();
            }
        }







        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            e.Handled = true;
            ShowContextMenu();
        }

        private void ShowContextMenu()
        {
            ContextMenu menu = new();

            menu.Items.Add(CreateMenuItem("Go to Next Error", "View.NextError"));
            menu.Items.Add(CreateMenuItem("Go to Previous Error", "View.PreviousError"));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Open Error List", "View.ErrorList"));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateSettingsMenuItem());

            menu.PlacementTarget = this;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Left;
            menu.IsOpen = true;
        }

        private static MenuItem CreateMenuItem(string header, string command)
        {
            MenuItem item = new() { Header = header };
            item.Click += (s, e) => VS.Commands.ExecuteAsync(command).FireAndForget();
            return item;
        }

        private static MenuItem CreateSettingsMenuItem()
        {
            MenuItem item = new() { Header = "Settings..." };
            item.Click += (s, e) => VS.Settings.OpenAsync<OptionsProvider.GeneralOptions>().FireAndForget();
            return item;
        }

        protected override void OnToolTipOpening(ToolTipEventArgs e)
        {
            _errorLabel.Content = _currentErrors;
            _warningLabel.Content = _currentWarnings;
            _messageLabel.Content = _currentMessages;
        }
    }
}
