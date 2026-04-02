using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace DocumentHealth
{
    [Export(typeof(IViewTaggerProvider))]
    [ContentType(StandardContentTypeNames.Text)]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    [TagType(typeof(DiagnosticGlyphTag))]
    internal sealed class DiagnosticGlyphTaggerProvider : IViewTaggerProvider
    {
        [Import]
        internal IViewTagAggregatorFactoryService ViewTagAggregatorFactoryService = null;

        [Import]
        internal JoinableTaskContext JoinableTaskContext = null;

        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            if (textView.TextBuffer != buffer)
            {
                return null;
            }

            General options = General.Instance;

            if (options.ShowGutterIcons == SeverityFilter.None)
            {
                return null;
            }

            ITagAggregator<IErrorTag> aggregator = ViewTagAggregatorFactoryService.CreateTagAggregator<IErrorTag>(
                textView,
                (TagAggregatorOptions)TagAggregatorOptions2.DeferTaggerCreation);

            return textView.Properties.GetOrCreateSingletonProperty(
                typeof(DiagnosticGlyphTagger),
                () => new DiagnosticGlyphTagger(textView, aggregator, JoinableTaskContext.Factory, options)) as ITagger<T>;
        }
    }

    internal sealed class DiagnosticGlyphTagger : ITagger<DiagnosticGlyphTag>, IDisposable
    {
        private readonly ITextView _view;
        private readonly ITagAggregator<IErrorTag> _aggregator;
        private readonly JoinableTaskFactory _joinableTaskFactory;
        private readonly General _options;
        private readonly object _updateGate = new object();

        private volatile bool _isDisposed;
        private CancellationTokenSource _debounceCts;
        private Dictionary<int, LineDiagnostic> _diagnosticsByLine = new Dictionary<int, LineDiagnostic>();

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public DiagnosticGlyphTagger(
            ITextView view,
            ITagAggregator<IErrorTag> aggregator,
            JoinableTaskFactory joinableTaskFactory,
            General options)
        {
            _view = view;
            _aggregator = aggregator;
            _joinableTaskFactory = joinableTaskFactory;
            _options = options;

            _aggregator.BatchedTagsChanged += OnBatchedTagsChanged;
            _view.Closed += OnViewClosed;
            _view.TextBuffer.Changed += OnTextBufferChanged;

            ScheduleUpdate(immediate: true);
        }

        private void OnBatchedTagsChanged(object sender, BatchedTagsChangedEventArgs e)
        {
            ScheduleUpdate();
        }

        private void OnTextBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (_isDisposed || _diagnosticsByLine.Count == 0)
            {
                return;
            }

            foreach (ITextChange change in e.Changes)
            {
                int newlinesBefore = CountNewlines(change.OldText);
                int newlinesAfter = CountNewlines(change.NewText);

                if (newlinesBefore != newlinesAfter)
                {
                    // Line count changed; clear stale diagnostics and fire TagsChanged
                    // so glyphs disappear immediately instead of lingering at old positions
                    _diagnosticsByLine = new Dictionary<int, LineDiagnostic>();

                    ITextSnapshot snapshot = _view.TextSnapshot;
                    TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
                    return;
                }
            }
        }

        private static int CountNewlines(string text)
        {
            int count = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\r')
                {
                    count++;

                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }
                }
                else if (text[i] == '\n')
                {
                    count++;
                }
            }

            return count;
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

            int delay = immediate ? 0 : Math.Max(0, _options.UpdateDelayMilliseconds);
            UpdateWithDebounceAsync(nextCts.Token, delay).FireAndForget();
        }

        private async System.Threading.Tasks.Task UpdateWithDebounceAsync(CancellationToken token, int delay)
        {
            try
            {
                if (delay > 0)
                {
                    await System.Threading.Tasks.Task.Delay(delay, token).ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();

                Dictionary<int, LineDiagnostic> diagnostics = await System.Threading.Tasks.Task.Run(
                    () => CollectDiagnostics(token), token).ConfigureAwait(false);

                await _joinableTaskFactory.SwitchToMainThreadAsync(token);

                if (_isDisposed || token.IsCancellationRequested)
                {
                    return;
                }

                _diagnosticsByLine = diagnostics;

                ITextSnapshot snapshot = _view.TextSnapshot;
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private Dictionary<int, LineDiagnostic> CollectDiagnostics(CancellationToken token)
        {
            var result = new Dictionary<int, LineDiagnostic>();

            try
            {
                ITextSnapshot snapshot = _view.TextSnapshot;
                SnapshotSpan fullSpan = new SnapshotSpan(snapshot, 0, snapshot.Length);

                foreach (IMappingTagSpan<IErrorTag> tagSpan in _aggregator.GetTags(fullSpan))
                {
                    token.ThrowIfCancellationRequested();

                    NormalizedSnapshotSpanCollection spans = tagSpan.Span.GetSpans(snapshot);
                    if (spans.Count == 0)
                    {
                        continue;
                    }

                    SnapshotSpan span = spans[0];
                    int lineNumber = snapshot.GetLineNumberFromPosition(span.Start.Position);
                    DiagnosticSeverity severity = GetSeverity(tagSpan.Tag.ErrorType);

                    if (!IsSeverityEnabled(severity))
                    {
                        continue;
                    }

                    string message = ExtractTooltipText(tagSpan.Tag.ToolTipContent);

                    if (string.IsNullOrWhiteSpace(message))
                    {
                        continue;
                    }

                    string diagnosticCode = ExtractDiagnosticCode(message);
                    if (!string.IsNullOrEmpty(diagnosticCode))
                    {
                        message = StripCodePrefix(message, diagnosticCode);
                    }

                    if (result.TryGetValue(lineNumber, out LineDiagnostic existing))
                    {
                        if (severity > existing.Severity)
                        {
                            existing.Severity = severity;
                            existing.PrimaryMessage = message;
                            existing.DiagnosticCode = diagnosticCode;
                        }

                        existing.Count++;
                    }
                    else
                    {
                        result[lineNumber] = new LineDiagnostic
                        {
                            Severity = severity,
                            PrimaryMessage = message,
                            DiagnosticCode = diagnosticCode,
                            Count = 1,
                        };
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }

            return result;
        }

        public IEnumerable<ITagSpan<DiagnosticGlyphTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (_diagnosticsByLine.Count == 0 || spans.Count == 0)
            {
                yield break;
            }

            ITextSnapshot snapshot = spans[0].Snapshot;

            foreach (SnapshotSpan span in spans)
            {
                int startLine = snapshot.GetLineNumberFromPosition(span.Start.Position);
                int endLine = snapshot.GetLineNumberFromPosition(span.End.Position);

                for (int line = startLine; line <= endLine; line++)
                {
                    if (_diagnosticsByLine.TryGetValue(line, out LineDiagnostic diagnostic) && ShouldShowGlyph(diagnostic.Severity))
                    {
                        ITextSnapshotLine snapshotLine = snapshot.GetLineFromLineNumber(line);
                        SnapshotSpan lineSpan = new SnapshotSpan(snapshotLine.Start, snapshotLine.Length > 0 ? 1 : 0);

                        yield return new TagSpan<DiagnosticGlyphTag>(lineSpan, new DiagnosticGlyphTag(diagnostic));
                    }
                }
            }
        }

        private bool ShouldShowGlyph(DiagnosticSeverity severity)
        {
            switch (_options.ShowGutterIcons)
            {
                case SeverityFilter.All:
                    return true;
                case SeverityFilter.ErrorsAndWarnings:
                    return severity >= DiagnosticSeverity.Warning;
                case SeverityFilter.Errors:
                    return severity >= DiagnosticSeverity.Error;
                default:
                    return false;
            }
        }

        private bool IsSeverityEnabled(DiagnosticSeverity severity)
        {
            switch (severity)
            {
                case DiagnosticSeverity.Error: return _options.ShowErrors;
                case DiagnosticSeverity.Warning: return _options.ShowWarnings;
                case DiagnosticSeverity.Message: return _options.ShowSuggestions;
                default: return true;
            }
        }

        private static DiagnosticSeverity GetSeverity(string errorType)
        {
            switch (errorType)
            {
                case PredefinedErrorTypeNames.CompilerError:
                case PredefinedErrorTypeNames.OtherError:
                case PredefinedErrorTypeNames.SyntaxError:
                    return DiagnosticSeverity.Error;
                case PredefinedErrorTypeNames.Warning:
                    return DiagnosticSeverity.Warning;
                default:
                    return DiagnosticSeverity.Message;
            }
        }

        private static string ExtractTooltipText(object content)
        {
            switch (content)
            {
                case string s:
                    return s;
                case Microsoft.VisualStudio.Text.Adornments.ClassifiedTextRun run:
                    return run.Text ?? "";
                case Microsoft.VisualStudio.Text.Adornments.ClassifiedTextElement textElement:
                    return string.Join("", textElement.Runs.Select(r => r.Text));
                case Microsoft.VisualStudio.Text.Adornments.ContainerElement container:
                    foreach (object element in container.Elements)
                    {
                        string text = ExtractTooltipText(element);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text;
                        }
                    }
                    return "";
                case null:
                    return "";
                default:
                    return content.ToString();
            }
        }

        private static string ExtractDiagnosticCode(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            var match = System.Text.RegularExpressions.Regex.Match(message, @"^([A-Z]{2,4}\d{4,5})\s*:");
            return match.Success ? match.Groups[1].Value : null;
        }

        private static string StripCodePrefix(string message, string code)
        {
            var match = System.Text.RegularExpressions.Regex.Match(message, @"^" + System.Text.RegularExpressions.Regex.Escape(code) + @"\s*:\s*");
            if (match.Success)
            {
                return message.Substring(match.Length);
            }

            return message;
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            Dispose();
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                _view.Closed -= OnViewClosed;
                _view.TextBuffer.Changed -= OnTextBufferChanged;
                _aggregator.BatchedTagsChanged -= OnBatchedTagsChanged;

                lock (_updateGate)
                {
                    _debounceCts?.Cancel();
                    _debounceCts?.Dispose();
                    _debounceCts = null;
                }

                _aggregator.Dispose();
            }
        }
    }
}
