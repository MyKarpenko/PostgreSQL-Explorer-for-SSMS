using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SsmsPgExplorer.Model
{
    /// <summary>
    /// Persists registrations to %APPDATA%\SsmsPgExplorer\registered-servers.json (no secrets).
    /// </summary>
    public static class ServerStore
    {
        [DataContract]
        private sealed class StoreFile
        {
            [DataMember(Order = 0)] public int Version { get; set; } = 1;
            [DataMember(Order = 1)] public List<ServerRegistration> Servers { get; set; } = new List<ServerRegistration>();
            /// <summary>Explicit groups (so a group can exist empty, as in SSMS). Older files don't have it.</summary>
            [DataMember(Order = 2, EmitDefaultValue = false)] public List<string> Groups { get; set; }
        }

        public static string Folder { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SsmsPgExplorer");

        public static string FilePath { get; } = Path.Combine(Folder, "registered-servers.json");

        public static event EventHandler Changed;

        private static readonly object Gate = new object();

        /// <summary>
        /// Loads all registrations. A file that exists but can't be read THROWS (never returns empty):
        /// returning empty would let the next Save overwrite every registration.
        /// </summary>
        public static List<ServerRegistration> Load()
        {
            lock (Gate)
            {
                if (!File.Exists(FilePath))
                {
                    Diagnostics.Log.Info($"store: {FilePath} does not exist, 0 registrations");
                    return new List<ServerRegistration>();
                }
                try
                {
                    var list = Read(FilePath);
                    // Duplicate ids would make Upsert/Delete hit the wrong entry; give later duplicates fresh ids.
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in list)
                        if (string.IsNullOrWhiteSpace(r.Id) || !seen.Add(r.Id))
                        {
                            Diagnostics.Log.Info($"store: duplicate/empty id '{r.Id}' on '{r.DisplayName}', assigning a new id");
                            r.Id = Guid.NewGuid().ToString("N");
                            seen.Add(r.Id);
                        }
                    return list;
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Error($"store: failed to read {FilePath}", ex);
                    throw new InvalidOperationException($"Could not read {FilePath}: {ex.Message}. The file was left untouched.", ex);
                }
            }
        }

        public static List<ServerRegistration> Read(string path)
        {
            using (var fs = File.OpenRead(path))
            {
                var file = (StoreFile)Serializer().ReadObject(fs);
                return file?.Servers ?? new List<ServerRegistration>();
            }
        }

        /// <summary>Saves registrations, keeping the explicit group list as it is on disk.</summary>
        public static void Save(IEnumerable<ServerRegistration> servers) => SaveAll(servers, null);

        /// <summary>Explicit (possibly empty) groups stored in the file.</summary>
        public static List<string> LoadExplicitGroups()
        {
            lock (Gate)
            {
                if (!File.Exists(FilePath)) return new List<string>();
                using (var fs = File.OpenRead(FilePath))
                    return ((StoreFile)Serializer().ReadObject(fs))?.Groups?.Select(NormalizeGroup).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                           ?? new List<string>();
            }
        }

        /// <param name="groups">null = keep the explicit groups currently on disk.</param>
        private static void SaveAll(IEnumerable<ServerRegistration> servers, IEnumerable<string> groups)
        {
            var list = servers.ToList();
            lock (Gate)
            {
                try
                {
                    var g = (groups ?? LoadExplicitGroups()).Select(NormalizeGroup).Where(x => x.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                    Write(FilePath, list, g);
                    Diagnostics.Log.Info($"store: saved {list.Count} registration(s), {g.Count} explicit group(s) to {FilePath}");
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Error($"store: failed to save {list.Count} registration(s) to {FilePath}", ex);
                    throw;
                }
            }
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Creates an empty group (and its parents). No-op if it already exists.</summary>
        public static void AddGroup(string group)
        {
            var g = NormalizeGroup(group);
            if (g.Length == 0) return;
            var groups = LoadExplicitGroups();
            if (groups.Contains(g, StringComparer.OrdinalIgnoreCase)) return;
            groups.Add(g);
            SaveAll(Load(), groups);
            Diagnostics.Log.Info($"store: created group '{g}'");
        }

        /// <summary>Deletes a group, its subgroups and every registration in them. Returns the deleted registration ids.</summary>
        public static List<string> DeleteGroup(string group)
        {
            var g = NormalizeGroup(group);
            bool Under(string path) => string.Equals(path, g, StringComparison.OrdinalIgnoreCase) || path.StartsWith(g + "/", StringComparison.OrdinalIgnoreCase);
            var all = Load();
            var doomed = all.Where(r => Under(r.GroupPath)).Select(r => r.Id).ToList();
            var groups = LoadExplicitGroups().Where(x => !Under(x)).ToList();
            SaveAll(all.Where(r => !Under(r.GroupPath)), groups);
            Diagnostics.Log.Info($"store: deleted group '{g}' ({doomed.Count} registrations)");
            return doomed;
        }

        public static void Write(string path, IEnumerable<ServerRegistration> servers, IEnumerable<string> groups = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, ownsStream: false, indent: true))
            {
                var g = groups?.ToList();
                Serializer().WriteObject(w, new StoreFile { Servers = servers.ToList(), Groups = g != null && g.Count > 0 ? g : null });
            }
            if (File.Exists(path))
            {
                // Keep one previous version next to the store; cheap insurance against a bad save.
                File.Copy(path, path + ".bak", overwrite: true);
                File.Copy(tmp, path, overwrite: true);
                File.Delete(tmp);
            }
            else File.Move(tmp, path);
        }

        public static void Upsert(ServerRegistration reg)
        {
            var all = Load();
            var i = all.FindIndex(s => s.Id == reg.Id);
            if (i >= 0) all[i] = reg; else all.Add(reg);
            Save(all);
        }

        public static void Delete(string id)
        {
            var all = Load();
            all.RemoveAll(s => s.Id == id);
            Save(all);
        }

        public static ServerRegistration Find(string id) => Load().FirstOrDefault(s => s.Id == id);

        /// <summary>Every group path that exists, including intermediate levels ("a", "a/b", "a/b/c"), sorted.</summary>
        public static List<string> AllGroupPaths()
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Load().Select(r => r.GroupPath).Concat(LoadExplicitGroups()))
            {
                var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 1; i <= parts.Length; i++) set.Add(string.Join("/", parts.Take(i).Select(p => p.Trim())));
            }
            return set.ToList();
        }

        /// <summary>Moves registrations into <paramref name="group"/> ("" = top level). Returns how many moved.</summary>
        public static int MoveServers(IEnumerable<string> ids, string group)
        {
            var target = NormalizeGroup(group);
            var set = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            var all = Load();
            var moved = 0;
            foreach (var r in all.Where(r => set.Contains(r.Id) && !string.Equals(r.GroupPath, target, StringComparison.OrdinalIgnoreCase)))
            {
                r.Group = target;
                moved++;
            }
            if (moved > 0) { Save(all); Diagnostics.Log.Info($"store: moved {moved} registration(s) to '{target}'"); }
            return moved;
        }

        /// <summary>
        /// Re-paths a group and everything under it: "a/b" to "x/b" (move under x) or to any new path (rename).
        /// Refuses to move a group into itself or one of its own subgroups.
        /// </summary>
        public static int RepathGroup(string group, string newPath)
        {
            var from = NormalizeGroup(group);
            var to = NormalizeGroup(newPath);
            if (from.Length == 0 || to.Length == 0 || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return 0;
            if (to.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Can't move '{from}' into its own subgroup '{to}'.");

            string Repath(string g) =>
                string.Equals(g, from, StringComparison.OrdinalIgnoreCase) ? to
                : g.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase) ? to + g.Substring(from.Length)
                : null;

            var all = Load();
            var moved = 0;
            foreach (var r in all)
            {
                var p = Repath(r.GroupPath);
                if (p != null) { r.Group = p; moved++; }
            }
            // Empty groups move too.
            var groups = LoadExplicitGroups().Select(g => Repath(g) ?? g).ToList();
            SaveAll(all, groups);
            Diagnostics.Log.Info($"store: group '{from}' -> '{to}' ({moved} registrations)");
            return moved;
        }

        public static string LeafName(string group)
        {
            var g = NormalizeGroup(group);
            var i = g.LastIndexOf('/');
            return i < 0 ? g : g.Substring(i + 1);
        }

        public static string NormalizeGroup(string group) =>
            string.Join("/", (group ?? "").Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0));

        private static DataContractJsonSerializer Serializer() =>
            new DataContractJsonSerializer(typeof(StoreFile), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
    }
}
