namespace NimBus.CommandLine;

internal sealed record DeploymentSecrets(
    string? SqlConnectionString,
    string? SqlAdminPassword,
    string? IdentityAdminPassword,
    string? AlertMailSmtpPassword = null)
{
    public const string SqlConnectionStringEnvironmentVariable = "NIMBUS_SQL_CONNECTION_STRING";
    public const string SqlAdminPasswordEnvironmentVariable = "NIMBUS_SQL_ADMIN_PASSWORD";
    public const string IdentityAdminPasswordEnvironmentVariable = "NIMBUS_IDENTITY_ADMIN_PASSWORD";
    public const string AlertMailSmtpPasswordEnvironmentVariable = "NIMBUS_ALERT_MAIL_SMTP_PASSWORD";

    public static DeploymentSecrets Load() => Load(Environment.GetEnvironmentVariable);

    internal static DeploymentSecrets Load(Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        return new DeploymentSecrets(
            ReadSecret(readEnvironmentVariable, SqlConnectionStringEnvironmentVariable),
            ReadSecret(readEnvironmentVariable, SqlAdminPasswordEnvironmentVariable),
            ReadSecret(readEnvironmentVariable, IdentityAdminPasswordEnvironmentVariable),
            ReadSecret(readEnvironmentVariable, AlertMailSmtpPasswordEnvironmentVariable));
    }

    internal static void RemoveFrom(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        environment.Remove(SqlConnectionStringEnvironmentVariable);
        environment.Remove(SqlAdminPasswordEnvironmentVariable);
        environment.Remove(IdentityAdminPasswordEnvironmentVariable);
        environment.Remove(AlertMailSmtpPasswordEnvironmentVariable);
    }

    private static string? ReadSecret(Func<string, string?> readEnvironmentVariable, string name)
    {
        var value = readEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
