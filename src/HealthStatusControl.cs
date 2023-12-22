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

        public HealthStatusControl()
        {
            SetResourceReference(BackgroundProperty, EnvironmentColors.ScrollBarBackgroundBrushKey);
            Height = 16;
            ToolTip = ""; // instantiate the tooltip
            Children.Add(_image);
        }

        public void Update(int errors, int warnings, int messages)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _image.Moniker = GetMoniker(errors, warnings);

            _currentErrors = errors;
            _currentWarnings = warnings;
            _currentMessages = messages;
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
            var tooltip = new ToolTip
            {
                Background = FindResource(EnvironmentColors.ScrollBarBackgroundBrushKey) as Brush,
                Padding = new Thickness(0),
                Placement = System.Windows.Controls.Primitives.PlacementMode.Left,
            };

            var lineOne = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(5, 0, 0, 0),
            };

            lineOne.Children.Add(new CrispImage
            {
                Moniker = KnownMonikers.StatusError,
                Width = 14,
                Height = 14,
            });

            lineOne.Children.Add(new Label
            {
                Content = _currentErrors,
                Foreground = FindResource(EnvironmentColors.CommandBarTextHoverBrushKey) as Brush
            });

            lineOne.Children.Add(new CrispImage
            {
                Moniker = KnownMonikers.StatusWarning,
                Width = 14,
                Height = 14,
                Margin = new Thickness(10, 0, 0, 0),
            });

            lineOne.Children.Add(new Label
            {
                Content = _currentWarnings,
                Foreground = FindResource(EnvironmentColors.CommandBarTextHoverBrushKey) as Brush
            });

            lineOne.Children.Add(new CrispImage
            {
                Moniker = KnownMonikers.StatusInformation,
                Width = 14,
                Height = 14,
                Margin = new Thickness(10, 0, 0, 0),
            });

            lineOne.Children.Add(new Label
            {
                Content = _currentMessages,
                Foreground = FindResource(EnvironmentColors.CommandBarTextHoverBrushKey) as Brush
            });

            tooltip.Content = lineOne;

            ToolTip = tooltip;
        }
    }
}
