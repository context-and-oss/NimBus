#pragma warning disable CA1707, CA2007
using System.Text.Json;
using NimBus.CommandLine;
using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// The WebApp resolves its <c>IPlatform</c> catalog from NimBus__PlatformType /
/// NimBus__PlatformAssembly, and throws on startup when the type is set but the assembly
/// is absent. Since the assembly travels inside the deployment zip, the settings and the
/// zip have to be written as one decision on every WebApp deployment.
/// </summary>
public sealed class PlatformAppSettingsTests
{
    private static readonly AppDeploymentOptions Options = new(
        SolutionId: "acme",
        Environment: "test",
        ResourceGroupName: "rg-acme-test",
        Configuration: "Release",
        Target: AppDeploymentTarget.WebApp);

    [Fact]
    public async Task DeployingWithoutAPlatformPackage_ClearsStalePlatformSettings()
    {
        // `nb deploy apps --only webapp` after an earlier --platform-package run ships the
        // plain released zip: the customer's assembly is gone, so leaving the settings
        // behind would take the management site down on its next start.
        var az = new RecordingAzureCli();
        var service = new AppDeploymentService(az, new StubArtifacts(), platformPackage: null);

        await service.DeployAsync(Options, CancellationToken.None);

        var settingsCall = Assert.Single(az.Calls, call => call.Contains("appsettings", StringComparison.Ordinal));
        Assert.Contains("delete", settingsCall, StringComparison.Ordinal);
        Assert.Contains("NimBus__PlatformType", settingsCall, StringComparison.Ordinal);
        Assert.Contains("NimBus__PlatformAssembly", settingsCall, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployingResolverOnly_LeavesWebAppSettingsAlone()
    {
        var az = new RecordingAzureCli();
        var service = new AppDeploymentService(az, new StubArtifacts(), platformPackage: null);

        await service.DeployAsync(Options with { Target = AppDeploymentTarget.Resolver }, CancellationToken.None);

        Assert.DoesNotContain(az.Calls, call => call.Contains("appsettings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeployingWithAPostfix_TargetsThePostfixedApps()
    {
        var az = new RecordingAzureCli();
        var service = new AppDeploymentService(az, new StubArtifacts(), platformPackage: null);

        await service.DeployAsync(Options with { Target = AppDeploymentTarget.All, ResourceNamePostfix = "avk" }, CancellationToken.None);

        Assert.Contains(az.Calls, call => call.StartsWith("webapp deploy", StringComparison.Ordinal) && call.Contains("--name webapp-acme-test-management-avk", StringComparison.Ordinal));
        Assert.Contains(az.Calls, call => call.Contains("func-acme-test-resolver-avk", StringComparison.Ordinal));
        // The App Service plan was never postfixed.
        Assert.DoesNotContain(az.Calls, call => call.Contains("asp-acme-test-core-avk", StringComparison.Ordinal));
    }

    private sealed class StubArtifacts : IDeploymentArtifactSource
    {
        private readonly string _zip = CreateEmptyFile();

        public Task<string> GetResolverZipAsync(CancellationToken cancellationToken) => Task.FromResult(_zip);

        public Task<string> GetWebAppZipAsync(CancellationToken cancellationToken) => Task.FromResult(_zip);

        public Task<string?> GetVersionAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("9.9.9");

        private static string CreateEmptyFile()
        {
            var path = Path.Combine(Path.GetTempPath(), "NimBusTests", $"{Guid.NewGuid():N}.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Array.Empty<byte>());
            return path;
        }
    }

    private sealed class RecordingAzureCli : IAzureCliRunner
    {
        public List<string> Calls { get; } = new();

        public Task EnsureLoggedInAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureExtensionAsync(string extensionName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureSuccessAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string failureMessage)
        {
            Calls.Add(string.Join(' ', arguments));
            return Task.CompletedTask;
        }

        public Task EnsureSuccessAsync(IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken, string failureMessage)
        {
            Calls.Add(string.Join(' ', arguments));
            return Task.CompletedTask;
        }

        public Task<string> CaptureValueAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string failureMessage)
        {
            Calls.Add(string.Join(' ', arguments));
            // Plan tier lookup: anything that is not Flex keeps the deployment path simple.
            return Task.FromResult("Basic");
        }

        public Task<JsonDocument> CaptureJsonAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string failureMessage)
        {
            Calls.Add(string.Join(' ', arguments));
            // `az version`: old enough to be irrelevant, new enough to clear the Flex gate.
            return Task.FromResult(JsonDocument.Parse("""{"azure-cli":"2.99.0"}"""));
        }

        public Task<ProcessResult> TryRunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(string.Join(' ', arguments));
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }
}
