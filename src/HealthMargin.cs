using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;

namespace DocumentHealth
{
    internal class HealthMargin : DockPanel, IWpfTextViewMargin
    {
        private static readonly RatingPrompt _rating = new("MadsKristensen.DocumentHealth", Vsix.Name, General.Instance, 3);
        private readonly IWpfTextView _view;
        private readonly ITagAggregator<IErrorTag> _aggregator;
        private bool _isDisposed;
        private int _currentErrors = -1;
        private int _currentWarnings = -1;
        private int _currentMessages = -1;
        private ImageMoniker _moniker;
        private bool _updateQueued;
        private readonly CrispImage _image = new()
        {
            Width = 12,
            Height = 12,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        public HealthMargin(IWpfTextView textView, ITagAggregator<IErrorTag> aggregator)
        {
            _view = textView;
            _aggregator = aggregator;
            _aggregator.BatchedTagsChanged += OnBatchedTagsChanged;

            MouseUp += OnMouseUp;
            SetResourceReference(BackgroundProperty, EnvironmentColors.ScrollBarBackgroundBrushKey);
            Height = 16;
            ToolTip = ""; // instantiate the tooltip
            Children.Add(_image);
        }

        private void OnBatchedTagsChanged(object sender, BatchedTagsChangedEventArgs e)
        {
            if (!_updateQueued)
            {
                _updateQueued = true;
                _ = ThreadHelper.JoinableTaskFactory.StartOnIdle(UpdateAsync, VsTaskRunContext.UIThreadIdlePriority);
            }
        }

        private void OnMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            VS.Commands.ExecuteAsync("View.NextError").FireAndForget();
        }

        public async Task UpdateAsync()
        {
            // Ensure to execute on the UI thread
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _updateQueued = false;

            GetErrorsAndWarnings(out var errors, out var warnings, out var messages);

            // If the values haven't changed, don't update the UI
            if (_currentErrors == errors && _currentWarnings == warnings && _currentMessages == messages)
            {
                return;
            }

            _currentErrors = errors;
            _currentWarnings = warnings;
            _currentMessages = messages;

            ImageMoniker moniker = GetMoniker();

            if (moniker.Id != _moniker.Id)
            {
                _image.Moniker = _moniker = moniker;
            }
        }

        private ImageMoniker GetMoniker()
        {
            if (_currentErrors > 0)
            {
                return KnownMonikers.StatusError;
            }
            else if (_currentWarnings > 0)
            {
                return KnownMonikers.StatusWarning;
            }

            return KnownMonikers.StatusOK;
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

            _ = ThreadHelper.JoinableTaskFactory.StartOnIdle(async () =>
            {
                await Task.Delay(5000);
                _rating.RegisterSuccessfulUsage();
            }, VsTaskRunContext.UIThreadIdlePriority);
        }

        private void GetErrorsAndWarnings(out int errors, out int warnings, out int messages)
        {
            errors = warnings = messages = 0;

            foreach (IMappingTagSpan<IErrorTag> tag in _aggregator.GetTags(new SnapshotSpan(_view.TextSnapshot, 0, _view.TextSnapshot.Length)))
            {
                switch (tag.Tag.ErrorType)
                {
                    case PredefinedErrorTypeNames.CompilerError:
                    case PredefinedErrorTypeNames.OtherError:
                    case PredefinedErrorTypeNames.SyntaxError:
                        errors++;
                        break;
                    case PredefinedErrorTypeNames.Warning:
                        warnings++;
                        break;
                    case PredefinedErrorTypeNames.Suggestion:
                    case "information":
                        messages++;
                        break;
                }
            }
        }

        public FrameworkElement VisualElement => this;

        public double MarginSize => ActualHeight;

        public bool Enabled => true;

        public ITextViewMargin GetTextViewMargin(string marginName)
        {
            return (marginName == nameof(HealthMargin)) ? this : null;
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                GC.SuppressFinalize(this);

                _aggregator.BatchedTagsChanged -= OnBatchedTagsChanged;
                _aggregator.Dispose();
                MouseUp -= OnMouseUp;
            }
        }
    }
}