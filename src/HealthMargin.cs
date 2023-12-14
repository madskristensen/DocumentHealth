using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;

namespace DocumentHealth
{
    internal class HealthMargin : DockPanel, IWpfTextViewMargin
    {
        private static readonly RatingPrompt _rating = new("MadsKristensen.DocumentHealth", Vsix.Name, General.Instance, 3);
        private readonly string _fileName;
        private readonly IWpfTableControl _table;
        private readonly IWpfTextView _view;
        private bool _isDisposed;
        private int _currentErrors = -1;
        private int _currentWarnings = -1;
        private int _currentMessages = -1;
        private int _currentTableVersion;
        private ImageMoniker _moniker;
        private readonly CrispImage _image = new()
        {
            Width = 12,
            Height = 12,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        public HealthMargin(IWpfTextView textView, IErrorList errorList)
        {
            _fileName = textView.TextBuffer.GetFileName();

            _table = errorList.TableControl;
            _table.EntriesChanged += OnEntriesChanged;

            _view = textView;
            _view.GotAggregateFocus += OnFocus;
            _view.LostAggregateFocus += OnFocusLost;

            MouseUp += OnMouseUp;
            SetResourceReference(BackgroundProperty, EnvironmentColors.ScrollBarBackgroundBrushKey);
            Height = 16;
            Children.Add(_image);

            UpdateAsync().FireAndForget();

            ToolTip = ""; // instantiate the tooltip
        }

        private void OnFocusLost(object sender, EventArgs e)
        {
            _table.EntriesChanged -= OnEntriesChanged;
        }

        private void OnFocus(object sender, EventArgs e)
        {
            _table.EntriesChanged += OnEntriesChanged;
        }

        private void OnMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            VS.Commands.ExecuteAsync("View.NextError").FireAndForget();
        }

        private void OnEntriesChanged(object sender, EntriesChangedEventArgs e)
        {
            if (e.VersionNumber > _currentTableVersion && _view.HasAggregateFocus)
            {
                _currentTableVersion = e.VersionNumber;
                UpdateAsync().FireAndForget();
            }
        }

        public async Task UpdateAsync()
        {
            // Move to the background thread if not already on it
            await TaskScheduler.Default;

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
                // Move back to the UI thread to interact with the UI
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
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

            foreach (ITableEntryHandle entry in _table.Entries)
            {
                if (!entry.TryGetValue(StandardTableKeyNames.DocumentName, out string fileName) || fileName != _fileName)
                {
                    continue;
                }

                if (!entry.TryGetValue(StandardTableKeyNames.ErrorSeverity, out __VSERRORCATEGORY severity))
                {
                    continue;
                }

                errors += severity == __VSERRORCATEGORY.EC_ERROR ? 1 : 0;
                warnings += severity == __VSERRORCATEGORY.EC_WARNING ? 1 : 0;
                messages += severity == __VSERRORCATEGORY.EC_MESSAGE ? 1 : 0;
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

                _table.EntriesChanged -= OnEntriesChanged;
                _view.GotAggregateFocus -= OnFocus;
                _view.LostAggregateFocus -= OnFocusLost;
                MouseUp -= OnMouseUp;
            }
        }
    }
}