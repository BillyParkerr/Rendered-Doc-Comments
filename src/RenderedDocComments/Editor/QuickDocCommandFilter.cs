using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using RenderedDocComments.Commands;
using RenderedDocComments.Diagnostics;

namespace RenderedDocComments.Editor
{
    /// <summary>Gives each C# editor the quick documentation shortcut and Escape handling, and defines the popup's space.</summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("CSharp")]
    [TextViewRole(Roles.Document)]
    [TextViewRole(Roles.Peek)]
    [TextViewRole(Roles.LeftDiff)]
    [TextViewRole(Roles.RightDiff)]
    internal sealed class QuickDocCommandFilterProvider : IVsTextViewCreationListener
    {
#pragma warning disable 169, 649, IDE0044, IDE0051 // Read by MEF.
        [Export(typeof(SpaceReservationManagerDefinition))]
        [Name(QuickDocPopup.SpaceName)]
        [Order(After = "quickinfo")]
        private SpaceReservationManagerDefinition _spaceDefinition;
#pragma warning restore 169, 649, IDE0044, IDE0051

        [Import]
        internal IVsEditorAdaptersFactoryService Adapters { get; set; }

        [Import]
        internal EditorServices Services { get; set; }

        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var view = Adapters.GetWpfTextView(textViewAdapter);
            if (view == null)
            {
                return;
            }

            var filter = new QuickDocCommandFilter(view);
            if (textViewAdapter.AddCommandFilter(filter, out var next) == VSConstants.S_OK)
            {
                filter.Next = next;
            }
        }
    }

    /// <summary>
    /// Ctrl+Shift+F1 (the Show Quick Documentation command) opens the popup for the focused
    /// editor, so it works in Peek and diffs too; Escape closes it.
    /// </summary>
    internal sealed class QuickDocCommandFilter : IOleCommandTarget
    {
        private readonly IWpfTextView _view;

        public QuickDocCommandFilter(IWpfTextView view)
        {
            _view = view;
        }

        public IOleCommandTarget Next { get; set; }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            if (pguidCmdGroup == DocCommentCommands.CommandSet && cCmds == 1 && prgCmds[0].cmdID == DocCommentCommands.QuickDocId)
            {
                prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
                return VSConstants.S_OK;
            }

            return Next?.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (pguidCmdGroup == DocCommentCommands.CommandSet && nCmdID == DocCommentCommands.QuickDocId)
            {
                Guard.Run("Quick documentation", () => QuickDocPopup.For(_view).ToggleAtCaret());
                return VSConstants.S_OK;
            }

            if (pguidCmdGroup == VSConstants.VSStd2K
                && nCmdID == (uint)VSConstants.VSStd2KCmdID.CANCEL
                && Guard.Run("Closing quick documentation", () => QuickDocPopup.TryDismiss(_view), false))
            {
                return VSConstants.S_OK;
            }

            return Next?.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
        }
    }
}
