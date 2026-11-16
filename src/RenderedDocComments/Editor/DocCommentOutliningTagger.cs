using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using RenderedDocComments.Parsing;
using RenderedDocComments.Diagnostics;

namespace RenderedDocComments.Editor
{
    [Export(typeof(ITaggerProvider))]
    [TagType(typeof(IOutliningRegionTag))]
    [ContentType("CSharp")]
    internal sealed class DocCommentOutliningTaggerProvider : ITaggerProvider
    {
        [Import]
        internal EditorServices Services { get; set; }

        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag =>
            buffer.Properties.GetOrCreateSingletonProperty(() => new DocCommentOutliningTagger(buffer)) as ITagger<T>;
    }

    /// <summary>
    /// Offers a fold over lines 2..N of every multi-line doc comment. The controller decides
    /// which ones are collapsed. Single-line comments need no fold: the card simply replaces the line.
    /// </summary>
    internal sealed class DocCommentOutliningTagger : ITagger<IOutliningRegionTag>
    {
        private readonly ITextBuffer _buffer;
        private ITextSnapshot _cachedSnapshot;
        private List<ITagSpan<IOutliningRegionTag>> _cachedTags;

        public DocCommentOutliningTagger(ITextBuffer buffer)
        {
            _buffer = buffer;
            // Folds follow edits on their own. Only an edit that changes a comment's shape (adds,
            // removes, joins or splits comments, or reaches the edge of a fold) can need a different
            // fold, and then VS is asked to re-read just the lines around it. Asking for the whole
            // file makes VS rebuild every outlining region in it while the keystroke waits.
            _buffer.Changed += (s, e) =>
            {
                Guard.Run("Finding comment folds", () =>
                {
                    if (DocCommentScanner.TouchesComments(e) && !DocCommentScanner.EditKeepsCommentShapes(e))
                    {
                        TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(AffectedSpan(e)));
                    }
                });
            };
        }

        /// <summary>
        /// The edited lines and the lines either side, widened to every comment there now and
        /// every comment that was there before the edit (wherever the edit moved it), so any fold
        /// that changed lies inside.
        /// </summary>
        private static SnapshotSpan AffectedSpan(TextContentChangedEventArgs e)
        {
            var after = e.After;
            int start = after.Length, end = 0;

            void Include(int from, int to)
            {
                start = Math.Min(start, from);
                end = Math.Max(end, to);
            }

            foreach (var change in e.Changes)
            {
                int first = Math.Max(0, after.GetLineNumberFromPosition(change.NewPosition) - 1);
                int last = Math.Min(after.LineCount - 1, after.GetLineNumberFromPosition(Math.Min(change.NewEnd, after.Length)) + 1);
                Include(after.GetLineFromLineNumber(first).Start.Position, after.GetLineFromLineNumber(last).EndIncludingLineBreak.Position);
                foreach (var block in DocCommentScanner.BlocksInLines(after, first, last))
                {
                    Include(block.Span.Start.Position, block.Span.End.Position);
                }

                var before = e.Before;
                int oldFirst = Math.Max(0, before.GetLineNumberFromPosition(change.OldPosition) - 1);
                int oldLast = Math.Min(before.LineCount - 1, before.GetLineNumberFromPosition(Math.Min(change.OldEnd, before.Length)) + 1);
                foreach (var block in DocCommentScanner.BlocksInLines(before, oldFirst, oldLast))
                {
                    var moved = block.Span.TranslateTo(after, SpanTrackingMode.EdgeInclusive);
                    Include(moved.Start.Position, moved.End.Position);
                }
            }

            return end <= start ? new SnapshotSpan(after, 0, after.Length) : new SnapshotSpan(after, Span.FromBounds(start, end));
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public IEnumerable<ITagSpan<IOutliningRegionTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0)
            {
                yield break;
            }

            var tags = Guard.Run("Finding comment folds", () => TagsFor(spans[0].Snapshot), new List<ITagSpan<IOutliningRegionTag>>());
            foreach (var tag in tags)
            {
                if (spans.IntersectsWith(tag.Span))
                {
                    yield return tag;
                }
            }
        }

        private List<ITagSpan<IOutliningRegionTag>> TagsFor(ITextSnapshot snapshot)
        {
            if (_cachedTags != null && _cachedSnapshot == snapshot)
            {
                return _cachedTags;
            }

            var tags = new List<ITagSpan<IOutliningRegionTag>>();
            foreach (var block in DocCommentScanner.GetBlocks(snapshot))
            {
                if (!block.IsSingleLine)
                {
                    tags.Add(new TagSpan<IOutliningRegionTag>(block.RestOfBlockSpan, new DocCommentRegionTag()));
                }
            }

            _cachedSnapshot = snapshot;
            _cachedTags = tags;
            return tags;
        }
    }
}
