#pragma warning disable CA1707, CA2007
using Xunit;

namespace NimBus.CommandLine.Tests;

// Disk semantics of `nb catalog export`: scaffolds a runnable EventCatalog project when the
// target is empty, never overwrites scaffold files, and fully owns (deletes + regenerates)
// the five generated resource directories on every run.
public sealed class EventCatalogCliTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nimbus-eventcatalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void RunExport_ScaffoldsFullCatalog_WhenTargetEmpty()
    {
        var dir = NewTempDir();
        try
        {
            var writer = new StringWriter();
            var exit = EventCatalogCli.RunExport(dir, writer);

            Assert.Equal(0, exit);

            var config = File.ReadAllText(Path.Combine(dir, "eventcatalog.config.js"));
            Assert.Contains("cId", config, StringComparison.Ordinal);
            Assert.Contains("organizationName", config, StringComparison.Ordinal);
            // The cId is a real GUID.
            var cId = ExtractCId(config);
            Assert.True(Guid.TryParse(cId, out _), $"cId '{cId}' is not a GUID");

            var package = File.ReadAllText(Path.Combine(dir, "package.json"));
            Assert.Contains("@eventcatalog/core", package, StringComparison.Ordinal);
            Assert.Contains("\"dev\"", package, StringComparison.Ordinal);
            Assert.Contains("\"build\"", package, StringComparison.Ordinal);

            Assert.True(File.Exists(Path.Combine(dir, ".gitignore")));
            Assert.True(Directory.Exists(Path.Combine(dir, "public")));

            // Generated resources from the built-in platform.
            Assert.True(File.Exists(Path.Combine(dir, "services", "StorefrontEndpoint", "index.mdx")));
            Assert.True(File.Exists(Path.Combine(dir, "events", "OrderPlaced", "index.mdx")));

            Assert.Contains("services", writer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RunExport_NeverOverwritesExistingScaffold()
    {
        var dir = NewTempDir();
        try
        {
            var writer = new StringWriter();
            Assert.Equal(0, EventCatalogCli.RunExport(dir, writer));
            var originalConfig = File.ReadAllText(Path.Combine(dir, "eventcatalog.config.js"));

            // User customizes the scaffold files.
            var customConfig = originalConfig + "\n// customized-marker\n";
            File.WriteAllText(Path.Combine(dir, "eventcatalog.config.js"), customConfig);
            File.WriteAllText(Path.Combine(dir, "package.json"), "{ \"custom\": true }");

            Assert.Equal(0, EventCatalogCli.RunExport(dir, new StringWriter()));

            Assert.Equal(customConfig, File.ReadAllText(Path.Combine(dir, "eventcatalog.config.js")));
            Assert.Equal("{ \"custom\": true }", File.ReadAllText(Path.Combine(dir, "package.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RunExport_RefreshRemovesStaleGeneratedResources()
    {
        var dir = NewTempDir();
        try
        {
            // Stale generated resource from a removed endpoint + a hand-written root file.
            Directory.CreateDirectory(Path.Combine(dir, "services", "OldEndpoint"));
            File.WriteAllText(Path.Combine(dir, "services", "OldEndpoint", "index.mdx"), "stale");
            File.WriteAllText(Path.Combine(dir, "hand-written.md"), "keep me");

            Assert.Equal(0, EventCatalogCli.RunExport(dir, new StringWriter()));

            Assert.False(Directory.Exists(Path.Combine(dir, "services", "OldEndpoint")));
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(dir, "hand-written.md")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RunExport_MissingAssembly_ReturnsOneWithMessage()
    {
        var dir = NewTempDir();
        try
        {
            // Existing generated content must survive a failed run (validate before deleting).
            Directory.CreateDirectory(Path.Combine(dir, "services", "Existing"));
            File.WriteAllText(Path.Combine(dir, "services", "Existing", "index.mdx"), "still here");

            var writer = new StringWriter();
            var exit = EventCatalogCli.RunExport(dir, writer, assemblyPath: Path.Combine(dir, "missing.dll"));

            Assert.Equal(1, exit);
            Assert.Contains("missing.dll", writer.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("still here", File.ReadAllText(Path.Combine(dir, "services", "Existing", "index.mdx")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RunExport_DefaultOutput_UsesEventcatalogDir()
    {
        // CurrentDirectory is process-global and other test classes run in parallel, so assert
        // against the path RunExport reports rather than a CWD we cannot pin reliably.
        var writer = new StringWriter();
        var exit = EventCatalogCli.RunExport(null, writer);
        try
        {
            Assert.Equal(0, exit);
            var reported = ReportedPath(writer.ToString());
            Assert.Equal("eventcatalog", Path.GetFileName(reported));
            Assert.True(File.Exists(Path.Combine(reported, "eventcatalog.config.js")));
        }
        finally
        {
            var reported = ReportedPath(writer.ToString());
            if (Directory.Exists(reported)) Directory.Delete(reported, recursive: true);
        }
    }

    private static string ReportedPath(string output)
    {
        const string marker = "EventCatalog exported to: ";
        var line = output.Split('\n').First(l => l.StartsWith(marker, StringComparison.Ordinal));
        return line[marker.Length..].Trim();
    }

    private static string ExtractCId(string config)
    {
        var marker = "cId: '";
        var start = config.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = config.IndexOf('\'', start);
        return config[start..end];
    }
}
