using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using RenderedDocComments.Rendering;
using RenderedDocComments.Tests.Fakes;
using Xunit;
using K = RenderedDocComments.Rendering.CodeTokenKind;

namespace RenderedDocComments.Tests
{
    /// <summary>The quick documentation popup's card: header, signature links, docs and width.</summary>
    public class QuickDocCardTests
    {
        private const double MaxWidth = 640;

        private static DocTheme Theme => DocTheme.Current;

        /// <summary>Records what the popup's links asked for.</summary>
        private sealed class Handler : IQuickDocHandler
        {
            public List<string> Calls { get; } = new List<string>();

            public void GoToDefinition(string cref) => Calls.Add("docs " + cref);

            public void Describe(string cref, Action<string> onDescribed) => Calls.Add("describe " + cref);

            public void GoToShownDefinition() => Calls.Add("definition");

            public void Back() => Calls.Add("back");
        }

        private static QuickDocContent Rename(string xml = "<summary>Renames it.</summary><returns>True if it changed.</returns>") => new QuickDocContent
        {
            Title = "Product<TId>.Rename",
            Container = "Shop",
            Signature = new[]
            {
                new SignaturePart("bool", K.Keyword),
                new SignaturePart(" ", K.Plain),
                new SignaturePart("Product", K.TypeName, linkId: "T:Shop.Product`1"),
                new SignaturePart("<", K.Plain),
                new SignaturePart("int", K.Keyword),
                new SignaturePart(">.", K.Plain),
                new SignaturePart("Rename", K.MethodName, isName: true),
                new SignaturePart("(", K.Plain),
                new SignaturePart("string", K.Keyword),
                new SignaturePart(" ", K.Plain),
                new SignaturePart("name", K.Parameter),
                new SignaturePart(")", K.Plain),
            },
            Xml = xml,
        };

        private static Border Card(QuickDocContent content, Handler handler, string back = null) =>
            (Border)DocCommentRenderer.CreateQuickDocCard(content, MaxWidth, back, handler);

        private static FlowDocument Document(Border card) =>
            ((Grid)card.Child).Children.OfType<RichTextBox>().Single().Document;

        private static string TextOf(TextElement element) => new TextRange(element.ContentStart, element.ContentEnd).Text.Trim();

        private static IEnumerable<T> All<T>(DependencyObject root)
            where T : DependencyObject
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (child is T match) yield return match;
                foreach (var nested in All<T>(child)) yield return nested;
            }
        }

        private static Hyperlink Link(FlowDocument document, string text) => All<Hyperlink>(document).Single(l => TextOf(l) == text);

        private static void Click(Hyperlink link) => link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent, link));

        [Fact]
        public void Shows_where_the_symbol_is_its_signature_then_its_docs() => Sta.Run(() =>
        {
            var content = Rename();
            content.Assembly = "Shop.Core";
            var blocks = Document(Card(content, new Handler())).Blocks.ToList();

            Assert.Equal("Shop · Shop.Core", TextOf(blocks[0]));
            Assert.Equal("bool Product<int>.Rename(string name)", TextOf(blocks[1]));
            Assert.Equal(Theme.CodeFont, blocks[1].FontFamily);
            Assert.Equal(1, blocks[1].BorderThickness.Bottom); // The rule above the docs.
            Assert.StartsWith("Renames it.", TextOf(blocks[2]));
            Assert.Contains("Returns", TextOf(blocks[2]));
        });

        [Fact]
        public void The_name_goes_to_the_definition_and_types_show_their_docs() => Sta.Run(() =>
        {
            var handler = new Handler();
            var document = Document(Card(Rename(), handler));

            var name = Link(document, "Rename");
            Assert.Equal(FontWeights.SemiBold, name.FontWeight);
            Click(name);
            Click(Link(document, "Product"));

            Assert.Equal(new[] { "definition", "docs T:Shop.Product`1" }, handler.Calls);
        });

        [Fact]
        public void Signature_parts_get_their_code_colours() => Sta.Run(() =>
        {
            var document = Document(Card(Rename(), new Handler()));
            var runs = All<Run>(document).ToList();
            Assert.Same(Theme.Keyword, runs.First(r => r.Text == "bool").Foreground);
            Assert.Same(Theme.Parameter, runs.First(r => r.Text == "name").Foreground);
            Assert.Same(Theme.TypeName, runs.First(r => r.Text == "Product").Foreground);
        });

        [Fact]
        public void Back_link_names_the_previous_symbol() => Sta.Run(() =>
        {
            var handler = new Handler();
            var document = Document(Card(Rename(), handler, back: "List<T>.Add"));
            Assert.Equal("← List<T>.Add", TextOf(document.Blocks.First()));
            Click(Link(document, "← List<T>.Add"));
            Assert.Equal(new[] { "back" }, handler.Calls);
        });

        [Fact]
        public void A_parameter_shows_its_kind_and_a_link_to_its_member() => Sta.Run(() =>
        {
            var handler = new Handler();
            var content = new QuickDocContent
            {
                Kind = "parameter",
                Signature = new[] { new SignaturePart("string", K.Keyword), new SignaturePart(" ", K.Plain), new SignaturePart("name", K.Parameter, isName: true) },
                Container = "Product<TId>.Rename",
                ContainerId = "M:Shop.Product`1.Rename(System.String)",
                Xml = "<summary>The new name.</summary>",
            };
            var document = Document(Card(content, handler));
            var blocks = document.Blocks.ToList();

            Assert.Equal("Product<TId>.Rename", TextOf(blocks[0]));
            Assert.Equal("(parameter) string name", TextOf(blocks[1]));
            Assert.Equal("The new name.", TextOf(blocks[2]));
            Click(Link(document, "Product<TId>.Rename"));
            Assert.Equal(new[] { "docs M:Shop.Product`1.Rename(System.String)" }, handler.Calls);
        });

        [Fact]
        public void No_docs_shows_just_the_signature_in_a_smaller_card() => Sta.Run(() =>
        {
            var undocumented = Card(Rename(xml: null), new Handler());
            var blocks = Document(undocumented).Blocks.ToList();
            Assert.Equal(new[] { "Shop", "bool Product<int>.Rename(string name)" }, blocks.Select(TextOf));
            Assert.Equal(0, blocks[1].BorderThickness.Bottom); // No rule under the signature.
            Assert.True(undocumented.Width < Card(Rename(), new Handler()).Width + 1);

            var local = new QuickDocContent { Kind = "local variable", Signature = new[] { new SignaturePart("int", K.Keyword), new SignaturePart(" x", K.Plain) } };
            var localBlocks = Document(Card(local, new Handler())).Blocks.ToList();
            Assert.Equal("(local variable) int x", TextOf(Assert.Single(localBlocks)));
            Assert.Equal(0, localBlocks[0].BorderThickness.Bottom);
        });

        [Fact]
        public void Docs_from_another_member_say_where_from() => Sta.Run(() =>
        {
            var content = Rename();
            content.InheritedFrom = "M:Shop.Product`1.Rename(System.String)";
            Assert.Contains("Inherited from\tProduct.Rename()", TextOf(Document(Card(content, new Handler())).Blocks.Last()));
        });

        [Fact]
        public void An_inheritdoc_that_found_nothing_says_so() => Sta.Run(() =>
        {
            var document = Document(Card(Rename(xml: "<inheritdoc/>"), new Handler()));
            Assert.Equal("No documentation found to inherit.", TextOf(document.Blocks.Last()));
        });

        [Fact]
        public void Short_docs_get_a_narrower_card_and_long_ones_the_full_width() => Sta.Run(() =>
        {
            var short_ = Card(Rename(), new Handler());
            Assert.True(short_.Width < MaxWidth - 100, "Short card is " + short_.Width);
            Assert.True(short_.Width >= 240);

            var text = string.Join(" ", Enumerable.Repeat("A long summary that goes on and on.", 20));
            Assert.Equal(MaxWidth, Card(Rename("<summary>" + text + "</summary>"), new Handler()).Width);
        });

        [Fact]
        public void A_narrowed_card_still_fits_its_widest_line() => Sta.Run(() =>
        {
            var content = Rename();
            content.Assembly = "Shop.Core";
            var card = Card(content, new Handler());
            var document = Document(card);

            // Laid out in a real (off-screen) window, as in VS, every line stays on one line.
            var window = new Window
            {
                Content = card, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None,
                ShowInTaskbar = false, ShowActivated = false, Left = -30000, Top = -30000,
            };
            try
            {
                window.Show();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                card.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                foreach (var paragraph in All<Paragraph>(document))
                {
                    var start = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward);
                    var end = paragraph.ContentEnd.GetInsertionPosition(LogicalDirection.Backward).GetCharacterRect(LogicalDirection.Backward);
                    // Fonts differ within a line (so do their tops); a wrapped line is a whole line lower.
                    Assert.True(Math.Abs(start.Top - end.Top) < 8, "Wrapped: " + TextOf(paragraph));
                }
            }
            finally
            {
                window.Close();
            }
        });
    }
}
