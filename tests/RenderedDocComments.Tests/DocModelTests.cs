using System;
using System.Linq;
using RenderedDocComments.Parsing;
using Xunit;

namespace RenderedDocComments.Tests
{
    /// <summary>The lenient XML reader and the section model built from it.</summary>
    public class DocModelTests
    {
        private static string Text(DocNode node) => node == null ? null : LenientXmlParser.InnerText(node).Trim();

        [Fact]
        public void Reads_the_usual_sections()
        {
            var model = DocModel.Parse(@"<summary>Adds two numbers.</summary>
<typeparam name=""T"">The number type.</typeparam>
<param name=""a"">First.</param>
<param name=""b"">Second.</param>
<returns>The sum.</returns>
<remarks>Never overflows.</remarks>
<exception cref=""T:System.OverflowException"">Sometimes.</exception>
<example>Add(1, 2)</example>
<seealso cref=""Subtract""/>");

            Assert.Equal("Adds two numbers.", Text(model.Summary));
            Assert.Equal("The number type.", Text(model.TypeParams.Single().Body));
            Assert.Equal(new[] { "a", "b" }, model.Params.Select(p => p.Name));
            Assert.Equal("The sum.", Text(model.Returns));
            Assert.Equal("Never overflows.", Text(model.Remarks));
            Assert.Equal("T:System.OverflowException", model.Exceptions.Single().Name);
            Assert.Equal("Add(1, 2)", Text(model.Example));
            Assert.Equal("Subtract", model.SeeAlso.Single().Attr("cref"));
            Assert.False(model.IsEmpty);
        }

        [Fact]
        public void Text_without_tags_is_the_summary()
        {
            Assert.Equal("Does a thing.", Text(DocModel.Parse("Does a thing.").Summary));
        }

        [Fact]
        public void Two_summaries_are_joined_with_a_paragraph_break()
        {
            var summary = DocModel.Parse("<summary>One.</summary><summary>Two.</summary>").Summary;
            Assert.Equal(new[] { null, "para", null }, summary.Children.Select(c => c.Name));
        }

        [Fact]
        public void An_unclosed_summary_does_not_swallow_the_parameters()
        {
            var model = DocModel.Parse("<summary>Half typed\n<param name=\"x\">The x.</param>");
            Assert.Equal("Half typed", Text(model.Summary));
            Assert.Equal("The x.", Text(model.Params.Single().Body));
        }

        [Fact]
        public void Stray_angle_brackets_and_entities_are_text()
        {
            var model = DocModel.Parse("<summary>True when a < b and List&lt;T&gt; is empty &amp; ready.</summary>");
            Assert.Equal("True when a < b and List<T> is empty & ready.", Text(model.Summary));
        }

        [Fact]
        public void Unknown_closing_tags_are_ignored()
        {
            Assert.Equal("Fine.", Text(DocModel.Parse("<summary>Fine.</b></summary>").Summary));
        }

        [Fact]
        public void Cdata_is_kept_as_is_and_xml_comments_are_dropped()
        {
            var model = DocModel.Parse("<summary><!-- hidden --><![CDATA[a < b && c]]></summary>");
            Assert.Equal("a < b && c", Text(model.Summary));
        }

        [Fact]
        public void Attribute_quoting_styles()
        {
            var root = LenientXmlParser.Parse("<see cref=\"A\"/><see cref='B'/><see cref=C/><see langword=\"null\" />");
            Assert.Equal(new[] { "A", "B", "C", null }, root.Children.Select(c => c.Attr("cref")));
            Assert.Equal("null", root.Children[3].Attr("LANGWORD")); // Attribute names ignore case.
        }

        [Fact]
        public void A_quoted_gt_does_not_end_the_tag()
        {
            var see = LenientXmlParser.Parse("<see cref=\"op_GreaterThan(A,B)\" alt=\"a > b\"/>").Children.Single();
            Assert.Equal("a > b", see.Attr("alt"));
        }

        [Fact]
        public void Element_names_are_lower_cased()
        {
            Assert.Equal("Shout.", Text(DocModel.Parse("<SUMMARY>Shout.</Summary>").Summary));
        }

        [Fact]
        public void Roslyn_member_wrapper_is_unwrapped()
        {
            var model = DocModel.Parse(@"<member name=""M:C.M(System.Int32)"">
  <summary>Inherited.</summary>
  <param name=""a"">The a.</param>
  <returns>Something.</returns>
</member>");

            Assert.Equal("Inherited.", Text(model.Summary));
            Assert.DoesNotContain(model.Summary.Children, c => c.Name == "para" || c.Name == "member");
            Assert.Equal("The a.", Text(model.Params.Single().Body));
            Assert.Equal("Something.", Text(model.Returns));
        }

        [Fact]
        public void An_unclosed_section_inside_a_wrapper_closes_at_the_next_section()
        {
            var model = DocModel.Parse("<doc><summary>Open <b>bold\n<param name=\"x\">The x.</param></doc>");
            Assert.Equal("Open bold", Text(model.Summary));
            Assert.Equal("The x.", Text(model.Params.Single().Body));
        }

        [Fact]
        public void Inheritdoc_is_recorded()
        {
            var model = DocModel.Parse("<inheritdoc cref=\"Base.M\"/>");
            Assert.Equal("Base.M", model.InheritDoc.Attr("cref"));
            Assert.False(model.IsEmpty);
            Assert.True(DocModel.Parse("   ").IsEmpty);
        }

        [Fact]
        public void Merging_inherited_docs_keeps_own_sections_and_fills_gaps()
        {
            var own = DocModel.Parse(@"<inheritdoc/>
<summary>Own summary.</summary>
<param name=""a"">Own a.</param>
<exception cref=""ArgumentNullException"">Own.</exception>
<seealso cref=""Other""/>");
            var inherited = DocModel.Parse(@"<summary>Base summary.</summary>
<param name=""a"">Base a.</param>
<param name=""b"">Base b.</param>
<returns>Base returns.</returns>
<exception cref=""T:System.ArgumentNullException"">Base.</exception>
<exception cref=""T:System.InvalidOperationException"">Base.</exception>
<seealso cref=""T:Other""/>
<seealso href=""https://example.com""/>");

            own.MergeInherited(inherited);

            Assert.Equal("Own summary.", Text(own.Summary));
            Assert.Equal("Base returns.", Text(own.Returns));
            Assert.Equal(new[] { "Own a.", "Base b." }, own.Params.Select(p => Text(p.Body)));
            Assert.Equal(new[] { "Own.", "Base." }, own.Exceptions.Select(e => Text(e.Body)));
            Assert.Equal("T:System.InvalidOperationException", own.Exceptions[1].Name);
            Assert.Equal(2, own.SeeAlso.Count);
        }

        /// <summary>
        /// Comments are rendered while they're being typed, so every half-written prefix of a
        /// comment must parse without throwing.
        /// </summary>
        [Fact]
        public void Every_prefix_of_a_comment_parses()
        {
            const string comment = @"<summary>Finds the <see cref=""List{T}""/> for <paramref name=""key""/> &amp; more.
<para>See <a href='https://example.com/?a=1&b=2'>docs</a>.</para></summary>
<param name=""key"">The <c>key</c>; never <see langword=""null""/>.</param>
<returns><list type=""bullet""><item><term>A</term><description>B</description></item></list></returns>
<example><code>var x = Find(""a"");  // <![CDATA[<raw>]]></code></example>
<!-- note -->";

            for (int length = 0; length <= comment.Length; length++)
            {
                string prefix = comment.Substring(0, length);
                var model = DocModel.Parse(prefix);
                Assert.NotNull(model);
                if (model.Summary != null) LenientXmlParser.InnerText(model.Summary);
            }
        }

        [Theory]
        [InlineData("<")]
        [InlineData("</")]
        [InlineData("<<<>>>")]
        [InlineData("<summary")]
        [InlineData("<summary attr=\"unterminated")]
        [InlineData("</summary></summary>")]
        [InlineData("<!--")]
        [InlineData("<![CDATA[")]
        [InlineData("&;&#xZZ;&#99999999;&unknown;")]
        [InlineData("<a =\"x\" ='y' b>")]
        public void Malformed_input_does_not_throw(string xml)
        {
            var exception = Record.Exception(() => DocModel.Parse(xml));
            Assert.Null(exception);
        }

        [Fact]
        public void Deep_nesting_does_not_throw()
        {
            string xml = "<summary>" + string.Concat(Enumerable.Repeat("<b>", 1000)) + "deep" + string.Concat(Enumerable.Repeat("</b>", 1000)) + "</summary>";
            Assert.Equal("deep", Text(DocModel.Parse(xml).Summary));
        }

        [Fact]
        public void Random_input_does_not_throw()
        {
            const string alphabet = "<>/=\"'&;! abc[]-CDATA\n";
            var random = new Random(1234);
            for (int run = 0; run < 2000; run++)
            {
                var chars = new char[random.Next(0, 40)];
                for (int i = 0; i < chars.Length; i++) chars[i] = alphabet[random.Next(alphabet.Length)];
                string xml = new string(chars);

                var exception = Record.Exception(() => DocModel.Parse(xml));
                Assert.True(exception == null, "Threw on: " + xml + "\n" + exception);
            }
        }

        [Fact]
        public void Reads_the_less_common_sections()
        {
            var model = DocModel.Parse(@"<summary>S.</summary>
<permission cref=""System.Security.PermissionSet"">Everyone.</permission>
<threadsafety static=""true"" instance=""false""/>
<include file=""docs.xml"" path=""doc/member[@name='M']/*""/>
<since>2.0</since>");

            Assert.Equal("System.Security.PermissionSet", model.Permissions.Single().Name);
            Assert.Equal("Everyone.", Text(model.Permissions.Single().Body));
            Assert.Equal("false", model.ThreadSafety.Attr("instance"));
            Assert.Equal("docs.xml", model.Includes.Single().Attr("file"));
            Assert.Equal("since", model.Custom.Single().Name);
            Assert.Equal("2.0", Text(model.Custom.Single().Body));
            Assert.Equal("S.", Text(model.Summary));
        }

        [Fact]
        public void Unknown_top_level_tags_are_custom_sections_in_order()
        {
            var model = DocModel.Parse("<summary>S.</summary><since>2.0</since><author>Sam</author>");
            Assert.Equal(new[] { "since", "author" }, model.Custom.Select(c => c.Name));
        }

        [Theory]
        [InlineData("<overloads>All the overloads.</overloads>")]
        [InlineData("<filterpriority>1</filterpriority>")]
        [InlineData("<exclude/>")]
        [InlineData("<preliminary/>")]
        public void Tooling_tags_are_left_out(string xml)
        {
            var model = DocModel.Parse("<summary>S.</summary>" + xml);
            Assert.Empty(model.Custom);
            Assert.Equal("S.", Text(model.Summary));
        }

        [Theory]
        [InlineData("b")]
        [InlineData("strong")]
        [InlineData("i")]
        [InlineData("em")]
        [InlineData("u")]
        [InlineData("s")]
        [InlineData("strike")]
        [InlineData("del")]
        [InlineData("sub")]
        [InlineData("sup")]
        [InlineData("c")]
        [InlineData("tt")]
        [InlineData("code")]
        [InlineData("pre")]
        [InlineData("span")]
        [InlineData("a")]
        [InlineData("para")]
        [InlineData("p")]
        [InlineData("list")]
        [InlineData("ul")]
        [InlineData("ol")]
        [InlineData("note")]
        [InlineData("h1")]
        [InlineData("h3")]
        [InlineData("h6")]
        public void Text_tags_outside_any_section_are_part_of_the_summary(string tag)
        {
            var model = DocModel.Parse($"Does a <{tag}>thing</{tag}>.");
            Assert.Empty(model.Custom);
            Assert.Contains(model.Summary.Children, c => c.Name == tag);
        }

        [Fact]
        public void A_line_break_outside_any_section_is_part_of_the_summary()
        {
            var model = DocModel.Parse("One line.<br/>Next line.");
            Assert.Empty(model.Custom);
            Assert.Contains(model.Summary.Children, c => c.Name == "br");
        }

        [Theory]
        [InlineData("<permission cref=\"P\">Anyone.</permission>")]
        [InlineData("<threadsafety static=\"true\"/>")]
        [InlineData("<include file=\"docs.xml\" path=\"doc/*\"/>")]
        [InlineData("<since>1.0</since>")]
        public void A_comment_with_only_a_less_common_section_is_not_empty(string xml)
        {
            Assert.False(DocModel.Parse(xml).IsEmpty);
        }

        [Fact]
        public void Merging_fills_in_permissions_custom_sections_and_thread_safety()
        {
            var own = DocModel.Parse(@"<summary>Own.</summary><permission cref=""T:A"">Own A.</permission><since>2.0</since>");
            own.MergeInherited(DocModel.Parse(
                @"<permission cref=""A"">Base A.</permission><permission cref=""B"">Base B.</permission>
<since>1.0</since><author>Sam</author><threadsafety static=""true""/>"));

            Assert.Equal(new[] { "Own A.", "Base B." }, own.Permissions.Select(p => Text(p.Body)));
            Assert.Equal(new[] { "2.0", "Sam" }, own.Custom.Select(c => Text(c.Body)));
            Assert.NotNull(own.ThreadSafety);
        }

        [Fact]
        public void Nested_inheritdoc_in_a_custom_section_takes_that_section_from_the_base()
        {
            var model = DocModel.Parse("<summary>S.</summary><since><inheritdoc/></since>");
            Assert.Equal(1, model.ExpandNestedInheritDoc("<summary>Base.</summary><since>1.0</since>"));
            Assert.Equal("1.0", Text(model.Custom.Single().Body));
        }
    }
}
