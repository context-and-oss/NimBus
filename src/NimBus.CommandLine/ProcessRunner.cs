using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NimBus.CommandLine;

internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        bool echoStandardOutput,
        bool echoStandardError,
        CancellationToken cancellationToken);
}

internal interface IProcessTermination
{
    void KillEntireProcessTree(Process process);

    Task WaitForExitAsync(Process process, CancellationToken cancellationToken);
}

internal sealed class ProcessRunner : IProcessRunner
{
    private static readonly TimeSpan DefaultTerminationTimeout = TimeSpan.FromSeconds(10);
    private readonly IProcessTermination _processTermination;
    private readonly TimeSpan _terminationTimeout;

    public ProcessRunner()
        : this(new ProcessTermination(), DefaultTerminationTimeout)
    {
    }

    internal ProcessRunner(IProcessTermination processTermination, TimeSpan terminationTimeout)
    {
        ArgumentNullException.ThrowIfNull(processTermination);

        if (terminationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(terminationTimeout),
                terminationTimeout,
                "The process termination timeout must be greater than zero.");
        }

        _processTermination = processTermination;
        _terminationTimeout = terminationTimeout;
    }

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken) =>
        RunAsync(fileName, arguments, workingDirectory, echoStandardOutput: true, echoStandardError: true, cancellationToken);

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        bool echoStandardOutput,
        CancellationToken cancellationToken) =>
        RunAsync(fileName, arguments, workingDirectory, echoStandardOutput, echoStandardError: true, cancellationToken);

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        bool echoStandardOutput,
        bool echoStandardError,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        DeploymentSecrets.RemoveFrom(startInfo.Environment);

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        if (fileName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) && arguments.Count >= 2)
        {
            // cmd.exe requires a raw Arguments string — ArgumentList auto-quoting
            // breaks cmd.exe /c parsing of the command string.
            startInfo.Arguments = string.Join(" ", arguments);
        }
        else
        {
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        var output = new StringBuilder();
        var error = new StringBuilder();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                if (echoStandardOutput)
                {
                    CliOutput.WriteLine(args.Data);
                }

                output.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                if (echoStandardError)
                {
                    CliOutput.WriteError(args.Data);
                }

                error.AppendLine(args.Data);
            }
        };

        bool started;

        try
        {
            started = process.Start();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 2)
        {
            throw new CommandException($"Could not start '{fileName}'. Ensure it is installed and available on PATH.");
        }

        if (!started)
        {
            throw new CommandException($"Failed to start '{fileName}'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The deployment command may still be reading an ephemeral ARM
            // parameter file. Stop the whole child tree and wait for shutdown
            // before the caller unwinds and deletes that file.
            await TerminateProcessAsync(process).ConfigureAwait(false);
            throw;
        }

        return new ProcessResult(process.ExitCode, output.ToString().Trim(), error.ToString().Trim());
    }

    private async Task TerminateProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                _processTermination.KillEntireProcessTree(process);
            }
        }
        catch (Exception exception) when (IsExpectedTerminationFailure(exception))
        {
            CliOutput.WriteError(
                "Warning: Could not terminate the child process tree cleanly (" +
                exception.GetType().Name +
                ").");
        }

        using var timeout = new CancellationTokenSource(_terminationTimeout);

        try
        {
            await _processTermination.WaitForExitAsync(process, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            CliOutput.WriteError("Warning: Timed out waiting for the child process tree to exit.");
        }
        catch (Exception exception) when (IsExpectedTerminationFailure(exception))
        {
            CliOutput.WriteError(
                "Warning: Could not confirm that the child process tree exited (" +
                exception.GetType().Name +
                ").");
        }
    }

    private static bool IsExpectedTerminationFailure(Exception exception) =>
        exception is Win32Exception
            or InvalidOperationException
            or NotSupportedException;
}

internal sealed class ProcessTermination : IProcessTermination
{
    public void KillEntireProcessTree(Process process) =>
        process.Kill(entireProcessTree: true);

    public Task WaitForExitAsync(Process process, CancellationToken cancellationToken) =>
        process.WaitForExitAsync(cancellationToken);
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}
