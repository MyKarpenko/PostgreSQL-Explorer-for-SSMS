using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using SsmsPgExplorer.Model;

namespace SsmsPgExplorer.Import
{
    /// <summary>One connection found in another tool, mapped to a registration (never with a password).</summary>
    internal sealed class ImportCandidate
    {
        public ServerRegistration Registration;
        public string Source;  // file it came from
        public string Note;    // what could not be carried over
    }

    /// <summary>
    /// Reads connection definitions the user's own tools wrote on this machine (interoperability only).
    /// Passwords are never read or decrypted: Secrets Manager / IAM entries keep working via the AWS
    /// profile; password entries prompt on first connect.
    /// </summary>
    internal static class Importers
    {
        // ------------------------------------------------------------------ DataGrip

        /// <summary>DataGrip project folders (.idea/dataSources.xml) from recentProjects.xml + ~/DataGripProjects, plus global data sources.</summary>
        public static List<string> FindDataGripSources()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var jb = Path.Combine(appData, "JetBrains");
            var files = new List<string>();
            if (Directory.Exists(jb))
            {
                foreach (var cfg in Directory.GetDirectories(jb, "DataGrip*"))
                {
                    var global = Path.Combine(cfg, "options", "dataSources.xml");
                    if (File.Exists(global)) files.Add(global);
                    var recent = Path.Combine(cfg, "options", "recentProjects.xml");
                    if (!File.Exists(recent)) continue;
                    try
                    {
                        var doc = new XmlDocument();
                        doc.Load(recent);
                        foreach (XmlElement e in doc.SelectNodes("//option[@name='additionalInfo']/map/entry"))
                        {
                            var p = e.GetAttribute("key").Replace("$USER_HOME$", home).Replace('/', Path.DirectorySeparatorChar);
                            var ds = Path.Combine(p, ".idea", "dataSources.xml");
                            if (File.Exists(ds)) files.Add(ds);
                        }
                    }
                    catch (Exception ex) { Diagnostics.Log.Error("datagrip: recentProjects " + recent, ex); }
                }
            }
            var projects = Path.Combine(home, "DataGripProjects");
            if (Directory.Exists(projects))
                foreach (var d in Directory.GetDirectories(projects))
                {
                    var ds = Path.Combine(d, ".idea", "dataSources.xml");
                    if (File.Exists(ds)) files.Add(ds);
                }
            return files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<ImportCandidate> ReadDataGrip(string dataSourcesXml)
        {
            var result = new List<ImportCandidate>();
            var doc = new XmlDocument();
            doc.Load(dataSourcesXml);

            // dataSources.local.xml (same folder): user name, auth provider, SSL. Passwords are not in either file.
            var local = new Dictionary<string, XmlElement>(StringComparer.OrdinalIgnoreCase);
            var localPath = Path.Combine(Path.GetDirectoryName(dataSourcesXml), "dataSources.local.xml");
            if (File.Exists(localPath))
            {
                var l = new XmlDocument();
                l.Load(localPath);
                foreach (XmlElement e in l.SelectNodes("//component[@name='dataSourceStorageLocal']/data-source"))
                    local[e.GetAttribute("uuid")] = e;
            }

            foreach (XmlElement ds in doc.SelectNodes("//component[@name='DataSourceManagerImpl']/data-source"))
            {
                var driver = Text(ds, "driver-ref");
                var url = Text(ds, "jdbc-url");
                if (!(driver.StartsWith("postgres", StringComparison.OrdinalIgnoreCase) || url.IndexOf(":postgresql:", StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;

                var uuid = ds.GetAttribute("uuid");
                var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (XmlElement p in ds.SelectNodes("jdbc-additional-properties/property"))
                    props[p.GetAttribute("name")] = p.GetAttribute("value");
                local.TryGetValue(uuid, out var loc);

                var (host, port, db, query) = ParseJdbcUrl(url);
                var reg = new ServerRegistration
                {
                    Id = "dg-" + uuid.Replace("-", ""),
                    Name = ds.GetAttribute("name"),
                    Group = Prefix("DataGrip", StripRoot(ds.GetAttribute("group"))),
                    Description = Text(ds, "remarks"),
                    Host = host,
                    Port = port,
                    Database = string.IsNullOrEmpty(db) ? "postgres" : db,
                    Username = loc != null ? Text(loc, "user-name") : "",
                    SslMode = MapSsl(loc != null ? Text(loc, "ssl-config/mode") : null, query),
                };

                var notes = new List<string>();
                var auth = loc != null ? Text(loc, "auth-provider") : "";
                props.TryGetValue("AWS.CredentialId", out var credId);
                var profile = credId != null && credId.StartsWith("profile:", StringComparison.OrdinalIgnoreCase) ? credId.Substring(8) : "";
                props.TryGetValue("AWS.RegionId", out var region);

                if (auth.IndexOf("secretsmanager", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    reg.Auth = AuthMode.AwsSecretsManager;
                    props.TryGetValue("AWS.SecretId", out var secret);
                    reg.SecretId = secret ?? "";
                    reg.AwsProfile = profile;
                    reg.AwsRegion = !string.IsNullOrEmpty(region) ? region : RegionFromArn(secret) ?? reg.AwsRegion;
                    if (string.IsNullOrEmpty(profile)) notes.Add("no AWS profile in DataGrip (uses default credentials)");
                }
                else if (auth.IndexOf("iam", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    reg.Auth = AuthMode.RdsIam;
                    reg.AwsProfile = profile;
                    if (!string.IsNullOrEmpty(region)) reg.AwsRegion = region;
                }
                else
                {
                    reg.Auth = AuthMode.Password;
                    if (!string.IsNullOrEmpty(auth) && auth != "credentials") notes.Add($"auth '{auth}' -> password");
                }
                props.TryGetValue("AWS.getUrlFromSecret", out var urlFromSecret);
                if (string.IsNullOrEmpty(host))
                {
                    if (reg.Auth == AuthMode.AwsSecretsManager)
                    {
                        // Blank host/port = filled from the secret at connect time (same as DataGrip's getUrlFromSecret).
                        reg.Port = 0;
                        if (urlFromSecret != "true") notes.Add("no host in URL - using the secret's host");
                    }
                    else notes.Add("no host in URL");
                }

                result.Add(new ImportCandidate { Registration = reg, Source = dataSourcesXml, Note = string.Join("; ", notes) });
            }
            return result;
        }

        // ------------------------------------------------------------------ pgAdmin 4

        /// <summary>Reads a pgAdmin 4 servers.json (Tools > Import/Export Servers > Export, or `setup.py dump-servers`).</summary>
        public static List<ImportCandidate> ReadPgAdmin(string serversJson)
        {
            var result = new List<ImportCandidate>();
            using (var doc = JsonDocument.Parse(File.ReadAllText(serversJson), new JsonDocumentOptions { AllowTrailingCommas = true }))
            {
                if (!doc.RootElement.TryGetProperty("Servers", out var servers) || servers.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Not a pgAdmin servers export: no \"Servers\" object.");

                foreach (var entry in servers.EnumerateObject())
                {
                    var s = entry.Value;
                    var cp = s.TryGetProperty("ConnectionParameters", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
                    var host = Str(s, "Host");
                    if (string.IsNullOrEmpty(host)) host = Str(s, "HostAddr");
                    var name = Str(s, "Name");
                    var group = Str(s, "Group");
                    var port = Int(s, "Port") ?? 5432;
                    var notes = new List<string>();
                    if (s.TryGetProperty("UseSSHTunnel", out var ssh) && ssh.ValueKind == JsonValueKind.Number && ssh.GetInt32() == 1) notes.Add("SSH tunnel not supported");
                    if (cp.ValueKind == JsonValueKind.Object && Str(cp, "passfile").Length > 0) notes.Add("passfile not read");

                    result.Add(new ImportCandidate
                    {
                        Source = serversJson,
                        Note = string.Join("; ", notes),
                        Registration = new ServerRegistration
                        {
                            Id = "pga-" + StableId($"{group}/{name}/{host}:{port}"),
                            Name = name,
                            Group = Prefix("pgAdmin", group == "Servers" ? "" : group),
                            Description = Str(s, "Comment"),
                            Host = host,
                            Port = port,
                            Database = FirstNonEmpty(Str(s, "MaintenanceDB"), "postgres"),
                            Username = Str(s, "Username"),
                            Auth = AuthMode.Password,
                            SslMode = MapSsl(FirstNonEmpty(cp.ValueKind == JsonValueKind.Object ? Str(cp, "sslmode") : "", Str(s, "SSLMode")), null),
                        },
                    });
                }
            }
            return result;
        }

        // ------------------------------------------------------------------ DBeaver

        public static List<string> FindDBeaverSources()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var files = new List<string>();
            foreach (var ws in new[] { Path.Combine(appData, "DBeaverData", "workspace6"), Path.Combine(appData, "DBeaverData", "workspace") })
            {
                if (!Directory.Exists(ws)) continue;
                foreach (var project in Directory.GetDirectories(ws))
                {
                    var dir = Path.Combine(project, ".dbeaver");
                    if (Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir, "data-sources*.json"));
                }
            }
            return files;
        }

        public static List<ImportCandidate> ReadDBeaver(string dataSourcesJson)
        {
            var result = new List<ImportCandidate>();
            using (var doc = JsonDocument.Parse(File.ReadAllText(dataSourcesJson), new JsonDocumentOptions { AllowTrailingCommas = true }))
            {
                var root = doc.RootElement;
                var folderParent = new Dictionary<string, string>();
                if (root.TryGetProperty("folders", out var folders) && folders.ValueKind == JsonValueKind.Object)
                    foreach (var f in folders.EnumerateObject())
                        folderParent[f.Name] = f.Value.ValueKind == JsonValueKind.Object ? Str(f.Value, "parent") : "";

                string FolderPath(string folder)
                {
                    var parts = new List<string>();
                    for (var i = 0; !string.IsNullOrEmpty(folder) && i < 20; i++)
                    {
                        // DBeaver stores nested folder keys as "parent/child"; keep the last segment.
                        parts.Insert(0, folder.Contains("/") ? folder.Substring(folder.LastIndexOf('/') + 1) : folder);
                        folder = folderParent.TryGetValue(folder, out var p) ? p : null;
                    }
                    return string.Join("/", parts);
                }

                if (!root.TryGetProperty("connections", out var conns) || conns.ValueKind != JsonValueKind.Object) return result;
                foreach (var entry in conns.EnumerateObject())
                {
                    var c = entry.Value;
                    var provider = Str(c, "provider");
                    var driver = Str(c, "driver");
                    if (provider.IndexOf("postgres", StringComparison.OrdinalIgnoreCase) < 0 && driver.IndexOf("postgres", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var cfg = c.TryGetProperty("configuration", out var cf) && cf.ValueKind == JsonValueKind.Object ? cf : default;
                    var host = cfg.ValueKind == JsonValueKind.Object ? Str(cfg, "host") : "";
                    var portText = cfg.ValueKind == JsonValueKind.Object ? Str(cfg, "port") : "";
                    var db = cfg.ValueKind == JsonValueKind.Object ? Str(cfg, "database") : "";
                    var url = cfg.ValueKind == JsonValueKind.Object ? Str(cfg, "url") : "";
                    string query = null;
                    if (string.IsNullOrEmpty(host) && url.Length > 0)
                    {
                        int p;
                        (host, p, db, query) = ParseJdbcUrl(url);
                        portText = p.ToString();
                    }

                    var notes = new List<string>();
                    var authModel = cfg.ValueKind == JsonValueKind.Object ? Str(cfg, "auth-model") : "";
                    var auth = authModel.IndexOf("iam", StringComparison.OrdinalIgnoreCase) >= 0 ? AuthMode.RdsIam : AuthMode.Password;
                    var sslMode = (string)null;
                    if (cfg.ValueKind == JsonValueKind.Object && cfg.TryGetProperty("handlers", out var handlers) && handlers.ValueKind == JsonValueKind.Object)
                    {
                        if (handlers.TryGetProperty("ssl", out var ssl) && ssl.ValueKind == JsonValueKind.Object && Bool(ssl, "enabled") &&
                            ssl.TryGetProperty("properties", out var sp) && sp.ValueKind == JsonValueKind.Object)
                            sslMode = FirstNonEmpty(Str(sp, "sslMode"), "require");
                        if (handlers.TryGetProperty("ssh_tunnel", out var t) && t.ValueKind == JsonValueKind.Object && Bool(t, "enabled"))
                            notes.Add("SSH tunnel not supported");
                    }
                    var user = cfg.ValueKind == JsonValueKind.Object ? Str(cfg, "user") : "";
                    if (string.IsNullOrEmpty(user)) notes.Add("user name is in DBeaver's encrypted credentials - set it after import");

                    result.Add(new ImportCandidate
                    {
                        Source = dataSourcesJson,
                        Note = string.Join("; ", notes),
                        Registration = new ServerRegistration
                        {
                            Id = "dbv-" + StableId(entry.Name),
                            Name = FirstNonEmpty(Str(c, "name"), entry.Name),
                            Group = Prefix("DBeaver", FolderPath(Str(c, "folder"))),
                            Description = Str(c, "description"),
                            Host = host,
                            Port = int.TryParse(portText, out var port) && port > 0 ? port : 5432,
                            Database = FirstNonEmpty(db, "postgres"),
                            Username = user,
                            Auth = auth,
                            SslMode = MapSsl(sslMode, query),
                        },
                    });
                }
            }
            return result;
        }

        // ------------------------------------------------------------------ merge

        /// <summary>Upserts by id; keeps the user's local choices (saved password, writes, renamed group) on re-import.</summary>
        public static (int added, int updated) Merge(IEnumerable<ServerRegistration> incoming)
        {
            var all = ServerStore.Load();
            int added = 0, updated = 0;
            foreach (var r in incoming)
            {
                var i = all.FindIndex(x => string.Equals(x.Id, r.Id, StringComparison.OrdinalIgnoreCase));
                if (i < 0) { r.RememberPassword = false; r.AllowWrites = false; all.Add(r); added++; continue; }
                var old = all[i];
                r.RememberPassword = old.RememberPassword && r.Auth == AuthMode.Password;
                r.AllowWrites = old.AllowWrites;
                all[i] = r;
                updated++;
            }
            ServerStore.Save(all);
            Diagnostics.Log.Info($"import: {added} added, {updated} updated");
            return (added, updated);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>jdbc:[aws-wrapper:]postgresql://host[:port][,host2...]/db?params</summary>
        internal static (string host, int port, string db, string query) ParseJdbcUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return ("", 5432, "", null);
            var u = url.Trim();
            var at = u.IndexOf("//", StringComparison.Ordinal);
            if (at < 0) return ("", 5432, "", null);
            u = u.Substring(at + 2);
            string query = null;
            var q = u.IndexOf('?');
            if (q >= 0) { query = u.Substring(q + 1); u = u.Substring(0, q); }
            var slash = u.IndexOf('/');
            var authority = slash >= 0 ? u.Substring(0, slash) : u;
            var db = slash >= 0 ? Uri.UnescapeDataString(u.Substring(slash + 1)) : "";
            authority = authority.Split(',')[0];
            if (authority.Contains("@")) authority = authority.Substring(authority.LastIndexOf('@') + 1);
            var port = 5432;
            string host;
            if (authority.StartsWith("["))
            {
                var end = authority.IndexOf(']');
                host = authority.Substring(1, Math.Max(0, end - 1));
                if (end + 2 <= authority.Length && authority.Length > end + 1 && authority[end + 1] == ':') int.TryParse(authority.Substring(end + 2), out port);
            }
            else
            {
                // host[:port] - tolerate junk like "host:5432:5432" (seen in real DataGrip files).
                var parts = authority.Split(':');
                host = parts[0];
                foreach (var p in parts.Skip(1)) if (int.TryParse(p, out var n) && n > 0) { port = n; break; }
            }
            return (host, port <= 0 ? 5432 : port, db, query);
        }

        internal static string MapSsl(string mode, string query)
        {
            if (string.IsNullOrEmpty(mode) && !string.IsNullOrEmpty(query))
                foreach (var kv in query.Split('&'))
                {
                    var p = kv.Split(new[] { '=' }, 2);
                    if (p.Length == 2 && p[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase)) mode = p[1];
                }
            switch ((mode ?? "").Replace("_", "").Replace("-", "").ToLowerInvariant())
            {
                case "disable": return "Disable";
                case "allow":
                case "prefer": return "Prefer";
                case "require": return "Require";
                case "verifyca": return "VerifyCA";
                case "verifyfull": return "VerifyFull";
                default: return "Prefer";
            }
        }

        private static string StripRoot(string group)
        {
            var g = (group ?? "").Trim('/');
            return g.StartsWith("All Servers", StringComparison.OrdinalIgnoreCase) ? g.Substring("All Servers".Length).Trim('/') : g;
        }

        private static string Prefix(string tool, string group) =>
            string.IsNullOrWhiteSpace(group) ? tool : tool + "/" + group.Trim('/').Replace('\\', '/');

        private static string RegionFromArn(string arn)
        {
            var p = (arn ?? "").Split(':');
            return p.Length > 3 && p[0] == "arn" ? p[3] : null;
        }

        private static string StableId(string key)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").Substring(0, 24).ToLowerInvariant();
        }

        private static string Text(XmlElement e, string xpath) => (e.SelectSingleNode(xpath)?.InnerText ?? "").Trim();

        private static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
                ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Null || v.ValueKind == JsonValueKind.Undefined ? "" : v.ToString())
                : "";

        private static int? Int(JsonElement e, string name) => int.TryParse(Str(e, name), out var i) ? i : (int?)null;

        private static bool Bool(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "true"));

        private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
    }
}
