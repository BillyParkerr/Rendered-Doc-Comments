using System.Linq;
using System.Text;
using Microsoft.VisualStudio.Text;
using RenderedDocComments.Parsing;
using RenderedDocComments.Tests.Fakes;
using Xunit;

namespace RenderedDocComments.Tests
{
    public class DocCommentScannerTests
    {
        private const string TwoMethods = @"public class C
{
    /// <summary>
    /// Adds.
    /// </summary>
    public void Add() { }

    /// <summary>Removes.</summary>
    public void Remove() { }
}";

        [Fact]
        public void Finds_each_run_of_triple_slash_lines()
        {
            var snapshot = new FakeSnapshot(TwoMethods);
            var blocks = DocCommentScanner.GetBlocks(snapshot);

            Assert.Equal(2, blocks.Count);
            Assert.Equal((2, 4), (blocks[0].StartLine, blocks[0].EndLine));
            Assert.Equal((7, 7), (blocks[1].StartLine, blocks[1].EndLine));
            Assert.True(blocks[1].IsSingleLine);
        }

        [Fact]
        public void Xml_drops_the_slashes_and_one_following_space()
        {
            var block = DocCommentScanner.GetBlocks(new FakeSnapshot(TwoMethods))[0];
            Assert.Equal("<summary>\nAdds.\n</summary>", block.Xml);

            var indented = DocCommentScanner.GetBlocks(new FakeSnapshot("///   two extra spaces\n///no space")).Single();
            Assert.Equal("  two extra spaces\nno space", indented.Xml);
        }

        [Fact]
        public void Span_runs_from_the_first_slash_to_the_end_of_the_last_line()
        {
            var snapshot = new FakeSnapshot(TwoMethods);
            var block = DocCommentScanner.GetBlocks(snapshot)[0];

            Assert.StartsWith("/// <summary>", block.Span.GetText());
            Assert.EndsWith("/// </summary>", block.Span.GetText());
            Assert.Equal("/// <summary>", block.FirstLineSpan.GetText());
            Assert.Equal("\r\n    /// Adds.\r\n    /// </summary>", block.RestOfBlockSpan.GetText());
        }

        [Theory]
        [InlineData("//// four slashes is an ordinary comment")]
        [InlineData("// two slashes")]
        [InlineData("x = 1; /// not at the start of the line")]
        [InlineData("/* /// */")]
        public void Ignores_lines_that_are_not_doc_comments(string line)
        {
            Assert.Empty(DocCommentScanner.GetBlocks(new FakeSnapshot(line)));
        }

        [Fact]
        public void Bare_triple_slash_is_a_doc_line()
        {
            var block = DocCommentScanner.GetBlocks(new FakeSnapshot("/// <summary>\n///\n/// </summary>")).Single();
            Assert.Equal(2, block.EndLine);
        }

        [Fact]
        public void A_blank_line_or_four_slash_line_splits_comments()
        {
            var text = "/// <summary>A</summary>\n\n/// <summary>B</summary>\n//// separator\n/// <summary>C</summary>";
            var blocks = DocCommentScanner.GetBlocks(new FakeSnapshot(text));
            Assert.Equal(new[] { "<summary>A</summary>", "<summary>B</summary>", "<summary>C</summary>" }, blocks.Select(b => b.Xml));
        }

        [Fact]
        public void Indent_counts_tabs_as_four_columns()
        {
            Assert.Equal(8, DocCommentScanner.GetBlocks(new FakeSnapshot("        /// spaces")).Single().IndentColumns);
            Assert.Equal(8, DocCommentScanner.GetBlocks(new FakeSnapshot("\t\t/// tabs")).Single().IndentColumns);
            Assert.Equal(4, DocCommentScanner.GetBlocks(new FakeSnapshot("  \t/// mixed")).Single().IndentColumns);
        }

        [Fact]
        public void Empty_and_comment_only_files()
        {
            Assert.Empty(DocCommentScanner.GetBlocks(new FakeSnapshot(string.Empty)));
            Assert.Single(DocCommentScanner.GetBlocks(new FakeSnapshot("///")));
        }

        [Fact]
        public void Caret_on_the_declaration_finds_the_comment_above()
        {
            var snapshot = new FakeSnapshot(TwoMethods);
            int declaration = snapshot.LineOf("public void Add()");

            Assert.Equal(2, DocCommentScanner.FindForCaretLine(snapshot, declaration).StartLine);
            Assert.Equal(2, DocCommentScanner.FindForCaretLine(snapshot, 3).StartLine);
            Assert.Null(DocCommentScanner.FindForCaretLine(snapshot, 0));
            Assert.Null(DocCommentScanner.FindForCaretLine(snapshot, declaration + 1));
        }

        [Fact]
        public void FindIntersecting_finds_the_comment_a_span_touches()
        {
            var snapshot = new FakeSnapshot(TwoMethods);
            var line = snapshot.GetLineFromLineNumber(snapshot.LineOf("Removes."));

            Assert.Equal(7, DocCommentScanner.FindIntersecting(snapshot, line.Extent).StartLine);
            Assert.Null(DocCommentScanner.FindIntersecting(snapshot, snapshot.GetLineFromLineNumber(0).Extent));
        }

        /// <summary>The binary-search lookups must agree with a plain walk over the comments on every line.</summary>
        [Fact]
        public void Indexed_lookups_match_a_linear_search_on_every_line()
        {
            var text = new StringBuilder();
            for (int i = 0; i < 300; i++)
            {
                if (i % 3 == 0) text.AppendLine("    /// <summary>One line.</summary>");
                if (i % 5 == 0) text.AppendLine("    /// <summary>\n    /// Several\n    /// lines.\n    /// </summary>");
                if (i % 7 == 0) text.AppendLine();
                text.AppendLine("    public void M" + i + "() { }");
            }

            var snapshot = new FakeSnapshot(text.ToString());
            var blocks = DocCommentScanner.GetBlocks(snapshot);
            Assert.True(blocks.Count > 100);

            for (int line = -1; line <= snapshot.LineCount; line++)
            {
                var at = blocks.FirstOrDefault(b => b.StartLine <= line && line <= b.EndLine);
                Assert.Same(at, DocCommentScanner.BlockAtLine(snapshot, line));
                Assert.Same(blocks.FirstOrDefault(b => b.StartLine == line), DocCommentScanner.BlockStartingAt(snapshot, line));
                Assert.Same(blocks.FirstOrDefault(b => b.EndLine == line), DocCommentScanner.BlockEndingAt(snapshot, line));

                for (int last = line; last <= line + 3; last++)
                {
                    var inLines = blocks.Where(b => b.StartLine <= last && b.EndLine >= line).ToList();
                    Assert.Equal(inLines.Count > 0, DocCommentScanner.AnyBlockInLines(snapshot, line, last));
                    Assert.Equal(inLines, DocCommentScanner.BlocksInLines(snapshot, line, last));
                }
            }
        }

        [Fact]
        public void Results_are_cached_per_snapshot()
        {
            var snapshot = new FakeSnapshot(TwoMethods);
            Assert.Same(DocCommentScanner.GetBlocks(snapshot), DocCommentScanner.GetBlocks(snapshot));
        }

        // TouchesComments decides whether an edit needs any comment work at all (tagging, card
        // layout, remembering). A wrong "no" would leave stale cards, so the edge cases matter.

        [Fact]
        public void Typing_in_code_away_from_comments_does_not_touch_them()
        {
            var snapshot = new FakeSnapshot(TwoMethods + "\n\nint x;\nint y;");
            Assert.False(DocCommentScanner.TouchesComments(snapshot.Edit("int y;", "int yz;")));
            Assert.False(DocCommentScanner.TouchesComments(snapshot.Edit("public class C", "public sealed class C")));
        }

        [Fact]
        public void Typing_inside_a_comment_touches_it()
        {
            var snapshot = new FakeSnapshot(TwoMethods);
            Assert.True(DocCommentScanner.TouchesComments(snapshot.Edit("Adds.", "Adds two numbers.")));
            Assert.True(DocCommentScanner.TouchesComments(snapshot.Edit("<summary>Removes.", "<summary>Deletes.")));
        }

        [Fact]
        public void Editing_the_line_next_to_a_comment_touches_it()
        {
            // The declaration below decides the comment's key; the line above can join comments.
            var snapshot = new FakeSnapshot(TwoMethods);
            Assert.True(DocCommentScanner.TouchesComments(snapshot.Edit("public void Add()", "public void AddAll()")));
            Assert.True(DocCommentScanner.TouchesComments(snapshot.Edit("{\n    /// <summary>\n", "{ \n    /// <summary>\n")));
        }

        [Fact]
        public void Adding_or_removing_a_comment_touches_comments()
        {
            var snapshot = new FakeSnapshot(TwoMethods + "\n\nint x;\n\nint y;");
            Assert.True(DocCommentScanner.TouchesComments(snapshot.Edit("int y;", "/// <summary>New.</summary>\nint y;")));
            Assert.True(DocCommentScanner.TouchesComments(snapshot.Edit("    /// <summary>Removes.</summary>\n", string.Empty)));
        }

        [Fact]
        public void Turning_a_comment_into_four_slashes_touches_comments()
        {
            var snapshot = new FakeSnapshot(TwoMethods);
            Assert.True(DocCommentScanner.TouchesComments(snapshot.Edit("/// <summary>Removes.", "//// <summary>Removes.")));
        }

        [Fact]
        public void Pasting_many_lines_far_away_does_not_touch_comments()
        {
            var snapshot = new FakeSnapshot(TwoMethods + "\n\n\nint x;");
            var e = snapshot.Edit("int x;", "int x;\nint a;\nint b;\nint c;");
            Assert.False(DocCommentScanner.TouchesComments(e));
            Assert.Equal(DocCommentScanner.GetBlocks(e.Before).Count, DocCommentScanner.GetBlocks(e.After).Count);
        }

        // EditKeepsCommentShapes lets ordinary typing in a comment skip fold and card updates. A
        // wrong "yes" would leave a fold that no longer matches its comment, so every edit that
        // changes a comment's lines, or reaches a fold's edge, must say no.

        private const string ThreeComments = @"public class C
{
    /// <summary>
    /// Adds the numbers.
    /// </summary>
    public void Add() { }

    /// <summary>Removes.</summary>
    public void Remove() { }
}";

        /// <summary>
        /// One keystroke-sized change: at the first occurrence of <paramref name="at"/>, skip
        /// <paramref name="skip"/> characters, delete <paramref name="delete"/> (-1: all of
        /// <paramref name="at"/>) and insert the text.
        /// </summary>
        private static Microsoft.VisualStudio.Text.TextContentChangedEventArgs Keystroke(string text, string at, int skip, int delete, string insert)
        {
            var snapshot = new FakeSnapshot(text);
            string find = at.Replace("\n", "\r\n");
            int position = snapshot.GetText().IndexOf(find, System.StringComparison.Ordinal);
            Assert.True(position >= 0, "Not found: " + at);
            return snapshot.Edit(position + skip, delete < 0 ? find.Length : delete, insert);
        }

        [Theory]
        [InlineData("Adds the", 5, 0, "all ")]                  // Typing in a middle line.
        [InlineData("Adds the", 8, 0, "\n    /// ")]            // Enter in a middle line (VS adds the ///).
        [InlineData("Adds the", 8, 1, "")]                      // Deleting in a middle line.
        [InlineData("<summary>\n    /// Adds", 8, 0, " ")]      // Inside the first line, before its end.
        [InlineData("<summary>Removes.", 9, 0, "x")]            // Single-line comment.
        [InlineData("Removes.</summary>", 18, 0, " ")]          // End of a single-line comment (it has no fold).
        public void Typing_inside_a_comment_keeps_its_shape(string at, int skip, int delete, string insert)
        {
            Assert.True(DocCommentScanner.EditKeepsCommentShapes(Keystroke(ThreeComments, at, skip, delete, insert)));
        }

        [Fact]
        public void Joining_two_lines_of_a_comment_keeps_its_shape()
        {
            string text = ThreeComments.Replace("Adds the numbers.", "Adds the\n    /// numbers.");
            int lineBreakAndSlashes = "\r\n    /// ".Length;
            Assert.True(DocCommentScanner.EditKeepsCommentShapes(Keystroke(text, "Adds the\n    /// numbers", 8, lineBreakAndSlashes, " ")));
        }

        [Theory]
        [InlineData("    /// </summary>\n    public void Add", 18, 0, " ")]   // End of the last line: the fold's end.
        [InlineData("    /// <summary>\n    /// Adds", 17, 0, " ")]           // End of the first line: the fold's start.
        [InlineData("/// Adds", 0, 0, "/")]                                   // "////" splits the comment.
        [InlineData("/// <summary>\n    /// Adds", 0, 1, "")]                 // The first line stops being a doc line.
        [InlineData("Removes.</summary>", 18, 0, "\n    /// More.")]         // Single-line becomes multi-line.
        [InlineData("public void Add()", 15, 0, "2")]                        // Not in a comment.
        [InlineData("    public void Add() { }\n\n", 0, -1, "")]             // Joins two comments.
        public void Edits_that_change_a_comment_shape_are_spotted(string at, int skip, int delete, string insert)
        {
            Assert.False(DocCommentScanner.EditKeepsCommentShapes(Keystroke(ThreeComments, at, skip, delete, insert)));
        }

        [Fact]
        public void Several_changes_at_once_are_never_assumed_harmless()
        {
            var snapshot = new FakeSnapshot(ThreeComments);
            string text = snapshot.GetText();
            int a = text.IndexOf("Adds", System.StringComparison.Ordinal);
            int b = text.IndexOf("Removes", System.StringComparison.Ordinal);
            var e = snapshot.Edit(new TextEdit(a, 0, "x"), new TextEdit(b, 0, "y"));
            Assert.False(DocCommentScanner.EditKeepsCommentShapes(e));
        }

        [Fact]
        public void Edit_at_the_very_end_of_the_file()
        {
            var snapshot = new FakeSnapshot("/// <summary>Last.</summary>");
            Assert.True(DocCommentScanner.TouchesComments(snapshot.Edit(snapshot.Length, 0, "\npublic int X;")));
        }
    }
}
