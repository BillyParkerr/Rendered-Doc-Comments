using Microsoft.VisualStudio.Text;

namespace RenderedDocComments.Parsing
{
    /// <summary>A run of consecutive <c>///</c> lines in one snapshot.</summary>
    internal sealed class DocCommentBlock
    {
        public DocCommentBlock(SnapshotSpan span, int startLine, int endLine, string xml, int indentColumns)
        {
            Span = span;
            StartLine = startLine;
            EndLine = endLine;
            Xml = xml;
            IndentColumns = indentColumns;
        }

        /// <summary>From the first '/' of the first line to the end of the last line (line break excluded).</summary>
        public SnapshotSpan Span { get; }

        public int StartLine { get; }

        public int EndLine { get; }

        /// <summary>The comment content with the leading <c>///</c> markers removed, lines joined by '\n'.</summary>
        public string Xml { get; }

        /// <summary>Visual columns before the first '/', with tabs expanded to 4.</summary>
        public int IndentColumns { get; }

        public bool IsSingleLine => StartLine == EndLine;

        public ITextSnapshot Snapshot => Span.Snapshot;

        public ITextSnapshotLine FirstLine => Span.Snapshot.GetLineFromLineNumber(StartLine);

        /// <summary>The comment's part of its first line, which the card covers.</summary>
        public SnapshotSpan FirstLineSpan => new SnapshotSpan(Span.Start, FirstLine.End);

        /// <summary>Lines 2..N, folded away under a card. Empty for single-line comments.</summary>
        public SnapshotSpan RestOfBlockSpan => new SnapshotSpan(FirstLine.End, Span.End);

        /// <summary>True when the point is on one of this comment's lines.</summary>
        public bool ContainsLineOf(SnapshotPoint point)
        {
            if (point.Snapshot != Span.Snapshot)
            {
                point = point.TranslateTo(Span.Snapshot, PointTrackingMode.Positive);
            }

            int line = point.GetContainingLine().LineNumber;
            return line >= StartLine && line <= EndLine;
        }
    }
}
