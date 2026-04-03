using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
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
        internal JoinableTaskContext JoinableTaskContext = null;

        [Import]
        internal ITableManagerProvider TableManagerProvider = null;

        [Import]
        internal SVsServiceProvider ServiceProvider = null;

        [Import]
        internal IViewTagAggregatorFactoryService ViewTagAggregatorFactoryService = null;

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

            DiagnosticDataProvider dataProvider = DiagnosticDataProvider.GetOrCreate(
                textView, JoinableTaskContext.Factory, options, TableManagerProvider, ServiceProvider, ViewTagAggregatorFactoryService);

            return textView.Properties.GetOrCreateSingletonProperty(() => new DiagnosticGlyphTagger(textView, options, dataProvider)) as ITagger<T>;
        }
    }

    internal sealed class DiagnosticGlyphTagger : ITagger<DiagnosticGlyphTag>, IDisposable
    {
        private readonly ITextView _view;
        private readonly General _options;
        private readonly DiagnosticDataProvider _dataProvider;

        private volatile bool _isDisposed;
        private int _lastDiagnosticCount;

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public DiagnosticGlyphTagger(
            ITextView view,
            General options,
            DiagnosticDataProvider dataProvider)
        {
            _view = view;
            _options = options;
            _dataProvider = dataProvider;

            _dataProvider.DiagnosticsUpdated += OnDiagnosticsUpdated;
            _view.Closed += OnViewClosed;

            // Fire an initial TagsChanged to ensure the tagger is registered with the glyph margin.
            // This must happen on the UI thread after the view is fully initialized.
            FireTagsChangedOnUIThreadAsync().FireAndForget();
        }

        private void OnDiagnosticsUpdated(object sender, EventArgs e)
        {
            if (_isDisposed || _view.IsClosed)
            {
                return;
            }

            int newCount = _dataProvider.DiagnosticsByLine.Count;

            // Only fire if there's actually a change
            if (newCount != _lastDiagnosticCount || newCount > 0)
            {
                _lastDiagnosticCount = newCount;
                FireTagsChangedOnUIThreadAsync().FireAndForget();
            }
        }

        private async Task FireTagsChangedOnUIThreadAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_isDisposed || _view.IsClosed)
            {
                return;
            }

            FireTagsChanged();
        }

        private void FireTagsChanged()
        {
            ITextSnapshot snapshot = _view.TextSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
        }

        public IEnumerable<ITagSpan<DiagnosticGlyphTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            IReadOnlyDictionary<int, LineDiagnostic> diagnosticsByLine = _dataProvider.DiagnosticsByLine;

            if (diagnosticsByLine.Count == 0 || spans.Count == 0)
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
                    if (diagnosticsByLine.TryGetValue(line, out LineDiagnostic diagnostic) && ShouldShowGlyph(diagnostic.Severity))
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
                _dataProvider.DiagnosticsUpdated -= OnDiagnosticsUpdated;
            }
        }
    }
}
