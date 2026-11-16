using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RenderedDocComments.Parsing;
using RenderedDocComments.Tests.Fakes;
using Xunit;

namespace RenderedDocComments.Tests
{
    /// <summary>
    /// Each keystroke's comments are worked out from the previous snapshot's, reading only the
    /// lines around the edit. These tests hold that to the same answer as reading the whole
    /// file, and check it really does read only a few lines.
    /// </summary>
    public class IncrementalScanTests
    {
        private static readonly string[] LinePool =
        {
            "    /// <summary>",
            "    /// Some text with <see cref=\"Foo\"/>.",
            "    /// </summary>",
            "///",
            "/// at column zero",
            "\t/// tab indented",
            "        /// deeper",
            "    //// four slashes",
            "    // ordinary comment",
            "    public void Method() { }",
            "    {",
            "    }",
            string.Empty,
            "x = a / b; /// not a doc comment",
        };

        private static readonly string[] Snippets =
        {
            "/", "//", "///", "////", " ", "\t", "x", "\n", "\n\n", "\n    /// ", "/// <summary>New.</summary>\n",
            "    /// a\n    /// b\n", "// ", "<c>code</c>", "public int X;\n", "\n///",
        };

        private static void AssertSameBlocks(IReadOnlyList<DocCommentBlock> expected, IReadOnlyList<DocCommentBlock> actual, string context)
        {
            string Describe(DocCommentBlock b) =>
                $"lines {b.StartLine}-{b.EndLine} at {b.Span.Start.Position}+{b.Span.Length} indent {b.IndentColumns}: {b.Xml.Replace("\n", "\\n")}";

            var want = expected.Select(Describe).ToList();
            var got = actual.Select(Describe).ToList();
            if (!want.SequenceEqual(got))
            {
                Assert.Fail(context + "\nExpected:\n  " + string.Join("\n  ", want) + "\nActual:\n  " + string.Join("\n  ", got));
            }

            Assert.All(actual, b => Assert.Same(expected.Count > 0 ? expected[0].Snapshot : b.Snapshot, b.Snapshot));
        }

        private static string RandomFile(Random random, int lines)
        {
            var text = new StringBuilder();
            for (int i = 0; i < lines; i++)
            {
                if (i > 0) text.Append('\n');
                text.Append(LinePool[random.Next(LinePool.Length)]);
            }

            return text.ToString();
        }

        /// <summary>A position that doesn't split a "\r\n" pair (the fake only knows "\r\n" line breaks).</summary>
        private static int SafePosition(string text, int position)
        {
            position = Math.Max(0, Math.Min(position, text.Length));
            return position > 0 && position < text.Length && text[position - 1] == '\r' && text[position] == '\n'
                ? position + 1
                : position;
        }

        private static TextEdit RandomChange(Random random, string text, int from, int to)
        {
            int position = SafePosition(text, random.Next(from, to + 1));
            int end = SafePosition(text, position + (random.Next(3) == 0 ? 0 : random.Next(1, 30)));
            if (end > to) end = Math.Max(position, SafePosition(text, to) == to ? to : position);
            string insert = random.Next(3) == 0 ? string.Empty : Snippets[random.Next(Snippets.Length)];
            return new TextEdit(position, end - position, insert);
        }

        private static FakeSnapshot RandomEdit(Random random, FakeSnapshot snapshot)
        {
            string text = snapshot.GetText();
            int kind = random.Next(10);
            if (kind < 6)
            {
                return (FakeSnapshot)snapshot.Edit(RandomChange(random, text, 0, text.Length)).After;
            }

            if (kind < 9)
            {
                // Several changes at once, like typing with multiple carets or a replace-all.
                int count = random.Next(2, 5);
                var edits = new List<TextEdit>();
                int from = 0;
                for (int i = 0; i < count && from < text.Length; i++)
                {
                    int to = Math.Min(text.Length, from + Math.Max(1, (text.Length - from) / (count - i)));
                    var change = RandomChange(random, text, from, to);
                    edits.Add(change);
                    from = change.Position + change.DeleteLength + 1;
                }

                return (FakeSnapshot)snapshot.Edit(edits.ToArray()).After;
            }

            // A big cut or paste.
            int start = SafePosition(text, random.Next(text.Length + 1));
            int length = SafePosition(text, start + random.Next(text.Length - start + 1)) - start;
            return (FakeSnapshot)snapshot.Edit(start, length, random.Next(2) == 0 ? string.Empty : RandomFile(random, 20)).After;
        }

        [Fact]
        public void Matches_a_whole_file_scan_after_random_edits()
        {
            var random = new Random(20261116);
            int scans = 0, incremental = 0;

            for (int file = 0; file < 40; file++)
            {
                // Some tiny files for the edge cases at the start and end; mostly bigger ones, where
                // an edit covers a small part of the file and the incremental path is taken.
                int lines = file % 4 == 0 ? random.Next(1, 12) : random.Next(60, 250);
                var snapshot = new FakeSnapshot(RandomFile(random, lines));
                AssertSameBlocks(DocCommentScanner.ScanWholeFile(snapshot), DocCommentScanner.GetBlocks(snapshot), "Initial file");

                for (int round = 0; round < 150; round++)
                {
                    // Usually one edit per scan (a keystroke); sometimes several before anyone asks.
                    int edits = random.Next(4) == 0 ? random.Next(2, 5) : 1;
                    string before = snapshot.GetText();
                    for (int i = 0; i < edits; i++)
                    {
                        snapshot = RandomEdit(random, snapshot);
                    }

                    var actual = DocCommentScanner.GetBlocks(snapshot);
                    scans++;
                    if (DocCommentScanner.LastScanWasIncremental) incremental++;

                    AssertSameBlocks(
                        DocCommentScanner.ScanWholeFile(snapshot),
                        actual,
                        $"File {file}, round {round}.\nBefore:\n{before}\nAfter:\n{snapshot.GetText()}");
                }
            }

            // The point is to test the incremental path, so most scans must have used it.
            Assert.True(incremental > scans * 3 / 4, $"Only {incremental} of {scans} scans were incremental.");
        }

        private static string LargeFile(int methods)
        {
            var text = new StringBuilder();
            for (int i = 0; i < methods; i++)
            {
                text.Append("    /// <summary>\n    /// Method ").Append(i).Append(".\n    /// </summary>\n");
                text.Append("    public void Method").Append(i).Append("()\n    {\n        var x = ").Append(i).Append(";\n    }\n\n");
            }

            return text.ToString();
        }

        [Fact]
        public void A_keystroke_in_code_reads_only_a_few_lines()
        {
            var snapshot = new FakeSnapshot(LargeFile(250)); // 2,000 lines
            DocCommentScanner.GetBlocks(snapshot);
            Assert.False(DocCommentScanner.LastScanWasIncremental);
            Assert.Equal(snapshot.LineCount, DocCommentScanner.LastScanLinesRead);

            var after = snapshot.Edit("var x = 120;", "var xy = 120;").After;
            var blocks = DocCommentScanner.GetBlocks(after);

            Assert.True(DocCommentScanner.LastScanWasIncremental);
            Assert.True(DocCommentScanner.LastScanLinesRead <= 5, DocCommentScanner.LastScanLinesRead + " lines read");
            AssertSameBlocks(DocCommentScanner.ScanWholeFile(after), blocks, "After typing");
        }

        [Fact]
        public void A_keystroke_in_a_comment_reads_about_that_comment()
        {
            var snapshot = new FakeSnapshot(LargeFile(250));
            DocCommentScanner.GetBlocks(snapshot);

            var after = snapshot.Edit("Method 120.", "Method 120 does things.").After;
            var blocks = DocCommentScanner.GetBlocks(after);

            Assert.True(DocCommentScanner.LastScanWasIncremental);
            Assert.True(DocCommentScanner.LastScanLinesRead <= 12, DocCommentScanner.LastScanLinesRead + " lines read");
            Assert.Contains(blocks, b => b.Xml == "<summary>\nMethod 120 does things.\n</summary>");
            AssertSameBlocks(DocCommentScanner.ScanWholeFile(after), blocks, "After typing in a comment");
        }

        [Fact]
        public void Pressing_enter_moves_every_comment_below()
        {
            var snapshot = new FakeSnapshot(LargeFile(50));
            var before = DocCommentScanner.GetBlocks(snapshot);

            var after = snapshot.Edit("var x = 10;", "var x = 10;\n\n").After;
            var blocks = DocCommentScanner.GetBlocks(after);

            Assert.True(DocCommentScanner.LastScanWasIncremental);
            Assert.Equal(before[10].StartLine, blocks[10].StartLine);
            Assert.Equal(before[11].StartLine + 2, blocks[11].StartLine);
            Assert.Equal(before[49].StartLine + 2, blocks[49].StartLine);
            AssertSameBlocks(DocCommentScanner.ScanWholeFile(after), blocks, "After Enter");
        }

        [Fact]
        public void Several_edits_between_scans()
        {
            var snapshot = new FakeSnapshot(LargeFile(50));
            DocCommentScanner.GetBlocks(snapshot);

            var s1 = (FakeSnapshot)snapshot.Edit("Method 5.", "Method five.").After;
            var s2 = (FakeSnapshot)s1.Edit("var x = 30;", "var x = 30;\n    /// <summary>Inserted.</summary>").After;
            var s3 = (FakeSnapshot)s2.Edit("    /// <summary>\n    /// Method 40.", "    /// <summary>\n\n    /// Method 40.").After;
            var blocks = DocCommentScanner.GetBlocks(s3);

            Assert.True(DocCommentScanner.LastScanWasIncremental);
            AssertSameBlocks(DocCommentScanner.ScanWholeFile(s3), blocks, "After three edits");
        }

        [Fact]
        public void Joining_two_comments_gives_one()
        {
            var snapshot = new FakeSnapshot(LargeFile(10) + "/// <summary>A</summary>\n\n/// <remarks>B</remarks>\npublic void M() { }\n" + LargeFile(10));
            Assert.Equal(22, DocCommentScanner.GetBlocks(snapshot).Count);

            var after = snapshot.Edit("</summary>\n\n", "</summary>\n").After;
            var blocks = DocCommentScanner.GetBlocks(after);

            Assert.True(DocCommentScanner.LastScanWasIncremental);
            Assert.Equal(21, blocks.Count);
            Assert.Equal("<summary>A</summary>\n<remarks>B</remarks>", blocks[10].Xml);
            AssertSameBlocks(DocCommentScanner.ScanWholeFile(after), blocks, "After joining");
        }

        [Fact]
        public void Asking_for_an_older_snapshot_reads_it_whole()
        {
            var s0 = new FakeSnapshot(LargeFile(20));
            var s1 = (FakeSnapshot)s0.Edit("Method 3.", "Method three.").After;
            var s2 = (FakeSnapshot)s1.Edit("Method 4.", "Method four.").After;

            DocCommentScanner.GetBlocks(s2);
            var older = DocCommentScanner.GetBlocks(s1);
            Assert.False(DocCommentScanner.LastScanWasIncremental);
            AssertSameBlocks(DocCommentScanner.ScanWholeFile(s1), older, "Older snapshot");

            // The newest snapshot is still the starting point for the next one.
            var s3 = s2.Edit("Method 5.", "Method five.").After;
            DocCommentScanner.GetBlocks(s3);
            Assert.True(DocCommentScanner.LastScanWasIncremental);
        }

        [Fact]
        public void Pasting_more_than_the_file_reads_the_whole_file()
        {
            var snapshot = new FakeSnapshot(LargeFile(5));
            DocCommentScanner.GetBlocks(snapshot);

            var after = snapshot.Edit("var x = 2;", "var x = 2;\n" + LargeFile(50)).After;
            var blocks = DocCommentScanner.GetBlocks(after);

            Assert.False(DocCommentScanner.LastScanWasIncremental);
            AssertSameBlocks(DocCommentScanner.ScanWholeFile(after), blocks, "After a big paste");
        }

        [Fact]
        public void Deleting_most_of_the_file_carries_over_the_rest()
        {
            var snapshot = new FakeSnapshot(LargeFile(50));
            DocCommentScanner.GetBlocks(snapshot);

            string text = snapshot.GetText();
            int from = text.IndexOf("Method 5.", StringComparison.Ordinal);
            int to = text.IndexOf("Method 45.", StringComparison.Ordinal);
            var after = snapshot.Edit(from, to - from, string.Empty).After;
            var blocks = DocCommentScanner.GetBlocks(after);

            Assert.True(DocCommentScanner.LastScanWasIncremental);
            Assert.Equal(10, blocks.Count);
            AssertSameBlocks(DocCommentScanner.ScanWholeFile(after), blocks, "After deleting most of the file");
        }

        [Fact]
        public void Very_deeply_indented_comments_are_found()
        {
            string indent = new string(' ', 100);
            var snapshot = new FakeSnapshot(indent + "/// <summary>Deep.</summary>\n" + indent + "//// not a doc comment\n" + indent + "///");
            var blocks = DocCommentScanner.GetBlocks(snapshot);

            Assert.Equal(2, blocks.Count);
            Assert.Equal(100, blocks[0].IndentColumns);
            Assert.Equal("<summary>Deep.</summary>", blocks[0].Xml);
        }
    }
}
