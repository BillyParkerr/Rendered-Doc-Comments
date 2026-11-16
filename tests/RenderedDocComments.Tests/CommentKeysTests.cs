using System.Collections.Generic;
using System.Linq;
using RenderedDocComments.Parsing;
using RenderedDocComments.Tests.Fakes;
using Xunit;

namespace RenderedDocComments.Tests
{
    /// <summary>
    /// Comment keys are how a card/raw choice is remembered between sessions, so they must
    /// find the same comment again after ordinary edits, and tell comments apart.
    /// </summary>
    public class CommentKeysTests
    {
        private const string Original = @"
public class Tests
{
    /// <summary>Adds.</summary>
    [Fact]
    public void Adds() { }

    /// <summary>Subtracts.</summary>
    [Fact]
    [Trait(""Area"", ""Math"")]
    public void Subtracts() { }

    /// <summary>First overload.</summary>
    public void Same() { }

    /// <summary>Second, same text.</summary>
    public void Same() { }
}
/// <summary>Trailing comment with nothing below.</summary>
";

        private static List<string> Keys(string text) =>
            CommentKeys.For(new FakeSnapshot(text)).Select(k => k.Value).ToList();

        [Fact]
        public void Key_is_the_declaration_below_with_attributes_skipped()
        {
            var keys = Keys(Original);
            Assert.Equal("public void Adds() { }#0", keys[0]);
            Assert.Equal("public void Subtracts() { }#0", keys[1]);
        }

        [Fact]
        public void Declarations_that_read_the_same_are_numbered()
        {
            var keys = Keys(Original);
            Assert.Equal("public void Same() { }#0", keys[2]);
            Assert.Equal("public void Same() { }#1", keys[3]);
            Assert.Equal(keys.Count, keys.Distinct().Count());
        }

        [Fact]
        public void A_comment_with_no_declaration_is_keyed_by_its_text()
        {
            Assert.Equal("comment: <summary>Trailing comment with nothing below.</summary>#0", Keys(Original)[4]);
        }

        [Fact]
        public void Keys_survive_edits_elsewhere_and_to_the_comment_itself()
        {
            string edited = Original
                .Replace("public class Tests\r\n{", "public class Tests\r\n{\r\n    private int _x;\r\n    private int _y;\r\n")
                .Replace("public class Tests\n{", "public class Tests\n{\n    private int _x;\n    private int _y;\n")
                .Replace("<summary>Subtracts.</summary>", "<summary>Subtracts two numbers.</summary>\n    /// <remarks>New line.</remarks>");

            Assert.Contains("_y;", edited);
            Assert.Equal(Keys(Original), Keys(edited));
        }

        [Fact]
        public void Whitespace_changes_in_the_declaration_do_not_change_the_key()
        {
            Assert.Equal(
                Keys("/// <summary>A</summary>\npublic   void  M(int a,  int b) { }"),
                Keys("/// <summary>A</summary>\n    public void M(int a, int b) { }"));
        }

        [Fact]
        public void A_comment_directly_above_another_is_keyed_by_its_text()
        {
            // Only a blank line separates the first comment from the next, so it documents nothing.
            var keys = Keys("/// <summary>A</summary>\n\n/// <summary>B</summary>\npublic void B() { }");
            Assert.Equal("comment: <summary>A</summary>#0", keys[0]);
            Assert.Equal("public void B() { }#0", keys[1]);
        }

        [Fact]
        public void Long_comment_text_keys_are_capped()
        {
            string longText = "/// <summary>" + new string('x', 500) + "</summary>";
            Assert.Equal(200 + "#0".Length, Keys(longText).Single().Length);
        }
    }
}
