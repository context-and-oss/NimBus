using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace NimBus.CommandLine;

#pragma warning disable CA1068 // Preserve the established AzureCliRunner method order.
internal interface IAzureCliRunner
{
    Task EnsureLoggedInAsync(CancellationToken cancellationToken);

    Task EnsureExtensionAsync(string extensionName, CancellationToken cancellationToken);

    Task EnsureSuccessAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string failureMessage);

    Task EnsureSuccessAsync(
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken,
        string failureMessage);

    Task<string> CaptureValueAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string failureMessage);

    Task<JsonDocument> CaptureJsonAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string failureMessage);

    Task<ProcessResult> TryRunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}
#pragma warning restore CA1068

internal sealed class AzureCliRunner : IAzureCliRunner
{
    private static readonly string[] AccountShowArguments = ["account", "show"];

    private readonly IProcessRunner _processRunner;

    public AzureCliRunner()
        : this(new ProcessRunner())
    {
    }

    internal AzureCliRunner(IProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        var result = await RunAzAsync(
            AccountShowArguments,
            cancellationToken,
            throwOnFailure: false,
            echoStandardOutput: false).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new CommandException("Azure CLI is not logged in. Run 'az login' first.");
        }
    }

    public Task EnsureExtensionAsync(string extensionName, CancellationToken cancellationToken) =>
        EnsureSuccessAsync(
            new[] { "extension", "add", "--name", extensionName, "--upgrade" },
            cancellationToken,
            $"Failed to install or update Azure CLI extension '{extensionName}'.");

    public Task EnsureSuccessAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string failureMessage) =>
        EnsureSuccessAsync(arguments, workingDirectory: null, cancellationToken, failureMessage);

    public async Task EnsureSuccessAsync(IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken, string failureMessage)
    {
        var result = await RunAzAsync(
            arguments,
            cancellationToken,
            workingDirectory: workingDirectory,
            throwOnFailure: false).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new CommandException($"{failureMessage}{Environment.NewLine}{result.StandardError}");
        }
    }

    public Task<string> CaptureValueAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string failureMessage) =>
        CaptureValueAsync(arguments, workingDirectory: null, cancellationToken, failureMessage);

    [SuppressMessage("Design", "CA1068:CancellationToken parameters must come last", Justification = "Every IAzureCliRunner method takes the cancellation token before the failure message; this overload keeps that order.")]
    public async Task<string> CaptureValueAsync(IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken, string failureMessage)
    {
        var result = await RunAzAsync(
            arguments,
            cancellationToken,
            workingDirectory: workingDirectory,
            throwOnFailure: false,
            echoStandardOutput: false).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new CommandException($"{failureMessage}{Environment.NewLine}{result.StandardError}");
        }

        return result.StandardOutput.Trim();
    }

    public async Task<JsonDocument> CaptureJsonAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string failureMessage)
    {
        var payload = await CaptureValueAsync(arguments, cancellationToken, failureMessage).ConfigureAwait(false);
        return JsonDocument.Parse(payload);
    }

    // Callers handle a failure themselves (often an expected one, like a show call that
    // probes for a resource a fresh deployment lacks), so az's stderr is kept on the
    // result rather than printed as an alarming "ERROR: (ResourceNotFound)".
    public Task<ProcessResult> TryRunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        RunAzAsync(arguments, cancellationToken, throwOnFailure: false, echoStandardOutput: false, echoStandardError: false);

    private async Task<ProcessResult> RunAzAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        bool throwOnFailure = true,
        bool echoStandardOutput = true,
        bool echoStandardError = true)
    {
        var allArguments = new List<string>(arguments.Count + 1);
        allArguments.AddRange(arguments);
        allArguments.Add("--only-show-errors");

        var (fileName, processArguments) = ResolveProcessCommand(allArguments);

        var result = await _processRunner.RunAsync(
            fileName,
            processArguments,
            workingDirectory,
            echoStandardOutput,
            echoStandardError,
            cancellationToken).ConfigureAwait(false);
        if (throwOnFailure && !result.Succeeded)
        {
            throw new CommandException(result.StandardError);
        }

        return result;
    }

    internal static (string FileName, IReadOnlyList<string> Arguments) ResolveProcessCommand(IReadOnlyList<string> allArguments)
    {
        if (OperatingSystem.IsWindows())
        {
            return ("cmd.exe", new[] { "/d", "/c", BuildWindowsCommand(allArguments) });
        }

        return ("az", allArguments);
    }

    private static string BuildWindowsCommand(IReadOnlyList<string> arguments) =>
        $"az.cmd {string.Join(" ", arguments.Select(QuoteWindowsCommandArgument))}";

    private static string QuoteWindowsCommandArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        if (!RequiresWindowsCommandQuoting(argument))
        {
            return argument;
        }

        return $"\"{argument.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static bool RequiresWindowsCommandQuoting(string argument) =>
        argument.Any(static ch => char.IsWhiteSpace(ch) || "\"&|<>^()%!".Contains(ch, StringComparison.Ordinal));
}
