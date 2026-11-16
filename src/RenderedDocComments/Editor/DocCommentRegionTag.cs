using System.Windows.Controls;
using Microsoft.VisualStudio.Text.Tagging;

namespace RenderedDocComments.Editor
{
    /// <summary>
    /// Our fold over lines 2..N of a doc comment. The card is drawn over line 1 by
    /// <see cref="DocCommentAdornmentTagger"/>, so the fold itself must be invisible.
    /// </summary>
    internal sealed class DocCommentRegionTag : IOutliningRegionTag
    {
        public bool IsDefaultCollapsed => false;

        public bool IsImplementation => false;

        /// <summary>
        /// A string here gets VS's usual bordered "..." box, which showed as a thin bar after
        /// each card. A zero-size element leaves nothing visible. A new element each time,
        /// because one WPF element can't be shown in two places (split windows).
        /// </summary>
        public object CollapsedForm => new Border { Width = 0, Height = 0 };

        /// <summary>Null still produced an empty tooltip box, so say something useful instead.</summary>
        public object CollapsedHintForm => "Rendered documentation comment. Ctrl+M, Ctrl+Q shows the raw text.";
    }
}
