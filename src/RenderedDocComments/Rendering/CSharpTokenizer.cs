using System;
using System.Collections.Generic;

namespace RenderedDocComments.Rendering
{
    internal enum CodeTokenKind
    {
        Plain,
        Keyword,
        ControlKeyword,
        TypeName,
        MethodName,
        String,
        Number,
        Comment,

        /// <summary>An element name in XML/HTML.</summary>
        Markup,

        /// <summary>An attribute name in XML/HTML.</summary>
        AttributeName,

        /// <summary>A parameter name (in a signature from Roslyn).</summary>
        Parameter,
    }

    /// <summary>A run of code text of one kind.</summary>
    internal struct CodeToken
    {
        public CodeToken(CodeTokenKind kind, string text)
        {
            Kind = kind;
            Text = text;
        }

        public CodeTokenKind Kind { get; }

        public string Text { get; }

        public override string ToString() => Kind + ":" + Text;
    }

    /// <summary>
    /// Splits C# from doc comments (<c>&lt;code&gt;</c> and <c>&lt;c&gt;</c>) into coloured runs.
    /// Snippets aren't compilable on their own, so this works on tokens: keywords, strings,
    /// numbers and comments exactly, and names by shape (Name( is a method, a capitalised name
    /// not after a dot is a type). Adjacent runs of the same kind are joined, and the runs
    /// always add up to the input text exactly.
    /// </summary>
    internal static class CSharpTokenizer
    {
        private static readonly HashSet<string> Keywords = new HashSet<string>
        {
            "abstract", "as", "base", "bool", "byte", "char", "checked", "class", "const", "decimal", "default",
            "delegate", "double", "enum", "event", "explicit", "extern", "false", "fixed", "float", "implicit",
            "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object",
            "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref", "sbyte",
            "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "this", "true", "typeof",
            "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "var",
            "dynamic", "async", "nameof", "when", "where", "record", "init", "required", "get", "set", "add",
            "remove", "value", "global", "not", "and", "or", "with", "from", "select", "let", "orderby", "join",
            "on", "equals", "into", "group", "by", "ascending", "descending", "nint", "nuint", "scoped", "file",
        };

        private static readonly HashSet<string> ControlKeywords = new HashSet<string>
        {
            "if", "else", "switch", "case", "do", "for", "foreach", "while", "break", "continue", "goto",
            "return", "throw", "try", "catch", "finally", "yield", "await",
        };

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
                if (last >= 0 && result[last].Kind == kind)
                {
                    result[last] = new CodeToken(kind, result[last].Text + text);
                }
                else
                {
                    result.Add(new CodeToken(kind, text));
                }
            }

            while (i < n)
            {
                char c = code[i];
                char next = i + 1 < n ? code[i + 1] : '\0';

                if (c == '/' && next == '/')
                {
                    int end = code.IndexOf('\n', i);
                    end = end < 0 ? n : end;
                    Add(i, end, CodeTokenKind.Comment);
                    i = end;
                }
                else if (c == '/' && next == '*')
                {
                    int end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? n : end + 2;
                    Add(i, end, CodeTokenKind.Comment);
                    i = end;
                }
                else if (IsStringStart(code, i, out int quote, out bool verbatim))
                {
                    int end = ReadString(code, quote, verbatim);
                    Add(i, end, CodeTokenKind.String);
                    i = end;
                }
                else if (c == '\'')
                {
                    int end = ReadString(code, i, verbatim: false, terminator: '\'');
                    Add(i, end, CodeTokenKind.String);
                    i = end;
                }
                else if (char.IsDigit(c))
                {
                    int end = i;
                    while (end < n && (char.IsLetterOrDigit(code[end]) || code[end] == '_' || (code[end] == '.' && end + 1 < n && char.IsDigit(code[end + 1])))) end++;
                    Add(i, end, CodeTokenKind.Number);
                    i = end;
                }
                else if (char.IsLetter(c) || c == '_' || (c == '@' && char.IsLetter(next)))
                {
                    int end = i + 1;
                    while (end < n && (char.IsLetterOrDigit(code[end]) || code[end] == '_')) end++;
                    Add(i, end, Classify(code, i, end));
                    i = end;
                }
                else
                {
                    Add(i, i + 1, CodeTokenKind.Plain);
                    i++;
                }
            }

            return result;
        }

        private static CodeTokenKind Classify(string code, int start, int end)
        {
            string word = code.Substring(start, end - start);
            if (ControlKeywords.Contains(word)) return CodeTokenKind.ControlKeyword;
            if (Keywords.Contains(word)) return CodeTokenKind.Keyword;

            int after = end;
            while (after < code.Length && code[after] == ' ') after++;
            if (after < code.Length && code[after] == '(') return CodeTokenKind.MethodName;

            int before = start - 1;
            while (before >= 0 && char.IsWhiteSpace(code[before])) before--;
            if (before >= 0 && code[before] == '.') return CodeTokenKind.Plain; // A member: property, field, etc.

            return char.IsUpper(word.TrimStart('@')[0]) ? CodeTokenKind.TypeName : CodeTokenKind.Plain;
        }

        private static bool IsStringStart(string code, int i, out int quote, out bool verbatim)
        {
            quote = -1;
            verbatim = false;
            int j = i;
            while (j < code.Length && j - i < 2 && (code[j] == '$' || code[j] == '@'))
            {
                if (code[j] == '@') verbatim = true;
                j++;
            }

            if (j < code.Length && code[j] == '"')
            {
                quote = j;
                return true;
            }

            return false;
        }

        private static int ReadString(string code, int quote, bool verbatim, char terminator = '"')
        {
            int k = quote + 1;
            while (k < code.Length)
            {
                char c = code[k];
                if (!verbatim && c == '\\') { k += 2; continue; }
                if (!verbatim && c == '\n') return k;
                if (c == terminator)
                {
                    if (verbatim && k + 1 < code.Length && code[k + 1] == terminator) { k += 2; continue; }
                    return k + 1;
                }

                k++;
            }

            return code.Length;
        }
    }
}
