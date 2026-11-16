using System;
using System.Linq;
using RenderedDocComments.Rendering;
using Xunit;
using K = RenderedDocComments.Rendering.CodeTokenKind;

namespace RenderedDocComments.Tests
{
    /// <summary>Colouring for &lt;code&gt; and &lt;c&gt; in cards.</summary>
    public class CSharpTokenizerTests
    {
        /// <summary>The kind of the run containing the first occurrence of a word.</summary>
        private static K KindOf(string code, string word)
        {
            int at = code.IndexOf(word, StringComparison.Ordinal);
            Assert.True(at >= 0, "Not in the code: " + word);

            int position = 0;
            foreach (var token in CSharpTokenizer.Tokenize(code))
            {
                if (at < position + token.Text.Length)
                {
                    Assert.True(at + word.Length <= position + token.Text.Length, word + " is split across runs");
                    return token.Kind;
                }

                position += token.Text.Length;
            }

            throw new InvalidOperationException("Ran off the end.");
        }

        [Theory]
        [InlineData("var list = new List<int>();", "var", "Keyword")]
        [InlineData("var list = new List<int>();", "List", "TypeName")]
        [InlineData("var list = new List<int>();", "int", "Keyword")]
        [InlineData("var list = new List<int>();", "list", "Plain")]
        [InlineData("return Compute(42);", "return", "ControlKeyword")]
        [InlineData("return Compute(42);", "Compute", "MethodName")]
        [InlineData("return Compute (42);", "Compute", "MethodName")]
        [InlineData("return Compute(42);", "42", "Number")]
        [InlineData("x = 1.5f + 0x1F;", "1.5f", "Number")]
        [InlineData("x = 1.5f + 0x1F;", "0x1F", "Number")]
        [InlineData("person.Name = name;", "Name", "Plain")]
        [InlineData("person.Save();", "Save", "MethodName")]
        [InlineData("await Task.Delay(10);", "await", "ControlKeyword")]
        [InlineData("await Task.Delay(10);", "Task", "TypeName")]
        [InlineData("var @class = 1;", "@class", "Plain")]
        [InlineData("var @Type = 1;", "@Type", "TypeName")]
        [InlineData("_field = value;", "_field", "Plain")]
        [InlineData("_field = value;", "value", "Keyword")]
        public void Classifies_words(string code, string word, string expected)
        {
            Assert.Equal(expected, KindOf(code, word).ToString());
        }

        [Theory]
        [InlineData("s = \"a \\\" b\";", "\"a \\\" b\"")]
        [InlineData("s = @\"c:\\path\\\";", "@\"c:\\path\\\"")]
        [InlineData("s = @\"say \"\"hi\"\"\";", "@\"say \"\"hi\"\"\"")]
        [InlineData("s = $\"{x} items\";", "$\"{x} items\"")]
        [InlineData("s = $@\"{x}\\n\";", "$@\"{x}\\n\"")]
        [InlineData("c = 'x';", "'x'")]
        [InlineData("c = '\\'';", "'\\''")]
        public void Strings_are_one_run(string code, string literal)
        {
            Assert.Equal(K.String, KindOf(code, literal));
        }

        [Fact]
        public void Line_comments_stop_at_the_end_of_the_line()
        {
            const string code = "x = 1; // note\nint y;";
            Assert.Equal(K.Comment, KindOf(code, "// note"));
            Assert.Equal(K.Keyword, KindOf(code, "int"));
        }

        [Fact]
        public void Unterminated_block_comment_runs_to_the_end()
        {
            Assert.Equal(K.Comment, CSharpTokenizer.Tokenize("x; /* never closed\nint y;").Last().Kind);
        }

        [Fact]
        public void Unterminated_string_stops_at_the_end_of_the_line()
        {
            const string code = "s = \"open\nint y;";
            Assert.Equal(K.String, KindOf(code, "\"open"));
            Assert.Equal(K.Keyword, KindOf(code, "int"));
        }

        [Fact]
        public void Adjacent_runs_of_one_kind_are_joined()
        {
            var token = Assert.Single(CSharpTokenizer.Tokenize("item.count = (total);"));
            Assert.Equal(K.Plain, token.Kind);
            Assert.Equal("item.count = (total);", token.Text);
        }

        [Fact]
        public void Empty_and_null_give_no_runs()
        {
            Assert.Empty(CSharpTokenizer.Tokenize(string.Empty));
            Assert.Empty(CSharpTokenizer.Tokenize(null));
        }

        [Theory]
        [InlineData("var x = new Dictionary<string, List<int>>();\nforeach (var (k, v) in x) { Console.WriteLine($\"{k}: {v.Count}\"); }")]
        [InlineData("\"")]
        [InlineData("@")]
        [InlineData("$")]
        [InlineData("$@")]
        [InlineData("'")]
        [InlineData("\"\\")]
        [InlineData("/")]
        [InlineData("/*")]
        [InlineData("1.")]
        [InlineData("@\"unterminated verbatim\nstill string")]
        [InlineData("x => x?.y ?? z!; #if DEBUG\n#endif")]
        public void Runs_always_add_up_to_the_input(string code)
        {
            Assert.Equal(code, string.Concat(CSharpTokenizer.Tokenize(code).Select(t => t.Text)));
        }

        [Fact]
        public void Random_input_round_trips_without_throwing()
        {
            const string alphabet = "ab_AZ09 .(\"'@$/*\\\n{}<>;";
            var random = new Random(4321);
            for (int run = 0; run < 3000; run++)
            {
                var chars = new char[random.Next(0, 30)];
                for (int i = 0; i < chars.Length; i++) chars[i] = alphabet[random.Next(alphabet.Length)];
                string code = new string(chars);

                string joined = string.Concat(CSharpTokenizer.Tokenize(code).Select(t => t.Text));
                Assert.True(code == joined, "Didn't round-trip: " + code);
            }
        }
    }
}
