using System.Linq;
using RenderedDocComments.Parsing;
using Xunit;

namespace RenderedDocComments.Tests
{
    /// <summary>Which part of inherited docs an &lt;inheritdoc/&gt; takes: its path, or where it sits.</summary>
    public class InheritDocPathTests
    {
        private const string Inherited =
            "<summary>Base summary.</summary>" +
            "<param name=\"a\">The a.</param>" +
            "<param name=\"b\">The b.</param>" +
            "<returns>Something.</returns>" +
            "<overloads>Not shown.</overloads>";

        private static string Text(DocNode node) => node == null ? null : LenientXmlParser.InnerText(node).Trim();

        [Fact]
        public void Without_a_path_everything_but_overloads()
        {
            string selected = InheritDocPath.Select(Inherited, null);
            Assert.Contains("<summary>Base summary.</summary>", selected);
            Assert.Contains("<returns>", selected);
            Assert.DoesNotContain("overloads", selected);
        }

        [Theory]
        [InlineData("/summary", "<summary>Base summary.</summary>")]
        [InlineData("/param[@name='b']", "<param name=\"b\">The b.</param>")]
        [InlineData("/summary/node()", "Base summary.")]
        [InlineData("/param/@name", "ab")]
        [InlineData("count(/*/param)", "2")]          // Only a leading "/" is made relative to the root.
        [InlineData("/remarks", "")]
        public void A_path_is_relative_to_the_root_element(string path, string expected)
        {
            Assert.Equal(expected, InheritDocPath.Select(Inherited, path));
        }

        [Theory]
        [InlineData("/[")]
        [InlineData("not xpath at all ((")]
        [InlineData("$undefined")]
        public void An_invalid_path_selects_nothing(string path)
        {
            Assert.Null(InheritDocPath.Select(Inherited, path));
        }

        [Fact]
        public void Inside_a_section_it_takes_that_section_content()
        {
            Assert.Equal("Base summary.", InheritDocPath.Select(Inherited, null, new[] { "summary" }));
            Assert.Equal("The b.", InheritDocPath.Select(Inherited, null, new[] { InheritDocPath.Step("param", "b") }));
        }

        [Theory]
        [InlineData("param", "x", "param[@name='x']")]
        [InlineData("typeparam", "T", "typeparam[@name='T']")]
        [InlineData("param", "it's", "param")]
        [InlineData("summary", null, "summary")]
        [InlineData("para", "x", "para")]
        public void Steps_name_the_parameter(string element, string name, string expected)
        {
            Assert.Equal(expected, InheritDocPath.Step(element, name));
        }

        [Fact]
        public void A_nested_inheritdoc_stays_inside_its_summary()
        {
            var model = DocModel.Parse("<summary>Before <inheritdoc/> after.</summary><param name=\"a\">Own a.</param>");
            Assert.Contains(model.Summary.Children, c => c.Name == "inheritdoc");
            Assert.Null(model.InheritDoc);
            Assert.Equal("Own a.", Text(model.Params.Single().Body));
        }

        [Fact]
        public void Nested_inheritdoc_is_filled_in_where_it_sits()
        {
            var model = DocModel.Parse(
                "<summary>Before <inheritdoc/> after.</summary>" +
                "<param name=\"b\"><inheritdoc/></param>" +
                "<remarks>See: <inheritdoc path=\"/returns/node()\"/></remarks>");

            Assert.Equal(3, model.ExpandNestedInheritDoc(Inherited));
            Assert.Equal("Before Base summary. after.", Text(model.Summary));
            Assert.Equal("The b.", Text(model.Params.Single().Body));
            Assert.Equal("See: Something.", Text(model.Remarks));
        }

        [Fact]
        public void A_nested_inheritdoc_with_nothing_to_take_just_goes()
        {
            var model = DocModel.Parse("<summary>Own <inheritdoc path=\"/remarks/node()\"/>text.</summary>");
            Assert.Equal(1, model.ExpandNestedInheritDoc(Inherited));
            Assert.Equal("Own text.", Text(model.Summary));
        }

        [Fact]
        public void Top_level_inheritdoc_with_a_path_merges_only_that_part()
        {
            var model = DocModel.Parse("<inheritdoc path=\"/summary\"/><remarks>Own remarks.</remarks>");
            model.MergeInherited(DocModel.Parse(InheritDocPath.Select(Inherited, model.InheritDoc.Attr("path"))));

            Assert.Equal("Base summary.", Text(model.Summary));
            Assert.Empty(model.Params);
            Assert.Null(model.Returns);
            Assert.Equal("Own remarks.", Text(model.Remarks));
        }
    }
}
