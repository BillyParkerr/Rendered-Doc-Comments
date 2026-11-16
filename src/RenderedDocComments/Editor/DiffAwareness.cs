using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using RenderedDocComments.Parsing;

namespace RenderedDocComments.Editor
{
    /// <summary>
    /// How cards behave in Visual Studio's diff windows.
    /// <list type="bullet">
    /// <item><description>Side-by-side: a comment that's identical on both sides becomes a card on
    /// both (same text and width, so the same height, so the sides stay aligned). A comment that
    /// differs stays raw, so the diff's red/green highlighting of what changed stays visible.</description></item>
    /// <item><description>Inline: left alone. Its text mixes both versions, so cards would hide the diff.</description></item>
    /// </list>
    /// </summary>
    internal static class DiffAwareness
    {
        private static readonly ConditionalWeakTable<ITextSnapshot, HashSet<string>> CommentTexts =
            new ConditionalWeakTable<ITextSnapshot, HashSet<string>>();

        public static bool IsInlineDiff(ITextView view) =>
            view.Roles.Contains(DifferenceViewerRoles.InlineViewTextViewRole);

        public static bool IsSideBySideDiff(ITextView view) =>
            view.Roles.Contains(DifferenceViewerRoles.LeftViewTextViewRole)
            || view.Roles.Contains(DifferenceViewerRoles.RightViewTextViewRole);

        /// <summary>True for a comment in a side-by-side diff whose text differs on the other side.</summary>
        public static bool IsChangedInDiff(ITextView view, DocCommentBlock block)
        {
            if (!IsSideBySideDiff(view))
            {
                return false;
            }

            var other = OtherSide(view);
            if (other == null)
            {
                return false;
            }

            var texts = CommentTexts.GetValue(other.CurrentSnapshot,
                snapshot => new HashSet<string>(DocCommentScanner.GetBlocks(snapshot).Select(b => b.Xml)));
            return !texts.Contains(block.Xml);
        }

        /// <summary>The buffer shown on the other side of a side-by-side diff, or null.</summary>
        public static ITextBuffer OtherSide(ITextView view)
        {
            var viewer = EditorServices.Instance?.DifferenceViewerFactory?.TryGetViewerForTextView(view);
            if (viewer == null)
            {
                return null;
            }

            if (viewer.LeftView == view) return viewer.DifferenceBuffer?.RightBuffer;
            if (viewer.RightView == view) return viewer.DifferenceBuffer?.LeftBuffer;
            return null;
        }
    }
}
