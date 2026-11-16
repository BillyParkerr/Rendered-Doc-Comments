using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;
using RenderedDocComments.Diagnostics;
using RenderedDocComments.Navigation;
using RenderedDocComments.Parsing;
using RenderedDocComments.Rendering;

namespace RenderedDocComments.Editor
{
    internal static class DocCardVisibility
    {
        /// <summary>
        /// A card is shown for a rendered comment once its lines 2..N are folded, so it never
        /// sits on top of visible raw lines. Single-line comments have nothing to fold.
        /// </summary>
        public static bool IsShowingCard(ITextView view, DocCommentBufferState state, IOutliningManager outlining, DocCommentBlock block) =>
            IsShowingCard(view, state, outlining, block, null);

        /// <param name="folds">The collapsed folds over the visible lines, read once per redraw (optional).</param>
        public static bool IsShowingCard(ITextView view, DocCommentBufferState state, IOutliningManager outlining, DocCommentBlock block, CollapsedFolds folds)
        {
            if (!WantsCard(view, state, block)) return false;
            bool known = folds != null && folds.Covers(block);
            if (known ? folds.HidesFirstLine(block) : IsHiddenByOtherFold(outlining, block)) return false;
            if (block.IsSingleLine || UsesOverlay(view)) return true;
            if (outlining == null || !outlining.Enabled) return false;
            return known
                ? folds.FoldsRestOf(block)
                : outlining.GetCollapsedRegions(block.RestOfBlockSpan).Any(c => c.Tag is DocCommentRegionTag);
        }

        /// <summary>
        /// In side-by-side diffs VS uses its own outlining (for hiding unchanged regions), which
        /// doesn't take our fold over a comment's lines 2..N. There the card is laid over the
        /// comment's raw lines instead of folding them away.
        /// </summary>
        public static bool UsesOverlay(ITextView view) => DiffAwareness.IsSideBySideDiff(view);

        /// <summary>
        /// Rendered, and in this editor a card is appropriate: not a comment that differs between
        /// the two sides of a diff (those stay raw so the diff highlighting shows).
        /// </summary>
        public static bool WantsCard(ITextView view, DocCommentBufferState state, DocCommentBlock block) =>
            state.IsRendered(block) && !DiffAwareness.IsChangedInDiff(view, block);

        /// <summary>
        /// True when some other fold hides the comment's first line: VS's own fold over the comment
        /// (collapsed with the margin arrow or Ctrl+M, O), or an enclosing fold such as a whole class.
        /// </summary>
        public static bool IsHiddenByOtherFold(IOutliningManager outlining, DocCommentBlock block)
        {
            if (outlining == null || !outlining.Enabled) return false;
            var snapshot = block.Snapshot;
            return outlining.GetCollapsedRegions(block.FirstLineSpan).Any(c =>
                !(c.Tag is DocCommentRegionTag) && c.Extent.GetSpan(snapshot).Contains(block.Span.Start));
        }
    }

    /// <summary>
    /// The collapsed folds over an area (the lines on screen), read from VS once and then asked
    /// about each comment there. Asking VS separately for every comment, twice, on every redraw
    /// adds up while scrolling.
    /// </summary>
    internal sealed class CollapsedFolds
    {
        private readonly SnapshotSpan _area;
        private readonly List<(SnapshotSpan Extent, bool Ours)> _folds = new List<(SnapshotSpan, bool)>();

        private CollapsedFolds(SnapshotSpan area)
        {
            _area = area;
        }

        /// <summary>The collapsed folds touching the area; null when outlining is off (nothing is folded).</summary>
        public static CollapsedFolds Read(IOutliningManager outlining, SnapshotSpan area)
        {
            if (outlining == null || !outlining.Enabled)
            {
                return null;
            }

            var folds = new CollapsedFolds(area);
            foreach (var collapsed in outlining.GetCollapsedRegions(area))
            {
                folds._folds.Add((collapsed.Extent.GetSpan(area.Snapshot), collapsed.Tag is DocCommentRegionTag));
            }

            return folds;
        }

        /// <summary>
        /// Whether the answers below hold for this comment: its first line is inside the area, so
        /// every fold that could hide it, or fold its lines 2..N, touches the area and was read.
        /// </summary>
        public bool Covers(DocCommentBlock block) =>
            block.Snapshot == _area.Snapshot && _area.Contains(block.Span.Start) && block.FirstLine.End.Position < _area.End.Position;

        /// <summary>Another fold (VS's own over the comment, or an enclosing one) hides the comment's first line.</summary>
        public bool HidesFirstLine(DocCommentBlock block)
        {
            foreach (var fold in _folds)
            {
                if (!fold.Ours && fold.Extent.Contains(block.Span.Start)) return true;
            }

            return false;
        }

        /// <summary>Our fold over the comment's lines 2..N is collapsed.</summary>
        public bool FoldsRestOf(DocCommentBlock block)
        {
            var rest = block.RestOfBlockSpan;
            foreach (var fold in _folds)
            {
                if (fold.Ours && fold.Extent.IntersectsWith(rest)) return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Built cards for one editor, reused until the comment's text, the width or the theme changes.
    /// Keyed by where the comment starts (tracked through edits), so typing elsewhere in the
    /// file doesn't rebuild every card.
    /// </summary>
    internal sealed class DocCardCache
    {
        /// <summary>Plenty for every card on a tall screen several times over, with room to scroll back.</summary>
        private const int MaxCards = 120;

        private readonly IWpfTextView _view;
        private readonly List<Entry> _entries = new List<Entry>();
        private long _uses;

        public DocCardCache(IWpfTextView view)
        {
            _view = view;
        }

        public double WidthFor(DocCommentBlock block)
        {
            double columnWidth = _view.FormattedLineSource?.ColumnWidth ?? DocTheme.Current.FontSize * 0.6;
            return DocCommentRenderer.CardWidth(_view.ViewportWidth, block.IndentColumns * columnWidth);
        }

        /// <summary>Whether this comment's card is already built for the current width, theme and inherited docs.</summary>
        public bool Contains(DocCommentBlock block) => Find(block, out _) != null;

        /// <summary>Whether this card is still one of the cache's (not rebuilt, dropped or cleared since).</summary>
        public bool Holds(FrameworkElement card)
        {
            foreach (var entry in _entries)
            {
                if (ReferenceEquals(entry.Card, card)) return true;
            }

            return false;
        }

        public FrameworkElement Get(DocCommentBlock block)
        {
            var found = Find(block, out var inherited);
            if (found != null)
            {
                found.LastUsed = ++_uses;
                return found.Card;
            }

            var snapshot = block.Snapshot;
            int start = block.Span.Start.Position;
            int version = snapshot.Version.VersionNumber;
            double width = WidthFor(block);
            int theme = DocTheme.Version;

            // Not built yet for this text, width, theme and inherited docs: drop any older card for
            // the same comment before building the new one.
            _entries.RemoveAll(e => e.Version <= version && e.Start.GetPosition(snapshot) == start);

            // Cards are heavy (several text boxes each), so a long file keeps only the most
            // recently used ones; others are built again if they come back into view.
            while (_entries.Count >= MaxCards)
            {
                int oldest = 0;
                for (int i = 1; i < _entries.Count; i++)
                {
                    if (_entries[i].LastUsed < _entries[oldest].LastUsed) oldest = i;
                }

                _entries.RemoveAt(oldest);
            }

            var comment = snapshot.CreateTrackingSpan(block.Span, SpanTrackingMode.EdgeInclusive);
            var card = Guard.Run(
                "Rendering a comment",
                () => DocCommentRenderer.CreateCard(block, width, new CardLinkHandler(snapshot.TextBuffer, comment), inherited),
                null) ?? DocCommentRenderer.CreateFallbackCard(block, width);

            _entries.Add(new Entry
            {
                Start = snapshot.CreateTrackingPoint(start, PointTrackingMode.Negative),
                Version = snapshot.Version.VersionNumber,
                Xml = block.Xml,
                Width = width,
                Theme = theme,
                Inherited = inherited,
                Card = card,
                LastUsed = ++_uses,
            });
            return card;
        }

        /// <summary>
        /// The built card for this comment as it is now, or null. Called for every visible card on
        /// every layout, so the cheap checks come first and the tracking point (which has to
        /// follow the edits since it was made) is asked last.
        /// </summary>
        private Entry Find(DocCommentBlock block, out InheritedDoc inherited)
        {
            var snapshot = block.Snapshot;
            int start = block.Span.Start.Position;
            double width = WidthFor(block);
            int theme = DocTheme.Version;

            // <inheritdoc/>: the card also depends on the inherited docs, which arrive in the background.
            inherited = InheritedDocStore.UsesInheritDoc(block)
                ? InheritedDocStore.Get(snapshot.TextBuffer).GetOrRequest(block)
                : null;

            int version = snapshot.Version.VersionNumber;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (entry.Theme == theme
                    && entry.Inherited == inherited
                    && Math.Abs(entry.Width - width) < 0.5
                    && entry.Version <= version
                    && string.Equals(entry.Xml, block.Xml, StringComparison.Ordinal)
                    && entry.Start.GetPosition(snapshot) == start)
                {
                    return entry;
                }
            }

            return null;
        }

        /// <summary>Drop cards for comments that no longer exist.</summary>
        public void Prune(ITextSnapshot snapshot)
        {
            var starts = new HashSet<int>(DocCommentScanner.GetBlocks(snapshot).Select(b => b.Span.Start.Position));
            _entries.RemoveAll(e =>
                e.Theme != DocTheme.Version
                || (e.Version <= snapshot.Version.VersionNumber && !starts.Contains(e.Start.GetPosition(snapshot))));
        }

        public void Clear() => _entries.Clear();

        private sealed class Entry
        {
            public ITrackingPoint Start { get; set; }
            public int Version { get; set; }
            public string Xml { get; set; }
            public double Width { get; set; }
            public int Theme { get; set; }
            public InheritedDoc Inherited { get; set; }
            public FrameworkElement Card { get; set; }
            public long LastUsed { get; set; }
        }
    }
}
