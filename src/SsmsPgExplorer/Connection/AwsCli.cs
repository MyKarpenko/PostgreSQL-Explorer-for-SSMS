using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SsmsPgExplorer.Connection
{
    /// <summary>
    /// Shells out to the AWS CLI so the extension uses the user's own AWS SSO profiles and ships no AWS SDK.
    /// Nothing returned here is written to disk.
    /// </summary>
    internal static class AwsCli
    {
        public sealed class SecretFields
        {
            public string Host, Username, Password, Database;
            public int? Port;
        }

        public static async Task<SecretFields> GetSecretAsync(string secretId, string profile, string region, CancellationToken ct)
        {
            var args = new List<string> { "secretsmanager", "get-secret-value", "--secret-id", secretId, "--query", "SecretString", "--output", "text" };
            AddCommon(args, profile, region);
            var json = (await RunAsync(args, ct)).Trim();

            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                string Str(params string[] names)
                {
                    foreach (var n in names)
                        if (root.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null)
                            return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
                    return null;
                }
                var port = Str("port");
                return new SecretFields
                {
                    Host = Str("host", "hostname", "endpoint"),
                    Username = Str("username", "user"),
                    Password = Str("password"),
                    Database = Str("dbname", "database", "dbName"),
                    Port = int.TryParse(port, out var p) ? p : (int?)null,
                };
            }
        }

        public static async Task<string> GenerateRdsAuthTokenAsync(string host, int port, string user, string profile, string region, CancellationToken ct)
        {
            var args = new List<string> { "rds", "generate-db-auth-token", "--hostname", host, "--port", port.ToString(), "--username", user };
            AddCommon(args, profile, region);
            return (await RunAsync(args, ct)).Trim();
        }

        private static void AddCommon(List<string> args, string profile, string region)
        {
            if (!string.IsNullOrWhiteSpace(profile)) { args.Add("--profile"); args.Add(profile.Trim()); }
            if (!string.IsNullOrWhiteSpace(region)) { args.Add("--region"); args.Add(region.Trim()); }
        }

        private static string FindAws()
        {
            var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => Path.Combine(d.Trim('"'), "aws.exe"))
                .Concat(new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon", "AWSCLIV2", "aws.exe"),
                });
            return candidates.FirstOrDefault(File.Exists)
                ?? throw new InvalidOperationException("AWS CLI v2 (aws.exe) not found on PATH or in Program Files\\Amazon\\AWSCLIV2.");
        }

        private static async Task<string> RunAsync(IList<string> args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(FindAws())
            {
                Arguments = string.Join(" ", args.Select(Quote)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using (var p = new Process { StartInfo = psi, EnableRaisingEvents = true })
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                p.Exited += (s, e) => exited.TrySetResult(true);
                p.Start();
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                using (timeout.Token.Register(() => { try { if (!p.HasExited) p.Kill(); } catch { } exited.TrySetCanceled(); }))
                {
                    await exited.Task.ConfigureAwait(false);
                }
                var output = await stdout.ConfigureAwait(false);
                var error = await stderr.ConfigureAwait(false);
                if (p.ExitCode != 0)
                {
                    var hint = error.IndexOf("sso", StringComparison.OrdinalIgnoreCase) >= 0 || error.IndexOf("expired", StringComparison.OrdinalIgnoreCase) >= 0
                        ? $"{Environment.NewLine}{Environment.NewLine}SSO session likely expired: aws sso login --profile {ProfileOf(args)}"
                        : "";
                    throw new InvalidOperationException($"aws {args[0]} {args[1]} failed (exit {p.ExitCode}): {error.Trim()}{hint}");
                }
                return output;
            }
        }

        private static string ProfileOf(IList<string> args)
        {
            var i = args.IndexOf("--profile");
            return i >= 0 && i + 1 < args.Count ? args[i + 1] : "<profile>";
        }

        private static string Quote(string a) =>
            a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
    }
}
