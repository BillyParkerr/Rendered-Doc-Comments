using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;

namespace RenderedDocComments.Tests.Fakes
{
    /// <summary>
    /// Just enough of a text snapshot for the scanner, comment keys and edit checks. Edits make
    /// a new snapshot of the same buffer, linked by version like the editor's, so code that
    /// works from one snapshot to the next can be tested.
    /// Lines end in "\r\n" like files in VS on Windows; text written with "\n" is converted.
    /// </summary>
    internal sealed class FakeSnapshot : ITextSnapshot
    {
        private readonly string _text;
        private readonly List<FakeLine> _lines = new List<FakeLine>();
        private readonly FakeVersion _version;
        private readonly FakeBuffer _buffer;

        public FakeSnapshot(string text)
            : this(Normalize(text), new FakeBuffer(), 0)
        {
        }

        private FakeSnapshot(string text, FakeBuffer buffer, int versionNumber)
        {
            _text = text;
            _buffer = buffer;
            _buffer.CurrentSnapshot = this;
            _version = new FakeVersion(this, versionNumber);

            int start = 0;
            while (true)
            {
                int nl = _text.IndexOf("\r\n", start, StringComparison.Ordinal);
                if (nl < 0)
                {
                    _lines.Add(new FakeLine(this, _lines.Count, start, _text.Length - start, 0));
                    break;
                }

                _lines.Add(new FakeLine(this, _lines.Count, start, nl - start, 2));
                start = nl + 2;
            }
        }

        public int Length => _text.Length;
        public int LineCount => _lines.Count;
        public IEnumerable<ITextSnapshotLine> Lines => _lines;
        public ITextVersion Version => _version;
        public ITextBuffer TextBuffer => _buffer;
        public char this[int position] => _text[position];

        public ITextSnapshotLine GetLineFromLineNumber(int lineNumber) => _lines[lineNumber];
        public ITextSnapshotLine GetLineFromPosition(int position) => _lines[GetLineNumberFromPosition(position)];
        public string GetText(Span span) => _text.Substring(span.Start, span.Length);
        public string GetText(int startIndex, int length) => _text.Substring(startIndex, length);
        public string GetText() => _text;
        public char[] ToCharArray(int startIndex, int length) => _text.ToCharArray(startIndex, length);
        public void CopyTo(int sourceIndex, char[] destination, int destinationIndex, int count) => _text.CopyTo(sourceIndex, destination, destinationIndex, count);
        public void Write(TextWriter writer, Span span) => writer.Write(GetText(span));
        public void Write(TextWriter writer) => writer.Write(_text);

        /// <summary>Binary search, as in the editor (line lookups are logarithmic there too).</summary>
        public int GetLineNumberFromPosition(int position)
        {
            if (position < 0 || position > _text.Length) throw new ArgumentOutOfRangeException(nameof(position));
            int lo = 0, hi = _lines.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_lines[mid].StartPosition <= position) lo = mid;
                else hi = mid - 1;
            }

            return lo;
        }

        /// <summary>The first line containing the text (for writing tests by content, not line number).</summary>
        public int LineOf(string text) => _lines.First(l => l.GetText().Contains(text)).LineNumber;

        /// <summary>
        /// Replace text and describe the edit the way the editor does after a change, so code
        /// that inspects <see cref="TextContentChangedEventArgs"/> can be tested.
        /// </summary>
        public TextContentChangedEventArgs Edit(int position, int deleteLength, string insert) =>
            Edit(new TextEdit(position, deleteLength, insert));

        /// <summary>Replace the first occurrence of some text.</summary>
        public TextContentChangedEventArgs Edit(string find, string replaceWith)
        {
            find = Normalize(find);
            int position = _text.IndexOf(find, StringComparison.Ordinal);
            if (position < 0) throw new ArgumentException("Text not found: " + find);
            return Edit(position, find.Length, replaceWith);
        }

        /// <summary>
        /// Several changes in one edit (like typing with multiple carets). Positions are in this
        /// snapshot; the edits must be in order and not overlap.
        /// </summary>
        public TextContentChangedEventArgs Edit(params TextEdit[] edits)
        {
            var text = new StringBuilder();
            var changes = new List<ITextChange>();
            int copied = 0, delta = 0;
            foreach (var edit in edits)
            {
                if (edit.Position < copied) throw new ArgumentException("Edits must be in order and not overlap.");
                string insert = Normalize(edit.Insert);
                string removed = _text.Substring(edit.Position, edit.DeleteLength);
                text.Append(_text, copied, edit.Position - copied).Append(insert);
                changes.Add(new FakeChange(edit.Position, edit.Position + delta, removed, insert));
                delta += insert.Length - removed.Length;
                copied = edit.Position + edit.DeleteLength;
            }

            text.Append(_text, copied, _text.Length - copied);
            var after = new FakeSnapshot(text.ToString(), _buffer, _version.VersionNumber + 1);
            _version.SetChanges(changes, after._version);
            return new TextContentChangedEventArgs(this, after, EditOptions.None, null);
        }

        public IContentType ContentType => throw new NotSupportedException();
        public ITrackingPoint CreateTrackingPoint(int position, PointTrackingMode trackingMode) => throw new NotSupportedException();
        public ITrackingPoint CreateTrackingPoint(int position, PointTrackingMode trackingMode, TrackingFidelityMode trackingFidelity) => throw new NotSupportedException();
        public ITrackingSpan CreateTrackingSpan(Span span, SpanTrackingMode trackingMode) => throw new NotSupportedException();
        public ITrackingSpan CreateTrackingSpan(Span span, SpanTrackingMode trackingMode, TrackingFidelityMode trackingFidelity) => throw new NotSupportedException();
        public ITrackingSpan CreateTrackingSpan(int start, int length, SpanTrackingMode trackingMode) => throw new NotSupportedException();
        public ITrackingSpan CreateTrackingSpan(int start, int length, SpanTrackingMode trackingMode, TrackingFidelityMode trackingFidelity) => throw new NotSupportedException();

        private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");

        private sealed class FakeLine : ITextSnapshotLine
        {
            private readonly FakeSnapshot _snapshot;
            private readonly int _start, _length, _lineBreak;

            public FakeLine(FakeSnapshot snapshot, int number, int start, int length, int lineBreak)
            {
                _snapshot = snapshot;
                LineNumber = number;
                _start = start;
                _length = length;
                _lineBreak = lineBreak;
            }

            public int StartPosition => _start;
            public ITextSnapshot Snapshot => _snapshot;
            public int LineNumber { get; }
            public SnapshotPoint Start => new SnapshotPoint(_snapshot, _start);
            public SnapshotPoint End => new SnapshotPoint(_snapshot, _start + _length);
            public SnapshotPoint EndIncludingLineBreak => new SnapshotPoint(_snapshot, _start + _length + _lineBreak);
            public SnapshotSpan Extent => new SnapshotSpan(Start, End);
            public SnapshotSpan ExtentIncludingLineBreak => new SnapshotSpan(Start, EndIncludingLineBreak);
            public int Length => _length;
            public int LengthIncludingLineBreak => _length + _lineBreak;
            public int LineBreakLength => _lineBreak;
            public string GetText() => _snapshot._text.Substring(_start, _length);
            public string GetTextIncludingLineBreak() => _snapshot._text.Substring(_start, _length + _lineBreak);
            public string GetLineBreakText() => _snapshot._text.Substring(_start + _length, _lineBreak);
        }

        private sealed class FakeVersion : ITextVersion
        {
            private readonly FakeSnapshot _snapshot;
            private FakeChanges _changes;

            public FakeVersion(FakeSnapshot snapshot, int number)
            {
                _snapshot = snapshot;
                VersionNumber = number;
            }

            public ITextVersion Next { get; private set; }
            public int Length => _snapshot.Length;
            public INormalizedTextChangeCollection Changes => _changes;
            public int VersionNumber { get; }
            public int ReiteratedVersionNumber => VersionNumber;
            public ITextBuffer TextBuffer => _snapshot._buffer;

            /// <summary>
            /// Editing an older snapshot again replaces its "next" version, like a branch in history.
            /// The editor never does that, but tests may, and code following versions must cope.
            /// </summary>
            public void SetChanges(List<ITextChange> changes, ITextVersion next)
            {
                _changes = new FakeChanges(changes);
                Next = next;
            }

            public ITrackingPoint CreateTrackingPoint(int position, PointTrackingMode trackingMode) => throw new NotSupportedException();
            public ITrackingPoint CreateTrackingPoint(int position, PointTrackingMode trackingMode, TrackingFidelityMode trackingFidelity) => throw new NotSupportedException();
            public ITrackingSpan CreateTrackingSpan(Span span, SpanTrackingMode trackingMode) => throw new NotSupportedException();
            public ITrackingSpan CreateTrackingSpan(Span span, SpanTrackingMode trackingMode, TrackingFidelityMode trackingFidelity) => throw new NotSupportedException();
            public ITrackingSpan CreateTrackingSpan(int start, int length, SpanTrackingMode trackingMode) => throw new NotSupportedException();
            public ITrackingSpan CreateTrackingSpan(int start, int length, SpanTrackingMode trackingMode, TrackingFidelityMode trackingFidelity) => throw new NotSupportedException();
            public ITrackingSpan CreateCustomTrackingSpan(Span span, TrackingFidelityMode trackingFidelity, object customState, CustomTrackToVersion behavior) => throw new NotSupportedException();
        }

        private sealed class FakeChanges : List<ITextChange>, INormalizedTextChangeCollection
        {
            public FakeChanges(IEnumerable<ITextChange> changes)
                : base(changes)
            {
            }

            public bool IncludesLineChanges => true;
        }

        private sealed class FakeChange : ITextChange
        {
            public FakeChange(int oldPosition, int newPosition, string oldText, string newText)
            {
                OldPosition = oldPosition;
                NewPosition = newPosition;
                OldText = oldText;
                NewText = newText;
            }

            public Span OldSpan => new Span(OldPosition, OldLength);
            public Span NewSpan => new Span(NewPosition, NewLength);
            public int OldPosition { get; }
            public int NewPosition { get; }
            public int Delta => NewLength - OldLength;
            public int OldEnd => OldPosition + OldLength;
            public int NewEnd => NewPosition + NewLength;
            public string OldText { get; }
            public string NewText { get; }
            public int OldLength => OldText.Length;
            public int NewLength => NewText.Length;
            public int LineCountDelta => Count(NewText, '\n') - Count(OldText, '\n');

            private static int Count(string s, char c) => s.Count(x => x == c);
        }
    }

    /// <summary>One change in a <see cref="FakeSnapshot.Edit(TextEdit[])"/>.</summary>
    internal struct TextEdit
    {
        public TextEdit(int position, int deleteLength, string insert)
        {
            Position = position;
            DeleteLength = deleteLength;
            Insert = insert;
        }

        public int Position { get; }
        public int DeleteLength { get; }
        public string Insert { get; }
    }

    /// <summary>The buffer a <see cref="FakeSnapshot"/> and the snapshots edited from it belong to.</summary>
    internal sealed class FakeBuffer : ITextBuffer
    {
        public ITextSnapshot CurrentSnapshot { get; set; }
        public PropertyCollection Properties { get; } = new PropertyCollection();
        public IContentType ContentType => throw new NotSupportedException();
        public bool EditInProgress => false;

        public event EventHandler<SnapshotSpanEventArgs> ReadOnlyRegionsChanged { add { } remove { } }
        public event EventHandler<TextContentChangedEventArgs> Changed { add { } remove { } }
        public event EventHandler<TextContentChangedEventArgs> ChangedLowPriority { add { } remove { } }
        public event EventHandler<TextContentChangedEventArgs> ChangedHighPriority { add { } remove { } }
        public event EventHandler<TextContentChangingEventArgs> Changing { add { } remove { } }
        public event EventHandler PostChanged { add { } remove { } }
        public event EventHandler<ContentTypeChangedEventArgs> ContentTypeChanged { add { } remove { } }

        public void ChangeContentType(IContentType newContentType, object editTag) => throw new NotSupportedException();
        public bool CheckEditAccess() => true;
        public ITextEdit CreateEdit(EditOptions options, int? reiteratedVersionNumber, object editTag) => throw new NotSupportedException();
        public ITextEdit CreateEdit() => throw new NotSupportedException();
        public IReadOnlyRegionEdit CreateReadOnlyRegionEdit() => throw new NotSupportedException();
        public ITextSnapshot Delete(Span deleteSpan) => throw new NotSupportedException();
        public NormalizedSpanCollection GetReadOnlyExtents(Span span) => throw new NotSupportedException();
        public ITextSnapshot Insert(int position, string text) => throw new NotSupportedException();
        public bool IsReadOnly(int position) => false;
        public bool IsReadOnly(int position, bool isEdit) => false;
        public bool IsReadOnly(Span span) => false;
        public bool IsReadOnly(Span span, bool isEdit) => false;
        public ITextSnapshot Replace(Span replaceSpan, string replaceWith) => throw new NotSupportedException();
        public void TakeThreadOwnership() { }
    }
}
