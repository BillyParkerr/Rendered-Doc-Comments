using System.Collections.Generic;
using System.Windows.Media;

namespace RenderedDocComments.Rendering
{
    /// <summary>
    /// Colours C# from doc comments: <see cref="CSharpTokenizer"/> finds the runs, and the
    /// colours come from the editor's own classification settings (via <see cref="DocTheme"/>).
    /// </summary>
    internal static class CSharpHighlighter
    {
        public static List<KeyValuePair<string, Brush>> Highlight(string code, DocTheme t) => Highlight(code, null, t);

        /// <param name="language">A code block's language attribute (null: C#). See <see cref="CodeLanguages"/>.</param>
        public static List<KeyValuePair<string, Brush>> Highlight(string code, string language, DocTheme t)
        {
            var result = new List<KeyValuePair<string, Brush>>();
            foreach (var token in CodeLanguages.Tokenize(code, language))
            {
                var brush = BrushFor(token.Kind, t);
                int last = result.Count - 1;
                if (last >= 0 && ReferenceEquals(result[last].Value, brush))
                {
                    result[last] = new KeyValuePair<string, Brush>(result[last].Key + token.Text, brush);
                }
                else
                {
                    result.Add(new KeyValuePair<string, Brush>(token.Text, brush));
                }
            }

            return result;
        }

        public static Brush BrushFor(CodeTokenKind kind, DocTheme t)
        {
            switch (kind)
            {
                case CodeTokenKind.Keyword: return t.Keyword;
                case CodeTokenKind.ControlKeyword: return t.ControlKeyword;
                case CodeTokenKind.TypeName: return t.TypeName;
                case CodeTokenKind.MethodName: return t.MethodName;
                case CodeTokenKind.String: return t.StringLiteral;
                case CodeTokenKind.Number: return t.NumberLiteral;
                case CodeTokenKind.Comment: return t.CommentText;
                case CodeTokenKind.Markup: return t.Keyword;
                case CodeTokenKind.AttributeName: return t.Parameter;
                case CodeTokenKind.Parameter: return t.Parameter;
                default: return t.PlainText;
            }
        }
    }
}
