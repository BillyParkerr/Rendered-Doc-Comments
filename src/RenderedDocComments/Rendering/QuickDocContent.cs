using System.Collections.Generic;

namespace RenderedDocComments.Rendering
{
    /// <summary>One run of a symbol's signature in the quick documentation popup.</summary>
    internal sealed class SignaturePart
    {
        public SignaturePart(string text, CodeTokenKind kind, string linkId = null, bool isName = false)
        {
            Text = text;
            Kind = kind;
            LinkId = linkId;
            IsName = isName;
        }

        public string Text { get; }

        /// <summary>How it's coloured.</summary>
        public CodeTokenKind Kind { get; }

        /// <summary>Documentation ID of a type named here: clicking it shows that type's docs. Null for none.</summary>
        public string LinkId { get; }

        /// <summary>The symbol's own name: clicking it goes to its definition.</summary>
        public bool IsName { get; }

        public override string ToString() => Text;
    }

    /// <summary>What the quick documentation popup shows for one symbol (built with Roslyn by QuickDocResolver).</summary>
    internal sealed class QuickDocContent
    {
        /// <summary>A short name for the symbol, e.g. "List&lt;T&gt;.Add", for the popup's Back link.</summary>
        public string Title { get; set; }

        /// <summary>"parameter", "local variable" and so on, shown before the signature; null for types and members.</summary>
        public string Kind { get; set; }

        public IReadOnlyList<SignaturePart> Signature { get; set; } = new SignaturePart[0];

        /// <summary>Shown above the signature: the namespace, or the member a parameter belongs to. Null for none.</summary>
        public string Container { get; set; }

        /// <summary>Documentation ID of the container, to show its docs on click; null when it isn't a link.</summary>
        public string ContainerId { get; set; }

        /// <summary>The assembly, for symbols from referenced assemblies (framework types and packages); null for source.</summary>
        public string Assembly { get; set; }

        /// <summary>The documentation, without a root element, with any &lt;inheritdoc/&gt; already filled in; null for none.</summary>
        public string Xml { get; set; }

        /// <summary>Documentation ID of the member the docs came from, when that's another member; otherwise null.</summary>
        public string InheritedFrom { get; set; }
    }

    /// <summary>
    /// What the quick documentation popup's links do. A cref in the docs (the inherited
    /// <see cref="ICardLinkHandler.GoToDefinition"/>) shows that symbol's docs in the popup.
    /// </summary>
    internal interface IQuickDocHandler : ICardLinkHandler
    {
        /// <summary>Go to the definition of the symbol the popup is showing.</summary>
        void GoToShownDefinition();

        /// <summary>Show the previous symbol's docs again.</summary>
        void Back();
    }
}
