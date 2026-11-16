using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using RenderedDocComments.Diagnostics;
using RenderedDocComments.Editor;
using RenderedDocComments.Options;

namespace RenderedDocComments.Rendering
{
    /// <summary>
    /// Colours and fonts for the cards, derived from the editor's own settings so the card
    /// follows the VS theme (and Tools > Options > Fonts and Colors) instead of hard-coded values,
    /// plus the extension's own options (card font and size).
    /// </summary>
    internal sealed class DocTheme
    {
        private static DocTheme _current;
        private static bool _subscribed;

        public static int Version { get; private set; }

        public static event EventHandler Changed;

        /// <summary>The current theme; falls back to built-in colours if the editor's settings can't be read.</summary>
        public static DocTheme Current => _current ??=
            Guard.Run("Reading theme colours", () => Create(useEditorSettings: true), null) ?? Create(useEditorSettings: false);

        public bool IsDark { get; private set; }

        /// <summary>The editor's font size; code in cards is sized from this.</summary>
        public double FontSize { get; private set; }

        /// <summary>Card text size: the editor's font size scaled by the "Text size" option.</summary>
        public double ProseFontSize { get; private set; }

        public FontFamily CodeFont { get; private set; }
        public FontFamily ProseFont { get; private set; }
        public Brush EditorBackground { get; private set; }
        public Brush SelectionBackground { get; private set; }
        public Brush PlainText { get; private set; }
        public Brush Prose { get; private set; }
        public Brush Muted { get; private set; }
        public Brush CardBackground { get; private set; }
        public Brush CardBorder { get; private set; }
        public Brush CodeBackground { get; private set; }
        public Brush InlineCodeBackground { get; private set; }
        public Brush Link { get; private set; }
        public Brush TypeName { get; private set; }
        public Brush Keyword { get; private set; }
        public Brush ControlKeyword { get; private set; }
        public Brush Parameter { get; private set; }
        public Brush MethodName { get; private set; }
        public Brush StringLiteral { get; private set; }
        public Brush NumberLiteral { get; private set; }
        public Brush CommentText { get; private set; }

        /// <summary>Rebuild every card with fresh colours and fonts (theme, font or option change).</summary>
        public static void Invalidate()
        {
            _current = null;
            Version++;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        private static DocTheme Create(bool useEditorSettings)
        {
            if (useEditorSettings) ExtensionOptions.EnsureLoaded();

            Color background = Colors.White;
            Color foreground = Colors.Black;
            FontFamily codeFont = new FontFamily("Cascadia Mono, Consolas");
            double fontSize = 13.0;
            Color? typeName = null, keyword = null, control = null, parameter = null, method = null;
            Color? text = null, number = null, comment = null, selection = null;

            var services = useEditorSettings ? EditorServices.Instance : null;
            if (services != null)
            {
                var formatMap = services.FormatMapService.GetEditorFormatMap("text");
                var classificationMap = services.ClassificationFormatMapService.GetClassificationFormatMap("text");
                if (!_subscribed)
                {
                    formatMap.FormatMappingChanged += (s, e) => Invalidate();
                    classificationMap.ClassificationFormatMappingChanged += (s, e) => Invalidate();
                    ExtensionOptions.Changed += (s, e) => Invalidate();
                    _subscribed = true;
                }

                background = ReadColor(formatMap.GetProperties("TextView Background")) ?? background;
                selection = ReadColor(formatMap.GetProperties("Selected Text"));
                var defaults = classificationMap.DefaultTextProperties;
                foreground = (defaults.ForegroundBrush as SolidColorBrush)?.Color
                    ?? (IsDarkColor(background) ? Color.FromRgb(0xDC, 0xDC, 0xDC) : Colors.Black);
                codeFont = defaults.Typeface?.FontFamily ?? codeFont;
                fontSize = defaults.FontRenderingEmSize > 0 ? defaults.FontRenderingEmSize : fontSize;

                typeName = Classified(services, classificationMap, "class name");
                keyword = Classified(services, classificationMap, "keyword");
                control = Classified(services, classificationMap, "keyword - control");
                parameter = Classified(services, classificationMap, "parameter name");
                method = Classified(services, classificationMap, "method name");
                text = Classified(services, classificationMap, "string");
                number = Classified(services, classificationMap, "number");
                comment = Classified(services, classificationMap, "comment");
            }

            bool dark = IsDarkColor(background);
            Color keywordColor = keyword ?? (dark ? Color.FromRgb(0x56, 0x9C, 0xD6) : Color.FromRgb(0x00, 0x00, 0xFF));
            return new DocTheme
            {
                IsDark = dark,
                FontSize = fontSize,
                ProseFontSize = fontSize * Math.Max(50, Math.Min(200, ExtensionOptions.ProseFontSizePercent)) / 100.0,
                CodeFont = codeFont,
                ProseFont = ProseFontFrom(ExtensionOptions.ProseFontFamily),
                EditorBackground = Freeze(background),
                SelectionBackground = Freeze(selection ?? (dark ? Color.FromRgb(0x26, 0x4F, 0x78) : Color.FromRgb(0xAD, 0xD6, 0xFF))),
                PlainText = Freeze(foreground),
                Prose = Freeze(Mix(background, foreground, 0.88)),
                Muted = Freeze(Mix(background, foreground, 0.60)),
                CardBackground = Freeze(Mix(background, foreground, dark ? 0.045 : 0.03)),
                CardBorder = Freeze(Mix(background, foreground, dark ? 0.14 : 0.12)),
                CodeBackground = Freeze(dark ? Mix(background, Colors.Black, 0.25) : Mix(background, foreground, 0.06)),
                InlineCodeBackground = Freeze(Mix(background, foreground, 0.10)),
                Link = Freeze(dark ? Color.FromRgb(0x6C, 0xB6, 0xFF) : Color.FromRgb(0x0B, 0x63, 0xC5)),
                TypeName = Freeze(typeName ?? (dark ? Color.FromRgb(0x4E, 0xC9, 0xB0) : Color.FromRgb(0x2B, 0x91, 0xAF))),
                Keyword = Freeze(keywordColor),
                ControlKeyword = Freeze(control ?? (dark ? Color.FromRgb(0xD8, 0xA0, 0xDF) : Color.FromRgb(0x8F, 0x08, 0xC4))),
                Parameter = Freeze(parameter ?? (dark ? Color.FromRgb(0x9C, 0xDC, 0xFE) : Color.FromRgb(0x1F, 0x37, 0x7F))),
                MethodName = Freeze(method ?? (dark ? Color.FromRgb(0xDC, 0xDC, 0xAA) : Color.FromRgb(0x74, 0x53, 0x1F))),
                StringLiteral = Freeze(text ?? (dark ? Color.FromRgb(0xD6, 0x9D, 0x85) : Color.FromRgb(0xA3, 0x15, 0x15))),
                NumberLiteral = Freeze(number ?? (dark ? Color.FromRgb(0xB5, 0xCE, 0xA8) : Color.FromRgb(0x09, 0x86, 0x58))),
                CommentText = Freeze(comment ?? (dark ? Color.FromRgb(0x57, 0xA6, 0x4A) : Color.FromRgb(0x00, 0x80, 0x00))),
            };
        }

        private static FontFamily ProseFontFrom(string setting)
        {
            string family = string.IsNullOrWhiteSpace(setting) ? ExtensionOptions.DefaultProseFontFamily : setting;
            return new FontFamily(family + ", " + ExtensionOptions.DefaultProseFontFamily);
        }

        private static Color? ReadColor(ResourceDictionary properties)
        {
            if (properties == null) return null;
            if (properties[EditorFormatDefinition.BackgroundBrushId] is SolidColorBrush brush) return brush.Color;
            if (properties[EditorFormatDefinition.BackgroundColorId] is Color color) return color;
            return null;
        }

        private static Color? Classified(EditorServices services, IClassificationFormatMap map, string name)
        {
            var type = services.ClassificationRegistry.GetClassificationType(name);
            if (type == null) return null;
            var properties = map.GetTextProperties(type);
            return properties.ForegroundBrushEmpty ? null : (properties.ForegroundBrush as SolidColorBrush)?.Color;
        }

        private static bool IsDarkColor(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0 < 0.5;

        private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));

        private static Brush Freeze(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
