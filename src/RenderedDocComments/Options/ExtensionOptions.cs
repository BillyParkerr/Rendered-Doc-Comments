using System;
using System.Windows.Threading;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities.UnifiedSettings;
using RenderedDocComments.Diagnostics;
using RenderedDocComments.Editor;

namespace RenderedDocComments.Options
{
    /// <summary>
    /// The extension's settings. The editor parts read them without loading the package.
    /// <list type="bullet">
    /// <item><description>In Visual Studio 2026 they're in the Settings window (Unified Settings,
    /// see Options\registration.json), which keeps them in step with the classic store below.
    /// Changes made there apply straight away.</description></item>
    /// <item><description>Otherwise they're in Visual Studio's user settings store (collection
    /// "RenderedDocComments"), written by the classic Tools > Options page.</description></item>
    /// </list>
    /// </summary>
    internal static class ExtensionOptions
    {
        public const string DefaultProseFontFamily = "Segoe UI Variable Text, Segoe UI";
        public const int DefaultMaxCardWidth = 760;

        private const string Collection = "RenderedDocComments";

        /// <summary>The shortcut as last applied from the Settings window, to tell its changes from the keyboard settings'.</summary>
        private const string AppliedShortcut = "AppliedQuickDocShortcut";

        private static bool _loaded;
        private static UnifiedSettingsStore _unified;
        private static IDisposable _subscription;

        public static event EventHandler Changed;

        /// <summary>Show comments as cards when a file opens.</summary>
        public static bool RenderByDefault { get; private set; } = true;

        /// <summary>Show the pencil/book toggle next to the line numbers.</summary>
        public static bool ShowGutterIcon { get; private set; } = true;

        /// <summary>Widest a card gets, in pixels; it also shrinks to fit the window.</summary>
        public static int MaxCardWidth { get; private set; } = DefaultMaxCardWidth;

        /// <summary>Font for card text (code keeps the editor font).</summary>
        public static string ProseFontFamily { get; private set; } = DefaultProseFontFamily;

        /// <summary>Card text size as a percentage of the editor font size.</summary>
        public static int ProseFontSizePercent { get; private set; } = 100;

        /// <summary>Hovering a name shows its documentation card instead of VS's Quick Info tooltip.</summary>
        public static bool ShowCardsOnHover { get; private set; }

        /// <summary>Read the settings once. Must run on the UI thread; until it can, defaults apply.</summary>
        public static void EnsureLoaded()
        {
            if (_loaded || !ThreadHelper.CheckAccess())
            {
                return;
            }

            ThreadHelper.ThrowIfNotOnUIThread();
            _loaded = true;
            LoadClassic();

            _unified = UnifiedSettingsStore.TryCreate();
            if (_unified == null)
            {
                return;
            }

            Guard.Run("Reading settings", () =>
            {
                LoadUnified();
                _subscription = _unified.Subscribe(OnUnifiedChanged);
            });

            // Visual Studio's key bindings may not be ready this early in startup.
            Guard.Later(Dispatcher.CurrentDispatcher, DispatcherPriority.ApplicationIdle, "Syncing the quick documentation shortcut", () => SyncShortcut(startup: true));
        }

        /// <summary>The classic Tools > Options page's OK (where there's no Settings window).</summary>
        public static void Save(bool renderByDefault, bool showGutterIcon, int maxCardWidth, string proseFontFamily, int proseFontSizePercent, bool showCardsOnHover)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _loaded = true;
            RenderByDefault = renderByDefault;
            ShowGutterIcon = showGutterIcon;
            MaxCardWidth = ClampWidth(maxCardWidth);
            ProseFontFamily = CleanFont(proseFontFamily);
            ProseFontSizePercent = ClampSize(proseFontSizePercent);
            ShowCardsOnHover = showCardsOnHover;

            try
            {
                var store = WritableStore();
                store.SetBoolean(Collection, nameof(RenderByDefault), RenderByDefault);
                store.SetBoolean(Collection, nameof(ShowGutterIcon), ShowGutterIcon);
                store.SetInt32(Collection, nameof(MaxCardWidth), MaxCardWidth);
                store.SetString(Collection, nameof(ProseFontFamily), ProseFontFamily);
                store.SetInt32(Collection, nameof(ProseFontSizePercent), ProseFontSizePercent);
                store.SetBoolean(Collection, nameof(ShowCardsOnHover), ShowCardsOnHover);
            }
            catch (Exception)
            {
                // Couldn't persist; the new values still apply for this session.
            }

            Changed?.Invoke(null, EventArgs.Empty);
        }

        private static void LoadClassic()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var store = new ShellSettingsManager(ServiceProvider.GlobalProvider).GetReadOnlySettingsStore(SettingsScope.UserSettings);
                if (!store.CollectionExists(Collection))
                {
                    return;
                }

                RenderByDefault = store.GetBoolean(Collection, nameof(RenderByDefault), RenderByDefault);
                ShowGutterIcon = store.GetBoolean(Collection, nameof(ShowGutterIcon), ShowGutterIcon);
                MaxCardWidth = ClampWidth(store.GetInt32(Collection, nameof(MaxCardWidth), MaxCardWidth));
                ProseFontFamily = CleanFont(store.GetString(Collection, nameof(ProseFontFamily), ProseFontFamily));
                ProseFontSizePercent = ClampSize(store.GetInt32(Collection, nameof(ProseFontSizePercent), ProseFontSizePercent));
                ShowCardsOnHover = store.GetBoolean(Collection, nameof(ShowCardsOnHover), ShowCardsOnHover);
            }
            catch (Exception)
            {
                // Settings store unavailable: keep the defaults.
            }
        }

        /// <summary>The Settings window's values, over the classic ones (any it can't provide stay as they are).</summary>
        private static void LoadUnified()
        {
            if (_unified.TryGet(UnifiedSettingsStore.RenderByDefault, out bool render)) RenderByDefault = render;
            if (_unified.TryGet(UnifiedSettingsStore.ShowGutterIcon, out bool gutter)) ShowGutterIcon = gutter;
            if (_unified.TryGet(UnifiedSettingsStore.MaxCardWidth, out int width)) MaxCardWidth = ClampWidth(width);
            if (_unified.TryGet(UnifiedSettingsStore.TextFont, out string font)) ProseFontFamily = CleanFont(font);
            if (_unified.TryGet(UnifiedSettingsStore.TextSizePercent, out int size)) ProseFontSizePercent = ClampSize(size);
            if (_unified.TryGet(UnifiedSettingsStore.ShowCardsOnHover, out bool hover)) ShowCardsOnHover = hover;
        }

        /// <summary>A change in the Settings window (it calls on any thread): apply it on the UI thread.</summary>
        private static void OnUnifiedChanged(SettingsUpdate update)
        {
            var jtf = ThreadHelper.JoinableTaskContext.Factory;
            jtf.RunAsync(async () =>
            {
                await jtf.SwitchToMainThreadAsync();
                Guard.Run("Applying changed settings", () =>
                {
                    LoadUnified();
                    if (update?.ChangedSettingMonikers == null || update.ChangedSettingMonikers.Contains(UnifiedSettingsStore.QuickDocShortcut))
                    {
                        SyncShortcut(startup: false);
                    }

                    Changed?.Invoke(null, EventArgs.Empty);
                });
            }).FileAndForget("RenderedDocComments/SettingsChanged");
        }

        /// <summary>
        /// Keeps the shortcut setting and Visual Studio's key binding in step. A change to the
        /// setting sets the binding. At startup, a binding changed in the keyboard settings since
        /// the setting was last applied is shown in the setting instead.
        /// </summary>
        private static void SyncShortcut(bool startup)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_unified == null || !_unified.TryGet(UnifiedSettingsStore.QuickDocShortcut, out string setting))
            {
                return;
            }

            string bound = QuickDocShortcut.Get();
            if (bound == null)
            {
                return; // Visual Studio can't say (yet).
            }

            string wanted = ShortcutText.Normalize(setting);
            bound = ShortcutText.Normalize(bound);
            string applied = ReadAppliedShortcut();
            if (Same(wanted, bound))
            {
                if (!Same(applied, wanted)) WriteAppliedShortcut(wanted);
                return;
            }

            // Unchanged in the Settings window since it was last applied (or never changed there):
            // the keyboard settings changed the binding, so show their keys here.
            bool settingUnchanged = applied != null ? Same(wanted, applied) : Same(wanted, ShortcutText.Normalize(QuickDocShortcut.Default));
            if (startup && settingUnchanged)
            {
                _unified.Set(UnifiedSettingsStore.QuickDocShortcut, bound);
                WriteAppliedShortcut(bound);
                return;
            }

            string problem = QuickDocShortcut.Set(wanted);
            if (problem == null)
            {
                WriteAppliedShortcut(wanted);
            }
            else
            {
                DocCommentActions.SetStatus("Rendered Doc Comments: " + problem);
            }
        }

        private static bool Same(string a, string b) => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string ReadAppliedShortcut()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var store = new ShellSettingsManager(ServiceProvider.GlobalProvider).GetReadOnlySettingsStore(SettingsScope.UserSettings);
                return store.PropertyExists(Collection, AppliedShortcut) ? store.GetString(Collection, AppliedShortcut) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WriteAppliedShortcut(string keys)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                WritableStore().SetString(Collection, AppliedShortcut, keys);
            }
            catch (Exception)
            {
                // Not remembered: the next startup checks the binding again, which is harmless.
            }
        }

        private static WritableSettingsStore WritableStore()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var store = new ShellSettingsManager(ServiceProvider.GlobalProvider).GetWritableSettingsStore(SettingsScope.UserSettings);
            if (!store.CollectionExists(Collection))
            {
                store.CreateCollection(Collection);
            }

            return store;
        }

        private static int ClampWidth(int width) => Math.Max(280, Math.Min(4000, width));

        private static int ClampSize(int percent) => Math.Max(50, Math.Min(200, percent));

        private static string CleanFont(string font) => string.IsNullOrWhiteSpace(font) ? DefaultProseFontFamily : font.Trim();
    }
}
