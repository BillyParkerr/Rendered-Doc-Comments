using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.VisualStudio.Text;

namespace RenderedDocComments.Parsing
{
    /// <summary>
    /// Finds <c>///</c> comment blocks by scanning text. Results are cached per snapshot.
    /// <para>
    /// Every keystroke makes a new snapshot, so a snapshot is normally worked out from the
    /// previous one of the same buffer: only the lines around the edits are read again, and the
    /// comments elsewhere are carried over (moved by the edit). The whole file is read only
    /// when there's nothing to start from (a newly opened file) or the edits cover much of it.
    /// </para>
    /// </summary>
    internal static class DocCommentScanner
    {
        private const int TabSize = 4;

        /// <summary>Beyond these, reading the whole file is simpler and about as fast.</summary>
        private const int MaxVersionsToFollow = 64;
        private const int MaxChangesToFollow = 512;

        private static readonly ConditionalWeakTable<ITextSnapshot, IReadOnlyList<DocCommentBlock>> Cache =
            new ConditionalWeakTable<ITextSnapshot, IReadOnlyList<DocCommentBlock>>();

        /// <summary>The newest scanned snapshot of each buffer: where the next scan starts from.</summary>
        private static readonly ConditionalWeakTable<ITextBuffer, LatestScan> Latest =
            new ConditionalWeakTable<ITextBuffer, LatestScan>();

        /// <summary>For tests and benchmarks: whether the last scan on this thread reused an earlier snapshot.</summary>
        [ThreadStatic]
        internal static bool LastScanWasIncremental;

        /// <summary>For tests and benchmarks: how many lines the last scan on this thread read.</summary>
        [ThreadStatic]
        internal static int LastScanLinesRead;

        /// <summary>All comment blocks in the snapshot, in file order.</summary>
        public static IReadOnlyList<DocCommentBlock> GetBlocks(ITextSnapshot snapshot) => Cache.GetValue(snapshot, ScanOrUpdate);

        /// <summary>Scan every line, ignoring the cache and earlier snapshots (for tests).</summary>
        internal static IReadOnlyList<DocCommentBlock> ScanWholeFile(ITextSnapshot snapshot)
        {
            var reader = new LineReader(snapshot);
            var blocks = new List<DocCommentBlock>();
            ScanLines(reader, 0, snapshot.LineCount - 1, blocks);
            LastScanWasIncremental = false;
            LastScanLinesRead = reader.LinesRead;
            return blocks;
        }

        public static DocCommentBlock FindIntersecting(ITextSnapshot snapshot, SnapshotSpan span)
        {
            if (span.Snapshot != snapshot)
            {
                span = span.TranslateTo(snapshot, SpanTrackingMode.EdgeInclusive);
            }

            int first = span.Start.GetContainingLine().LineNumber;
            int last = span.End.GetContainingLine().LineNumber;
            var blocks = GetBlocks(snapshot);
            for (int i = FirstEndingAtOrAfter(blocks, first); i < blocks.Count && blocks[i].StartLine <= last; i++)
            {
                if (blocks[i].Span.IntersectsWith(span)) return blocks[i];
            }

            return null;
        }

        /// <summary>The comment on the caret's line, or the one directly above it (caret on the declaration).</summary>
        public static DocCommentBlock FindForCaretLine(ITextSnapshot snapshot, int lineNumber) =>
            BlockAtLine(snapshot, lineNumber) ?? BlockEndingAt(snapshot, lineNumber - 1);

        /// <summary>The comment covering this line, or null. (Binary search: called per line during layout.)</summary>
        public static DocCommentBlock BlockAtLine(ITextSnapshot snapshot, int lineNumber)
        {
            var blocks = GetBlocks(snapshot);
            int i = FirstEndingAtOrAfter(blocks, lineNumber);
            return i < blocks.Count && blocks[i].StartLine <= lineNumber ? blocks[i] : null;
        }

        public static DocCommentBlock BlockStartingAt(ITextSnapshot snapshot, int lineNumber)
        {
            var block = BlockAtLine(snapshot, lineNumber);
            return block != null && block.StartLine == lineNumber ? block : null;
        }

        public static DocCommentBlock BlockEndingAt(ITextSnapshot snapshot, int lineNumber)
        {
            var block = BlockAtLine(snapshot, lineNumber);
            return block != null && block.EndLine == lineNumber ? block : null;
        }

        /// <summary>The comments with a line in [first, last], in file order.</summary>
        public static IEnumerable<DocCommentBlock> BlocksInLines(ITextSnapshot snapshot, int first, int last)
        {
            var blocks = GetBlocks(snapshot);
            for (int i = FirstEndingAtOrAfter(blocks, first); i < blocks.Count && blocks[i].StartLine <= last; i++)
            {
                yield return blocks[i];
            }
        }

        /// <summary>True if any comment has a line in [first, last].</summary>
        public static bool AnyBlockInLines(ITextSnapshot snapshot, int first, int last)
        {
            var blocks = GetBlocks(snapshot);
            int i = FirstEndingAtOrAfter(blocks, first);
            return i < blocks.Count && blocks[i].StartLine <= last;
        }

        /// <summary>
        /// Whether an edit could have changed any comment: it touched a comment's lines (or the
        /// line next to one, which can join or split comments), or the number of comments changed.
        /// Edits elsewhere need no comment-related work at all.
        /// </summary>
        public static bool TouchesComments(TextContentChangedEventArgs e)
        {
            if (GetBlocks(e.Before).Count != GetBlocks(e.After).Count)
            {
                return true;
            }

            foreach (var change in e.Changes)
            {
                if (NearAnyBlock(e.Before, change.OldSpan) || NearAnyBlock(e.After, change.NewSpan))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when an edit only changed text inside one comment and every comment kept its
        /// shape: the same comments, the edited one still starting on the same line, and its fold
        /// (lines 2..N) still running from the end of its first line to the end of its last. That's
        /// ordinary typing in a comment, and folds, cards and remembered views need no update for
        /// it: the editor moves fold edges along with the text by itself.
        /// <para>
        /// Typing exactly at an edge of the fold (the end of the first or last line of a multi-line
        /// comment) doesn't count: whether text added there falls inside the fold depends on how
        /// the editor tracks it, so the fold is offered again.
        /// </para>
        /// </summary>
        public static bool EditKeepsCommentShapes(TextContentChangedEventArgs e)
        {
            if (e.Changes.Count != 1)
            {
                return false;
            }

            var change = e.Changes[0];
            var before = BlockContaining(e.Before, change.OldSpan);
            var after = BlockContaining(e.After, change.NewSpan);
            if (before == null || after == null
                || GetBlocks(e.Before).Count != GetBlocks(e.After).Count
                || after.StartLine != before.StartLine)
            {
                return false;
            }

            // The comment grew or shrank by exactly the lines the edit added or removed.
            int linesAdded = (e.After.GetLineNumberFromPosition(change.NewEnd) - e.After.GetLineNumberFromPosition(change.NewPosition))
                - (e.Before.GetLineNumberFromPosition(change.OldEnd) - e.Before.GetLineNumberFromPosition(change.OldPosition));
            if (after.EndLine - before.EndLine != linesAdded)
            {
                return false;
            }

            if (before.IsSingleLine && after.IsSingleLine)
            {
                return true; // No fold before or after.
            }

            return !TouchesEdge(before.RestOfBlockSpan.Span, change.OldSpan) && !TouchesEdge(after.RestOfBlockSpan.Span, change.NewSpan);
        }

        /// <summary>The comment whose text (from its first slash to the end of its last line) contains the whole span.</summary>
        private static DocCommentBlock BlockContaining(ITextSnapshot snapshot, Span span)
        {
            var block = BlockAtLine(snapshot, snapshot.GetLineNumberFromPosition(span.Start));
            return block != null && span.Start >= block.Span.Start.Position && span.End <= block.Span.End.Position ? block : null;
        }

        private static bool TouchesEdge(Span fold, Span change) =>
            (change.Start <= fold.Start && fold.Start <= change.End) || (change.Start <= fold.End && fold.End <= change.End);

        private static bool NearAnyBlock(ITextSnapshot snapshot, Span span)
        {
            int first = snapshot.GetLineNumberFromPosition(span.Start);
            int last = snapshot.GetLineNumberFromPosition(Math.Min(span.End, snapshot.Length));
            return AnyBlockInLines(snapshot, first - 1, last + 1);
        }

        /// <summary>Index of the first block whose EndLine is at or after the line (blocks are in file order).</summary>
        private static int FirstEndingAtOrAfter(IReadOnlyList<DocCommentBlock> blocks, int lineNumber)
        {
            int lo = 0, hi = blocks.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (blocks[mid].EndLine < lineNumber) lo = mid + 1;
                else hi = mid;
            }

            return lo;
        }

        private static IReadOnlyList<DocCommentBlock> ScanOrUpdate(ITextSnapshot snapshot)
        {
            var buffer = snapshot.TextBuffer;
            if (buffer == null)
            {
                return ScanWholeFile(snapshot);
            }

            var latest = Latest.GetValue(buffer, _ => new LatestScan());

            ITextSnapshot previousSnapshot;
            IReadOnlyList<DocCommentBlock> previousBlocks;
            lock (latest)
            {
                previousSnapshot = latest.Snapshot;
                previousBlocks = latest.Blocks;
            }

            var reader = new LineReader(snapshot);
            var blocks = previousSnapshot == null ? null : TryUpdate(previousSnapshot, previousBlocks, snapshot, reader);
            LastScanWasIncremental = blocks != null;
            if (blocks == null)
            {
                var all = new List<DocCommentBlock>();
                ScanLines(reader, 0, snapshot.LineCount - 1, all);
                blocks = all;
            }

            LastScanLinesRead = reader.LinesRead;

            lock (latest)
            {
                if (latest.Snapshot == null || latest.Snapshot.Version.VersionNumber < snapshot.Version.VersionNumber)
                {
                    latest.Snapshot = snapshot;
                    latest.Blocks = blocks;
                }
            }

            return blocks;
        }

        /// <summary>
        /// Work out the target's comments from an earlier snapshot's. Returns null when that isn't
        /// possible or worthwhile (the caller then reads the whole file).
        /// </summary>
        private static IReadOnlyList<DocCommentBlock> TryUpdate(
            ITextSnapshot from, IReadOnlyList<DocCommentBlock> fromBlocks, ITextSnapshot target, LineReader reader)
        {
            // The changes from one version to the next, in order, up to the target.
            var steps = new List<INormalizedTextChangeCollection>();
            int changeCount = 0;
            for (var version = from.Version; !ReferenceEquals(version, target.Version); version = version.Next)
            {
                if (version == null
                    || version.VersionNumber >= target.Version.VersionNumber
                    || version.Changes == null
                    || steps.Count == MaxVersionsToFollow)
                {
                    return null; // Not an earlier version of the target (or too far back).
                }

                changeCount += version.Changes.Count;
                if (changeCount > MaxChangesToFollow) return null;
                steps.Add(version.Changes);
            }

            int lineCount = target.LineCount;

            // Lines to read again: every changed line, widened over any comment lines touching it
            // (above and below), so a comment the edit joined, split or changed is read whole.
            var dirty = new List<LineRange>();
            for (int s = 0; s < steps.Count; s++)
            {
                foreach (var change in steps[s])
                {
                    int start = change.NewPosition, end = change.NewEnd;
                    for (int later = s + 1; later < steps.Count; later++)
                    {
                        start = Map(start, steps[later], isEnd: false);
                        end = Map(end, steps[later], isEnd: true);
                    }

                    start = Math.Min(start, target.Length);
                    end = Math.Min(Math.Max(start, end), target.Length);
                    dirty.Add(new LineRange(target.GetLineNumberFromPosition(start), target.GetLineNumberFromPosition(end)));
                }
            }

            dirty = Merge(dirty);
            for (int i = 0; i < dirty.Count; i++)
            {
                int first = dirty[i].First, last = dirty[i].Last;
                while (first > 0 && reader.IsDocLine(first - 1)) first--;
                while (last < lineCount - 1 && reader.IsDocLine(last + 1)) last++;
                dirty[i] = new LineRange(first, last);
            }

            dirty = Merge(dirty);
            int dirtyLines = 0;
            foreach (var range in dirty) dirtyLines += range.Last - range.First + 1;
            if (dirtyLines > lineCount / 2)
            {
                return null;
            }

            // Positions of the dirty ranges, to place the earlier snapshot's comments against.
            var starts = new int[dirty.Count];
            var ends = new int[dirty.Count];
            for (int i = 0; i < dirty.Count; i++)
            {
                starts[i] = target.GetLineFromLineNumber(dirty[i].First).Start.Position;
                ends[i] = target.GetLineFromLineNumber(dirty[i].Last).EndIncludingLineBreak.Position;
            }

            // Walk the earlier comments in order. Those in a dirty range are dropped (that range is
            // read again); the rest haven't changed and only move. Between two dirty ranges
            // everything moves by the same number of lines, worked out once per gap and checked.
            var result = new List<DocCommentBlock>(fromBlocks.Count + 4);
            int next = 0, gap = -1, lineShift = 0;
            foreach (var block in fromBlocks)
            {
                int start = block.Span.Start.Position, end = block.Span.End.Position;
                foreach (var step in steps)
                {
                    start = Map(start, step, isEnd: false);
                    end = Map(end, step, isEnd: true);
                }

                while (next < dirty.Count && ends[next] <= start)
                {
                    ScanLines(reader, dirty[next].First, dirty[next].Last, result);
                    next++;
                }

                if (next < dirty.Count && end >= starts[next])
                {
                    continue; // In a dirty range: read again below.
                }

                if (gap != next)
                {
                    gap = next;
                    lineShift = target.GetLineNumberFromPosition(start) - block.StartLine;
                    if (!StillAComment(target, start, end, block.EndLine + lineShift))
                    {
                        return null; // Shouldn't happen; be safe and read the whole file.
                    }
                }

                result.Add(new DocCommentBlock(
                    new SnapshotSpan(target, start, end - start),
                    block.StartLine + lineShift,
                    block.EndLine + lineShift,
                    block.Xml,
                    block.IndentColumns));
            }

            for (; next < dirty.Count; next++)
            {
                ScanLines(reader, dirty[next].First, dirty[next].Last, result);
            }

            return result;
        }

        /// <summary>A carried-over comment still starts with "///" and ends on the line expected.</summary>
        private static bool StillAComment(ITextSnapshot target, int start, int end, int endLine) =>
            end <= target.Length
            && start + 3 <= end
            && target[start] == '/' && target[start + 1] == '/' && target[start + 2] == '/'
            && target.GetLineNumberFromPosition(end) == endLine;

        /// <summary>
        /// Where a position ends up after a set of changes. A position inside a change moves to
        /// its start (or, for the end of a span, to its end), so spans grow to cover changes.
        /// </summary>
        private static int Map(int position, INormalizedTextChangeCollection changes, bool isEnd)
        {
            // The last change starting at or before the position.
            int lo = 0, hi = changes.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (changes[mid].OldPosition <= position)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (found < 0) return position;
            var change = changes[found];
            if (position >= change.OldEnd) return position + (change.NewEnd - change.OldEnd);
            return isEnd ? change.NewEnd : change.NewPosition;
        }

        private static List<LineRange> Merge(List<LineRange> ranges)
        {
            if (ranges.Count < 2) return ranges;
            ranges.Sort((a, b) => a.First.CompareTo(b.First));
            var merged = new List<LineRange> { ranges[0] };
            for (int i = 1; i < ranges.Count; i++)
            {
                var last = merged[merged.Count - 1];
                if (ranges[i].First <= last.Last + 1)
                {
                    merged[merged.Count - 1] = new LineRange(last.First, Math.Max(last.Last, ranges[i].Last));
                }
                else
                {
                    merged.Add(ranges[i]);
                }
            }

            return merged;
        }

        /// <summary>
        /// Find the comments in lines [first, last] and add them in order. The lines just outside
        /// the range must not be doc comment lines (or must be outside the file), so no comment
        /// crosses its edges.
        /// </summary>
        private static void ScanLines(LineReader reader, int first, int last, List<DocCommentBlock> into)
        {
            int i = first;
            while (i <= last)
            {
                if (!reader.IsDocLine(i))
                {
                    i++;
                    continue;
                }

                int start = i;
                while (i + 1 <= last && reader.IsDocLine(i + 1))
                {
                    i++;
                }

                into.Add(Create(reader.Snapshot, start, i));
                i += 2; // Line i + 1 was just read: it isn't a doc line (or is past the range).
            }
        }

        /// <summary>A line whose first non-blank characters are exactly three slashes.</summary>
        private static bool IsDocLine(string text, out int slashOffset)
        {
            int k = 0;
            while (k < text.Length && (text[k] == ' ' || text[k] == '\t'))
            {
                k++;
            }

            slashOffset = k;
            return text.Length - k >= 3
                && text[k] == '/' && text[k + 1] == '/' && text[k + 2] == '/'
                && (text.Length - k == 3 || text[k + 3] != '/');
        }

        private static DocCommentBlock Create(ITextSnapshot snapshot, int startLine, int endLine)
        {
            var first = snapshot.GetLineFromLineNumber(startLine);
            var last = snapshot.GetLineFromLineNumber(endLine);
            string firstText = first.GetText();
            IsDocLine(firstText, out int firstOffset);

            var xml = new StringBuilder();
            for (int l = startLine; l <= endLine; l++)
            {
                string text = l == startLine ? firstText : snapshot.GetLineFromLineNumber(l).GetText();
                IsDocLine(text, out int offset);
                int contentStart = offset + 3;
                if (contentStart < text.Length && text[contentStart] == ' ')
                {
                    contentStart++;
                }

                if (l > startLine)
                {
                    xml.Append('\n');
                }

                xml.Append(text, contentStart, text.Length - contentStart);
            }

            int columns = 0;
            for (int k = 0; k < firstOffset; k++)
            {
                columns = firstText[k] == '\t' ? (columns / TabSize + 1) * TabSize : columns + 1;
            }

            var span = new SnapshotSpan(first.Start + firstOffset, last.End);
            return new DocCommentBlock(span, startLine, endLine, xml.ToString(), columns);
        }

        private struct LineRange
        {
            public LineRange(int first, int last)
            {
                First = first;
                Last = last;
            }

            public int First { get; }

            public int Last { get; }
        }

        private sealed class LatestScan
        {
            public ITextSnapshot Snapshot { get; set; }

            public IReadOnlyList<DocCommentBlock> Blocks { get; set; }
        }

        /// <summary>
        /// Checks lines for "///" by copying just their start into a reused buffer, instead of
        /// making a string of every line (most lines aren't comments).
        /// </summary>
        private sealed class LineReader
        {
            private readonly char[] _buffer = new char[64];

            public LineReader(ITextSnapshot snapshot)
            {
                Snapshot = snapshot;
            }

            public ITextSnapshot Snapshot { get; }

            public int LinesRead { get; private set; }

            public bool IsDocLine(int lineNumber)
            {
                LinesRead++;
                var line = Snapshot.GetLineFromLineNumber(lineNumber);
                int length = line.Length;
                int take = Math.Min(length, _buffer.Length);
                if (take < 3)
                {
                    return false;
                }

                Snapshot.CopyTo(line.Start.Position, _buffer, 0, take);
                int k = 0;
                while (k < take && (_buffer[k] == ' ' || _buffer[k] == '\t'))
                {
                    k++;
                }

                if (k + 4 > take && take < length)
                {
                    return DocCommentScanner.IsDocLine(line.GetText(), out _); // Very deep indentation: rare.
                }

                return take - k >= 3
                    && _buffer[k] == '/' && _buffer[k + 1] == '/' && _buffer[k + 2] == '/'
                    && (take - k == 3 || _buffer[k + 3] != '/');
            }
        }
    }
}
