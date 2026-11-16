using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;

namespace RenderedDocComments.Editor
{
    /// <summary>
    /// Editor services that code outside MEF parts (theme, cards) needs. Every MEF part in the
    /// extension imports this, so it's created before anything renders.
    /// </summary>
    [Export]
    internal sealed class EditorServices
    {
        [ImportingConstructor]
        public EditorServices(
            IEditorFormatMapService formatMapService,
            IClassificationFormatMapService classificationFormatMapService,
            IClassificationTypeRegistryService classificationRegistry,
            IOutliningManagerService outliningManagerService)
        {
            FormatMapService = formatMapService;
            ClassificationFormatMapService = classificationFormatMapService;
            ClassificationRegistry = classificationRegistry;
            OutliningManagerService = outliningManagerService;
            Instance = this;
        }

        public static EditorServices Instance { get; private set; }

        public IEditorFormatMapService FormatMapService { get; }

        public IClassificationFormatMapService ClassificationFormatMapService { get; }

        public IClassificationTypeRegistryService ClassificationRegistry { get; }

        public IOutliningManagerService OutliningManagerService { get; }

        /// <summary>Finds the diff window an editor belongs to (to compare against the other side).</summary>
        [Import(AllowDefault = true)]
        public IWpfDifferenceViewerFactoryService DifferenceViewerFactory { get; set; }
    }

    /// <summary>
    /// Where the editor parts attach. Normal editors have the Document role; Peek and the two
    /// sides of a side-by-side diff are added explicitly in case VS doesn't give them that role
    /// (attributes with several roles match a view with any of them).
    /// </summary>
    internal static class Roles
    {
        public const string Document = PredefinedTextViewRoles.Document;
        public const string Peek = PredefinedTextViewRoles.EmbeddedPeekTextView;
        public const string LeftDiff = DifferenceViewerRoles.LeftViewTextViewRole;
        public const string RightDiff = DifferenceViewerRoles.RightViewTextViewRole;
    }
}
