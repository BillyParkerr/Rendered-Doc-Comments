using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Xunit;

namespace RenderedDocComments.Tests
{
    /// <summary>
    /// Options\registration.json, which puts the settings in Visual Studio 2026's Settings window.
    /// Visual Studio ignores a registration it can't use without saying why, so these check it
    /// matches the code that reads the settings.
    /// </summary>
    public class SettingsRegistrationTests
    {
        private static readonly Regex Moniker = new Regex(@"^([a-z0-9][A-Za-z0-9]+\.)*[a-z0-9][A-Za-z0-9]+$");

        private static string OptionsFolder()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string options = Path.Combine(dir.FullName, "src", "RenderedDocComments", "Options");
                if (File.Exists(Path.Combine(options, "registration.json"))) return options;
            }

            throw new DirectoryNotFoundException("No src/RenderedDocComments/Options above " + AppContext.BaseDirectory);
        }

        /// <summary>The registration, without its leading comment (Visual Studio reads JSON with comments).</summary>
        private static Dictionary<string, object> Registration()
        {
            string text = File.ReadAllText(Path.Combine(OptionsFolder(), "registration.json"));
            text = Regex.Replace(text, @"^\s*/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            return (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(text);
        }

        private static Dictionary<string, Dictionary<string, object>> Section(string name) =>
            ((Dictionary<string, object>)Registration()[name]).ToDictionary(p => p.Key, p => (Dictionary<string, object>)p.Value);

        private static string MigrationPath(Dictionary<string, object> setting)
        {
            if (!setting.TryGetValue("migration", out var migration)) return null;
            var pass = (Dictionary<string, object>)((Dictionary<string, object>)migration)["pass"];
            Assert.Equal("full", pass["mode"]); // Kept in step both ways with the classic store.
            var input = (Dictionary<string, object>)pass["input"];
            Assert.Equal("VsUserSettingsRegistry", input["store"]);
            return (string)input["path"];
        }

        [Fact]
        public void Every_setting_has_a_valid_moniker_title_description_and_default()
        {
            foreach (var setting in Section("properties"))
            {
                Assert.Matches(Moniker, setting.Key);
                Assert.StartsWith("renderedDocComments.", setting.Key);
                foreach (string key in new[] { "type", "title", "description", "default" })
                {
                    Assert.True(setting.Value.ContainsKey(key), setting.Key + " has no " + key);
                }
            }

            foreach (var category in Section("categories"))
            {
                Assert.Matches(Moniker, category.Key);
            }
        }

        [Fact]
        public void Stored_settings_map_to_the_classic_store_and_its_defaults()
        {
            // The values ExtensionOptions reads from collection "RenderedDocComments", and its defaults.
            var expected = new Dictionary<string, (string Path, object Default)>
            {
                ["renderedDocComments.renderByDefault"] = (@"RenderedDocComments\RenderByDefault", true),
                ["renderedDocComments.showGutterIcon"] = (@"RenderedDocComments\ShowGutterIcon", true),
                ["renderedDocComments.maxCardWidth"] = (@"RenderedDocComments\MaxCardWidth", 760),
                ["renderedDocComments.textFont"] = (@"RenderedDocComments\ProseFontFamily", "Segoe UI Variable Text, Segoe UI"),
                ["renderedDocComments.textSizePercent"] = (@"RenderedDocComments\ProseFontSizePercent", 100),
                ["renderedDocComments.showCardsOnHover"] = (@"RenderedDocComments\ShowCardsOnHover", false),
            };

            var settings = Section("properties");
            foreach (var setting in expected)
            {
                Assert.True(settings.ContainsKey(setting.Key), "Missing " + setting.Key);
                Assert.Equal(setting.Value.Path, MigrationPath(settings[setting.Key]));
                Assert.Equal(setting.Value.Default, settings[setting.Key]["default"]);
            }

            // The shortcut is Visual Studio's key binding, so it has nothing stored to migrate.
            var shortcut = settings["renderedDocComments.quickDocumentationShortcut"];
            Assert.Null(MigrationPath(shortcut));
            Assert.Equal("Ctrl+Shift+F1", shortcut["default"]);
            Assert.Equal(expected.Count + 1, settings.Count);
        }

        [Fact]
        public void The_code_reads_exactly_the_registered_settings()
        {
            string store = File.ReadAllText(Path.Combine(OptionsFolder(), "UnifiedSettingsStore.cs"));
            var used = Regex.Matches(store, "\"(renderedDocComments\\.[A-Za-z]+)\"").Cast<Match>().Select(m => m.Groups[1].Value);
            Assert.Equal(Section("properties").Keys.OrderBy(k => k), used.OrderBy(k => k));
        }

        [Fact]
        public void The_category_replaces_the_classic_options_page()
        {
            string page = File.ReadAllText(Path.Combine(OptionsFolder(), "OptionsPage.cs"));
            string pageGuid = Regex.Match(page, "\\[Guid\\(\"([0-9a-fA-F-]+)\"\\)\\]").Groups[1].Value;
            var category = Section("categories")["renderedDocComments"];
            Assert.Equal(pageGuid, (string)category["legacyOptionPageId"], ignoreCase: true);
            Assert.Equal("Rendered Doc Comments", category["title"]);
        }
    }
}
