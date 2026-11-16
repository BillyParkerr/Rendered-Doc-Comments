using System;
using System.Linq;
using System.Runtime.InteropServices;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using RenderedDocComments.Commands;

namespace RenderedDocComments.Options
{
    /// <summary>
    /// The quick documentation shortcut, as an option. VS keeps key bindings itself, so this
    /// reads and sets the Show Quick Documentation command's own binding: the same setting as
    /// in Tools > Options > Environment > Keyboard.
    /// </summary>
    internal static class QuickDocShortcut
    {
        public const string Default = "Ctrl+Shift+F1";

        /// <summary>The scope for a new binding when the command has none to copy it from (the English name).</summary>
        private const string FallbackScope = "Text Editor";

        /// <summary>The command's keys, e.g. "Ctrl+Shift+F1" (its first binding); empty for none, null if VS can't say.</summary>
        public static string Get()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = Command();
            if (command == null)
            {
                return null;
            }

            var first = Bindings(command).FirstOrDefault();
            return first == null ? string.Empty : KeysOf(first);
        }

        /// <summary>Bind the command to these keys instead of its current ones; empty removes the shortcut.</summary>
        /// <returns>Null when done, otherwise what went wrong, to tell the user.</returns>
        public static string Set(string keys)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = Command();
            if (command == null)
            {
                return "Visual Studio didn't find the Show Quick Documentation command, so the shortcut wasn't changed.";
            }

            string normalized = ShortcutText.Normalize(keys);
            try
            {
                if (normalized.Length == 0)
                {
                    command.Bindings = new object[0];
                    return null;
                }

                // Keep the scope it had: scope names are in VS's display language.
                string scope = Bindings(command).Select(ScopeOf).FirstOrDefault(s => s.Length > 0) ?? FallbackScope;
                command.Bindings = new object[] { scope + "::" + normalized };
                return null;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is COMException)
            {
                return "\"" + keys.Trim() + "\" isn't a shortcut Visual Studio understands. Use a form like Ctrl+Shift+F1, or Ctrl+K, Ctrl+Q for two keys in a row.";
            }
        }

        private static Command Command()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = Package.GetGlobalService(typeof(SDTE)) as DTE;
                return dte?.Commands.Item(DocCommentCommands.CommandSet.ToString("B"), DocCommentCommands.QuickDocId);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is COMException)
            {
                return null;
            }
        }

        private static string[] Bindings(Command command)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return command?.Bindings is object[] bindings ? bindings.OfType<string>().ToArray() : new string[0];
        }

        /// <summary>"Text Editor::Ctrl+Shift+F1" → "Text Editor".</summary>
        private static string ScopeOf(string binding)
        {
            int at = binding.IndexOf("::", StringComparison.Ordinal);
            return at < 0 ? string.Empty : binding.Substring(0, at);
        }

        /// <summary>"Text Editor::Ctrl+Shift+F1" → "Ctrl+Shift+F1".</summary>
        private static string KeysOf(string binding)
        {
            int at = binding.IndexOf("::", StringComparison.Ordinal);
            return at < 0 ? binding : binding.Substring(at + 2);
        }
    }
}
