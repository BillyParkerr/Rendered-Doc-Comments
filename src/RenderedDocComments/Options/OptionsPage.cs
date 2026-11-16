using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace RenderedDocComments.Options
{
    /// <summary>Tools > Options > Rendered Doc Comments > General.</summary>
    [Guid("b6c0a4f2-6d0e-4a52-9a3e-2f1c7d8e5b41")]
    public sealed class OptionsPage : DialogPage
    {
        [Category("Display")]
        [DisplayName("Render comments when a file opens")]
        [Description("Show documentation comments as cards when a file opens. When off, comments start raw; the gutter icon and Ctrl+M, Ctrl+Q still render individual comments. Applies to files opened after the change.")]
        [DefaultValue(true)]
        public bool RenderByDefault { get; set; } = true;

        [Category("Display")]
        [DisplayName("Show gutter icon")]
        [Description("Show the pencil (edit) and book (render) icon next to the line numbers on every documentation comment.")]
        [DefaultValue(true)]
        public bool ShowGutterIcon { get; set; } = true;

        [Category("Card")]
        [DisplayName("Maximum card width")]
        [Description("Widest a card gets, in pixels. Cards also shrink to fit the editor window.")]
        [DefaultValue(ExtensionOptions.DefaultMaxCardWidth)]
        public int MaxCardWidth { get; set; } = ExtensionOptions.DefaultMaxCardWidth;

        [Category("Card")]
        [DisplayName("Text font")]
        [Description("Font for card text. Code in cards keeps the editor font.")]
        [DefaultValue(ExtensionOptions.DefaultProseFontFamily)]
        public string ProseFontFamily { get; set; } = ExtensionOptions.DefaultProseFontFamily;

        [Category("Card")]
        [DisplayName("Text size (%)")]
        [Description("Card text size relative to the editor font size, from 50 to 200.")]
        [DefaultValue(100)]
        public int ProseFontSizePercent { get; set; } = 100;

        [Category("Quick documentation")]
        [DisplayName("Show cards on hover")]
        [Description("Hovering over a name shows its documentation as a card instead of Visual Studio's tooltip (Quick Info). Where there's an error or warning under the mouse, Visual Studio's tooltip shows as usual, and Ctrl+K, Ctrl+I still shows it. The shortcut shows the card for the name at the cursor either way.")]
        [DefaultValue(false)]
        public bool ShowCardsOnHover { get; set; }

        [Category("Quick documentation")]
        [DisplayName("Shortcut")]
        [Description("Keys that show the documentation of the name at the cursor, such as Ctrl+Shift+F1, or Ctrl+K, Ctrl+Q for two keys in a row. Leave empty for no shortcut. This is the same setting as the RenderedDocComments.ShowQuickDocumentation command in Tools > Options > Environment > Keyboard.")]
        [DefaultValue(QuickDocShortcut.Default)]
        public string QuickDocShortcutKeys { get; set; } = QuickDocShortcut.Default;

        /// <summary>The shortcut as VS had it when the page last read it (null if VS couldn't say).</summary>
        private string _loadedShortcut;

        public override void LoadSettingsFromStorage()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ExtensionOptions.EnsureLoaded();
            RenderByDefault = ExtensionOptions.RenderByDefault;
            ShowGutterIcon = ExtensionOptions.ShowGutterIcon;
            MaxCardWidth = ExtensionOptions.MaxCardWidth;
            ProseFontFamily = ExtensionOptions.ProseFontFamily;
            ProseFontSizePercent = ExtensionOptions.ProseFontSizePercent;
            ShowCardsOnHover = ExtensionOptions.ShowCardsOnHover;
            LoadShortcut();
        }

        private void LoadShortcut()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _loadedShortcut = QuickDocShortcut.Get();
            QuickDocShortcutKeys = _loadedShortcut ?? QuickDocShortcut.Default;
        }

        /// <summary>The shortcut may have been changed in Tools > Options > Keyboard since: show it as it is now.</summary>
        protected override void OnActivate(CancelEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (QuickDocShortcutKeys == (_loadedShortcut ?? QuickDocShortcut.Default))
            {
                LoadShortcut(); // Not edited on this page: refresh it.
            }

            base.OnActivate(e);
        }

        /// <summary>Sets the shortcut first, so keys VS doesn't understand keep the page open to fix them.</summary>
        protected override void OnApply(PageApplyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (e.ApplyBehavior == ApplyKind.Apply
                && _loadedShortcut != null
                && !string.Equals(ShortcutText.Normalize(QuickDocShortcutKeys), ShortcutText.Normalize(_loadedShortcut), System.StringComparison.OrdinalIgnoreCase))
            {
                string problem = QuickDocShortcut.Set(QuickDocShortcutKeys);
                if (problem != null)
                {
                    VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, problem, "Rendered Doc Comments", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                    e.ApplyBehavior = ApplyKind.CancelNoNavigate;
                    return;
                }

                LoadShortcut(); // As VS stored it.
            }

            base.OnApply(e);
        }

        public override void SaveSettingsToStorage()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ExtensionOptions.Save(RenderByDefault, ShowGutterIcon, MaxCardWidth, ProseFontFamily, ProseFontSizePercent, ShowCardsOnHover);
        }
    }
}
