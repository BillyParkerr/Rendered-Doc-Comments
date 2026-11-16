using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using RenderedDocComments.Navigation;
using RenderedDocComments.Parsing;
using RenderedDocComments.Rendering;
using RenderedDocComments.Tests.Fakes;
using Xunit;

namespace RenderedDocComments.Tests
{
    /// <summary>
    /// How each doc-comment tag, and the common HTML ones, comes out on a card: the blocks,
    /// formatting, colours and text of the card's document.
    /// </summary>
    public class CardRenderingTests
    {
        private const double CardWidth = 600;

        /// <summary>The card's width for text: less the padding (12 each side) and border (1 each side).</summary>
        private const double TextWidth = CardWidth - 24 - 2;

        private static DocTheme Theme => DocTheme.Current;

        /// <summary>The card's document for a comment, given as what follows "/// " on each line.</summary>
        private static FlowDocument Card(string xml, InheritedDoc inherited = null)
        {
            string source = string.Join("\n", xml.Replace("\r\n", "\n").Split('\n').Select(line => "/// " + line)) + "\nvoid M() { }";
            var block = DocCommentScanner.GetBlocks(new FakeSnapshot(source)).Single();
            var card = (Border)DocCommentRenderer.CreateCard(block, CardWidth, null, inherited);
            return ((Grid)card.Child).Children.OfType<RichTextBox>().Single().Document;
        }

        private static string TextOf(TextElement element) => new TextRange(element.ContentStart, element.ContentEnd).Text;

        private static string TextOf(FlowDocument document) => new TextRange(document.ContentStart, document.ContentEnd).Text.Trim();

        /// <summary>A list item's text without its marker ("•" or "1." and a tab, as copying gives it).</summary>
        private static string ItemText(ListItem item)
        {
            string text = TextOf(item).Trim();
            int tab = text.IndexOf('\t');
            return tab >= 0 ? text.Substring(tab + 1) : text;
        }

        /// <summary>What follows a web link's text: a thin space and an arrow.</summary>
        private const string Arrow = "\u2009↗";

        /// <summary>Every element of a type in the document, depth first.</summary>
        private static IEnumerable<T> All<T>(DependencyObject root)
            where T : DependencyObject
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (child is T match) yield return match;
                foreach (var nested in All<T>(child)) yield return nested;
            }
        }

        private static Run RunWith(FlowDocument document, string text) => All<Run>(document).First(r => r.Text.Contains(text));

        /// <summary>Code blocks: the only paragraphs with a tag.</summary>
        private static List<Paragraph> CodeBlocks(FlowDocument document) => All<Paragraph>(document).Where(p => p.Tag != null).ToList();

        /// <summary>The section table (the labelled rows), or null; table lists are tagged and it isn't.</summary>
        private static Table SectionTable(FlowDocument document) => document.Blocks.OfType<Table>().SingleOrDefault(t => t.Tag == null);

        /// <summary>Each section row's label and content text.</summary>
        private static List<KeyValuePair<string, string>> Rows(FlowDocument document)
        {
            var table = SectionTable(document);
            if (table == null) return new List<KeyValuePair<string, string>>();
            return table.RowGroups[0].Rows
                .Select(r => new KeyValuePair<string, string>(TextOf(r.Cells[0]).Trim(), TextOf(r.Cells[1]).Trim()))
                .ToList();
        }

        private static string Row(FlowDocument document, string label) =>
            Rows(document).Where(r => r.Key == label).Select(r => r.Value).SingleOrDefault();

        // --- Inline formatting -----------------------------------------------------------------

        [Theory]
        [InlineData("b", typeof(Bold))]
        [InlineData("strong", typeof(Bold))]
        [InlineData("i", typeof(Italic))]
        [InlineData("em", typeof(Italic))]
        [InlineData("u", typeof(Underline))]
        public void Bold_italic_and_underline(string tag, Type expected) => Sta.Run(() =>
        {
            var document = Card($"<summary>A <{tag}>word</{tag}> here.</summary>");
            Assert.IsType(expected, All<Span>(document).Single(s => TextOf(s) == "word"));
            Assert.Equal("A word here.", TextOf(document));
        });

        [Theory]
        [InlineData("s")]
        [InlineData("strike")]
        [InlineData("del")]
        public void Strikethrough(string tag) => Sta.Run(() =>
        {
            var document = Card($"<summary>Was <{tag}>old</{tag}> new.</summary>");
            var span = All<Span>(document).Single(s => TextOf(s) == "old");
            Assert.Contains(span.TextDecorations, d => d.Location == TextDecorationLocation.Strikethrough);
            Assert.Equal("Was old new.", TextOf(document));
        });

        [Theory]
        [InlineData("sub", BaselineAlignment.Subscript)]
        [InlineData("sup", BaselineAlignment.TextTop)] // Not Superscript, which gets clipped by the fixed line height.
        public void Subscript_and_superscript_are_smaller_and_moved(string tag, BaselineAlignment expected) => Sta.Run(() =>
        {
            var document = Card($"<summary>x<{tag}>2</{tag}> and more.</summary>");
            var span = All<Span>(document).Single(s => TextOf(s) == "2");
            Assert.Equal(expected, span.BaselineAlignment);
            Assert.True(span.FontSize < document.FontSize);
            Assert.Equal("x2 and more.", TextOf(document));
        });

        [Theory]
        [InlineData("<br/>")]
        [InlineData("<br>")]
        [InlineData("<br />")]
        public void Line_break_starts_a_new_line_in_the_same_paragraph(string br) => Sta.Run(() =>
        {
            var document = Card($"<summary>First line.{br}Second line.</summary>");
            var paragraph = All<Paragraph>(document).Single();
            Assert.Single(All<LineBreak>(paragraph));
            Assert.Equal(new[] { "First line.", "Second line." }, TextOf(document).Split(new[] { "\r\n" }, StringSplitOptions.None));
        });

        [Theory]
        [InlineData("c")]
        [InlineData("tt")]
        public void Inline_code_gets_the_code_font_and_a_background(string tag) => Sta.Run(() =>
        {
            var document = Card($"<summary>Call <{tag}>Run()</{tag}> first.</summary>");
            var span = All<Span>(document).Single(s => s.Background != null);
            Assert.Same(Theme.InlineCodeBackground, span.Background);
            Assert.Equal(Theme.CodeFont, span.FontFamily);
            Assert.Equal(" Run() ", TextOf(span));
            Assert.Same(Theme.MethodName, RunWith(document, "Run").Foreground);
        });

        [Fact]
        public void Span_keeps_its_text() => Sta.Run(() =>
        {
            Assert.Equal("Plain text here.", TextOf(Card("<summary>Plain <span class=\"x\">text</span> here.</summary>")));
        });

        // --- Links and references --------------------------------------------------------------

        [Theory]
        [InlineData("<see href=\"https://example.com\">the site</see>", "the site" + Arrow)]
        [InlineData("<a href=\"https://example.com\">the site</a>", "the site" + Arrow)]
        [InlineData("<see href=\"https://example.com\"/>", "https://example.com" + Arrow)]
        [InlineData("<see cref=\"System.String\">a string</see>", "a string")]
        [InlineData("<see cref=\"List{T}\"/>", "List<T>")]
        [InlineData("<see cref=\"M:System.String.Format(System.String)\"/>", "String.Format()")]
        public void Links(string xml, string text) => Sta.Run(() =>
        {
            var document = Card($"<summary>See {xml}.</summary>");
            Assert.Equal(text, TextOf(All<Hyperlink>(document).Single()));
        });

        [Fact]
        public void An_anchor_without_href_is_plain_text() => Sta.Run(() =>
        {
            var document = Card("<summary>An <a name=\"x\">anchor</a>.</summary>");
            Assert.Empty(All<Hyperlink>(document));
            Assert.Equal("An anchor.", TextOf(document));
        });

        [Fact]
        public void Langword_paramref_and_typeparamref_are_coloured_code() => Sta.Run(() =>
        {
            var document = Card("<summary>Returns <see langword=\"null\"/> if <paramref name=\"value\"/> isn't a <typeparamref name=\"T\"/>.</summary>");
            Assert.Same(Theme.Keyword, RunWith(document, "null").Foreground);
            Assert.Same(Theme.Parameter, RunWith(document, "value").Foreground);
            Assert.Same(Theme.TypeName, RunWith(document, "T").Foreground);
            Assert.Equal(Theme.CodeFont, RunWith(document, "value").FontFamily);
            Assert.Equal("Returns null if value isn't a T.", TextOf(document));
        });

        // --- Blocks ----------------------------------------------------------------------------

        [Theory]
        [InlineData("para")]
        [InlineData("p")]
        public void Paragraphs_are_separate_blocks_with_a_gap(string tag) => Sta.Run(() =>
        {
            var document = Card($"<summary>One.<{tag}>Two.</{tag}><{tag}>Three.</{tag}></summary>");
            var blocks = document.Blocks.ToList();
            Assert.Equal(3, blocks.Count);
            Assert.IsType<Section>(blocks[1]);
            Assert.True(blocks[1].Margin.Top > 0 && blocks[2].Margin.Top > 0);
            Assert.Equal("One.\r\nTwo.\r\nThree.", TextOf(document));
        });

        [Theory]
        [InlineData("h1")]
        [InlineData("h2")]
        [InlineData("h3")]
        [InlineData("h4")]
        [InlineData("h5")]
        [InlineData("h6")]
        public void Headings_are_semibold_paragraphs(string tag) => Sta.Run(() =>
        {
            var document = Card($"<summary>Intro.<{tag}>Title</{tag}>Body.</summary>");
            var heading = All<Paragraph>(document).Single(p => TextOf(p) == "Title");
            Assert.Equal(FontWeights.SemiBold, heading.FontWeight);
            Assert.Equal("Intro.\r\nTitle\r\nBody.", TextOf(document));
        });

        [Fact]
        public void Code_block_is_csharp_by_default() => Sta.Run(() =>
        {
            var document = Card("<example>\n<code>\nif (ready)\n    return true;\n</code>\n</example>");
            var code = CodeBlocks(document).Single();
            Assert.Equal(Theme.CodeFont, code.FontFamily);
            Assert.Same(Theme.ControlKeyword, RunWith(document, "return").Foreground);
            Assert.Same(Theme.Keyword, RunWith(document, "true").Foreground);
            Assert.Equal("if (ready)\r\n    return true;", TextOf(code).TrimEnd()); // Dedented, line breaks kept.
        });

        [Theory]
        [InlineData("language")]
        [InlineData("lang")]
        public void Code_block_in_xml_colours_tags_and_attributes(string attribute) => Sta.Run(() =>
        {
            var document = Card($"<example><code {attribute}=\"xml\">&lt;item key=\"a\"&gt;text&lt;/item&gt;</code></example>");
            Assert.Equal("<item key=\"a\">text</item>", TextOf(CodeBlocks(document).Single()).TrimEnd());
            Assert.Same(Theme.Keyword, RunWith(document, "item").Foreground);
            Assert.Same(Theme.Parameter, RunWith(document, "key").Foreground);
            Assert.Same(Theme.StringLiteral, RunWith(document, "\"a\"").Foreground);
        });

        [Fact]
        public void Code_block_in_an_unknown_language_is_plain() => Sta.Run(() =>
        {
            var document = Card("<example><code language=\"sql\">SELECT 'x' FROM t WHERE id = 1</code></example>");
            var code = CodeBlocks(document).Single();
            Assert.All(All<Run>(code), r => Assert.Same(Theme.PlainText, r.Foreground));
        });

        [Fact]
        public void Pre_is_plain_unless_it_names_a_language() => Sta.Run(() =>
        {
            var plain = Card("<summary><pre>return true;</pre></summary>");
            Assert.All(All<Run>(CodeBlocks(plain).Single()), r => Assert.Same(Theme.PlainText, r.Foreground));

            var csharp = Card("<summary><pre lang=\"cs\">return true;</pre></summary>");
            Assert.Same(Theme.ControlKeyword, RunWith(csharp, "return").Foreground);
        });

        [Fact]
        public void Text_after_a_code_block_is_a_new_paragraph_with_a_gap() => Sta.Run(() =>
        {
            var document = Card("<summary>Before.<code>x = 1;</code>After.</summary>");
            var blocks = document.Blocks.ToList();
            Assert.Equal(3, blocks.Count);
            Assert.NotNull(blocks[1].Tag);
            Assert.True(blocks[2].Margin.Top > 0);
        });

        // --- Lists -----------------------------------------------------------------------------

        [Theory]
        [InlineData("<list type=\"bullet\"><item><description>One</description></item><item><description>Two</description></item></list>", TextMarkerStyle.Disc)]
        [InlineData("<list><item><description>One</description></item><item><description>Two</description></item></list>", TextMarkerStyle.Disc)]
        [InlineData("<list type=\"number\"><item><description>One</description></item><item><description>Two</description></item></list>", TextMarkerStyle.Decimal)]
        [InlineData("<list type=\"Number\"><item>One</item><item>Two</item></list>", TextMarkerStyle.Decimal)]
        [InlineData("<ul><li>One</li><li>Two</li></ul>", TextMarkerStyle.Disc)]
        [InlineData("<ol><li>One</li><li>Two</li></ol>", TextMarkerStyle.Decimal)]
        public void Bullet_and_numbered_lists(string xml, TextMarkerStyle marker) => Sta.Run(() =>
        {
            var list = All<List>(Card("<summary>" + xml + "</summary>")).Single();
            Assert.Equal(marker, list.MarkerStyle);
            Assert.Equal(new[] { "One", "Two" }, list.ListItems.Select(ItemText));
        });

        [Fact]
        public void List_item_with_a_term_shows_it_bold_before_the_description() => Sta.Run(() =>
        {
            var document = Card("<summary><list type=\"bullet\"><item><term>Fast</term><description>Quick to build.</description></item></list></summary>");
            var item = All<List>(document).Single().ListItems.Single();
            Assert.Equal("Fast – Quick to build.", ItemText(item));
            Assert.Equal(new[] { "Fast" }, All<Bold>(document).Select(b => string.Concat(All<Run>(b).Select(r => r.Text))));
        });

        [Fact]
        public void Lists_nest() => Sta.Run(() =>
        {
            var document = Card(@"<summary><list type=""bullet"">
<item><description>Outer<list type=""number""><item><description>Inner</description></item></list></description></item>
</list></summary>");
            var outer = document.Blocks.OfType<List>().Single();
            var inner = All<List>(outer.ListItems.Single()).Single();
            Assert.Equal(TextMarkerStyle.Disc, outer.MarkerStyle);
            Assert.Equal(TextMarkerStyle.Decimal, inner.MarkerStyle);
            Assert.Equal("Inner", ItemText(inner.ListItems.Single()));
        });

        [Fact]
        public void Table_list_is_a_two_column_table_under_a_header() => Sta.Run(() =>
        {
            var document = Card(@"<summary><list type=""table"">
<listheader><term>Name</term><description>Meaning</description></listheader>
<item><term>a</term><description>The first.</description></item>
<item><term>b</term><description>The second.</description></item>
</list></summary>");
            var table = document.Blocks.OfType<Table>().Single();
            Assert.Equal(TextWidth, table.Columns.Sum(c => c.Width.Value), 3);

            var rows = table.RowGroups[0].Rows;
            Assert.Equal(3, rows.Count);
            Assert.Equal(new[] { "Name", "Meaning" }, rows[0].Cells.Select(c => TextOf(c).Trim()));
            Assert.All(rows[0].Cells, c =>
            {
                Assert.Same(Theme.Muted, c.Foreground);
                Assert.Equal(1, c.BorderThickness.Bottom);
            });
            Assert.Equal(new[] { "a", "The first." }, rows[1].Cells.Select(c => TextOf(c).Trim()));
            Assert.Equal(FontWeights.SemiBold, rows[1].Cells[0].FontWeight);
            Assert.Equal(0, rows[1].Cells[1].BorderThickness.Bottom);
        });

        [Fact]
        public void Table_list_item_without_a_term_spans_both_columns() => Sta.Run(() =>
        {
            var table = Card(@"<summary><list type=""table"">
<item><term>a</term></item>
<item><description>Across the table.</description></item>
</list></summary>").Blocks.OfType<Table>().Single();

            var rows = table.RowGroups[0].Rows;
            Assert.Equal(2, rows[0].Cells.Count); // A term with no description still has its (empty) cell.
            Assert.Equal(2, rows[1].Cells.Single().ColumnSpan);
            Assert.Equal("Across the table.", TextOf(rows[1].Cells[0]).Trim());
        });

        [Fact]
        public void Table_list_terms_take_at_most_two_fifths_of_the_width() => Sta.Run(() =>
        {
            var table = Card(@"<summary><list type=""table"">
<item><term>A very long term that would take up most of the card if it could</term><description>D.</description></item>
</list></summary>").Blocks.OfType<Table>().Single();
            Assert.True(table.Columns[0].Width.Value <= TextWidth * 0.4 + 0.5);
        });

        // --- Notes -----------------------------------------------------------------------------

        [Theory]
        [InlineData(null, "Note:")]
        [InlineData("note", "Note:")]
        [InlineData("tip", "Tip:")]
        [InlineData("TIP", "Tip:")]
        [InlineData("important", "Important:")]
        [InlineData("warning", "Warning:")]
        [InlineData("caution", "Caution:")]
        [InlineData("security", "Security:")]
        [InlineData("implement", "Note to implementers:")]
        [InlineData("caller", "Note to callers:")]
        [InlineData("inherit", "Note to inheritors:")]
        [InlineData("something else", "Note:")]
        public void Notes_are_callouts_with_a_label(string type, string label) => Sta.Run(() =>
        {
            string open = type == null ? "<note>" : $"<note type=\"{type}\">";
            var document = Card($"<summary>Intro.{open}Be <b>careful</b>.</note></summary>");
            var note = document.Blocks.OfType<Section>().Single();
            Assert.Equal(3, note.BorderThickness.Left);
            Assert.Equal(label + " Be careful.", TextOf(note).Trim());
            Assert.Equal(FontWeights.SemiBold, RunWith(document, label).FontWeight);
        });

        // --- Sections --------------------------------------------------------------------------

        [Fact]
        public void Sections_come_in_a_fixed_order() => Sta.Run(() =>
        {
            var document = Card(@"<summary>S.</summary>
<seealso cref=""Other""/>
<since>1.0</since>
<example>ex</example>
<threadsafety static=""true"" instance=""true""/>
<remarks>rm</remarks>
<permission cref=""P"">pm</permission>
<exception cref=""E"">e</exception>
<value>v</value>
<returns>r</returns>
<param name=""x"">p</param>
<typeparam name=""T"">t</typeparam>");

            Assert.Equal(
                new[] { "Type parameters", "Parameters", "Returns", "Value", "Exceptions", "Permissions", "Remarks", "Thread safety", "Example", "Since", "See also" },
                Rows(document).Select(r => r.Key));
        });

        [Fact]
        public void The_two_section_columns_fill_the_card() => Sta.Run(() =>
        {
            // Two lines: a one-line comment is a compact card, with less padding.
            var table = SectionTable(Card("<summary>S.</summary>\n<param name=\"x\">The x.</param>\n<returns>Y.</returns>"));
            Assert.Equal(TextWidth, table.Columns.Sum(c => c.Width.Value), 3);
        });

        [Fact]
        public void Exceptions_and_permissions_show_the_type_name() => Sta.Run(() =>
        {
            var document = Card(@"<summary>S.</summary>
<exception cref=""System.ArgumentNullException""><paramref name=""x""/> is null.</exception>
<exception cref=""T:System.IO.IOException""/>
<permission cref=""System.Security.PermissionSet"">Everyone can call this.</permission>");

            Assert.Equal("ArgumentNullException – x is null.\r\nIOException", Row(document, "Exceptions"));
            Assert.Equal("PermissionSet – Everyone can call this.", Row(document, "Permissions"));
        });

        [Theory]
        [InlineData("returns", "Returns")]
        [InlineData("value", "Value")]
        [InlineData("remarks", "Remarks")]
        [InlineData("example", "Example")]
        [InlineData("since", "Since")]
        public void Empty_sections_are_left_out(string tag, string label) => Sta.Run(() =>
        {
            var document = Card($"<summary>S.</summary>\n<{tag}>\n</{tag}>\n<param name=\"x\">The x.</param>");
            Assert.DoesNotContain(Rows(document), r => r.Key == label);
            Assert.Equal(new[] { "Parameters" }, Rows(document).Select(r => r.Key));
        });

        [Theory]
        [InlineData("static=\"true\" instance=\"true\"", "Safe to use from several threads at once.")]
        [InlineData("static=\"true\" instance=\"false\"", "Static members are safe to use from several threads at once; instance members aren't.")]
        [InlineData("", "Static members are safe to use from several threads at once; instance members aren't.")]
        [InlineData("static=\"false\" instance=\"true\"", "Instance members are safe to use from several threads at once; static members aren't.")]
        [InlineData("static=\"false\" instance=\"false\"", "Not safe to use from several threads at once.")]
        public void Thread_safety_in_words(string attributes, string text) => Sta.Run(() =>
        {
            Assert.Equal(text, Row(Card($"<summary>S.</summary><threadsafety {attributes}/>"), "Thread safety"));
        });

        [Fact]
        public void Thread_safety_with_its_own_text_shows_that() => Sta.Run(() =>
        {
            Assert.Equal("Lock first.", Row(Card("<summary>S.</summary><threadsafety>Lock <b>first</b>.</threadsafety>"), "Thread safety"));
        });

        [Theory]
        [InlineData("since", "Since")]
        [InlineData("author", "Author")]
        [InlineData("api-version", "Api version")]
        [InlineData("min_os", "Min os")]
        public void Other_top_level_tags_get_a_row_named_after_the_tag(string tag, string label) => Sta.Run(() =>
        {
            Assert.Equal("Something.", Row(Card($"<summary>S.</summary><{tag}>Something.</{tag}>"), label));
        });

        [Fact]
        public void Tooling_tags_are_not_shown() => Sta.Run(() =>
        {
            var document = Card("<summary>S.</summary><overloads>All overloads.</overloads><filterpriority>1</filterpriority><exclude/><preliminary/>");
            Assert.Equal("S.", TextOf(document));
            Assert.Null(SectionTable(document));
        });

        [Theory]
        [InlineData("<include file=\"docs.xml\" path=\"doc/member[@name='M']/*\"/>", "docs.xml")]
        [InlineData("<include file=\"..\\docs\\Api.xml\" path=\"doc/*\"/>", "Api.xml")]
        [InlineData("<include file=\"../docs/Api.xml\" path=\"doc/*\"/>", "Api.xml")]
        [InlineData("<include path=\"doc/*\"/>", "another file")]
        public void Include_says_where_the_docs_are(string xml, string file) => Sta.Run(() =>
        {
            Assert.Equal($"Documentation included from {file}, not shown here.", TextOf(Card(xml)));
        });

        [Fact]
        public void See_also_links_are_listed_in_one_row() => Sta.Run(() =>
        {
            var document = Card("<summary>S.</summary><seealso cref=\"List{T}\"/><seealso href=\"https://example.com\">Docs</seealso>");
            Assert.Equal("List<T>, Docs" + Arrow, Row(document, "See also"));
        });

        // --- Inherited docs --------------------------------------------------------------------

        [Fact]
        public void Inheritdoc_before_the_docs_arrive_says_where_they_come_from() => Sta.Run(() =>
        {
            Assert.Equal("Inherits documentation from the base member.", TextOf(Card("<inheritdoc/>")));
        });

        [Fact]
        public void Inherited_docs_fill_the_card_and_say_where_from() => Sta.Run(() =>
        {
            var inherited = new InheritedDoc("<summary>Base summary.</summary><since>1.0</since>", "M:Ns.Base.Run");
            var document = Card("<inheritdoc/>", inherited);
            Assert.StartsWith("Base summary.", TextOf(document));
            Assert.Equal("1.0", Row(document, "Since"));
            Assert.Equal("Base.Run()", Row(document, "Inherited from"));
        });

        [Fact]
        public void Nested_inheritdoc_waiting_for_its_docs_shows_a_placeholder() => Sta.Run(() =>
        {
            Assert.Equal("Runs it. (inherited)", TextOf(Card("<summary>Runs it. <inheritdoc/></summary>")));
        });

        // --- Whole cards -----------------------------------------------------------------------

        [Fact]
        public void An_empty_comment_says_so() => Sta.Run(() =>
        {
            Assert.Equal("Empty documentation comment", TextOf(Card("<summary></summary>")));
        });

        [Fact]
        public void Every_sample_comment_renders() => Sta.Run(() =>
        {
            var files = Directory.GetFiles(SamplesFolder(), "*.cs");
            Assert.NotEmpty(files);
            int cards = 0;
            foreach (var file in files)
            {
                foreach (var block in DocCommentScanner.GetBlocks(new FakeSnapshot(File.ReadAllText(file))))
                {
                    var card = (Border)DocCommentRenderer.CreateCard(block, CardWidth, null, null);
                    var document = ((Grid)card.Child).Children.OfType<RichTextBox>().Single().Document;
                    Assert.True(document.Blocks.Count > 0, $"{Path.GetFileName(file)} line {block.StartLine + 1} is empty");
                    cards++;
                }
            }

            Assert.True(cards > 20);
        });

        private static string SamplesFolder()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string samples = Path.Combine(dir.FullName, "samples");
                if (File.Exists(Path.Combine(samples, "TagSamples.cs"))) return samples;
            }

            throw new DirectoryNotFoundException("No samples folder above " + AppContext.BaseDirectory);
        }
    }
}
