#pragma warning disable CA1707, CA2007

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NimBus.ServiceBusEmulator.Tests;

/// <summary>
/// Launches the NimBus Service Bus emulator as a local process for a test, or points at an
/// existing one through <c>NIMBUS_SBEMULATOR_TEST_CS</c>. Shared by the emulator suite and,
/// as a linked file, by <c>NimBus.WebApp.Tests</c>' simulation end-to-end tests.
/// </summary>
internal sealed class EmulatorProcess : IAsyncDisposable
{
    private readonly Process? _process;
    private readonly string? _journalPath;
    private readonly bool _deleteJournalOnDispose;
    private readonly ConcurrentQueue<string> _output = new();

    private EmulatorProcess(
        Process? process,
        int port,
        string? connectionString = null,
        string? journalPath = null,
        bool deleteJournalOnDispose = true)
    {
        _process = process;
        _journalPath = journalPath;
        _deleteJournalOnDispose = deleteJournalOnDispose;
        ConnectionString = connectionString ?? $"Endpoint=sb://127.0.0.1:{port};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=;UseDevelopmentEmulator=true";
        var endpoint = ConnectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Single(part => part.StartsWith("Endpoint=", StringComparison.OrdinalIgnoreCase))["Endpoint=".Length..];
        HttpEndpoint = new UriBuilder(new Uri(endpoint)) { Scheme = Uri.UriSchemeHttp }.Uri;
    }

    public string ConnectionString { get; }

    public Uri HttpEndpoint { get; }

    /// <summary>
    /// The default overall startup deadline across all launch attempts. It leaves room for
    /// the test itself inside the suite's usual 60 s <c>[Timeout]</c>.
    /// </summary>
    public static readonly TimeSpan DefaultStartupDeadline = TimeSpan.FromSeconds(30);

    private const int MaxLaunches = 3;

    /// <summary>
    /// Starts the emulator, retrying on a new port up to three times, all within
    /// <paramref name="startupDeadline"/> (default <see cref="DefaultStartupDeadline"/>).
    /// A failure carries the emulator output of the last launch attempt.
    /// </summary>
    public static async Task<EmulatorProcess> StartAsync(
        string? journalPath = null,
        bool deleteJournalOnDispose = true,
        TimeSpan? startupDeadline = null)
    {
        if (System.Environment.GetEnvironmentVariable("NIMBUS_SBEMULATOR_TEST_CS") is { Length: > 0 } existing)
        {
            return new EmulatorProcess(null, 0, existing);
        }

        var deadline = startupDeadline ?? DefaultStartupDeadline;
        var clock = Stopwatch.StartNew();

        // Startup can fail transiently on a loaded runner (port stolen
        // between probe and bind, resource pressure); retry on a new port.
        Exception? lastFailure = null;
        var lastOutput = string.Empty;
        var launches = 0;
        while (launches < MaxLaunches && (launches == 0 || clock.Elapsed < deadline))
        {
            launches++;
            var attempt = await StartOnceAsync(journalPath, deleteJournalOnDispose, clock, deadline);
            if (attempt.Instance is { } instance)
            {
                Console.WriteLine($"[step] emulator startup: {clock.ElapsedMilliseconds} ms ({launches} launch attempt(s))");
                return instance;
            }

            lastFailure = attempt.Failure;
            lastOutput = attempt.Output;
        }

        var newLine = System.Environment.NewLine;
        if (clock.Elapsed >= deadline)
        {
            throw new TimeoutException(
                $"The emulator did not become healthy within the {deadline.TotalSeconds:0.###} s startup deadline " +
                $"({launches} launch attempt(s), {clock.ElapsedMilliseconds} ms). " +
                $"Output of the last attempt:{newLine}{lastOutput}",
                lastFailure);
        }

        throw new InvalidOperationException(
            $"The emulator failed to start after {launches} attempts ({clock.ElapsedMilliseconds} ms). " +
            $"Output of the last attempt:{newLine}{lastOutput}",
            lastFailure);
    }

    private readonly record struct StartAttempt(EmulatorProcess? Instance, Exception? Failure, string Output);

    private static async Task<StartAttempt> StartOnceAsync(
        string? journalPath,
        bool deleteJournalOnDispose,
        Stopwatch clock,
        TimeSpan deadline)
    {
        var port = GetAvailablePort();
        var project = FindProject();
        journalPath ??= Path.Combine(Path.GetTempPath(), $"nimbus-sbemulator-test-{Guid.NewGuid():N}", "topology.json");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            Arguments = $"run --no-build --configuration \"{GetBuildConfiguration()}\" --project \"{project}\" -- --port {port}",
            WorkingDirectory = Path.GetDirectoryName(project),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["NIMBUS_SBEMULATOR_TOPOLOGY_PATH"] = journalPath;
        startInfo.Environment["NIMBUS_SBEMULATOR_DIAGNOSTICS"] = "1";
        var process = Process.Start(startInfo);
        if (process is null)
        {
            return new StartAttempt(null, new InvalidOperationException("Could not start the emulator process."), string.Empty);
        }

        var instance = new EmulatorProcess(
            process,
            port,
            journalPath: journalPath,
            deleteJournalOnDispose: deleteJournalOnDispose);
        process.OutputDataReceived += (_, args) => instance.CaptureOutput(args.Data);
        process.ErrorDataReceived += (_, args) => instance.CaptureOutput(args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        Exception? failure = null;
        while (deadline - clock.Elapsed is var remaining && remaining > TimeSpan.Zero)
        {
            if (process.HasExited)
            {
                failure = new InvalidOperationException($"The emulator exited during startup with code {process.ExitCode}.");
                break;
            }

            using var probe = new CancellationTokenSource(remaining);
            try
            {
                using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/health"), probe.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return new StartAttempt(instance, null, string.Empty);
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (OperationCanceledException)
            {
            }

            try
            {
                await Task.Delay(50, probe.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        failure ??= new TimeoutException(
            $"The emulator did not become healthy within the {deadline.TotalSeconds:0.###} s startup deadline.");
        await instance.DisposeAsync();
        return new StartAttempt(null, failure, instance.DumpOutput());
    }

    public string DumpOutput() => string.Join(System.Environment.NewLine, _output);

    private void CaptureOutput(string? line)
    {
        if (line is null)
        {
            return;
        }

        _output.Enqueue(line);
        while (_output.Count > 1000 && _output.TryDequeue(out _))
        {
        }
    }

    private static string GetBuildConfiguration()
    {
        return new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the active test build configuration.");
    }

    public ValueTask DisposeAsync()
    {
        if (_process is null)
        {
            return ValueTask.CompletedTask;
        }

        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5_000);
        }

        _process.Dispose();
        if (_deleteJournalOnDispose && _journalPath is not null &&
            Path.GetDirectoryName(_journalPath) is { } directory && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "NimBus.ServiceBusEmulator", "NimBus.ServiceBusEmulator.csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the emulator project.");
    }
}
