using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using ITextDocument = Microsoft.VisualStudio.Text.ITextDocument;

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

        [Import]
        internal ITextDocumentFactoryService TextDocumentFactoryService = null;

        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            if (textView.TextBuffer != buffer)
            {
                return null;
            }

            if (textView.Roles.Contains(DifferenceViewerRoles.DiffTextViewRole))
            {
                return null;
            }

            General options = General.Instance;

            if (options.ShowGutterIcons == SeverityFilter.None)
            {
                return null;
            }

            // Skip if the glyph margin is disabled (e.g., by another extension).
            // No point creating a tagger when nothing will consume the tags.
            if (!textView.Options.GetOptionValue(DefaultTextViewHostOptions.GlyphMarginId))
            {
                return null;
            }

            if (TextDocumentFactoryService.TryGetTextDocument(textView.TextBuffer, out ITextDocument textDocument)
                && options.IsFileExtensionIgnored(textDocument.FilePath))
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
        private const int OnSaveContinuousGraceMilliseconds = 1500;
        private const int InitialLoadContinuousGraceMilliseconds = 10000;

        private readonly ITextView _view;
        private readonly General _options;
        private readonly DiagnosticDataProvider _dataProvider;
        private readonly ITextDocument _textDocument;

        private volatile bool _isDisposed;
        private volatile bool _pendingSaveRefresh = true;
        private DateTime _onSaveContinuousUntilUtc = DateTime.MinValue;
        private readonly HashSet<int> _visibleLineNumbersOnSave = new HashSet<int>();
        private readonly HashSet<int> _publishedLineNumbers = new HashSet<int>();

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public DiagnosticGlyphTagger(
            ITextView view,
            General options,
            DiagnosticDataProvider dataProvider)
        {
            _view = view;
            _options = options;
            _dataProvider = dataProvider;

            if (_view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument textDocument))
            {
                _textDocument = textDocument;
                _textDocument.FileActionOccurred += OnFileActionOccurred;
            }

            _dataProvider.DiagnosticsUpdated += OnDiagnosticsUpdated;
            _view.Closed += OnViewClosed;

            // During solution restore, diagnostics can arrive shortly after the first empty refresh.
            // Keep a short startup grace window so late Roslyn diagnostics can still be added.
            _onSaveContinuousUntilUtc = DateTime.UtcNow.AddMilliseconds(InitialLoadContinuousGraceMilliseconds);

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

            IReadOnlyDictionary<int, LineDiagnostic> diagnosticsByLine = _dataProvider.DiagnosticsByLine;

            if (_options.UpdateMode == UpdateMode.OnSave)
            {
                bool shouldRaise;

                if (_pendingSaveRefresh)
                {
                    _pendingSaveRefresh = false;
                    shouldRaise = ReplaceLineSet(_visibleLineNumbersOnSave, diagnosticsByLine);
                }
                else if (IsWithinOnSaveContinuousGracePeriod())
                {
                    shouldRaise = ReplaceLineSet(_visibleLineNumbersOnSave, diagnosticsByLine);
                }
                else
                {
                    shouldRaise = RemoveResolvedLineNumbers(_visibleLineNumbersOnSave, diagnosticsByLine);
                }

                if (shouldRaise)
                {
                    FireTagsChangedOnUIThreadAsync().FireAndForget();
                }

                return;
            }

            if (ReplaceLineSet(_publishedLineNumbers, diagnosticsByLine))
            {
                FireTagsChangedOnUIThreadAsync().FireAndForget();
            }
        }

        private void OnFileActionOccurred(object sender, TextDocumentFileActionEventArgs e)
        {
            if (_isDisposed || _view.IsClosed)
            {
                return;
            }

            if (e.FileActionType == FileActionTypes.ContentSavedToDisk)
            {
                _pendingSaveRefresh = true;
                _onSaveContinuousUntilUtc = DateTime.UtcNow.AddMilliseconds(OnSaveContinuousGraceMilliseconds);
                _dataProvider.ScheduleUpdate(immediate: true);
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
                    if (_options.UpdateMode == UpdateMode.OnSave && !_visibleLineNumbersOnSave.Contains(line))
                    {
                        continue;
                    }

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

        private static bool ReplaceLineSet(HashSet<int> target, IReadOnlyDictionary<int, LineDiagnostic> diagnosticsByLine)
        {
            if (target.Count != diagnosticsByLine.Count)
            {
                target.Clear();

                foreach (KeyValuePair<int, LineDiagnostic> diagnostic in diagnosticsByLine)
                {
                    target.Add(diagnostic.Key);
                }

                return true;
            }

            foreach (KeyValuePair<int, LineDiagnostic> diagnostic in diagnosticsByLine)
            {
                if (!target.Contains(diagnostic.Key))
                {
                    target.Clear();

                    foreach (KeyValuePair<int, LineDiagnostic> updatedDiagnostic in diagnosticsByLine)
                    {
                        target.Add(updatedDiagnostic.Key);
                    }

                    return true;
                }
            }

            return false;
        }

        private bool IsWithinOnSaveContinuousGracePeriod()
        {
            return DateTime.UtcNow <= _onSaveContinuousUntilUtc;
        }

        private static bool RemoveResolvedLineNumbers(HashSet<int> target, IReadOnlyDictionary<int, LineDiagnostic> diagnosticsByLine)
        {
            if (target.Count == 0)
            {
                return false;
            }

            var resolvedLineNumbers = new List<int>();

            foreach (int lineNumber in target)
            {
                if (!diagnosticsByLine.ContainsKey(lineNumber))
                {
                    resolvedLineNumbers.Add(lineNumber);
                }
            }

            if (resolvedLineNumbers.Count == 0)
            {
                return false;
            }

            foreach (int lineNumber in resolvedLineNumbers)
            {
                target.Remove(lineNumber);
            }

            return true;
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                _view.Closed -= OnViewClosed;
                _dataProvider.DiagnosticsUpdated -= OnDiagnosticsUpdated;

                if (_textDocument != null)
                {
                    _textDocument.FileActionOccurred -= OnFileActionOccurred;
                }
            }
        }
    }
}
