using System;

namespace RenderedDocComments.Rendering
{
    /// <summary>What a card's type/member links do. Supplied per card, since it needs the card's file and comment.</summary>
    internal interface ICardLinkHandler
    {
        /// <summary>Navigate to what the cref refers to.</summary>
        void GoToDefinition(string cref);

        /// <summary>Work out the cref's signature (may be asynchronous) and report it on the UI thread.</summary>
        void Describe(string cref, Action<string> onDescribed);
    }
}
