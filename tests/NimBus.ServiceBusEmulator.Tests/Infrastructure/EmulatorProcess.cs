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
    /// <paramref name="environment"/> adds environment variables to the emulator process.
    /// A failure carries the emulator output of the last launch attempt.
    /// </summary>
    public static async Task<EmulatorProcess> StartAsync(
        string? journalPath = null,
        bool deleteJournalOnDispose = true,
        IReadOnlyDictionary<string, string>? environment = null,
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
            var attempt = await StartOnceAsync(journalPath, deleteJournalOnDispose, environment, clock, deadline);
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
        IReadOnlyDictionary<string, string>? environment,
        Stopwatch clock,
        TimeSpan deadline)
    {
        var port = GetAvailablePort();
        var project = FindProject();
        var ownsJournal = journalPath is null;
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
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

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
        // A failed attempt only kills its process. A caller-supplied journal is left in place
        // for the retry that reuses it; only a journal this attempt generated is removed.
        // A process that does not exit after the kill is folded into this attempt's failure,
        // so it never throws past the startup retry.
        if (!instance.KillProcess())
        {
            failure = new AggregateException(
                $"Launch attempt failed, and its process did not exit within {ExitTimeout.TotalSeconds:0.###} s of being killed.",
                failure,
                new TimeoutException("The failed launch attempt's emulator process did not exit after being killed."));
        }
        else if (ownsJournal)
        {
            DeleteJournalDirectory(journalPath);
        }

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

    /// <summary>
    /// Disposes the emulator as the logged step "dispose emulator", and never throws on an
    /// exit timeout. Callers use <c>await using</c>, where a throwing dispose would replace the
    /// body's exception. If the process does not exit within <see cref="ExitTimeout"/> after
    /// being killed, the timeout is written to the test output with the emulator output, and
    /// the journal is left in place. Use <see cref="DisposeOrThrowAsync"/> to fail on it instead.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (DisposeCore() is { } exitTimeout)
        {
            Console.WriteLine($"[step] dispose emulator: {exitTimeout}");
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Disposes the emulator like <see cref="DisposeAsync"/>, but throws a
    /// <see cref="TimeoutException"/> naming the step "dispose emulator" and carrying the
    /// emulator output when the process does not exit within <see cref="ExitTimeout"/> of
    /// being killed. Opt in only where the dispose runs as a failure-preserving cleanup
    /// (<c>TestSteps.RunWithCleanupsAsync</c>), never under <c>await using</c>.
    /// </summary>
    public ValueTask DisposeOrThrowAsync()
    {
        if (DisposeCore() is { } exitTimeout)
        {
            throw new TimeoutException($"Step 'dispose emulator' did not finish: {exitTimeout}");
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Kills the process and, if it exited, deletes the journal when configured to. Returns
    /// null on success, or a description of the exit timeout with the emulator output.
    /// </summary>
    private string? DisposeCore()
    {
        if (_process is null)
        {
            return null;
        }

        var watch = Stopwatch.StartNew();
        if (!KillProcess())
        {
            return $"the emulator process did not exit within " +
                $"{ExitTimeout.TotalSeconds:0.###} s of being killed ({watch.ElapsedMilliseconds} ms); " +
                $"its journal was left at '{_journalPath}'. Emulator output:{System.Environment.NewLine}{DumpOutput()}";
        }

        if (_deleteJournalOnDispose && _journalPath is not null)
        {
            DeleteJournalDirectory(_journalPath);
        }

        Console.WriteLine($"[step] dispose emulator: {watch.ElapsedMilliseconds} ms");
        return null;
    }

    /// <summary>How long a kill waits for the emulator process to exit.</summary>
    internal static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Test seam: replaces the wait for the killed process to exit. It receives
    /// <see cref="ExitTimeout"/> and returns whether the process exited.
    /// </summary>
    internal Func<TimeSpan, bool>? WaitForExitOverride { get; set; }

    /// <summary>
    /// Kills the process without touching the journal, waiting at most
    /// <see cref="ExitTimeout"/> for it to exit. Returns whether it exited.
    /// </summary>
    private bool KillProcess()
    {
        if (_process is null)
        {
            return true;
        }

        var exited = true;
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            exited = WaitForExitOverride is { } wait
                ? wait(ExitTimeout)
                : _process.WaitForExit(ExitTimeout);
        }

        _process.Dispose();
        return exited;
    }

    private static void DeleteJournalDirectory(string journalPath)
    {
        if (Path.GetDirectoryName(journalPath) is { } directory && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
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
