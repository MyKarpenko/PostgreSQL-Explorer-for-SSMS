using System;
using System.Runtime.Serialization;

namespace SsmsPgExplorer.Model
{
    public enum AuthMode
    {
        /// <summary>Username + password. Password optionally kept in Windows Credential Manager, never in the JSON file.</summary>
        Password = 0,
        /// <summary>Host/port/user/password/dbname pulled at connect time via `aws secretsmanager get-secret-value` (SSO profile).</summary>
        AwsSecretsManager = 1,
        /// <summary>Short-lived token from `aws rds generate-db-auth-token` (SSO profile).</summary>
        RdsIam = 2,
    }

    /// <summary>
    /// One registered server. Mirrors SSMS Registered Servers: a name, a group and connection properties.
    /// Contains no secrets — safe to export / share.
    /// </summary>
    [DataContract]
    public sealed class ServerRegistration
    {
        [DataMember(Order = 0)] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [DataMember(Order = 1)] public string Name { get; set; } = "";
        [DataMember(Order = 2)] public string Group { get; set; } = "";
        [DataMember(Order = 3)] public string Description { get; set; } = "";
        [DataMember(Order = 4)] public string Host { get; set; } = "";
        [DataMember(Order = 5)] public int Port { get; set; } = 5432;
        [DataMember(Order = 6)] public string Database { get; set; } = "postgres";
        [DataMember(Order = 7)] public string Username { get; set; } = "";
        [DataMember(Order = 8)] public AuthMode Auth { get; set; } = AuthMode.Password;
        [DataMember(Order = 9)] public bool RememberPassword { get; set; }
        [DataMember(Order = 10)] public string SecretId { get; set; } = "";
        [DataMember(Order = 11)] public string AwsProfile { get; set; } = "";
        [DataMember(Order = 12)] public string AwsRegion { get; set; } = "us-west-2";
        /// <summary>Npgsql SslMode name: Disable, Allow, Prefer, Require, VerifyCA, VerifyFull.</summary>
        [DataMember(Order = 13)] public string SslMode { get; set; } = "Require";
        /// <summary>When false (default) every session is forced to default_transaction_read_only = on.</summary>
        [DataMember(Order = 14)] public bool AllowWrites { get; set; }

        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{Host}:{Port}" : Name;

        public string GroupPath => string.IsNullOrWhiteSpace(Group) ? "" : Group.Trim().Trim('/', '\\');

        public override string ToString() =>
            GroupPath.Length == 0 ? DisplayName : $"{GroupPath} / {DisplayName}";

        public ServerRegistration Clone() => (ServerRegistration)MemberwiseClone();
    }
}
