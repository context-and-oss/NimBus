using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CrmErpDemo.AppHost;

/// <summary>
/// Gives the images that <c>aspire deploy</c> builds a real version. The repository keeps a
/// <c>0.0.0</c> placeholder (docs/versioning.md) and Aspire passes no version to the builds
/// it starts, so without this the deployed nimbus-ops footer reads "NimBus 0.0.0".
/// <c>nb deploy apps</c> stamps its builds from the git tag in the same way.
/// </summary>
public static partial class DemoBuildVersion
{
    /// <summary>
    /// The environment variable <c>Directory.Build.props</c> reads the version from. Set it on
    /// the deploy process to choose the version yourself.
    /// </summary>
    public const string VariableName = "NIMBUS_BUILD_VERSION";

    /// <summary>
    /// Sets <see cref="VariableName"/> for this process, and so for the builds Aspire starts
    /// from it, to what <c>git describe --tags</c> reports for the checkout: the tag itself on
    /// a release commit ("4.13.0"), or the tag plus the distance from it ("4.13.0-2-gabc1234").
    /// A value that is already set wins. Returns the version in effect, or null when there is
    /// none (no git, no tags), which leaves the build at the placeholder.
    /// </summary>
    public static string? Apply(string repositoryDirectory)
    {
        var configured = Environment.GetEnvironmentVariable(VariableName);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (!TryNormalize(Describe(repositoryDirectory), out var version))
        {
            return null;
        }

        Environment.SetEnvironmentVariable(VariableName, version);
        return version;
    }

    /// <summary>
    /// Turns <c>git describe --tags</c> output ("v4.13.0", "v4.13.0-2-gabc1234") into a version.
    /// Anything that is not three numeric parts with an optional suffix is refused, so an
    /// unrelated tag cannot break the build with an invalid version.
    /// </summary>
    public static bool TryNormalize(string? describe, out string version)
    {
        version = string.Empty;
        var match = DescribePattern().Match(describe?.Trim() ?? string.Empty);
        if (!match.Success)
        {
            return false;
        }

        version = match.Groups["version"].Value;
        return true;
    }

    private static string? Describe(string repositoryDirectory)
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", "describe --tags")
            {
                WorkingDirectory = repositoryDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (git is null)
            {
                return null;
            }

            // Drain stderr on the side so a chatty git cannot block on a full pipe.
            _ = git.StandardError.ReadToEndAsync();
            var output = git.StandardOutput.ReadToEnd();
            if (!git.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                git.Kill(entireProcessTree: true);
                return null;
            }

            return git.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // git is not installed or could not start: stamping is best-effort.
            return null;
        }
    }

    [GeneratedRegex(@"^[vV]?(?<version>\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?)$")]
    private static partial Regex DescribePattern();
}
