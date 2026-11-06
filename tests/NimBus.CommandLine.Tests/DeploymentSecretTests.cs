#pragma warning disable CA1707, CA1861, CA2007

using System.Text.Json;
using Xunit;

namespace NimBus.CommandLine.Tests;

[Collection("Console output")]
public sealed class DeploymentSecretTests
{
    [Fact]
    public void Load_ReadsSecretsFromEnvironmentSource()
    {
        var values = new Dictionary<string, string?>
        {
            [DeploymentSecrets.SqlConnectionStringEnvironmentVariable] = "Server=example;Password=sql-secret",
            [DeploymentSecrets.SqlAdminPasswordEnvironmentVariable] = "admin-secret",
            [DeploymentSecrets.IdentityAdminPasswordEnvironmentVariable] = "identity-secret",
        };

        var secrets = DeploymentSecrets.Load(name => values.GetValueOrDefault(name));

        Assert.Equal(values[DeploymentSecrets.SqlConnectionStringEnvironmentVariable], secrets.SqlConnectionString);
        Assert.Equal(values[DeploymentSecrets.SqlAdminPasswordEnvironmentVariable], secrets.SqlAdminPassword);
        Assert.Equal(values[DeploymentSecrets.IdentityAdminPasswordEnvironmentVariable], secrets.IdentityAdminPassword);
    }

    [Fact]
    public void Load_TreatsWhitespaceValuesAsMissing()
    {
        var secrets = DeploymentSecrets.Load(_ => "   ");

        Assert.Null(secrets.SqlConnectionString);
        Assert.Null(secrets.SqlAdminPassword);
        Assert.Null(secrets.IdentityAdminPassword);
    }

    [Fact]
    public void RemoveFrom_RemovesDeploymentSecretsButPreservesOtherVariables()
    {
        var environment = new Dictionary<string, string?>
        {
            [DeploymentSecrets.SqlConnectionStringEnvironmentVariable] = "sql-connection-secret",
            [DeploymentSecrets.SqlAdminPasswordEnvironmentVariable] = "sql-password-secret",
            [DeploymentSecrets.IdentityAdminPasswordEnvironmentVariable] = "identity-password-secret",
            ["PATH"] = "expected-path",
        };

        DeploymentSecrets.RemoveFrom(environment);

        Assert.DoesNotContain(DeploymentSecrets.SqlConnectionStringEnvironmentVariable, environment.Keys);
        Assert.DoesNotContain(DeploymentSecrets.SqlAdminPasswordEnvironmentVariable, environment.Keys);
        Assert.DoesNotContain(DeploymentSecrets.IdentityAdminPasswordEnvironmentVariable, environment.Keys);
        Assert.Equal("expected-path", environment["PATH"]);
    }

    [Fact]
    public async Task CaptureCommands_RequestSilentStandardOutput()
    {
        var processRunner = new RecordingProcessRunner("captured-secret");
        var azureCli = new AzureCliRunner(processRunner);

        var captured = await azureCli.CaptureValueAsync(
            new[] { "example", "capture" },
            CancellationToken.None,
            "capture failed");
        var attempted = await azureCli.TryRunAsync(
            new[] { "example", "try" },
            CancellationToken.None);

        Assert.Equal("captured-secret", captured);
        Assert.True(attempted.Succeeded);
        Assert.Equal(new[] { false, false }, processRunner.EchoStandardOutputValues);
    }

    /// <summary>
    /// TryRunAsync backs existence probes such as "servicebus namespace show" on a fresh
    /// deployment, whose expected failure used to print "ERROR: (ResourceNotFound)". Its
    /// callers handle the failure; the stderr stays on the result.
    /// </summary>
    [Fact]
    public async Task TryRun_DoesNotEchoStandardError_WhileOtherCommandsDo()
    {
        var processRunner = new RecordingProcessRunner("output");
        var azureCli = new AzureCliRunner(processRunner);

        await azureCli.CaptureValueAsync(new[] { "example", "capture" }, CancellationToken.None, "capture failed");
        await azureCli.TryRunAsync(new[] { "example", "try" }, CancellationToken.None);

        Assert.Equal(new[] { true, false }, processRunner.EchoStandardErrorValues);
    }

    [Fact]
    public async Task ProcessRunner_CapturesWithoutEchoingStandardOutput()
    {
        var originalOut = Console.Out;
        using var consoleOutput = new StringWriter();
        try
        {
            Console.SetOut(consoleOutput);
            var result = await new ProcessRunner().RunAsync(
                "dotnet",
                new[] { "--version" },
                workingDirectory: null,
                echoStandardOutput: false,
                CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput));
            Assert.DoesNotContain(result.StandardOutput, consoleOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessRunner_EchoesStandardErrorOnlyWhenAsked(bool echoStandardError)
    {
        var originalError = Console.Error;
        using var consoleError = new StringWriter();
        try
        {
            Console.SetError(consoleError);
            var result = await new ProcessRunner().RunAsync(
                "dotnet",
                new[] { "nimbus-no-such-command" },
                workingDirectory: null,
                echoStandardOutput: false,
                echoStandardError,
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(result.StandardError));
            Assert.Equal(echoStandardError, consoleError.ToString().Contains(result.StandardError.Trim(), StringComparison.Ordinal));
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void BuildArguments_KeepsSecureParametersOutOfProcessArguments()
    {
        const string secret = "not-visible-in-process-arguments";
        string parameterFilePath;
        string parameterDirectory;

        using (var command = new AzureDeploymentCommand(
                   new[] { "deployment", "group", "create", "--parameters", "environment=dev" }))
        {
            command.AddSecureParameter("apiKey", secret);

            var arguments = command.BuildArguments();

            Assert.DoesNotContain(arguments, argument => argument.Contains(secret, StringComparison.Ordinal));
            var parameterFileReference = Assert.Single(arguments, argument => argument.StartsWith('@'));
            parameterFilePath = parameterFileReference[1..];
            parameterDirectory = Path.GetDirectoryName(parameterFilePath)!;

            Assert.True(File.Exists(parameterFilePath));
            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode groupOrOtherPermissions =
                    UnixFileMode.GroupRead |
                    UnixFileMode.GroupWrite |
                    UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead |
                    UnixFileMode.OtherWrite |
                    UnixFileMode.OtherExecute;
                Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(parameterFilePath) & groupOrOtherPermissions);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(parameterFilePath));
            Assert.Equal(
                secret,
                document.RootElement
                    .GetProperty("parameters")
                    .GetProperty("apiKey")
                    .GetProperty("value")
                    .GetString());
        }

        Assert.False(File.Exists(parameterFilePath));
        Assert.False(Directory.Exists(parameterDirectory));
    }

    [Fact]
    public void BuildArguments_DoesNotCreateParameterFileWithoutSecrets()
    {
        using var command = new AzureDeploymentCommand(new[] { "deployment", "group", "create" });

        var arguments = command.BuildArguments();

        Assert.DoesNotContain(arguments, argument => argument.StartsWith('@'));
    }

    [Fact]
    public void WebAppBicep_MarksDeploymentSecretsAsSecure()
    {
        var repositoryRoot = FindRepositoryRoot();
        var bicep = File.ReadAllText(Path.Combine(repositoryRoot, "deploy", "bicep", "deploy.webapp.bicep"))
            .ReplaceLineEndings("\n");

        // The deprecated apiKey keeps its deprecation note and lint suppression between the
        // decorator and the declaration. It stays secure for callers that still pass a key.
        Assert.Matches(@"@secure\(\)\n(?:[@#].*\n)*param apiKey string", bicep);
        Assert.Contains("@secure()\nparam instrumentationKey string", bicep, StringComparison.Ordinal);
        Assert.Contains("@secure()\nparam sqlConnectionString string", bicep, StringComparison.Ordinal);
        Assert.Contains("@secure()\nparam identityAdminPassword string", bicep, StringComparison.Ordinal);
    }

    [Fact]
    public void BicepModules_KeepSecretsSecureAcrossNestedDeploymentBoundaries()
    {
        var repositoryRoot = FindRepositoryRoot();
        var bicepRoot = Path.Combine(repositoryRoot, "deploy", "bicep");
        var webAppDeployment = ReadNormalized(Path.Combine(bicepRoot, "deploy.webapp.bicep"));
        var coreDeployment = ReadNormalized(Path.Combine(bicepRoot, "deploy.core.bicep"));
        var webApp = ReadNormalized(Path.Combine(bicepRoot, "templates", "webApp.bicep"));
        var functionApp = ReadNormalized(Path.Combine(bicepRoot, "templates", "functionApp.bicep"));
        var flexFunctionApp = ReadNormalized(Path.Combine(bicepRoot, "templates", "flexConsumptionFunctionApp.bicep"));
        var appInsights = ReadNormalized(Path.Combine(bicepRoot, "templates", "applicationInsights.bicep"));
        var storageAccount = ReadNormalized(Path.Combine(bicepRoot, "templates", "storageaccount.bicep"));
        var cosmos = ReadNormalized(Path.Combine(bicepRoot, "templates", "cosmosDB.bicep"));

        Assert.Contains("@secure()\nparam secretSettings object", webApp, StringComparison.Ordinal);
        Assert.Contains("secretSettings: webAppSecretSettings", webAppDeployment, StringComparison.Ordinal);
        Assert.Contains("@secure()\nparam secretSettings object", functionApp, StringComparison.Ordinal);
        Assert.Contains("@secure()\nparam secretSettings object", flexFunctionApp, StringComparison.Ordinal);
        Assert.Contains("secretSettings: resolverSecretSettings", coreDeployment, StringComparison.Ordinal);
        Assert.Contains("@secure()\nparam storageConnectionString string", functionApp, StringComparison.Ordinal);
        Assert.Contains("@secure()\nparam appInsightsInstrumentationKey string", functionApp, StringComparison.Ordinal);
        Assert.Contains("@secure()\noutput instrumentationKey string", appInsights, StringComparison.Ordinal);
        Assert.Contains("@secure()\noutput connectionString string", appInsights, StringComparison.Ordinal);
        Assert.Contains("@secure()\noutput connectionString string", storageAccount, StringComparison.Ordinal);
        // The Cosmos module no longer produces a connection string at all (spec 034): the
        // apps use Entra RBAC, and nothing may read the account keys into outputs.
        Assert.DoesNotContain("listConnectionStrings", cosmos, StringComparison.Ordinal);
        Assert.DoesNotContain("output connectionString", cosmos, StringComparison.Ordinal);
    }

    [Fact]
    public void CoreBicep_KeepsTheAlertMailPasswordSecure()
    {
        var coreDeployment = ReadNormalized(Path.Combine(FindRepositoryRoot(), "deploy", "bicep", "deploy.core.bicep"));

        Assert.Contains("@secure()\nparam alertMailSmtpPassword string", coreDeployment, StringComparison.Ordinal);
        // Only the secure settings object may carry it: the plain settings array is kept in
        // the nested deployment's history.
        Assert.Contains("NimBus__Alerts__Mail__Smtp__Password: alertMailSmtpPassword", coreDeployment, StringComparison.Ordinal);
        Assert.DoesNotContain("value: alertMailSmtpPassword", coreDeployment, StringComparison.Ordinal);
        Assert.Contains("alertMailResolverSecretSettings)", coreDeployment, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "deploy", "bicep", "deploy.webapp.bicep")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root from the test output directory.");
    }

    private static string ReadNormalized(string path) => File.ReadAllText(path).ReplaceLineEndings("\n");

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        private readonly string _standardOutput;

        public RecordingProcessRunner(string standardOutput)
        {
            _standardOutput = standardOutput;
        }

        public List<bool> EchoStandardOutputValues { get; } = new();

        public List<bool> EchoStandardErrorValues { get; } = new();

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            bool echoStandardOutput,
            bool echoStandardError,
            CancellationToken cancellationToken)
        {
            EchoStandardOutputValues.Add(echoStandardOutput);
            EchoStandardErrorValues.Add(echoStandardError);
            return Task.FromResult(new ProcessResult(0, _standardOutput, string.Empty));
        }
    }
}
