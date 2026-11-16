using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using RenderedDocComments.Editor;

namespace RenderedDocComments.Commands
{
    internal static class DocCommentCommands
    {
        public static readonly Guid CommandSet = new Guid("03921a73-acd4-44a2-9e52-adb881b2942b");
        public const int ToggleAtCaretId = 0x0100;
        public const int ToggleAllId = 0x0101;
        public const int QuickDocId = 0x0102;

        public static void Register(OleMenuCommandService commandService, IComponentModel componentModel, IVsTextManager textManager)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var adapters = componentModel.GetService<IVsEditorAdaptersFactoryService>();

            IWpfTextView ActiveView()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return textManager.GetActiveView(0, null, out IVsTextView vsView) == VSConstants.S_OK && vsView != null
                    ? adapters.GetWpfTextView(vsView)
                    : null;
            }

            DocCommentViewController ActiveController()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var wpfView = ActiveView();
                return wpfView == null ? null : DocCommentViewController.For(wpfView);
            }

            Add(commandService, ToggleAtCaretId, () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var controller = ActiveController();
                if (controller == null) DocCommentActions.SetStatus("Open a C# file to toggle rendered comments.");
                else controller.ToggleAtCaret();
            });

            Add(commandService, ToggleAllId, () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var controller = ActiveController();
                if (controller == null) DocCommentActions.SetStatus("Open a C# file to toggle rendered comments.");
                else controller.ToggleAll();
            });

            // Normally the focused editor handles this itself (QuickDocCommandFilter). This is for
            // when focus is elsewhere, such as Solution Explorer: it uses the last active editor.
            Add(commandService, QuickDocId, () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var view = ActiveView();
                if (view == null || !view.TextBuffer.ContentType.IsOfType("CSharp")) DocCommentActions.SetStatus("Open a C# file and put the cursor on a name to see its documentation.");
                else QuickDocPopup.For(view).ToggleAtCaret();
            });
        }

        private static void Add(OleMenuCommandService commandService, int id, Action action)
        {
            commandService.AddCommand(new MenuCommand((s, e) => action(), new CommandID(CommandSet, id)));
        }
    }
}
