using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Text;

namespace RenderedDocComments.Parsing
{
    /// <summary>
    /// A stable name for each doc comment in a file, used to remember card/raw choices between
    /// sessions. It's the declaration the comment documents (attribute lines skipped), so it
    /// survives edits elsewhere in the file and edits to the comment itself; "#n" tells apart
    /// declarations that read the same.
    /// </summary>
    internal static class CommentKeys
    {
        private const int MaxLinesToDeclaration = 10;
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

        public static List<KeyValuePair<DocCommentBlock, string>> For(ITextSnapshot snapshot)
        {
            var result = new List<KeyValuePair<DocCommentBlock, string>>();
            var seen = new Dictionary<string, int>();
            foreach (var block in DocCommentScanner.GetBlocks(snapshot))
            {
                string anchor = DeclarationText(snapshot, block);
                if (anchor.Length == 0)
                {
                    // Nothing below it (end of file): fall back to the comment's own text.
                    anchor = "comment: " + Normalize(block.Xml);
                    if (anchor.Length > 200) anchor = anchor.Substring(0, 200);
                }

                seen.TryGetValue(anchor, out int count);
                seen[anchor] = count + 1;
                result.Add(new KeyValuePair<DocCommentBlock, string>(block, anchor + "#" + count));
            }

            return result;
        }

        /// <summary>The first line after the comment that isn't blank or an attribute.</summary>
        private static string DeclarationText(ITextSnapshot snapshot, DocCommentBlock block)
        {
            int last = System.Math.Min(snapshot.LineCount - 1, block.EndLine + MaxLinesToDeclaration);
            for (int line = block.EndLine + 1; line <= last; line++)
            {
                string text = Normalize(snapshot.GetLineFromLineNumber(line).GetText());
                if (text.Length == 0 || text.StartsWith("[", System.StringComparison.Ordinal)) continue;
                if (text.StartsWith("///", System.StringComparison.Ordinal)) return string.Empty; // Another comment: no declaration.
                return text;
            }

            return string.Empty;
        }

        private static string Normalize(string text) => Whitespace.Replace(text, " ").Trim();
    }
}
