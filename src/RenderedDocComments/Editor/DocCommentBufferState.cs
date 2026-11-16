using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.Text;
using RenderedDocComments.Options;
using RenderedDocComments.Parsing;
using RenderedDocComments.Diagnostics;

namespace RenderedDocComments.Editor
{
    /// <summary>
    /// Which comments in a file show as cards and which show raw. Every comment follows the
    /// "Render comments when a file opens" option unless it has an override; overrides follow
    /// edits, and the ones you chose yourself are remembered for the next time the file opens.
    /// </summary>
    internal sealed class DocCommentBufferState
    {
        private readonly ITextBuffer _buffer;
        private readonly bool _renderByDefault;
        private readonly List<Override> _overrides = new List<Override>();

        /// <summary>Opened by VS (Find, Go To Line). Re-rendered once the caret leaves; never remembered.</summary>
        private readonly List<ITrackingSpan> _temporary = new List<ITrackingSpan>();

        private IReadOnlyList<DocCommentBlock> _lastBlocks;
        private string _filePath;
        private Dictionary<string, bool> _lastRemembered = new Dictionary<string, bool>();

        private DocCommentBufferState(ITextBuffer buffer)
        {
            _buffer = buffer;
            _lastBlocks = DocCommentScanner.GetBlocks(buffer.CurrentSnapshot);
            buffer.Changed += OnBufferChanged;

            ExtensionOptions.EnsureLoaded();
            _renderByDefault = ExtensionOptions.RenderByDefault;

            if (buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document))
            {
                _filePath = RememberablePath(document.FilePath);
                document.FileActionOccurred += OnFileAction;
                if (_filePath != null) RestoreRemembered();
            }
        }

        /// <summary>
        /// Only real files on disk are worth remembering. The old side of a diff, for example, is
        /// usually a temporary copy that's gone after the diff closes.
        /// </summary>
        private static string RememberablePath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                string full = Path.GetFullPath(path);
                return full.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) ? null : full;
            }
            catch (Exception)
            {
                return null; // Odd paths (e.g. virtual documents): don't remember.
            }
        }

        public event EventHandler Changed;

        public bool HasTemporary => _temporary.Count > 0;

        public static DocCommentBufferState Get(ITextBuffer buffer) =>
            buffer.Properties.GetOrCreateSingletonProperty(() => new DocCommentBufferState(buffer));

        public bool IsRendered(DocCommentBlock block) =>
            !Hits(_temporary, block) && (FindOverride(block)?.Rendered ?? _renderByDefault);

        public bool IsTemporarilyRaw(DocCommentBlock block) => Hits(_temporary, block);

        public bool AnyRendered() => DocCommentScanner.GetBlocks(_buffer.CurrentSnapshot).Any(IsRendered);

        public DocCommentBlock FindBlock(ITrackingSpan span)
        {
            var snapshot = _buffer.CurrentSnapshot;
            return DocCommentScanner.FindIntersecting(snapshot, span.GetSpan(snapshot));
        }

        public void SetRendered(DocCommentBlock block, bool rendered, bool raise = true)
        {
            RemoveOverrides(block);
            RemoveHits(_temporary, block);
            if (rendered != _renderByDefault)
            {
                _overrides.Add(new Override(Track(block), rendered, remember: true));
            }

            if (raise) RaiseChanged();
        }

        public void SetTemporarilyRaw(DocCommentBlock block, bool raise = true)
        {
            RemoveOverrides(block);
            RemoveHits(_temporary, block);
            _temporary.Add(Track(block));
            if (raise) RaiseChanged();
        }

        public void SetAll(bool rendered)
        {
            _overrides.Clear();
            _temporary.Clear();
            if (rendered != _renderByDefault)
            {
                foreach (var block in DocCommentScanner.GetBlocks(_buffer.CurrentSnapshot))
                {
                    _overrides.Add(new Override(Track(block), rendered, remember: true));
                }
            }

            RaiseChanged();
        }

        public void RaiseChanged()
        {
            Changed?.Invoke(this, EventArgs.Empty);
            Remember();
        }

        /// <summary>
        /// Store the overrides you chose for this file (only if they changed since last time).
        /// Called after every change, when the file is saved, and when its editor closes.
        /// </summary>
        public void Remember() =>
            Guard.Run("Tracking comment views", () => RememberCore());

        private void RememberCore()
        {
            if (_filePath == null)
            {
                return;
            }

            var remembered = new Dictionary<string, bool>();
            foreach (var entry in CommentKeys.For(_buffer.CurrentSnapshot))
            {
                var found = FindOverride(entry.Key);
                if (found != null && found.Remember)
                {
                    remembered[entry.Value] = found.Rendered;
                }
            }

            if (remembered.Count == _lastRemembered.Count && remembered.All(r => _lastRemembered.TryGetValue(r.Key, out bool v) && v == r.Value))
            {
                return;
            }

            CommentViewMemory.Save(_filePath, remembered);
            _lastRemembered = remembered;
        }

        private void RestoreRemembered()
        {
            var remembered = CommentViewMemory.Load(_filePath);
            if (remembered.Count == 0)
            {
                return;
            }

            foreach (var entry in CommentKeys.For(_buffer.CurrentSnapshot))
            {
                if (remembered.TryGetValue(entry.Value, out bool rendered) && rendered != _renderByDefault)
                {
                    _overrides.Add(new Override(Track(entry.Key), rendered, remember: true));
                    _lastRemembered[entry.Value] = rendered;
                }
            }
        }

        private void OnFileAction(object sender, TextDocumentFileActionEventArgs e) =>
            Guard.Run("Tracking comment views", () => OnFileActionCore(sender, e));

        private void OnFileActionCore(object sender, TextDocumentFileActionEventArgs e)
        {
            if ((e.FileActionType & FileActionTypes.DocumentRenamed) != 0)
            {
                CommentViewMemory.Save(_filePath, new Dictionary<string, bool>()); // Forget the old name.
                _filePath = RememberablePath(e.FilePath);
                _lastRemembered = new Dictionary<string, bool>();
            }
            else if (_filePath == null && (e.FileActionType & FileActionTypes.ContentSavedToDisk) != 0)
            {
                _filePath = RememberablePath(e.FilePath); // A new file saved for the first time.
            }

            // Declarations may have been edited since the last change: store the current keys.
            Remember();
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e) =>
            Guard.Run("Tracking comment views", () => OnBufferChangedCore(sender, e));

        private void OnBufferChangedCore(object sender, TextContentChangedEventArgs e)
        {
            // A comment that didn't exist before this edit is being written right now, so it
            // starts raw and stays raw until you render it (IntelliJ behaves the same). That's a
            // convenience while typing, not a choice to remember.
            var current = DocCommentScanner.GetBlocks(e.After);
            if (_renderByDefault && DocCommentScanner.TouchesComments(e) && !DocCommentScanner.EditKeepsCommentShapes(e))
            {
                // Both lists are in file order, so one pass finds the comments with no
                // predecessor (rather than comparing every comment with every other).
                var previous = _lastBlocks.Select(b => b.Span.TranslateTo(e.After, SpanTrackingMode.EdgeInclusive)).ToList();
                int p = 0;
                foreach (var block in current)
                {
                    while (p < previous.Count && previous[p].End < block.Span.Start) p++;
                    if (p >= previous.Count || !previous[p].IntersectsWith(block.Span))
                    {
                        _overrides.Add(new Override(Track(block), rendered: false, remember: false));
                    }
                }
            }

            _lastBlocks = current;
        }

        private Override FindOverride(DocCommentBlock block)
        {
            if (_overrides.Count == 0) return null;
            var snapshot = _buffer.CurrentSnapshot;
            var span = block.Span.TranslateTo(snapshot, SpanTrackingMode.EdgeInclusive);
            return _overrides.LastOrDefault(o => o.Span.GetSpan(snapshot).IntersectsWith(span));
        }

        private void RemoveOverrides(DocCommentBlock block)
        {
            var snapshot = _buffer.CurrentSnapshot;
            var span = block.Span.TranslateTo(snapshot, SpanTrackingMode.EdgeInclusive);
            _overrides.RemoveAll(o => o.Span.GetSpan(snapshot).IntersectsWith(span));
        }

        private bool Hits(List<ITrackingSpan> spans, DocCommentBlock block)
        {
            if (spans.Count == 0) return false;
            var snapshot = _buffer.CurrentSnapshot;
            var span = block.Span.TranslateTo(snapshot, SpanTrackingMode.EdgeInclusive);
            return spans.Any(s => s.GetSpan(snapshot).IntersectsWith(span));
        }

        private void RemoveHits(List<ITrackingSpan> spans, DocCommentBlock block)
        {
            var snapshot = _buffer.CurrentSnapshot;
            var span = block.Span.TranslateTo(snapshot, SpanTrackingMode.EdgeInclusive);
            spans.RemoveAll(s => s.GetSpan(snapshot).IntersectsWith(span));
        }

        private ITrackingSpan Track(DocCommentBlock block)
        {
            var snapshot = _buffer.CurrentSnapshot;
            return snapshot.CreateTrackingSpan(block.Span.TranslateTo(snapshot, SpanTrackingMode.EdgeInclusive), SpanTrackingMode.EdgeInclusive);
        }

        private sealed class Override
        {
            public Override(ITrackingSpan span, bool rendered, bool remember)
            {
                Span = span;
                Rendered = rendered;
                Remember = remember;
            }

            public ITrackingSpan Span { get; }

            /// <summary>Card (true) or raw (false).</summary>
            public bool Rendered { get; }

            /// <summary>You chose it, so it's saved for next time (vs. a new comment you're typing).</summary>
            public bool Remember { get; }
        }
    }
}
