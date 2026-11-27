using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Npgsql;
using SsmsPgExplorer.Model;
using SsmsPgExplorer.RegisteredServers;
using Microsoft.VisualStudio.Threading;

namespace SsmsPgExplorer.Connection
{
    /// <summary>
    /// Builds and opens Npgsql connections for a registration. Secrets live only in memory (short TTL)
    /// or in Windows Credential Manager when the user ticked "Remember password".
    /// </summary>
    internal static class ConnectionFactory
    {
        public const string AppName = "SSMS PostgreSQL Explorer";

        private sealed class Resolved
        {
            public string Host, User, Password, Database;
            public int Port;
            public DateTime ExpiresUtc;
        }

        private static readonly ConcurrentDictionary<string, Resolved> Cache = new ConcurrentDictionary<string, Resolved>();

        // Passwords typed in the registration dialog without "Remember": kept for this SSMS session only.
        private static readonly ConcurrentDictionary<string, string> SessionPasswords = new ConcurrentDictionary<string, string>();

        public static void RememberForSession(string registrationId, string password)
        {
            if (string.IsNullOrEmpty(registrationId) || password == null) return;
            SessionPasswords[registrationId] = password;
            Cache.TryRemove(registrationId, out _);
        }

        public static void Forget(string registrationId)
        {
            Cache.TryRemove(registrationId, out _);
        }

        /// <summary>Forget everything about a registration, including a session-only password.</summary>
        public static void ForgetAll(string registrationId)
        {
            Cache.TryRemove(registrationId, out _);
            SessionPasswords.TryRemove(registrationId, out _);
        }

        /// <summary>Opens a connection. readOnly=true forces every transaction in the session to READ ONLY.</summary>
        public static async Task<NpgsqlConnection> OpenAsync(ServerRegistration reg, string database, bool readOnly, CancellationToken ct, bool pooled = true)
        {
            var r = await ResolveAsync(reg, ct).ConfigureAwait(false);

            var csb = new NpgsqlConnectionStringBuilder
            {
                Host = r.Host,
                Port = r.Port,
                Database = string.IsNullOrWhiteSpace(database) ? r.Database : database,
                Username = r.User,
                Password = r.Password,
                ApplicationName = AppName,
                Timeout = 15,
                CommandTimeout = 0,
                Pooling = pooled,
                MaxPoolSize = 5,
                KeepAlive = 30,
            };
            if (Enum.TryParse<SslMode>(reg.SslMode, ignoreCase: true, out var ssl)) csb.SslMode = ssl;

            var conn = new NpgsqlConnection(csb.ConnectionString);
            try
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == "28P01" || ex.SqlState == "28000")
            {
                // Bad password / IAM token: drop cached secret so the next attempt re-prompts / re-fetches.
                Forget(reg.Id);
                conn.Dispose();
                throw;
            }
            catch
            {
                conn.Dispose();
                throw;
            }

            // Session guardrail (not a security boundary: a user can SET it back). Pooled connections are
            // DISCARD ALL'd on return, so this runs on every open.
            await SetReadOnlyAsync(conn, readOnly || !reg.AllowWrites, ct).ConfigureAwait(false);
            return conn;
        }

        public static async Task SetReadOnlyAsync(NpgsqlConnection conn, bool readOnly, CancellationToken ct)
        {
            using (var cmd = new NpgsqlCommand(readOnly
                       ? "SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY"
                       : "SET SESSION CHARACTERISTICS AS TRANSACTION READ WRITE", conn))
            {
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        private static async Task<Resolved> ResolveAsync(ServerRegistration reg, CancellationToken ct)
        {
            if (Cache.TryGetValue(reg.Id, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
                return cached;

            var r = new Resolved
            {
                Host = reg.Host,
                Port = reg.Port <= 0 ? 5432 : reg.Port,
                User = reg.Username,
                Database = string.IsNullOrWhiteSpace(reg.Database) ? "postgres" : reg.Database,
            };

            switch (reg.Auth)
            {
                case AuthMode.AwsSecretsManager:
                    var s = await AwsCli.GetSecretAsync(reg.SecretId, reg.AwsProfile, reg.AwsRegion, ct).ConfigureAwait(false);
                    // Registration values win; secret fills the blanks.
                    if (string.IsNullOrWhiteSpace(r.Host)) r.Host = s.Host;
                    if (string.IsNullOrWhiteSpace(r.User)) r.User = s.Username;
                    if (string.IsNullOrWhiteSpace(reg.Database) && !string.IsNullOrWhiteSpace(s.Database)) r.Database = s.Database;
                    if (reg.Port <= 0 && s.Port.HasValue) r.Port = s.Port.Value;
                    r.Password = s.Password;
                    r.ExpiresUtc = DateTime.UtcNow.AddMinutes(10);
                    break;

                case AuthMode.RdsIam:
                    r.Password = await AwsCli.GenerateRdsAuthTokenAsync(r.Host, r.Port, r.User, reg.AwsProfile, reg.AwsRegion, ct).ConfigureAwait(false);
                    r.ExpiresUtc = DateTime.UtcNow.AddMinutes(10); // tokens live 15 min
                    break;

                default:
                    r.Password = reg.RememberPassword ? CredentialStore.Read(reg.Id) : null;
                    if (r.Password == null && SessionPasswords.TryGetValue(reg.Id, out var sessionPw)) r.Password = sessionPw;
                    if (r.Password == null)
                        Diagnostics.Log.Info($"connect '{reg.DisplayName}' ({reg.Id}): no saved password (RememberPassword={reg.RememberPassword}), prompting");
                    if (r.Password == null)
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                        using (var prompt = new PasswordPrompt(reg))
                        {
                            if (prompt.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                                throw new OperationCanceledException("Login cancelled.");
                            r.Password = prompt.Password;
                            if (prompt.Remember)
                            {
                                CredentialStore.Write(reg.Id, reg.Username, r.Password);
                                if (!reg.RememberPassword) { reg.RememberPassword = true; ServerStore.Upsert(reg); }
                            }
                        }
                        await TaskScheduler.Default;
                    }
                    r.ExpiresUtc = DateTime.UtcNow.AddHours(8);
                    break;
            }

            if (string.IsNullOrWhiteSpace(r.Host)) throw new InvalidOperationException($"'{reg.DisplayName}': no host (registration and secret are both empty).");
            if (string.IsNullOrWhiteSpace(r.User)) throw new InvalidOperationException($"'{reg.DisplayName}': no username.");

            Cache[reg.Id] = r;
            return r;
        }

        public static string Describe(Exception ex)
        {
            if (ex is PostgresException pg)
            {
                var msg = $"{pg.Severity} {pg.SqlState}: {pg.MessageText}";
                if (!string.IsNullOrEmpty(pg.Detail)) msg += Environment.NewLine + "DETAIL: " + pg.Detail;
                if (!string.IsNullOrEmpty(pg.Hint)) msg += Environment.NewLine + "HINT: " + pg.Hint;
                if (pg.Position > 0) msg += Environment.NewLine + "POSITION: " + pg.Position;
                if (!string.IsNullOrEmpty(pg.Where)) msg += Environment.NewLine + "WHERE: " + pg.Where;
                return msg;
            }
            if (ex is AggregateException ag && ag.InnerExceptions.Count == 1) return Describe(ag.InnerException);
            return ex.InnerException != null && ex is NpgsqlException ? $"{ex.Message} ({ex.InnerException.Message})" : ex.Message;
        }
    }
}
