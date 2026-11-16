using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using RenderedDocComments.Editor;
using RenderedDocComments.Navigation;
using RenderedDocComments.Options;
using RenderedDocComments.Parsing;

namespace RenderedDocComments.Rendering
{
    /// <summary>Builds the Fluent-style WPF card for one doc comment.</summary>
    internal sealed class DocCommentRenderer
    {
        private const double MinCardWidth = 280;

        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

        private static readonly ControlTemplate BareTextBoxTemplate = CreateBareTextBoxTemplate();

        private const double ProseLineHeightFactor = 1.45;
        private const double CodeLineHeightFactor = 1.35;

        /// <summary>Marks a code block's paragraph, so its row label can line up with the code.</summary>
        private static readonly object CodeBlockTag = new object();

        private double ProseLineHeight => Math.Round(_t.ProseFontSize * ProseLineHeightFactor);

        private double CodeLineHeight => Math.Round(_codeSize * CodeLineHeightFactor);

        /// <summary>Where a line's baseline sits as a fraction of its height, for a font.</summary>
        private static double BaselineRatio(FontFamily font) =>
            font.LineSpacing > 0 ? font.Baseline / font.LineSpacing : 0.8;

        private static ControlTemplate CreateBareTextBoxTemplate()
        {
            var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            host.SetValue(UIElement.FocusableProperty, false);
            host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            host.SetValue(Control.PaddingProperty, new Thickness(0));

            var template = new ControlTemplate(typeof(RichTextBox)) { VisualTree = host };
            template.Seal();
            return template;
        }

        private readonly DocTheme _t;
        private readonly ICardLinkHandler _links;
        private readonly double _codeSize;

        /// <summary>The code paragraphs in the card being built, for their rounded backgrounds.</summary>
        private readonly List<Paragraph> _codeBlocks = new List<Paragraph>();

        /// <summary>Width available to the blocks being built: table lists size their columns from it.</summary>
        private double _available = 400;

        /// <summary>Marks notes and table lists, so the text after them gets a block's gap.</summary>
        private static readonly object BlockTag = new object();

        private DocCommentRenderer(DocTheme theme, ICardLinkHandler links)
        {
            _t = theme;
            _links = links;

            // Code, names and links follow the "Text size" option too, so the whole card scales.
            _codeSize = theme.ProseFontSize * 0.95;
        }

        /// <summary>Card width for a viewport, in 8px steps so small resizes don't rebuild every card.</summary>
        public static double CardWidth(double viewportWidth, double indentPixels) =>
            Math.Floor(Math.Max(MinCardWidth, Math.Min(ExtensionOptions.MaxCardWidth, viewportWidth - indentPixels - 48)) / 8) * 8;

        /// <param name="inherited">
        /// For a comment with &lt;inheritdoc/&gt;: what it inherits, or null while that's still being looked up.
        /// </param>
        public static FrameworkElement CreateCard(DocCommentBlock block, double width, ICardLinkHandler links, InheritedDoc inherited)
        {
            var renderer = new DocCommentRenderer(DocTheme.Current, links);
            var model = DocModel.Parse(block.Xml);
            bool merged = false;
            if (inherited?.Xml != null)
            {
                // <inheritdoc/> inside a section (e.g. in the summary) brings in that part in place.
                merged = model.ExpandNestedInheritDoc(inherited.Xml) > 0;

                // A top-level one fills in the sections this comment doesn't have; a path picks
                // which part of the inherited docs that is.
                if (model.InheritDoc != null)
                {
                    string selected = InheritDocPath.Select(inherited.Xml, model.InheritDoc.Attr("path"));
                    if (selected != null) model.MergeInherited(DocModel.Parse(selected));
                    merged = true;
                }
            }

            return renderer.BuildCard(model, width, compact: block.IsSingleLine && !merged, inherited, merged);
        }

        /// <summary>
        /// Used if rendering a comment throws: a plain card with the comment's text, built from
        /// nothing but basic controls so it can't fail the same way.
        /// </summary>
        public static FrameworkElement CreateFallbackCard(DocCommentBlock block, double width)
        {
            var t = DocTheme.Current;
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = "Couldn't render this comment; showing its text.",
                Foreground = t.Muted,
                FontStyle = FontStyles.Italic,
                FontSize = t.FontSize * 0.9,
                Margin = new Thickness(0, 0, 0, 4),
            });
            panel.Children.Add(new TextBlock
            {
                Text = block.Xml,
                FontFamily = t.CodeFont,
                FontSize = t.FontSize * 0.95,
                Foreground = t.Prose,
                TextWrapping = TextWrapping.Wrap,
            });

            var card = new Border
            {
                Background = t.CardBackground,
                BorderBrush = t.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 9),
                Margin = new Thickness(0, 3, 0, 4),
                Width = width,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = panel,
            };
            card.MouseLeftButtonDown += (s, e) => e.Handled = true;
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return card;
        }

        private const double MinQuickDocWidth = 240;

        private static readonly Thickness QuickDocPadding = new Thickness(12, 8, 12, 9);

        /// <summary>
        /// The quick documentation popup's card: where the symbol lives, its signature (types in it
        /// link to their docs, its name to its definition), then its documentation laid out as on
        /// a comment's card. Only as wide as its content needs, up to <paramref name="maxWidth"/>.
        /// </summary>
        /// <param name="back">The previous symbol's title, for a Back link; null for none.</param>
        public static FrameworkElement CreateQuickDocCard(QuickDocContent content, double maxWidth, string back, IQuickDocHandler handler)
        {
            maxWidth = Math.Max(MinQuickDocWidth, maxWidth);
            var card = new DocCommentRenderer(DocTheme.Current, handler).BuildQuickDoc(content, maxWidth, back, handler, out double natural);

            // Short docs get a narrower card. Columns are fixed when a card is built, so build it again.
            // The slack covers the text box's own inset and glyph widths rounded to pixels in VS.
            double width = Math.Max(MinQuickDocWidth, Math.Ceiling(natural) + QuickDocPadding.Left + QuickDocPadding.Right + 2 + 14);
            if (width < maxWidth - 1)
            {
                card = new DocCommentRenderer(DocTheme.Current, handler).BuildQuickDoc(content, width, back, handler, out _);
            }

            return card;
        }

        private FrameworkElement BuildQuickDoc(QuickDocContent c, double width, string back, IQuickDocHandler handler, out double natural)
        {
            var box = NewTextBox();
            var blocks = box.Document.Blocks;
            double textWidth = width - QuickDocPadding.Left - QuickDocPadding.Right - 2;
            _available = textWidth;

            if (back != null)
            {
                blocks.Add(BackLine(back, handler));
            }

            if (c.Container != null || c.Assembly != null)
            {
                blocks.Add(ContainerLine(c, blocks.Count > 0 ? 2 : 0));
            }

            var signature = SignatureLine(c, handler, blocks.Count > 0 ? 2 : 0);
            blocks.Add(signature);

            var model = string.IsNullOrWhiteSpace(c.Xml) ? null : DocModel.Parse(c.Xml);
            var docs = new Section { Margin = new Thickness(0, 8, 0, 0) };
            if (model != null && !model.IsEmpty)
            {
                // Docs from another member get an "Inherited from" row. An <inheritdoc/> still in
                // the docs found nothing to inherit.
                bool fromElsewhere = c.InheritedFrom != null;
                var inherited = fromElsewhere ? new InheritedDoc(c.Xml, c.InheritedFrom) : InheritedDoc.NotFound;
                AddDocumentation(docs.Blocks, model, inherited, fromElsewhere, textWidth);
            }

            // With no docs the popup is just the signature, nothing more.
            if (docs.Blocks.Count > 0)
            {
                // A rule between the signature and the docs.
                signature.BorderBrush = _t.CardBorder;
                signature.BorderThickness = new Thickness(0, 0, 0, 1);
                signature.Padding = new Thickness(0, 0, 0, 7);
                blocks.Add(docs);
            }

            natural = NaturalWidth(blocks);
            var card = (Border)Frame(box, QuickDocPadding, width);
            card.Margin = new Thickness(0);
            return card;
        }

        private Paragraph BackLine(string title, IQuickDocHandler handler)
        {
            var line = NewParagraph(0);
            line.FontSize = LabelFontSize;
            var link = new Hyperlink(new Run("← " + title))
            {
                TextDecorations = null,
                Cursor = Cursors.Hand,
                Foreground = _t.Link,
                ToolTip = "Back",
            };
            HoverUnderline(link);
            link.Click += (s, e) => handler.Back();
            line.Inlines.Add(link);
            return line;
        }

        /// <summary>Where the symbol is: its namespace (or the member a parameter belongs to), and its assembly.</summary>
        private Paragraph ContainerLine(QuickDocContent c, double top)
        {
            var line = NewParagraph(top);
            line.FontFamily = _t.CodeFont;
            line.FontSize = LabelFontSize;
            line.Foreground = _t.Muted;

            if (c.Container != null)
            {
                if (c.ContainerId != null)
                {
                    var link = (Hyperlink)CrefLink(c.ContainerId, c.Container);
                    foreach (var run in link.Inlines.OfType<Run>()) run.Foreground = _t.Muted;
                    line.Inlines.Add(link);
                }
                else
                {
                    line.Inlines.Add(new Run(c.Container));
                }
            }

            if (c.Assembly != null)
            {
                if (c.Container != null) line.Inlines.Add(new Run(" · "));
                line.Inlines.Add(new Run(c.Assembly) { FontFamily = _t.ProseFont });
            }

            return line;
        }

        /// <summary>The signature in code colours: the symbol's name goes to its definition, and types show their docs.</summary>
        private Paragraph SignatureLine(QuickDocContent c, IQuickDocHandler handler, double top)
        {
            var line = NewParagraph(top);
            line.FontFamily = _t.CodeFont;
            line.FontSize = _codeSize;
            line.Foreground = _t.PlainText;

            if (c.Kind != null)
            {
                line.Inlines.Add(new Run("(" + c.Kind + ") ") { Foreground = _t.Muted });
            }

            foreach (var part in c.Signature)
            {
                var brush = CSharpHighlighter.BrushFor(part.Kind, _t);
                if (part.IsName)
                {
                    var name = new Hyperlink(new Run(part.Text))
                    {
                        TextDecorations = null,
                        Cursor = Cursors.Hand,
                        Foreground = brush,
                        FontWeight = FontWeights.SemiBold,
                        ToolTip = "Go to definition",
                    };
                    HoverUnderline(name);
                    name.Click += (s, e) => handler.GoToShownDefinition();
                    line.Inlines.Add(name);
                }
                else if (part.LinkId != null)
                {
                    var link = (Hyperlink)CrefLink(part.LinkId, part.Text);
                    foreach (var run in link.Inlines.OfType<Run>()) run.Foreground = brush;
                    line.Inlines.Add(link);
                }
                else
                {
                    line.Inlines.Add(new Run(part.Text) { Foreground = brush });
                }
            }

            return line;
        }

        /// <summary>
        /// About how wide the blocks' content is with no wrapping, from their text's measured
        /// width plus indents and fixed table columns. Used to fit the popup to short docs.
        /// </summary>
        private double NaturalWidth(BlockCollection blocks) =>
            blocks.Select(NaturalWidth).DefaultIfEmpty(0).Max();

        private double NaturalWidth(Block block)
        {
            double inset = block.Margin.Left + block.Margin.Right + block.Padding.Left + block.Padding.Right
                + block.BorderThickness.Left + block.BorderThickness.Right;
            switch (block)
            {
                case Paragraph paragraph:
                    double widest = 0, current = 0;
                    MeasureInlines(paragraph.Inlines, ref widest, ref current);
                    return inset + Math.Max(widest, current);

                case Section section:
                    return inset + NaturalWidth(section.Blocks);

                case List list:
                    return inset + list.ListItems.Select(i => i.Margin.Left + NaturalWidth(i.Blocks)).DefaultIfEmpty(0).Max();

                case Table table:
                    double fixedColumn = table.Columns.Count > 0 ? table.Columns[0].Width.Value : 0;
                    return inset + table.RowGroups.SelectMany(g => g.Rows).Select(row =>
                    {
                        var last = row.Cells[row.Cells.Count - 1];
                        double lastWidth = last.Padding.Left + last.Padding.Right + NaturalWidth(last.Blocks);
                        return row.Cells.Count > 1 ? fixedColumn + lastWidth : lastWidth;
                    }).DefaultIfEmpty(0).Max();

                default:
                    return double.PositiveInfinity; // Something else: give it the full width.
            }
        }

        private static void MeasureInlines(InlineCollection inlines, ref double widest, ref double current)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run run when run.Text.Length > 0:
                        var typeface = new Typeface(run.FontFamily, run.FontStyle, run.FontWeight, run.FontStretch);
                        current += new FormattedText(run.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, run.FontSize, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
                        break;
                    case LineBreak _:
                        widest = Math.Max(widest, current);
                        current = 0;
                        break;
                    case Span span:
                        MeasureInlines(span.Inlines, ref widest, ref current);
                        break;
                }
            }
        }

        /// <param name="merged">Inherited docs were merged in: they get an "Inherited from" row.</param>
        private FrameworkElement BuildCard(DocModel model, double width, bool compact, InheritedDoc inherited, bool merged)
        {
            // The whole card is one document in one text box, so text can be selected and copied
            // across all of it (summary, sections, code), and a card builds one control instead of
            // one per paragraph.
            var box = NewTextBox();
            var blocks = box.Document.Blocks;
            var padding = compact ? new Thickness(10, 3, 10, 3) : new Thickness(12, 8, 12, 9);
            double textWidth = width - padding.Left - padding.Right - 2; // Less the border.
            _available = textWidth;

            AddDocumentation(blocks, model, inherited, merged, textWidth);
            if (blocks.Count == 0)
            {
                blocks.Add(MutedLine("Empty documentation comment", 0));
            }

            return Frame(box, padding, width);
        }

        /// <summary>The summary, then any notes about inherited or included docs, then the labelled sections.</summary>
        private void AddDocumentation(BlockCollection blocks, DocModel model, InheritedDoc inherited, bool merged, double textWidth)
        {
            if (model.Summary != null)
            {
                AddBlocks(blocks, model.Summary);
            }

            // Until the inherited docs arrive (or when there are none), say where they would come from.
            if (model.InheritDoc != null && !merged)
            {
                blocks.Add(InheritDocLine(model.InheritDoc, inherited, GapAfter(blocks)));
            }

            foreach (var include in model.Includes)
            {
                blocks.Add(IncludeLine(include, GapAfter(blocks)));
            }

            var sections = SectionTable(model, merged ? inherited : null, blocks.Count > 0 ? SectionsGap : 0, textWidth);
            if (sections != null)
            {
                blocks.Add(sections);
            }
        }

        private Paragraph MutedLine(string text, double top)
        {
            var line = NewParagraph(top);
            line.Foreground = _t.Muted;
            line.FontStyle = FontStyles.Italic;
            line.Inlines.Add(new Run(text));
            return line;
        }

        /// <summary>The card around a built text box: its code backdrops, border, background and mouse handling.</summary>
        private FrameworkElement Frame(RichTextBox box, Thickness padding, double width)
        {
            // Code blocks get rounded backgrounds drawn behind the text box (see CodeBackdrops).
            var backdrops = new Canvas { IsHitTestVisible = false };
            var content = new Grid();
            content.Children.Add(backdrops);
            content.Children.Add(box);
            if (_codeBlocks.Count > 0)
            {
                new CodeBackdrops(box, backdrops, _codeBlocks, _t.CodeBackground).Attach();
            }

            var card = new Border
            {
                Background = _t.CardBackground,
                BorderBrush = _t.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = padding,
                Margin = new Thickness(0, 3, 0, 4), // Same gap above and below for every card.
                Width = width,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = content,
                Cursor = Cursors.Arrow,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
            };

            // A plain click on the card keeps it rendered and must not move the caret.
            // The text box and hyperlinks handle their own clicks before these run.
            card.MouseLeftButtonDown += (s, e) => e.Handled = true;
            card.MouseLeftButtonUp += (s, e) => e.Handled = true;

            // The text box would swallow the mouse wheel; pass it on so the editor scrolls.
            card.PreviewMouseWheel += (s, e) =>
            {
                if (e.Handled) return;
                e.Handled = true;
                card.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = card });
            };

            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return card;
        }

        /// <summary>Space between paragraphs, and above the first section row.</summary>
        private const double ParagraphGap = 6;
        private const double BlockGap = 8;
        private const double SectionsGap = 8;
        private const double RowGap = 6;
        private const double ItemGap = 3;

        /// <summary>
        /// The labelled sections (parameters, returns, remarks...) as a two-column table: labels on
        /// the left, as wide as the widest label, and content on the right.
        /// </summary>
        /// <param name="inheritedFrom">Merged inherited docs, shown as a final "Inherited from" row.</param>
        private Table SectionTable(DocModel m, InheritedDoc inheritedFrom, double top, double textWidth)
        {
            var rows = new List<KeyValuePair<string, Action<BlockCollection>>>();
            if (m.TypeParams.Count > 0) rows.Add(Row("Type parameters", b => NamedList(b, m.TypeParams, s => CodeRun(s.Name, _t.TypeName))));
            if (m.Params.Count > 0) rows.Add(Row("Parameters", b => NamedList(b, m.Params, s => CodeRun(s.Name, _t.Parameter))));
            if (HasContent(m.Returns)) rows.Add(Row("Returns", b => AddBlocks(b, m.Returns)));
            if (HasContent(m.Value)) rows.Add(Row("Value", b => AddBlocks(b, m.Value)));
            if (m.Exceptions.Count > 0) rows.Add(Row("Exceptions", b => NamedList(b, m.Exceptions, s => CrefLink(s.Name, null, isType: true))));
            if (m.Permissions.Count > 0) rows.Add(Row("Permissions", b => NamedList(b, m.Permissions, s => CrefLink(s.Name, null, isType: true))));
            if (HasContent(m.Remarks)) rows.Add(Row("Remarks", b => AddBlocks(b, m.Remarks)));
            if (m.ThreadSafety != null) rows.Add(Row("Thread safety", b => ThreadSafety(b, m.ThreadSafety)));
            if (HasContent(m.Example)) rows.Add(Row("Example", b => AddBlocks(b, m.Example)));
            foreach (var custom in m.Custom.Where(c => HasContent(c.Body)))
            {
                rows.Add(Row(Humanize(custom.Name), b => AddBlocks(b, custom.Body)));
            }

            if (m.SeeAlso.Count > 0) rows.Add(Row("See also", b => b.Add(SeeAlsoLine(m.SeeAlso))));
            if (inheritedFrom != null) rows.Add(Row("Inherited from", b => b.Add(InheritedFromLine(inheritedFrom.SourceId))));

            if (rows.Count == 0)
            {
                return null;
            }

            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, top, 0, 0), Padding = new Thickness(0) };
            // Both columns in pixels, adding up to the card's text width: a document table shares
            // its width out in proportion to the columns' widths when they mix pixels and stars.
            double labelWidth = LabelColumnWidth(rows.Select(r => r.Key));
            table.Columns.Add(new TableColumn { Width = new GridLength(labelWidth) });
            table.Columns.Add(new TableColumn { Width = new GridLength(Math.Max(40, textWidth - labelWidth)) });
            var group = new TableRowGroup();
            table.RowGroups.Add(group);

            foreach (var row in rows)
            {
                double rowTop = group.Rows.Count > 0 ? RowGap : 0;
                var body = new TableCell { Padding = new Thickness(0, rowTop, 0, 0) };
                double available = _available;
                _available = Math.Max(40, textWidth - labelWidth);
                row.Value(body.Blocks);
                _available = available;

                // Same line height and stacking as the content's text, so both baselines line up.
                // A row that opens with a code block gets its label moved down to the code's first line.
                double labelTop = 0;
                if (body.Blocks.FirstBlock is Paragraph first && ReferenceEquals(first.Tag, CodeBlockTag))
                {
                    labelTop = first.Margin.Top + first.Padding.Top
                        + CodeLineHeight * BaselineRatio(_t.CodeFont)
                        - ProseLineHeight * BaselineRatio(_t.ProseFont);
                }

                var label = new Paragraph(new Run(row.Key))
                {
                    Foreground = _t.Muted,
                    FontSize = LabelFontSize,
                    FontWeight = FontWeights.Medium,
                    Margin = new Thickness(0, Math.Max(0, labelTop), 0, 0),
                };

                var tableRow = new TableRow();
                tableRow.Cells.Add(new TableCell(label) { Padding = new Thickness(0, rowTop, 0, 0) });
                tableRow.Cells.Add(body);
                group.Rows.Add(tableRow);
            }

            return table;
        }

        private static KeyValuePair<string, Action<BlockCollection>> Row(string label, Action<BlockCollection> fill) =>
            new KeyValuePair<string, Action<BlockCollection>>(label, fill);

        private double LabelFontSize => _t.ProseFontSize * 0.92;

        /// <summary>The widest label plus a gap (document table columns can't size themselves to their content).</summary>
        private double LabelColumnWidth(IEnumerable<string> labels) =>
            Math.Ceiling(labels.Select(l => TextWidth(l, FontWeights.Medium, LabelFontSize)).DefaultIfEmpty(0).Max()) + 16;

        private double TextWidth(string text, FontWeight weight, double size)
        {
            var typeface = new Typeface(_t.ProseFont, FontStyles.Normal, weight, FontStretches.Normal);
            return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
        }

        /// <summary>Parameters, type parameters and exceptions: "name – description", one per item.</summary>
        private void NamedList(BlockCollection target, List<NamedSection> items, Func<NamedSection, Inline> prefix)
        {
            foreach (var item in items)
            {
                var first = NewParagraph(target.Count > 0 ? ItemGap : 0);
                target.Add(first);
                var sink = new InlineSink(first.Inlines);
                sink.Add(prefix(item));
                if (HasContent(item.Body))
                {
                    sink.Text(" – ");
                    AddBlocks(target, item.Body, first, sink);
                }
            }
        }

        private Paragraph NewParagraph(double top) => new Paragraph { Margin = new Thickness(0, top, 0, 0) };

        /// <summary>
        /// Space above a new paragraph: none at the top of its container, a little more after a
        /// code block or list. (A document collapses adjacent margins into the larger one.)
        /// </summary>
        private static double GapAfter(BlockCollection target)
        {
            var last = target.LastBlock;
            if (last == null) return 0;
            return last is List || last is Table || ReferenceEquals(last.Tag, CodeBlockTag) || ReferenceEquals(last.Tag, BlockTag) ? BlockGap : ParagraphGap;
        }

        /// <summary>
        /// Adds a node's content as blocks: runs of inline content become paragraphs, and
        /// para/code/list elements become their own blocks.
        /// </summary>
        private void AddBlocks(BlockCollection target, DocNode node, Paragraph current = null, InlineSink sink = null)
        {
            foreach (var child in node.Children)
            {
                switch (child.Name)
                {
                    case "para":
                    case "p":
                        current = null;
                        sink = null;
                        var para = new Section { Margin = new Thickness(0, GapAfter(target), 0, 0) };
                        AddBlocks(para.Blocks, child);
                        if (para.Blocks.Count > 0) target.Add(para);
                        break;

                    case "code":
                    case "pre":
                        current = null;
                        sink = null;

                        // <code> is C# unless it says otherwise; HTML's <pre> is plain text unless it says.
                        string language = child.Attr("language") ?? child.Attr("lang") ?? (child.Name == "pre" ? CodeLanguages.PlainText : null);
                        target.Add(CodeBlock(child, language));
                        break;

                    case "note":
                        current = null;
                        sink = null;
                        target.Add(NoteBlock(child, GapAfter(target)));
                        break;

                    case "h1":
                    case "h2":
                    case "h3":
                    case "h4":
                    case "h5":
                    case "h6":
                        current = null;
                        sink = null;
                        var heading = NewParagraph(GapAfter(target));
                        heading.FontWeight = FontWeights.SemiBold;
                        ChildrenInline(new InlineSink(heading.Inlines), child);
                        target.Add(heading);
                        break;

                    case "list":
                    case "ul":
                    case "ol":
                        current = null;
                        sink = null;
                        target.Add(ListBlock(child, target.Count > 0));
                        break;

                    default:
                        if (current == null)
                        {
                            if (child.IsText && string.IsNullOrWhiteSpace(child.Text)) break;
                            current = NewParagraph(GapAfter(target));
                            sink = new InlineSink(current.Inlines);
                            target.Add(current);
                        }

                        AddInline(sink, child);
                        break;
                }
            }
        }

        private void AddInline(InlineSink sink, DocNode n)
        {
            switch (n.Name)
            {
                case null:
                    sink.Text(n.Text);
                    break;
                case "c":
                case "tt":
                    sink.Add(InlineCode(LenientXmlParser.InnerText(n).Trim()));
                    break;
                case "b":
                case "strong":
                    Nest(sink, new Bold(), n);
                    break;
                case "i":
                case "em":
                    Nest(sink, new Italic(), n);
                    break;
                case "u":
                    Nest(sink, new Underline(), n);
                    break;
                case "s":
                case "strike":
                case "del":
                    Nest(sink, new Span { TextDecorations = TextDecorations.Strikethrough }, n);
                    break;
                case "sub":
                    Nest(sink, new Span { BaselineAlignment = BaselineAlignment.Subscript, FontSize = _t.ProseFontSize * 0.75 }, n);
                    break;
                case "sup":
                    // Top-aligned rather than Superscript: lines have a fixed height, and Superscript raises
                    // the text past the top of the line, where it gets cut off.
                    Nest(sink, new Span { BaselineAlignment = BaselineAlignment.TextTop, FontSize = _t.ProseFontSize * 0.75 }, n);
                    break;
                case "br":
                    sink.Break();
                    break;
                case "inheritdoc":
                    // Inside a section, until the inherited docs arrive (or when there are none).
                    sink.Add(new Run("(inherited)") { Foreground = _t.Muted, FontStyle = FontStyles.Italic });
                    break;
                case "paramref":
                    sink.Add(CodeRun(n.Attr("name") ?? string.Empty, _t.Parameter));
                    break;
                case "typeparamref":
                    sink.Add(CodeRun(n.Attr("name") ?? string.Empty, _t.TypeName));
                    break;
                case "see":
                case "seealso":
                    sink.Add(SeeInline(n));
                    break;
                case "a":
                    string href = n.Attr("href");
                    if (href != null) sink.Add(UrlLink(href, Label(n)));
                    else ChildrenInline(sink, n);
                    break;
                case "para":
                case "p":
                case "code":
                case "pre":
                case "list":
                case "note":
                    // Block content nested inside inline formatting; keep it readable.
                    sink.Break();
                    ChildrenInline(sink, n);
                    sink.Break();
                    break;
                default:
                    ChildrenInline(sink, n);
                    break;
            }
        }

        private void ChildrenInline(InlineSink sink, DocNode n)
        {
            foreach (var child in n.Children)
            {
                AddInline(sink, child);
            }
        }

        private void Nest(InlineSink sink, Span span, DocNode n)
        {
            sink.Add(span);
            sink.Push(span.Inlines);
            ChildrenInline(sink, n);
            sink.Pop();
        }

        private Inline SeeInline(DocNode n)
        {
            string label = Label(n);
            string langword = n.Attr("langword");
            if (langword != null) return CodeRun(langword, _t.Keyword);
            string href = n.Attr("href");
            if (href != null) return UrlLink(href, label);
            string cref = n.Attr("cref");
            if (cref != null) return CrefLink(cref, label);
            return new Run(label ?? string.Empty);
        }

        /// <param name="isType">The cref names a type (see <see cref="CrefFormatter.Format"/>).</param>
        private Inline CrefLink(string cref, string label, bool isType = false)
        {
            // The tooltip starts as the raw cref and becomes the symbol's signature once Roslyn
            // has resolved it (only done the first time the tooltip opens).
            // Same size as code in the card, so it follows the "Text size" option too.
            var tip = new TextBlock { Text = cref, FontFamily = _t.CodeFont, FontSize = _codeSize };
            var link = new Hyperlink
            {
                TextDecorations = null,
                Cursor = Cursors.Hand,
                Foreground = _t.TypeName,
                ToolTip = tip,
            };

            bool described = false;
            link.ToolTipOpening += (s, e) =>
            {
                if (described || _links == null) return;
                described = true;
                _links.Describe(cref, text => tip.Text = text);
            };

            if (!string.IsNullOrWhiteSpace(label))
            {
                link.Inlines.Add(new Run(label) { Foreground = _t.TypeName });
            }
            else
            {
                var display = CrefFormatter.Format(cref, isType);
                link.FontFamily = _t.CodeFont;
                link.FontSize = _codeSize;
                link.FontStyle = FontStyles.Normal; // Code is never italic, even inside an italic line.
                if (display.TypePart.Length > 0) link.Inlines.Add(new Run(display.TypePart) { Foreground = _t.TypeName });
                if (display.MemberPart.Length > 0) link.Inlines.Add(new Run(display.MemberPart) { Foreground = _t.PlainText });
            }

            HoverUnderline(link);
            link.Click += (s, e) => _links?.GoToDefinition(cref);
            return link;
        }

        private Inline UrlLink(string url, string label)
        {
            var link = new Hyperlink(new Run(string.IsNullOrWhiteSpace(label) ? url : label))
            {
                TextDecorations = null,
                Cursor = Cursors.Hand,
                Foreground = _t.Link,
                ToolTip = new TextBlock { Text = url, FontFamily = _t.ProseFont, FontSize = _t.ProseFontSize },
            };
            link.Inlines.Add(new Run(" ↗") { FontSize = _t.ProseFontSize * 0.85 });
            HoverUnderline(link);
            link.Click += (s, e) => DocCommentActions.OpenUrl(url);
            return link;
        }

        private static void HoverUnderline(Hyperlink link)
        {
            link.MouseEnter += (s, e) => link.TextDecorations = TextDecorations.Underline;
            link.MouseLeave += (s, e) => link.TextDecorations = null;
        }

        private Run CodeRun(string text, Brush foreground) => new Run(text)
        {
            FontFamily = _t.CodeFont,
            FontSize = _codeSize,
            Foreground = foreground,
        };

        /// <summary>Inline <c>&lt;c&gt;</c> code: editor font, syntax colours, a subtle background.</summary>
        private Inline InlineCode(string text)
        {
            var span = new Span
            {
                FontFamily = _t.CodeFont,
                FontSize = _codeSize,
                Background = _t.InlineCodeBackground,
            };

            // Pad with ordinary spaces in the code font. A thin space (U+2009) isn't in most code
            // fonts, so WPF drew it in a fallback font with taller line metrics and the background
            // stuck out above and below at both ends.
            span.Inlines.Add(new Run(" "));
            foreach (var token in CSharpHighlighter.Highlight(text, _t))
            {
                span.Inlines.Add(new Run(token.Key) { Foreground = token.Value });
            }

            span.Inlines.Add(new Run(" "));
            return span;
        }

        /// <summary>
        /// A code block: a paragraph in the code font with syntax colours. Its rounded background
        /// is drawn behind the text box (see <see cref="CodeBackdrops"/>), since document blocks
        /// can't have rounded corners; keeping it a paragraph lets selection run through it.
        /// </summary>
        /// <param name="language">The block's language (null: C#); see <see cref="CodeLanguages"/>.</param>
        private Paragraph CodeBlock(DocNode n, string language)
        {
            var code = new Paragraph
            {
                FontFamily = _t.CodeFont,
                FontSize = _codeSize,
                LineHeight = CodeLineHeight,
                Foreground = _t.PlainText,
                FontStyle = FontStyles.Normal,
                FontWeight = FontWeights.Normal,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 4, 0, 2),
                Tag = CodeBlockTag,
            };

            foreach (var token in CSharpHighlighter.Highlight(Dedent(LenientXmlParser.InnerText(n)), language, _t))
            {
                var lines = token.Key.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0) code.Inlines.Add(new LineBreak());
                    if (lines[i].Length > 0) code.Inlines.Add(new Run(lines[i]) { Foreground = token.Value });
                }
            }

            _codeBlocks.Add(code);
            return code;
        }

        private Block ListBlock(DocNode n, bool afterOtherBlocks)
        {
            string type = (n.Attr("type") ?? (n.Name == "ol" ? "number" : "bullet")).ToLowerInvariant();
            if (type == "table")
            {
                return TableList(n, afterOtherBlocks);
            }

            double available = _available;
            _available = Math.Max(40, _available - 15);
            var list = new List
            {
                MarkerStyle = type == "number" ? TextMarkerStyle.Decimal : type == "table" ? TextMarkerStyle.None : TextMarkerStyle.Disc,
                Foreground = _t.Muted, // The markers; item text sets its own colour.
                Margin = new Thickness(0, afterOtherBlocks ? 4 : 0, 0, 2),
                Padding = new Thickness(type == "table" ? 0 : 15, 0, 0, 0),
                MarkerOffset = 7,
            };

            foreach (var item in n.Children)
            {
                if (item.Name != "item" && item.Name != "listheader" && item.Name != "li") continue;

                var listItem = new ListItem { Foreground = _t.Prose, Margin = new Thickness(0, list.ListItems.Count > 0 ? 2 : 0, 0, 0) };
                var term = item.Children.FirstOrDefault(c => c.Name == "term");
                var description = item.Children.FirstOrDefault(c => c.Name == "description");
                if (term != null)
                {
                    var first = NewParagraph(0);
                    listItem.Blocks.Add(first);
                    var sink = new InlineSink(first.Inlines);
                    Nest(sink, new Bold(), term);
                    if (description != null && HasContent(description))
                    {
                        sink.Text(" – ");
                        AddBlocks(listItem.Blocks, description, first, sink);
                    }
                }
                else
                {
                    AddBlocks(listItem.Blocks, description ?? item);
                }

                if (listItem.Blocks.Count == 0) listItem.Blocks.Add(NewParagraph(0));
                list.ListItems.Add(listItem);
            }

            _available = available;
            return list;
        }

        /// <summary>
        /// &lt;list type="table"&gt;: a two-column table, terms on the left (as wide as the widest,
        /// up to 40% of the width) and descriptions on the right, under a header row if there's a
        /// &lt;listheader&gt;. An item with no term spans both columns.
        /// </summary>
        private Block TableList(DocNode n, bool afterOtherBlocks)
        {
            var items = n.Children.Where(c => c.Name == "item" || c.Name == "listheader" || c.Name == "li").ToList();
            double widest = items
                .Select(i => i.Children.FirstOrDefault(c => c.Name == "term"))
                .Where(t => t != null)
                .Select(t => TextWidth(Label(t) ?? string.Empty, FontWeights.SemiBold, _t.ProseFontSize))
                .DefaultIfEmpty(0)
                .Max();
            double available = _available;
            double termWidth = Math.Min(Math.Ceiling(widest) + 16, available * 0.4);
            double descriptionWidth = Math.Max(40, available - termWidth);

            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, afterOtherBlocks ? 4 : 0, 0, 2), Tag = BlockTag };
            table.Columns.Add(new TableColumn { Width = new GridLength(termWidth) });
            table.Columns.Add(new TableColumn { Width = new GridLength(descriptionWidth) });
            var group = new TableRowGroup();
            table.RowGroups.Add(group);

            foreach (var item in items)
            {
                bool header = item.Name == "listheader";
                double top = group.Rows.Count > 0 ? 3 : 0;
                var term = item.Children.FirstOrDefault(c => c.Name == "term");
                var description = item.Children.FirstOrDefault(c => c.Name == "description");
                var row = new TableRow();

                TableCell Cell(DocNode content, double right)
                {
                    var cell = new TableCell { Padding = new Thickness(0, top, right, header ? 3 : 0) };
                    if (header)
                    {
                        cell.Foreground = _t.Muted;
                        cell.FontWeight = FontWeights.SemiBold;
                        cell.BorderBrush = _t.CardBorder;
                        cell.BorderThickness = new Thickness(0, 0, 0, 1);
                    }

                    if (content != null)
                    {
                        AddBlocks(cell.Blocks, content);
                    }

                    if (cell.Blocks.Count == 0) cell.Blocks.Add(NewParagraph(0));
                    return cell;
                }

                if (term != null)
                {
                    var termCell = Cell(term, 12);
                    if (!header) termCell.FontWeight = FontWeights.SemiBold;
                    row.Cells.Add(termCell);
                    _available = descriptionWidth;
                    row.Cells.Add(Cell(description, 0));
                }
                else
                {
                    var cell = Cell(description ?? item, 0);
                    cell.ColumnSpan = 2;
                    row.Cells.Add(cell);
                }

                _available = available;
                group.Rows.Add(row);
            }

            return table;
        }

        /// <summary>
        /// &lt;note type="..."&gt; (from Sandcastle and DocFX): a callout with a coloured bar and a
        /// label (Note, Tip, Important, Warning, Caution...).
        /// </summary>
        private Block NoteBlock(DocNode n, double top)
        {
            string label;
            Brush accent;
            switch ((n.Attr("type") ?? "note").Trim().ToLowerInvariant())
            {
                case "tip": label = "Tip"; accent = _t.NumberLiteral; break;
                case "important": label = "Important"; accent = _t.ControlKeyword; break;
                case "warning": label = "Warning"; accent = _t.StringLiteral; break;
                case "caution": label = "Caution"; accent = _t.StringLiteral; break;
                case "security": case "security note": label = "Security"; accent = _t.StringLiteral; break;
                case "implement": label = "Note to implementers"; accent = _t.Link; break;
                case "caller": label = "Note to callers"; accent = _t.Link; break;
                case "inherit": label = "Note to inheritors"; accent = _t.Link; break;
                default: label = "Note"; accent = _t.Link; break;
            }

            var note = new Section
            {
                Margin = new Thickness(0, Math.Max(4, top), 0, 2),
                Padding = new Thickness(10, 4, 8, 5),
                BorderBrush = accent,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Background = _t.InlineCodeBackground,
                Tag = BlockTag,
            };

            var first = NewParagraph(0);
            note.Blocks.Add(first);
            var sink = new InlineSink(first.Inlines);
            sink.Add(new Run(label + ":") { FontWeight = FontWeights.SemiBold, Foreground = accent });
            sink.Text(" ");

            double available = _available;
            _available = Math.Max(40, _available - 21);
            AddBlocks(note.Blocks, n, first, sink);
            _available = available;
            return note;
        }

        /// <summary>
        /// &lt;threadsafety static="true" instance="false"/&gt;, in words; its own text if it has any.
        /// (Sandcastle's defaults: static members safe, instance members not.)
        /// </summary>
        private void ThreadSafety(BlockCollection target, DocNode n)
        {
            if (HasContent(n))
            {
                AddBlocks(target, n);
                return;
            }

            bool staticSafe = !string.Equals(n.Attr("static"), "false", StringComparison.OrdinalIgnoreCase);
            bool instanceSafe = string.Equals(n.Attr("instance"), "true", StringComparison.OrdinalIgnoreCase);
            string text = staticSafe && instanceSafe ? "Safe to use from several threads at once."
                : staticSafe ? "Static members are safe to use from several threads at once; instance members aren't."
                : instanceSafe ? "Instance members are safe to use from several threads at once; static members aren't."
                : "Not safe to use from several threads at once.";
            var line = NewParagraph(0);
            line.Inlines.Add(new Run(text));
            target.Add(line);
        }

        /// <summary>&lt;include file="..."/&gt;: the docs are in another file, which the card doesn't read.</summary>
        private Paragraph IncludeLine(DocNode include, double top)
        {
            var line = NewParagraph(top);
            line.Foreground = _t.Muted;
            line.FontStyle = FontStyles.Italic;
            string file = include.Attr("file");
            line.Inlines.Add(new Run("Documentation included from "));
            var name = CodeRun(string.IsNullOrWhiteSpace(file) ? "another file" : System.IO.Path.GetFileName(file.Trim()), _t.Muted);
            name.FontStyle = FontStyles.Normal;
            line.Inlines.Add(name);
            line.Inlines.Add(new Run(", not shown here."));
            return line;
        }

        /// <summary>
        /// A tag name as a row label: "since" becomes "Since", "api-version" or "api_version"
        /// becomes "Api version". (Tag names arrive lower-cased from the parser.)
        /// </summary>
        private static string Humanize(string tag)
        {
            var words = (tag ?? string.Empty).Replace('-', ' ').Replace('_', ' ').Trim().ToLowerInvariant();
            return words.Length == 0 ? "Other" : char.ToUpperInvariant(words[0]) + words.Substring(1);
        }

        private Paragraph SeeAlsoLine(List<DocNode> items)
        {
            var line = NewParagraph(0);
            var sink = new InlineSink(line.Inlines);
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sink.Text(", ");
                sink.Add(SeeInline(items[i]));
            }

            return line;
        }

        /// <summary>Shown while the inherited docs are being looked up, or when there are none.</summary>
        private Paragraph InheritDocLine(DocNode n, InheritedDoc inherited, double top)
        {
            var line = NewParagraph(top);
            line.Foreground = _t.Muted;
            line.FontStyle = FontStyles.Italic;
            var sink = new InlineSink(line.Inlines);
            string cref = n.Attr("cref");
            bool none = inherited != null && inherited.Xml == null;

            if (cref != null)
            {
                sink.Text(none ? "No documentation found to inherit from " : "Inherits documentation from ");
                sink.Add(CrefLink(cref, null));
            }
            else
            {
                sink.Text(none ? "No documentation found to inherit." : "Inherits documentation from the base member.");
            }

            return line;
        }

        /// <summary>Content of the "Inherited from" row: a link to the member the docs came from.</summary>
        private Paragraph InheritedFromLine(string sourceId)
        {
            var line = NewParagraph(0);
            var sink = new InlineSink(line.Inlines);
            if (!string.IsNullOrEmpty(sourceId))
            {
                sink.Add(CrefLink(sourceId, null));
            }
            else
            {
                sink.Text("The base member");
            }

            return line;
        }

        /// <summary>
        /// The card's text box: read-only rich text, so it can be selected with the mouse and
        /// copied (Ctrl+C or right-click), with clickable links.
        /// </summary>
        private RichTextBox NewTextBox()
        {
            var document = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = _t.ProseFont,
                FontSize = _t.ProseFontSize,
                Foreground = _t.Prose,
                LineHeight = ProseLineHeight,

                // Every line exactly LineHeight tall, baseline placed by the block's own font: a line
                // that's only code (e.g. a link) then sits exactly like a line of prose, and lines
                // up with the section label beside it, which uses the same settings.
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextAlignment = TextAlignment.Left,
            };

            var box = new RichTextBox(document)
            {
                IsReadOnly = true,
                IsDocumentEnabled = true,
                IsUndoEnabled = false,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = _t.Prose,
                CaretBrush = Brushes.Transparent,
                SelectionBrush = _t.SelectionBackground,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                FocusVisualStyle = null,
            };

            // A bare template (just the scrolling host): VS's and WPF's text box templates add a
            // border and padding, which pushed card text right of the card's edge.
            box.Template = BareTextBoxTemplate;

            // If anything still offsets the first character, shift the box back once, so card
            // text lines up exactly with the card's edge.
            bool aligned = false;
            box.Loaded += (s, e) =>
            {
                if (aligned) return;
                aligned = true;
                var first = box.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
                var rect = first.GetCharacterRect(LogicalDirection.Forward);
                double offset = rect.IsEmpty ? 0 : rect.Left - LeftInset(first);
                if (offset > 0.5 && offset < 24)
                {
                    var m = box.Margin;
                    box.Margin = new Thickness(m.Left - offset, m.Top, m.Right, m.Bottom);
                }
            };

            // VS sends Ctrl+C and the right-click Copy to the editor, not to this box; the
            // editor's Copy command picks up the card selection from here instead.
            CardTextSelection.Register(box);
            return box;
        }

        /// <summary>
        /// How far the blocks around a position indent it on purpose (a code block's padding, a
        /// list's indent), so that isn't mistaken for the text box's own offset.
        /// </summary>
        private static double LeftInset(TextPointer position)
        {
            double inset = 0;
            for (var element = position.Parent as FrameworkContentElement; element != null && !(element is FlowDocument); element = element.Parent as FrameworkContentElement)
            {
                if (element is Block block) inset += block.Margin.Left + block.Padding.Left + block.BorderThickness.Left;
                else if (element is ListItem item) inset += item.Margin.Left + item.Padding.Left;
                else if (element is TableCell cell) inset += cell.Padding.Left + cell.BorderThickness.Left;
            }

            return inset;
        }

        /// <summary>
        /// Rounded backgrounds for the card's code blocks, drawn on a canvas behind the text box
        /// and placed where layout put each code paragraph. Updated whenever the box is laid out.
        /// </summary>
        private sealed class CodeBackdrops
        {
            private readonly RichTextBox _box;
            private readonly Canvas _canvas;
            private readonly List<Paragraph> _blocks;
            private readonly Brush _fill;
            private List<Rect> _drawn = new List<Rect>();

            public CodeBackdrops(RichTextBox box, Canvas canvas, List<Paragraph> blocks, Brush fill)
            {
                _box = box;
                _canvas = canvas;
                _blocks = blocks.ToList();
                _fill = fill;
            }

            public void Attach()
            {
                _box.Loaded += (s, e) => Update();
                _box.SizeChanged += (s, e) => Update();
            }

            private void Update()
            {
                List<Rect> rects;
                try
                {
                    rects = _blocks.Select(Bounds).Where(r => !r.IsEmpty).ToList();
                }
                catch (InvalidOperationException)
                {
                    return; // Not laid out yet; the next layout calls back.
                }

                if (rects.SequenceEqual(_drawn))
                {
                    return;
                }

                _drawn = rects;
                _canvas.Children.Clear();
                foreach (var rect in rects)
                {
                    var backdrop = new Border { Background = _fill, CornerRadius = new CornerRadius(4), Width = rect.Width, Height = rect.Height };
                    Canvas.SetLeft(backdrop, rect.Left);
                    Canvas.SetTop(backdrop, rect.Top);
                    _canvas.Children.Add(backdrop);
                }
            }

            /// <summary>
            /// From the top of the paragraph's first line to the bottom of its last, plus its padding;
            /// from its left edge to the right edge of the card's text.
            /// </summary>
            private Rect Bounds(Paragraph code)
            {
                var start = code.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
                var end = code.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
                var first = start.GetCharacterRect(LogicalDirection.Forward);
                var last = end.GetCharacterRect(LogicalDirection.Backward);
                if (first.IsEmpty || last.IsEmpty)
                {
                    return Rect.Empty;
                }

                var topLeft = _box.TranslatePoint(new Point(first.Left - code.Padding.Left, first.Top - code.Padding.Top), _canvas);
                var bottom = _box.TranslatePoint(new Point(0, last.Bottom + code.Padding.Bottom), _canvas).Y;
                double right = _canvas.ActualWidth;
                return right > topLeft.X && bottom > topLeft.Y
                    ? new Rect(topLeft.X, topLeft.Y, right - topLeft.X, bottom - topLeft.Y)
                    : Rect.Empty;
            }
        }

        private static string Label(DocNode n)
        {
            string text = Whitespace.Replace(LenientXmlParser.InnerText(n), " ").Trim();
            return text.Length > 0 ? text : null;
        }

        private static bool HasContent(DocNode n) =>
            n != null && n.Children.Any(c => !c.IsText || !string.IsNullOrWhiteSpace(c.Text));

        private static string Dedent(string text)
        {
            var lines = text.Replace("\r", string.Empty).Split('\n').ToList();
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0])) lines.RemoveAt(0);
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[lines.Count - 1])) lines.RemoveAt(lines.Count - 1);

            int indent = lines
                .Where(l => l.Trim().Length > 0)
                .Select(l => l.Length - l.TrimStart().Length)
                .DefaultIfEmpty(0)
                .Min();

            return string.Join("\n", lines.Select(l => l.Length >= indent ? l.Substring(indent) : l.TrimStart()));
        }

        /// <summary>
        /// Appends inline content while collapsing XML whitespace the way a browser would:
        /// runs of whitespace become one space, and nothing leads or trails a paragraph.
        /// </summary>
        private sealed class InlineSink
        {
            private readonly Stack<InlineCollection> _targets = new Stack<InlineCollection>();
            private bool _atStart = true;
            private bool _pendingSpace;

            public InlineSink(InlineCollection target)
            {
                _targets.Push(target);
            }

            public void Text(string raw)
            {
                if (string.IsNullOrEmpty(raw)) return;

                string s = Whitespace.Replace(raw, " ");
                bool leading = s[0] == ' ';
                bool trailing = s[s.Length - 1] == ' ';
                s = s.Trim();

                if (leading && !_atStart) _pendingSpace = true;
                if (s.Length > 0)
                {
                    Flush();
                    _targets.Peek().Add(new Run(s));
                    _atStart = false;
                }

                if (trailing && !_atStart) _pendingSpace = true;
            }

            public void Add(Inline inline)
            {
                Flush();
                _targets.Peek().Add(inline);
                _atStart = false;
            }

            public void Push(InlineCollection target) => _targets.Push(target);

            public void Pop() => _targets.Pop();

            public void Break()
            {
                if (_atStart) return;
                _targets.Peek().Add(new LineBreak());
                _pendingSpace = false;
                _atStart = true;
            }

            private void Flush()
            {
                if (_pendingSpace)
                {
                    _targets.Peek().Add(new Run(" "));
                    _pendingSpace = false;
                }
            }
        }
    }
}
