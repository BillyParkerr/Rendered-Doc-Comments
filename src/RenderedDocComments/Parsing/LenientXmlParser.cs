using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;

namespace RenderedDocComments.Parsing
{
    /// <summary>An element or text node from a doc comment.</summary>
    internal sealed class DocNode
    {
        /// <summary>Lower-cased element name, or null for a text node.</summary>
        public string Name { get; set; }

        /// <summary>Decoded text for text nodes.</summary>
        public string Text { get; set; }

        public Dictionary<string, string> Attributes { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public List<DocNode> Children { get; } = new List<DocNode>();

        public bool IsText => Name == null;

        public string Attr(string name) => Attributes.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>
    /// A forgiving XML/HTML reader. Comments are often half-typed or contain stray '&lt;', so this
    /// never throws: unknown closing tags are ignored and unclosed tags close at the end.
    /// </summary>
    internal static class LenientXmlParser
    {
        private static readonly HashSet<string> VoidElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "br", "hr", "img" };

        private static readonly HashSet<string> SectionElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "summary", "remarks", "param", "typeparam", "returns", "value", "exception", "example",
        };

        // <inheritdoc/> isn't one: it can also sit inside a section, to inherit just that part.

        /// <summary>Elements that wrap a whole comment, as in compiler / Roslyn documentation XML.</summary>
        private static readonly HashSet<string> WrapperElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "member", "doc" };

        private static readonly Regex AttributePattern = new Regex(
            @"([A-Za-z_][\w:.\-]*)\s*(?:=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+)))?",
            RegexOptions.Compiled);

        public static DocNode Parse(string xml)
        {
            var root = new DocNode { Name = "#root" };
            var stack = new List<DocNode> { root };
            int i = 0;

            while (i < xml.Length)
            {
                int lt = xml.IndexOf('<', i);
                if (lt < 0)
                {
                    AddText(stack, xml.Substring(i), decode: true);
                    break;
                }

                if (lt > i)
                {
                    AddText(stack, xml.Substring(i, lt - i), decode: true);
                }

                if (StartsWithAt(xml, lt, "<!--"))
                {
                    int end = xml.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                    i = end < 0 ? xml.Length : end + 3;
                    continue;
                }

                if (StartsWithAt(xml, lt, "<![CDATA["))
                {
                    int end = xml.IndexOf("]]>", lt + 9, StringComparison.Ordinal);
                    AddText(stack, end < 0 ? xml.Substring(lt + 9) : xml.Substring(lt + 9, end - lt - 9), decode: false);
                    i = end < 0 ? xml.Length : end + 3;
                    continue;
                }

                int gt = IsTagStart(xml, lt) ? FindTagEnd(xml, lt + 1) : -1;
                if (gt < 0)
                {
                    AddText(stack, "<", decode: false);
                    i = lt + 1;
                    continue;
                }

                string inner = xml.Substring(lt + 1, gt - lt - 1).Trim();
                i = gt + 1;

                if (inner.StartsWith("/", StringComparison.Ordinal))
                {
                    Close(stack, inner.Substring(1).Trim());
                    continue;
                }

                bool selfClosing = inner.EndsWith("/", StringComparison.Ordinal);
                if (selfClosing)
                {
                    inner = inner.Substring(0, inner.Length - 1);
                }

                var node = ParseTag(inner);

                // Sections are always top-level (or directly inside a <member>/<doc> wrapper), so a
                // new one closes anything left open: a <summary> missing its </summary> doesn't
                // swallow the <param>s.
                if (SectionElements.Contains(node.Name))
                {
                    int top = SectionParentIndex(stack);
                    if (stack.Count > top + 1)
                    {
                        stack.RemoveRange(top + 1, stack.Count - top - 1);
                    }
                }

                stack[stack.Count - 1].Children.Add(node);
                if (!selfClosing && !VoidElements.Contains(node.Name))
                {
                    stack.Add(node);
                }
            }

            return root;
        }

        /// <summary>All text under a node, without whitespace normalisation.</summary>
        public static string InnerText(DocNode node)
        {
            if (node.IsText)
            {
                return node.Text;
            }

            var parts = new System.Text.StringBuilder();
            foreach (var child in node.Children)
            {
                parts.Append(InnerText(child));
            }

            return parts.ToString();
        }

        /// <summary>Where sections live: the innermost open documentation wrapper, else the root.</summary>
        private static int SectionParentIndex(List<DocNode> stack)
        {
            for (int k = stack.Count - 1; k >= 1; k--)
            {
                if (WrapperElements.Contains(stack[k].Name))
                {
                    return k;
                }
            }

            return 0;
        }

        private static bool StartsWithAt(string s, int index, string value) =>
            string.CompareOrdinal(s, index, value, 0, value.Length) == 0;

        private static bool IsTagStart(string s, int lt) =>
            lt + 1 < s.Length && (char.IsLetter(s[lt + 1]) || s[lt + 1] == '/');

        private static int FindTagEnd(string s, int from)
        {
            char quote = '\0';
            for (int k = from; k < s.Length; k++)
            {
                char c = s[k];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                }
                else if (c == '>')
                {
                    return k;
                }
                else if (c == '<')
                {
                    return -1;
                }
            }

            return -1;
        }

        private static void Close(List<DocNode> stack, string name)
        {
            for (int k = stack.Count - 1; k >= 1; k--)
            {
                if (string.Equals(stack[k].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    stack.RemoveRange(k, stack.Count - k);
                    return;
                }
            }
        }

        private static DocNode ParseTag(string inner)
        {
            int k = 0;
            while (k < inner.Length && !char.IsWhiteSpace(inner[k]))
            {
                k++;
            }

            var node = new DocNode { Name = inner.Substring(0, k).ToLowerInvariant() };
            foreach (Match m in AttributePattern.Matches(inner.Substring(k)))
            {
                string value = m.Groups[2].Success ? m.Groups[2].Value
                    : m.Groups[3].Success ? m.Groups[3].Value
                    : m.Groups[4].Success ? m.Groups[4].Value
                    : string.Empty;
                node.Attributes[m.Groups[1].Value] = WebUtility.HtmlDecode(value);
            }

            return node;
        }

        private static void AddText(List<DocNode> stack, string text, bool decode)
        {
            if (text.Length == 0)
            {
                return;
            }

            stack[stack.Count - 1].Children.Add(new DocNode { Text = decode ? WebUtility.HtmlDecode(text) : text });
        }
    }
}
