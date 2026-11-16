using System.Linq;

namespace RenderedDocComments.Options
{
    /// <summary>Typed shortcut text in VS's form, for the quick documentation shortcut option.</summary>
    internal static class ShortcutText
    {
        /// <summary>
        /// "ctrl + shift + f1" becomes "Ctrl+Shift+F1", and "ctrl+k,ctrl+q" becomes
        /// "Ctrl+K, Ctrl+Q" (two keys in a row). Empty or blank text stays empty.
        /// </summary>
        public static string Normalize(string keys)
        {
            if (string.IsNullOrWhiteSpace(keys))
            {
                return string.Empty;
            }

            var chords = keys.Split(',')
                .Select(chord => string.Join("+", chord.Split('+').Select(k => Key(k.Trim())).Where(k => k.Length > 0)))
                .Where(chord => chord.Length > 0);
            return string.Join(", ", chords);
        }

        private static string Key(string key)
        {
            switch (key.ToLowerInvariant())
            {
                case "": return string.Empty;
                case "ctrl":
                case "control": return "Ctrl";
                case "shift": return "Shift";
                case "alt": return "Alt";
                default: return key.Length <= 3 ? key.ToUpperInvariant() : char.ToUpperInvariant(key[0]) + key.Substring(1);
            }
        }
    }
}
