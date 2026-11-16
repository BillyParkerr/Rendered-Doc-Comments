using System;
using System.Linq;
using RenderedDocComments.Rendering;
using Xunit;
using K = RenderedDocComments.Rendering.CodeTokenKind;

namespace RenderedDocComments.Tests
{
    /// <summary>Colouring code blocks by their language attribute, and the XML/HTML tokenizer.</summary>
    public class CodeLanguagesTests
    {
        /// <summary>The kind of the run containing the first occurrence of some text.</summary>
        private static K KindOf(string code, string text, string language)
        {
            int at = code.IndexOf(text, StringComparison.Ordinal);
            Assert.True(at >= 0, "Not in the code: " + text);

            int position = 0;
            foreach (var token in CodeLanguages.Tokenize(code, language))
            {
                if (at < position + token.Text.Length)
                {
                    Assert.True(at + text.Length <= position + token.Text.Length, text + " is split across runs");
                    return token.Kind;
                }

                position += token.Text.Length;
            }

            throw new InvalidOperationException("Ran off the end.");
        }

        [Theory]
        [InlineData("<item key=\"a\">text</item>", "item", "Markup")]
        [InlineData("<item key=\"a\">text</item>", "key", "AttributeName")]
        [InlineData("<item key=\"a\">text</item>", "\"a\"", "String")]
        [InlineData("<item key='a b'>text</item>", "'a b'", "String")]
        [InlineData("<item key=\"a\">text</item>", "text", "Plain")]
        [InlineData("<a>x</a>", "</", "Plain")]
        [InlineData("<a/><!-- a note --><b/>", "<!-- a note -->", "Comment")]
        [InlineData("<x><![CDATA[a < b]]></x>", "<![CDATA[a < b]]>", "String")]
        [InlineData("<?xml version='1.0'?>", "xml", "Markup")]
        [InlineData("<?xml version='1.0'?>", "version", "AttributeName")]
        [InlineData("<Grid x:Name=\"Root\" />", "x:Name", "AttributeName")]
        [InlineData("<Grid x:Name=\"Root\" />", "Grid", "Markup")]
        [InlineData("<my-element data-id=\"1\">", "my-element", "Markup")]
        [InlineData("<my-element data-id=\"1\">", "data-id", "AttributeName")]
        public void Markup_kinds(string code, string text, string expected)
        {
            Assert.Equal(expected, KindOf(code, text, "xml").ToString());
        }

        [Theory]
        [InlineData("xml")]
        [InlineData("XML")]
        [InlineData("xaml")]
        [InlineData("html")]
        [InlineData(" html ")]
        [InlineData("csproj")]
        public void Markup_languages_use_the_markup_colours(string language)
        {
            Assert.Equal(K.Markup, KindOf("<Button Content=\"OK\"/>", "Button", language));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("cs")]
        [InlineData("c#")]
        [InlineData("C#")]
        [InlineData("csharp")]
        [InlineData("CSharp")]
        public void No_language_or_csharp_is_csharp(string language)
        {
            Assert.Equal(K.ControlKeyword, KindOf("return true;", "return", language));
            Assert.Equal(K.Keyword, KindOf("return true;", "true", language));
        }

        [Theory]
        [InlineData("json")]
        [InlineData("js")]
        [InlineData("javascript")]
        [InlineData("ts")]
        [InlineData("java")]
        [InlineData("cpp")]
        public void C_like_languages_get_strings_numbers_and_literals(string language)
        {
            const string code = "{ \"name\": \"Ada\", \"age\": 36, \"admin\": true }";
            Assert.Equal(K.String, KindOf(code, "\"name\"", language));
            Assert.Equal(K.Number, KindOf(code, "36", language));
            Assert.Equal(K.Keyword, KindOf(code, "true", language));
        }

        [Theory]
        [InlineData("text")]
        [InlineData("sql")]
        [InlineData("powershell")]
        [InlineData("python")]
        [InlineData("anything")]
        public void Other_languages_are_plain(string language)
        {
            const string code = "SELECT name FROM people WHERE id = 1 -- \"quoted\"";
            var tokens = CodeLanguages.Tokenize(code, language);
            Assert.Equal(K.Plain, Assert.Single(tokens).Kind);
            Assert.Equal(code, tokens[0].Text);
        }

        [Fact]
        public void Pre_without_a_language_is_plain_text()
        {
            Assert.Equal(K.Plain, Assert.Single(CodeLanguages.Tokenize("return true;", CodeLanguages.PlainText)).Kind);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("xml")]
        [InlineData("sql")]
        public void Empty_and_null_give_no_runs(string language)
        {
            Assert.Empty(CodeLanguages.Tokenize(null, language));
            Assert.Empty(CodeLanguages.Tokenize(string.Empty, language));
        }

        [Fact]
        public void Adjacent_markup_runs_of_one_kind_are_joined()
        {
            var tokens = MarkupTokenizer.Tokenize("<a><b>text</b></a>");
            for (int i = 1; i < tokens.Count; i++)
            {
                Assert.NotEqual(tokens[i - 1].Kind, tokens[i].Kind);
            }
        }

        [Theory]
        [InlineData("<")]
        [InlineData("<a")]
        [InlineData("<a b=\"unclosed")]
        [InlineData("<!-- unclosed comment")]
        [InlineData("<![CDATA[ unclosed")]
        [InlineData("<a <b>")]
        [InlineData("a > b")]
        [InlineData("<//>")]
        [InlineData("<a b='1' c=\"2\" d/>")]
        public void Markup_runs_always_add_up_to_the_input(string code)
        {
            Assert.Equal(code, string.Concat(MarkupTokenizer.Tokenize(code).Select(t => t.Text)));
        }

        [Fact]
        public void Random_markup_round_trips_without_throwing()
        {
            const string alphabet = "<>/=\"'!-[]?CDATA ab:\n";
            var random = new Random(8765);
            for (int run = 0; run < 3000; run++)
            {
                var chars = new char[random.Next(0, 40)];
                for (int i = 0; i < chars.Length; i++) chars[i] = alphabet[random.Next(alphabet.Length)];
                string code = new string(chars);

                string joined = string.Concat(MarkupTokenizer.Tokenize(code).Select(t => t.Text));
                Assert.True(joined == code, "Didn't round-trip: " + code);
            }
        }
    }
}
