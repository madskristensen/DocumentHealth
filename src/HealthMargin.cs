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
        private readonly General _options;
        private bool _isDisposed;
        private int _updateQueued;
        private CancellationTokenSource _debounceCts;

        public HealthMargin(IWpfTextView textView, ITagAggregator<IErrorTag> aggregator, JoinableTaskFactory joinableTaskFactory, General options)
        {
            _view = textView;
            _aggregator = aggregator;
            _joinableTaskFactory = joinableTaskFactory;
            _options = options;
            _aggregator.BatchedTagsChanged += OnBatchedTagsChanged;
        }

        private void OnBatchedTagsChanged(object sender, BatchedTagsChangedEventArgs e)
        {
            if (Interlocked.CompareExchange(ref _updateQueued, 1, 0) == 0)
            {
                UpdateWithDebounceAsync().FireAndForget();
            }
        }

        private async Task UpdateWithDebounceAsync()
        {
            try
            {
                // Cancel any previous pending update
                _debounceCts?.Cancel();
                _debounceCts = new CancellationTokenSource();
                CancellationToken token = _debounceCts.Token;

                var delay = Math.Max(0, _options.UpdateDelayMilliseconds);
                if (delay > 0)
                {
                    await Task.Delay(delay, token);
                }

                await _joinableTaskFactory.SwitchToMainThreadAsync(token);

                if (_isDisposed || token.IsCancellationRequested)
                {
                    return;
                }

                GetErrorsAndWarnings(out var errors, out var warnings, out var messages);
                _status.Update(errors, warnings, _options.ShowMessages ? messages : 0);
            }
            catch (OperationCanceledException)
            {
                // Debounce cancelled, new update is coming
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

                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
                _aggregator.BatchedTagsChanged -= OnBatchedTagsChanged;
                _aggregator.Dispose();
            }
        }
    }
}