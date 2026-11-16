using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace RenderedDocComments.Parsing
{
    /// <summary>
    /// Turns a cref into the text a card shows for it ("List&lt;T&gt;.Add()"), from the cref alone.
    /// Cards are built without asking Roslyn, which would be too slow; Roslyn resolves the
    /// symbol only when a link is hovered or clicked.
    /// </summary>
    internal static class CrefFormatter
    {
        private static readonly Regex ArityMarker = new Regex("``?\\d+", RegexOptions.Compiled);

        public sealed class Display
        {
            /// <summary>The type part, shown in the type colour (may be empty).</summary>
            public string TypePart { get; set; } = string.Empty;

            /// <summary>The member part including its leading '.', shown in the plain text colour.</summary>
            public string MemberPart { get; set; } = string.Empty;

            public override string ToString() => TypePart + MemberPart;
        }

        /// <param name="isType">
        /// The cref names a type (as in &lt;exception&gt; and &lt;permission&gt;), so a dotted name
        /// like "System.Security.PermissionSet" is a namespace and type, not a type and member.
        /// </param>
        public static Display Format(string cref, bool isType = false)
        {
            var result = new Display();
            if (string.IsNullOrWhiteSpace(cref))
            {
                return result;
            }

            string s = cref.Trim();
            char kind = '\0';
            if (s.Length > 2 && s[1] == ':' && char.IsLetter(s[0]))
            {
                kind = char.ToUpperInvariant(s[0]);
                s = s.Substring(2);
            }

            int paren = s.IndexOf('(');
            bool isMethod = kind == 'M' || paren >= 0;
            if (paren >= 0)
            {
                s = s.Substring(0, paren);
            }

            s = ArityMarker.Replace(s, string.Empty).Replace('{', '<').Replace('}', '>');
            var parts = SplitOutsideAngles(s);
            bool isTypeOnly = kind == 'T' || kind == 'N'
                || (kind == '\0' && !isMethod && (isType || (parts.Count == 1 && StartsUpper(parts[0]))));

            if (isTypeOnly)
            {
                result.TypePart = parts[parts.Count - 1];
                return result;
            }

            string member = parts[parts.Count - 1].Replace("#ctor", "ctor") + (isMethod ? "()" : string.Empty);
            if (parts.Count >= 2)
            {
                result.TypePart = parts[parts.Count - 2];
                result.MemberPart = "." + member;
            }
            else
            {
                result.MemberPart = member;
            }

            return result;
        }

        private static bool StartsUpper(string s) => s.Length > 0 && char.IsUpper(s[0]);

        private static List<string> SplitOutsideAngles(string s)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            int depth = 0;
            foreach (char c in s)
            {
                if (c == '<') depth++;
                if (c == '>') depth--;
                if (c == '.' && depth == 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            parts.Add(current.ToString());
            return parts;
        }
    }
}
