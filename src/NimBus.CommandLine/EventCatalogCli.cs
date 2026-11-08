using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NimBus.Core;

namespace NimBus.CommandLine;

/// <summary>
/// Shared, process-independent implementation of <c>nb catalog export</c>. Returns the process
/// exit code and writes human-readable output to an injected <see cref="TextWriter"/>, mirroring
/// <see cref="AsyncApiCli"/>. Owns the disk semantics: the five generated resource directories
/// (<c>domains/ services/ events/ commands/ channels/</c>) are fully owned by the exporter and
/// deleted + regenerated on every run; everything else in the output directory — the scaffold
/// files, <c>public/</c>, and any hand-added resources outside those directories — is never touched.
/// </summary>
public static class EventCatalogCli
{
    private static readonly string[] GeneratedDirectories = { "domains", "services", "events", "commands", "channels" };

    /// <summary>
    /// Exports the catalog. By default the built-in platform is exported (attribute enrichment
    /// only); with <paramref name="assemblyPath"/>, a public parameterless <see cref="IPlatform"/>
    /// is loaded from that host assembly. Scaffold files (<c>eventcatalog.config.js</c>,
    /// <c>package.json</c>, <c>.gitignore</c>, <c>public/</c>) are created only when missing, so an
    /// empty target becomes a runnable EventCatalog project and re-runs preserve customizations.
    /// Returns 0 on success, 1 when the platform cannot be loaded or the write fails.
    /// </summary>
    public static int RunExport(
        string? output,
        TextWriter writer,
        string? assemblyPath = null,
        string? platformTypeName = null,
        string? title = null)
    {
        ArgumentNullException.ThrowIfNull(writer);

        try
        {
            // Resolve the platform and build the whole catalog in memory BEFORE deleting
            // anything, so a load/build failure never leaves a half-wiped catalog behind.
            IPlatform platform = string.IsNullOrWhiteSpace(assemblyPath)
                ? new PlatformConfiguration()
                : PlatformLoader.Load(assemblyPath!, platformTypeName);

            var files = EventCatalogExporter.Build(platform);

            var outputPath = string.IsNullOrEmpty(output)
                ? Path.Combine(Environment.CurrentDirectory, "eventcatalog")
                : Path.GetFullPath(output!);
            Directory.CreateDirectory(outputPath);

            foreach (var generated in GeneratedDirectories)
            {
                var dir = Path.Combine(outputPath, generated);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }

            foreach (var (relativePath, content) in files)
            {
                var fullPath = Path.Combine(outputPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, content);
            }

            ScaffoldIfMissing(outputPath, title);

            int Count(string prefix) => files.Keys.Count(k =>
                k.StartsWith(prefix + "/", StringComparison.Ordinal) && k.EndsWith("/index.mdx", StringComparison.Ordinal));

            writer.WriteLine($"EventCatalog exported to: {outputPath}");
            writer.WriteLine(
                $"  {Count("domains")} domains, {Count("services")} services, {Count("events")} events, " +
                $"{Count("commands")} commands, {Count("channels")} channels");
            writer.WriteLine($"  Run it: cd \"{outputPath}\" && npm install && npm run dev   (requires Node 22+)");
            return 0;
        }
        catch (Exception ex)
        {
            writer.WriteLine($"Failed to export EventCatalog: {ex.Message}");
            return 1;
        }
    }

    // Create-if-missing only: never overwrite, so user customizations (and the generated-once
    // cId) survive every re-export.
    private static void ScaffoldIfMissing(string outputPath, string? title)
    {
        var effectiveTitle = string.IsNullOrWhiteSpace(title) ? "NimBus" : title!;

        var configPath = Path.Combine(outputPath, "eventcatalog.config.js");
        if (!File.Exists(configPath))
        {
            File.WriteAllText(configPath, BuildConfig(effectiveTitle));
        }

        var packagePath = Path.Combine(outputPath, "package.json");
        if (!File.Exists(packagePath))
        {
            File.WriteAllText(packagePath, BuildPackageJson());
        }

        var gitignorePath = Path.Combine(outputPath, ".gitignore");
        if (!File.Exists(gitignorePath))
        {
            File.WriteAllText(gitignorePath, string.Join("\n", new[]
            {
                "/node_modules", "/build", ".astro", "out", "dist", ".eventcatalog-core", ".env*", ".DS_Store", string.Empty,
            }));
        }

        Directory.CreateDirectory(Path.Combine(outputPath, "public"));
    }

    private static string BuildConfig(string title) =>
        string.Join("\n", new[]
        {
            "/** @type {import('@eventcatalog/core/bin/eventcatalog.config').Config} */",
            "export default {",
            $"  cId: '{Guid.NewGuid()}',",
            $"  title: '{title.Replace("'", "\\'", StringComparison.Ordinal)}',",
            $"  organizationName: '{title.Replace("'", "\\'", StringComparison.Ordinal)}',",
            "  output: 'static',",
            "  base: '/',",
            "  trailingSlash: false,",
            "  search: { type: 'resource' },",
            "  mermaid: { iconPacks: ['logos'] },",
            "};",
            string.Empty,
        });

    private static string BuildPackageJson() =>
        string.Join("\n", new[]
        {
            "{",
            "  \"name\": \"nimbus-eventcatalog\",",
            "  \"version\": \"0.1.0\",",
            "  \"private\": true,",
            "  \"engines\": { \"node\": \">=22\" },",
            "  \"scripts\": {",
            "    \"dev\": \"eventcatalog dev\",",
            "    \"build\": \"eventcatalog build\",",
            "    \"preview\": \"eventcatalog preview\",",
            "    \"start\": \"eventcatalog start\"",
            "  },",
            "  \"dependencies\": {",
            "    \"@eventcatalog/core\": \"^2.0.0\"",
            "  }",
            "}",
            string.Empty,
        });
}
