using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;

namespace RenderedDocComments.Editor
{
    /// <summary>
    /// Remembers, per file, which comments you switched away from the default view, in VS's
    /// per-user settings. One entry per file (most recently used kept, oldest dropped).
    /// </summary>
    internal static class CommentViewMemory
    {
        private const string Collection = @"RenderedDocComments\CommentViews";
        private const int MaxFiles = 300;
        private const int KeepAfterPrune = 240;

        /// <summary>Remembered views for a file: comment key → card (true) or raw (false).</summary>
        public static Dictionary<string, bool> Load(string filePath)
        {
            var views = new Dictionary<string, bool>();
            var store = Store(writable: false);
            string name = PropertyName(filePath);
            if (store == null || !store.PropertyExists(Collection, name))
            {
                return views;
            }

            try
            {
                var lines = store.GetString(Collection, name).Split('\n');
                foreach (var line in lines.Skip(2))
                {
                    if (line.Length > 2 && line[1] == '\t')
                    {
                        views[line.Substring(2)] = line[0] == 'C';
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable entry: start fresh for this file.
            }

            return views;
        }

        public static void Save(string filePath, IDictionary<string, bool> views)
        {
            if (filePath == null || !(Store(writable: true) is WritableSettingsStore store))
            {
                return;
            }

            try
            {
                string name = PropertyName(filePath);
                if (views.Count == 0)
                {
                    if (store.CollectionExists(Collection) && store.PropertyExists(Collection, name))
                    {
                        store.DeleteProperty(Collection, name);
                    }

                    return;
                }

                if (!store.CollectionExists(Collection))
                {
                    store.CreateCollection(Collection);
                }

                // Format: last-used ticks, file path, then one "C\tkey" (card) or "R\tkey" (raw) per comment.
                var value = new StringBuilder();
                value.Append(DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
                value.Append(filePath);
                foreach (var view in views)
                {
                    value.Append('\n').Append(view.Value ? 'C' : 'R').Append('\t').Append(view.Key);
                }

                store.SetString(Collection, name, value.ToString());
                Prune(store);
            }
            catch (Exception)
            {
                // Couldn't save; the choices still apply until the file is closed.
            }
        }

        private static void Prune(WritableSettingsStore store)
        {
            if (store.GetPropertyCount(Collection) <= MaxFiles)
            {
                return;
            }

            var byAge = store.GetPropertyNames(Collection)
                .Select(n => new { Name = n, Ticks = LastUsed(store.GetString(Collection, n)) })
                .OrderBy(e => e.Ticks)
                .ToList();

            foreach (var old in byAge.Take(byAge.Count - KeepAfterPrune))
            {
                store.DeleteProperty(Collection, old.Name);
            }
        }

        private static long LastUsed(string value)
        {
            int end = value.IndexOf('\n');
            return long.TryParse(end < 0 ? value : value.Substring(0, end), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks) ? ticks : 0;
        }

        private static SettingsStore Store(bool writable)
        {
            if (!ThreadHelper.CheckAccess())
            {
                return null;
            }

            try
            {
                var manager = new ShellSettingsManager(ServiceProvider.GlobalProvider);
                return writable
                    ? manager.GetWritableSettingsStore(SettingsScope.UserSettings)
                    : manager.GetReadOnlySettingsStore(SettingsScope.UserSettings);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>A short, fixed-length name for a file path (paths can be long and contain any character).</summary>
        private static string PropertyName(string filePath)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(filePath.ToUpperInvariant()));
                return BitConverter.ToString(hash, 0, 16).Replace("-", string.Empty);
            }
        }
    }
}
