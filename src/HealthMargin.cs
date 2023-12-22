using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;

namespace DocumentHealth
{
    internal class HealthMargin : IWpfTextViewMargin
    {
        private readonly HealthStatusControl _status = new();
        private readonly IWpfTextView _view;
        private readonly ITagAggregator<IErrorTag> _aggregator;
        private bool _isDisposed, _updateQueued;

        public HealthMargin(IWpfTextView textView, ITagAggregator<IErrorTag> aggregator)
        {
            _view = textView;
            _aggregator = aggregator;
            _aggregator.BatchedTagsChanged += OnBatchedTagsChanged;
        }

        private void OnBatchedTagsChanged(object sender, BatchedTagsChangedEventArgs e)
        {
            if (!_updateQueued)
            {
                _updateQueued = true;
                UpdateAsync().FireAndForget();
            }
        }

        public async Task UpdateAsync()
        {
            // Debounce the update
            await TaskScheduler.Default;
            await Task.Delay(100);

            // Schedule the update on the UI thread when it's idle
            await ThreadHelper.JoinableTaskFactory.StartOnIdle(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                GetErrorsAndWarnings(out var errors, out var warnings, out var messages);
                _status.Update(errors, warnings, messages);
                _updateQueued = false;
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

        public FrameworkElement VisualElement => _status;

        public double MarginSize => _status.ActualHeight;

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
            }
        }
    }
}