using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SsmsPgExplorer.UI
{
    /// <summary>
    /// Reads SSMS 22 Unified Settings so PostgreSQL features follow the user's own SSMS configuration:
    /// defaults from the registration files SSMS ships (SqlStudio.registration.json for
    /// sqlServerObjectExplorer.scriptingOptions.*, SSMS.SqlFormatter\SqlFormatter.registration.json for sqlFormatter.*),
    /// overridden by the user's %LOCALAPPDATA%\Microsoft\SSMS\22.0_&lt;id&gt;\settings.json. Read-only; cached by file timestamp.
    /// </summary>
    internal static class SsmsSettings
    {
        private static readonly object Gate = new object();
        private static Dictionary<string, JsonElement> _values;
        private static DateTime _stamp;

        private const string DefaultIdeDir = @"C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE";

        public static string IdeDir
        {
            get
            {
                try
                {
                    var exe = Process.GetCurrentProcess().MainModule?.FileName;
                    if (exe != null && Path.GetFileName(exe).Equals("Ssms.exe", StringComparison.OrdinalIgnoreCase)) return Path.GetDirectoryName(exe);
                }
                catch { }
                return DefaultIdeDir;
            }
        }

        /// <summary>The SSMS instance folder (…\SSMS\22.0_xxxx) this extension is installed in, else the most recently used one.</summary>
        public static string UserSettingsFile
        {
            get
            {
                try
                {
                    for (var d = new DirectoryInfo(Path.GetDirectoryName(typeof(SsmsSettings).Assembly.Location)); d?.Parent != null; d = d.Parent)
                        if (d.Name.Equals("Extensions", StringComparison.OrdinalIgnoreCase))
                        {
                            var f = Path.Combine(d.Parent.FullName, "settings.json");
                            if (File.Exists(f)) return f;
                        }
                }
                catch { }
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "SSMS");
                if (!Directory.Exists(root)) return null;
                return Directory.GetDirectories(root, "22.0_*").Select(d => Path.Combine(d, "settings.json")).Where(File.Exists)
                                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }
        }

        private static Dictionary<string, JsonElement> Values
        {
            get
            {
                lock (Gate)
                {
                    var user = UserSettingsFile;
                    var stamp = user != null && File.Exists(user) ? File.GetLastWriteTimeUtc(user) : DateTime.MinValue;
                    if (_values != null && stamp == _stamp) return _values;

                    var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    foreach (var reg in new[]
                             {
                                 Path.Combine(IdeDir, "Extensions", "Application", "SqlStudio.registration.json"),
                                 Path.Combine(IdeDir, "Extensions", "Microsoft", "SSMS.SqlFormatter", "SqlFormatter.registration.json"),
                             })
                        LoadDefaults(reg, values);
                    if (user != null) LoadOverrides(user, values);
                    _values = values;
                    _stamp = stamp;
                    Diagnostics.Log.Info($"ssms settings: {values.Count} keys (user file: {user ?? "none"})");
                    return values;
                }
            }
        }

        private static readonly JsonDocumentOptions Lenient = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        private static void LoadDefaults(string file, Dictionary<string, JsonElement> into)
        {
            try
            {
                if (!File.Exists(file)) return;
                using (var doc = JsonDocument.Parse(File.ReadAllText(file), Lenient))
                {
                    if (!doc.RootElement.TryGetProperty("properties", out var props)) return;
                    foreach (var p in props.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("default", out var d))
                            into[p.Name] = d.Clone();
                }
            }
            catch (Exception ex) { Diagnostics.Log.Error("ssms settings defaults " + file, ex); }
        }

        private static void LoadOverrides(string file, Dictionary<string, JsonElement> into)
        {
            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(file), Lenient))
                    foreach (var p in doc.RootElement.EnumerateObject())
                        into[p.Name] = p.Value.Clone();
            }
            catch (Exception ex) { Diagnostics.Log.Error("ssms settings user " + file, ex); }
        }

        public static bool Has(string key) => Values.ContainsKey(key);

        public static bool Bool(string key, bool fallback)
        {
            if (!Values.TryGetValue(key, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b)) return b;
            return fallback;
        }

        public static string String(string key, string fallback)
        {
            if (!Values.TryGetValue(key, out var v)) return fallback;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        }

        public static int Int(string key, int fallback)
        {
            if (!Values.TryGetValue(key, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
            return int.TryParse(v.ToString(), out var j) ? j : fallback;
        }
    }
}
