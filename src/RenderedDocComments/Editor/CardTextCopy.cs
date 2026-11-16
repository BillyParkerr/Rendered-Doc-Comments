using System;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;

namespace RenderedDocComments.Editor
{
    /// <summary>
    /// Remembers which card text box you last selected text in, until focus moves back to the
    /// editor (or another non-card element).
    /// </summary>
    internal static class CardTextSelection
    {
        private static readonly object Marker = new object();
        private static WeakReference<RichTextBox> _active;

        public static void Register(RichTextBox box)
        {
            box.Tag = Marker;
            box.GotKeyboardFocus += (s, e) => _active = new WeakReference<RichTextBox>(box);
            box.LostKeyboardFocus += (s, e) =>
            {
                // A VS context menu takes focus without a WPF element (null): keep the selection
                // so the menu's Copy still finds it. Another card box takes over in GotKeyboardFocus.
                if (e.NewFocus == null || (e.NewFocus is RichTextBox other && other.Tag == Marker)) return;
                if (_active != null && _active.TryGetTarget(out var current) && current == box) _active = null;
            };
        }

        /// <summary>The selected card text, or null when no card text is selected.</summary>
        public static string SelectedText =>
            _active != null && _active.TryGetTarget(out var box) && !box.Selection.IsEmpty ? box.Selection.Text : null;
    }

    /// <summary>
    /// VS routes Ctrl+C and the right-click Copy to the editor's Copy command, not to the card's
    /// text box. When card text is selected, copy that; otherwise let the editor copy as usual.
    /// </summary>
    [Export(typeof(ICommandHandler))]
    [ContentType("CSharp")]
    [TextViewRole(Roles.Document)]
    [TextViewRole(Roles.Peek)]
    [TextViewRole(Roles.LeftDiff)]
    [TextViewRole(Roles.RightDiff)]
    [Name(nameof(CardTextCopyCommandHandler))]
    internal sealed class CardTextCopyCommandHandler : ICommandHandler<CopyCommandArgs>
    {
        public string DisplayName => "Copy rendered doc comment text";

        public CommandState GetCommandState(CopyCommandArgs args) =>
            CardTextSelection.SelectedText != null ? CommandState.Available : CommandState.Unspecified;

        public bool ExecuteCommand(CopyCommandArgs args, CommandExecutionContext executionContext)
        {
            string text = CardTextSelection.SelectedText;
            if (text == null)
            {
                return false;
            }

            try
            {
                Clipboard.SetText(text);
            }
            catch (ExternalException)
            {
                // Clipboard busy; nothing else to do.
            }

            return true;
        }
    }
}
