using System;
using System.Collections.Generic;

namespace RenderedDocComments.Rendering
{
    /// <summary>
    /// Colours code blocks by their language (<c>&lt;code language="xml"&gt;</c>, or <c>lang</c>).
    /// C# and C-like languages use the C# tokenizer (strings, numbers, keywords like true/false/null
    /// come out right); XML-like ones get tags, attributes and values; anything else stays plain,
    /// since colouring it as C# would be misleading.
    /// </summary>
    internal static class CodeLanguages
    {
        private enum Kind
        {
            CSharp,
            Markup,
            Plain,
        }

        private static readonly Dictionary<string, Kind> Known = new Dictionary<string, Kind>(StringComparer.OrdinalIgnoreCase)
        {
            ["cs"] = Kind.CSharp, ["c#"] = Kind.CSharp, ["csharp"] = Kind.CSharp, ["csharp-script"] = Kind.CSharp,
            ["json"] = Kind.CSharp, ["js"] = Kind.CSharp, ["javascript"] = Kind.CSharp, ["ts"] = Kind.CSharp, ["typescript"] = Kind.CSharp,
            ["java"] = Kind.CSharp, ["c"] = Kind.CSharp, ["cpp"] = Kind.CSharp, ["c++"] = Kind.CSharp,
            ["xml"] = Kind.Markup, ["xaml"] = Kind.Markup, ["html"] = Kind.Markup, ["htm"] = Kind.Markup, ["xhtml"] = Kind.Markup,
            ["svg"] = Kind.Markup, ["csproj"] = Kind.Markup, ["msbuild"] = Kind.Markup, ["config"] = Kind.Markup, ["razor"] = Kind.Markup,
        };

        /// <param name="language">The block's language attribute; null or empty means C# (the default for &lt;code&gt;).</param>
        public static List<CodeToken> Tokenize(string code, string language)
        {
            switch (KindOf(language))
            {
                case Kind.CSharp:
                    return CSharpTokenizer.Tokenize(code);
                case Kind.Markup:
                    return MarkupTokenizer.Tokenize(code);
                default:
                    return string.IsNullOrEmpty(code) ? new List<CodeToken>() : new List<CodeToken> { new CodeToken(CodeTokenKind.Plain, code) };
            }
        }

        /// <summary>The language "plain text": for &lt;pre&gt; without a language.</summary>
        public const string PlainText = "text";

        private static Kind KindOf(string language)
        {
            if (string.IsNullOrWhiteSpace(language)) return Kind.CSharp;
            return Known.TryGetValue(language.Trim(), out var kind) ? kind : Kind.Plain;
        }
    }

    /// <summary>
    /// XML/HTML/XAML colouring: element names, attribute names, attribute values, comments and
    /// CDATA. Everything between tags is plain text. Like the C# tokenizer, the runs always add up
    /// to the input exactly, however malformed it is.
    /// </summary>
    internal static class MarkupTokenizer
    {
        public static List<CodeToken> Tokenize(string code)
        {
            var result = new List<CodeToken>();
            if (string.IsNullOrEmpty(code))
            {
                return result;
            }

            int i = 0, n = code.Length;

            void Add(int start, int end, CodeTokenKind kind)
            {
                if (end <= start) return;
                string text = code.Substring(start, end - start);
                int last = result.Count - 1;
                if (last >= 0 && result[last].Kind == kind) result[last] = new CodeToken(kind, result[last].Text + text);
                else result.Add(new CodeToken(kind, text));
            }

            int Find(string what, int from)
            {
                int at = code.IndexOf(what, from, StringComparison.Ordinal);
                return at < 0 ? n : at + what.Length;
            }

            while (i < n)
            {
                if (string.CompareOrdinal(code, i, "<!--", 0, 4) == 0)
                {
                    int end = Find("-->", i + 4);
                    Add(i, end, CodeTokenKind.Comment);
                    i = end;
                }
                else if (string.CompareOrdinal(code, i, "<![CDATA[", 0, 9) == 0)
                {
                    int end = Find("]]>", i + 9);
                    Add(i, end, CodeTokenKind.String);
                    i = end;
                }
                else if (code[i] == '<')
                {
                    i = Tag(code, i, Add);
                }
                else
                {
                    int end = code.IndexOf('<', i);
                    end = end < 0 ? n : end;
                    Add(i, end, CodeTokenKind.Plain);
                    i = end;
                }
            }

            return result;
        }

        /// <summary>One tag from its '&lt;': name, then attribute names and quoted values, to its '&gt;'.</summary>
        private static int Tag(string code, int i, Action<int, int, CodeTokenKind> add)
        {
            int n = code.Length;
            int start = i;
            i++; // '<'
            while (i < n && (code[i] == '/' || code[i] == '?' || code[i] == '!')) i++;
            add(start, i, CodeTokenKind.Plain);

            int name = i;
            while (i < n && IsNameChar(code[i])) i++;
            add(name, i, CodeTokenKind.Markup);

            while (i < n && code[i] != '>' && code[i] != '<')
            {
                char c = code[i];
                if (c == '"' || c == '\'')
                {
                    int close = code.IndexOf(c, i + 1);
                    int end = close < 0 ? n : close + 1;
                    add(i, end, CodeTokenKind.String);
                    i = end;
                }
                else if (IsNameChar(c))
                {
                    int end = i;
                    while (end < n && IsNameChar(code[end])) end++;
                    add(i, end, CodeTokenKind.AttributeName);
                    i = end;
                }
                else
                {
                    add(i, i + 1, CodeTokenKind.Plain);
                    i++;
                }
            }

            if (i < n && code[i] == '>')
            {
                add(i, i + 1, CodeTokenKind.Plain);
                i++;
            }

            return i;
        }

        private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == ':' || c == '-' || c == '.';
    }
}
