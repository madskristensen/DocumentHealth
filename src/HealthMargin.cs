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
        private readonly object _updateGate = new();
        private volatile bool _isDisposed;
        private CancellationTokenSource _debounceCts;

        public HealthMargin(IWpfTextView textView, ITagAggregator<IErrorTag> aggregator, JoinableTaskFactory joinableTaskFactory, General options)
        {
            _view = textView;
            _aggregator = aggregator;
            _joinableTaskFactory = joinableTaskFactory;
            _options = options;
            _aggregator.BatchedTagsChanged += OnBatchedTagsChanged;
            ScheduleUpdate(immediate: true);
        }

        private void OnBatchedTagsChanged(object sender, BatchedTagsChangedEventArgs e)
        {
            ScheduleUpdate();
        }

        private void ScheduleUpdate(bool immediate = false)
        {
            CancellationTokenSource nextCts;

            lock (_updateGate)
            {
                if (_isDisposed)
                {
                    return;
                }

                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
                _debounceCts = new CancellationTokenSource();
                nextCts = _debounceCts;
            }

            var delay = immediate ? 0 : Math.Max(0, _options.UpdateDelayMilliseconds);
            UpdateWithDebounceAsync(nextCts.Token, delay).FireAndForget();
        }

        private async Task UpdateWithDebounceAsync(CancellationToken token, int delay)
        {
            try
            {
                if (delay > 0)
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();

                (int errors, int warnings, int messages) = await Task.Run(() => GetErrorsAndWarnings(token), token).ConfigureAwait(false);

                await _joinableTaskFactory.SwitchToMainThreadAsync(token);

                if (_isDisposed || token.IsCancellationRequested)
                {
                    return;
                }

                _status.Update(errors, warnings, _options.ShowMessages ? messages : 0);
            }
            catch (OperationCanceledException)
            {
                // Debounce cancelled, new update is coming
            }
            catch (ObjectDisposedException)
            {
                // Margin or aggregator disposed while update was in flight
            }
        }

        private (int Errors, int Warnings, int Messages) GetErrorsAndWarnings(CancellationToken token)
        {
            int errors = 0;
            int warnings = 0;
            int messages = 0;

            try
            {
                ITextSnapshot snapshot = _view.TextSnapshot;
                foreach (IMappingTagSpan<IErrorTag> tag in _aggregator.GetTags(new SnapshotSpan(snapshot, 0, snapshot.Length)))
                {
                    token.ThrowIfCancellationRequested();

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
                        case PredefinedErrorTypeNames.HintedSuggestion:
                        case "information":
                            messages++;
                            break;
                        default:
                            string errorType = tag.Tag.ErrorType ?? "";

                            if (errorType.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                errors++;
                            }
                            else if (errorType.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                warnings++;
                            }
                            else
                            {
                                messages++;
                            }

                            break;
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // View or aggregator may be disposed while tags are being enumerated
            }

            catch (InvalidOperationException)
            {
                // Snapshot may have changed during enumeration; counts will update on next change
            }

            return (errors, warnings, messages);
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
                _aggregator.BatchedTagsChanged -= OnBatchedTagsChanged;

                lock (_updateGate)
                {
                    _isDisposed = true;
                    _debounceCts?.Cancel();
                    _debounceCts?.Dispose();
                    _debounceCts = null;
                }

                _aggregator.Dispose();
                GC.SuppressFinalize(this);
            }
        }
    }
}