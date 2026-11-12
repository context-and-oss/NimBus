#pragma warning disable CA1707, CA2007

using System.Text.Json;
using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// Fake Azure CLI that records every command and every deployment the <see cref="InfrastructureDeployer"/>
/// issues. list/show calls return "[]", so no existing plan or resource is ever detected and an explicit
/// plan choice resolves as requested; value captures answer the App Insights and Cosmos lookups
/// with fixed markers.
/// </summary>
internal sealed class RecordingAzureCliRunner : IAzureCliRunner
{
    internal const string InstrumentationKey = "instrumentation-key-marker";
    internal const string CosmosEndpoint = "https://cosmos.example.test:443/";
    internal const string ResourceGroupIdPrefix = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/";

    public List<IReadOnlyList<string>> Commands { get; } = new();

    public List<RecordedDeployment> Deployments { get; } = new();

    /// <summary>
    /// Optional scripted answers: return the standard output for a command, or null to fall
    /// back to the defaults above. Lets a test describe existing Azure resources.
    /// </summary>
    public Func<IReadOnlyList<string>, string?>? Responder { get; set; }

    /// <summary>
    /// Optional: commands that fail. TryRunAsync reports a failed az call; CaptureJsonAsync throws
    /// with its failure message, like the real runner.
    /// </summary>
    public Func<IReadOnlyList<string>, bool>? FailWhen { get; set; }

    public Task<JsonDocument> CaptureJsonAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string failureMessage)
    {
        Commands.Add(arguments.ToArray());
        if (FailWhen?.Invoke(arguments) == true)
        {
            throw new CommandException(failureMessage);
        }

        var fallback =arguments.Contains("group", StringComparer.Ordinal) && arguments.Contains("show", StringComparer.Ordinal)
            ? $$"""{"id":"{{ResourceGroupIdPrefix}}{{arguments[Array.IndexOf(arguments.ToArray(), "--name") + 1]}}","tags":null}"""
            : "{}";
        return Task.FromResult(JsonDocument.Parse(Responder?.Invoke(arguments) ?? fallback));
    }

    public Task EnsureLoggedInAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EnsureExtensionAsync(string extensionName, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EnsureSuccessAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string failureMessage)
    {
        Commands.Add(arguments.ToArray());
        return Task.CompletedTask;
    }

    public Task<ProcessResult> TryRunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        Commands.Add(arguments.ToArray());
        if (FailWhen?.Invoke(arguments) == true)
        {
            return Task.FromResult(new ProcessResult(1, string.Empty, "ERROR: simulated az failure"));
        }

        var output = Responder?.Invoke(arguments)
            ?? (arguments.Contains("list", StringComparer.Ordinal) || arguments.Contains("show", StringComparer.Ordinal)
                ? "[]"
                : string.Empty);
        return Task.FromResult(new ProcessResult(0, output, string.Empty));
    }

    public Task<string> CaptureValueAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string failureMessage)
    {
        Commands.Add(arguments.ToArray());
        if (Responder?.Invoke(arguments) is { } scripted)
        {
            return Task.FromResult(scripted);
        }

        var queryIndex = Array.IndexOf(arguments.ToArray(), "--query");
        var query = queryIndex >= 0 ? arguments[queryIndex + 1] : string.Empty;
        return Task.FromResult(query switch
        {
            "appId" => "app-insights-app-id",
            "instrumentationKey" => InstrumentationKey,
            "documentEndpoint" => CosmosEndpoint,
            _ => throw new InvalidOperationException($"Unexpected capture command: {string.Join(' ', arguments)}"),
        });
    }

    public Task EnsureSuccessAsync(
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken,
        string failureMessage)
    {
        Commands.Add(arguments.ToArray());

        // A deployment with no secure parameters carries no @file reference at all.
        var parameterFileReferences = arguments.Where(argument => argument.StartsWith('@')).ToList();
        Assert.True(parameterFileReferences.Count <= 1, "expected at most one secure parameter file");

        var secureParameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parameterFileReferences.Count == 1)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(parameterFileReferences[0][1..]));
            foreach (var parameter in document.RootElement.GetProperty("parameters").EnumerateObject())
            {
                secureParameters[parameter.Name] = parameter.Value.GetProperty("value").GetString() ?? string.Empty;
            }
        }

        Deployments.Add(new RecordedDeployment(arguments.ToArray(), secureParameters));
        return Task.CompletedTask;
    }
}

internal sealed record RecordedDeployment(
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> SecureParameters);
