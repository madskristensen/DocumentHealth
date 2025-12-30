using System.Threading;
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
        private readonly JoinableTaskFactory _joinableTaskFactory;
        private bool _isDisposed;
        private int _updateQueued;

        public HealthMargin(IWpfTextView textView, ITagAggregator<IErrorTag> aggregator, JoinableTaskFactory joinableTaskFactory)
        {
            _view = textView;
            _aggregator = aggregator;
            _joinableTaskFactory = joinableTaskFactory;
            _aggregator.BatchedTagsChanged += OnBatchedTagsChanged;
        }

        private void OnBatchedTagsChanged(object sender, BatchedTagsChangedEventArgs e)
        {
            if (Interlocked.CompareExchange(ref _updateQueued, 1, 0) == 0)
            {
                UpdateAsync().FireAndForget();
            }
        }


        public async Task UpdateAsync()
        {
            try
            {
                await Task.Yield();
                await _joinableTaskFactory.SwitchToMainThreadAsync();

                if (_isDisposed)
                {
                    return;
                }

                GetErrorsAndWarnings(out var errors, out var warnings, out var messages);
                _status.Update(errors, warnings, messages);
            }
            finally
            {
                Interlocked.Exchange(ref _updateQueued, 0);
            }
        }

        private void GetErrorsAndWarnings(out int errors, out int warnings, out int messages)
        {
            errors = warnings = messages = 0;

            try
            {
                ITextSnapshot snapshot = _view.TextSnapshot;
                foreach (IMappingTagSpan<IErrorTag> tag in _aggregator.GetTags(new SnapshotSpan(snapshot, 0, snapshot.Length)))
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
            catch (InvalidOperationException)
            {
                // Snapshot may have changed during enumeration; counts will update on next change
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