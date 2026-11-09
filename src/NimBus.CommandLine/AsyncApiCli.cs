using System;
using System.IO;
using System.Linq;
using NimBus.Core.Events;
using CoreAsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;
using ServiceBusAsyncApiExporter = NimBus.ServiceBus.AsyncApi.AsyncApiExporter;

namespace NimBus.CommandLine;

/// <summary>
/// Shared, process-independent implementations of the <c>nb asyncapi export|validate|diff</c>
/// commands. Each method returns the exact process exit code and writes human-readable output to an
/// injected <see cref="TextWriter"/>, so the CLI wiring is a one-liner and command-level tests can
/// assert the exit code (and captured text) without spawning a process.
/// </summary>
public static class AsyncApiCli
{
    /// <summary>
    /// Exports an AsyncAPI document to a file. By default the built-in platform is serialized (attribute
    /// enrichment only). When <paramref name="assemblyPath"/> is supplied, an
    /// <see cref="IAsyncApiDocumentProvider"/> loaded from that host assembly produces the document, so
    /// fluent <c>Publish&lt;T&gt;(o =&gt; o.AsyncApi…)</c> enrichment recorded by the host surfaces too.
    /// Returns 0 on success, 1 when the provider cannot be loaded or the write fails.
    /// </summary>
    public static int RunExport(
        string? output,
        CoreAsyncApiFormat? format,
        TextWriter writer,
        string? assemblyPath = null,
        string? providerType = null)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var resolvedFormat = format
            ?? (output != null && output.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? CoreAsyncApiFormat.Json
                : CoreAsyncApiFormat.Yaml);

        var defaultName = resolvedFormat == CoreAsyncApiFormat.Json ? "asyncapi.json" : "asyncapi.yaml";
        var outputPath = string.IsNullOrEmpty(output)
            ? Path.Combine(Environment.CurrentDirectory, defaultName)
            : output!;

        try
        {
            if (!string.IsNullOrWhiteSpace(assemblyPath))
            {
                var provider = AsyncApiProviderLoader.Load(assemblyPath!, providerType);
                File.WriteAllText(outputPath, provider.GetDocument(resolvedFormat));
                writer.WriteLine($"AsyncAPI 3.0 spec exported to: {outputPath}");
                writer.WriteLine($"  from provider in '{Path.GetFileName(assemblyPath)}' ({resolvedFormat.ToString().ToUpperInvariant()})");
                return 0;
            }

            var platform = new PlatformConfiguration();
            File.WriteAllText(outputPath, ServiceBusAsyncApiExporter.Serialize(platform, resolvedFormat));

            var endpointCount = platform.Endpoints.Count();
            var eventCount = platform.EventTypes.Count()
                + platform.DynamicForwards.Select(f => f.EventTypeId).Distinct().Count();
            writer.WriteLine($"AsyncAPI 3.0 spec exported to: {outputPath}");
            writer.WriteLine($"  {endpointCount} endpoints, {eventCount} event types ({resolvedFormat.ToString().ToUpperInvariant()})");
            return 0;
        }
        catch (Exception ex)
        {
            writer.WriteLine($"Failed to export AsyncAPI document: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Validates an AsyncAPI document. Returns 0 when valid, 1 when invalid or unreadable.</summary>
    public static int RunValidate(string file, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        try
        {
            var document = AsyncApiDocumentLoader.LoadFile(file);
            var result = AsyncApiValidator.Validate(document);

            if (result.IsValid)
            {
                writer.WriteLine($"'{file}' is a valid AsyncAPI 3.0 document.");
                return 0;
            }

            writer.WriteLine($"'{file}' is not a valid AsyncAPI 3.0 document ({result.Errors.Count} error(s)):");
            foreach (var error in result.Errors)
            {
                writer.WriteLine($"  - {error}");
            }

            return 1;
        }
        catch (Exception ex)
        {
            writer.WriteLine($"Failed to validate '{file}': {ex.Message}");
            return 1;
        }
    }

    /// <summary>Diffs two AsyncAPI documents. Returns 0 for additive-only changes, 1 when breaking.</summary>
    public static int RunDiff(string oldFile, string newFile, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        try
        {
            var oldDoc = AsyncApiDocumentLoader.LoadFile(oldFile);
            var newDoc = AsyncApiDocumentLoader.LoadFile(newFile);
            var result = AsyncApiDiff.Diff(oldDoc, newDoc);

            if (result.Changes.Count == 0)
            {
                writer.WriteLine("No differences.");
                return 0;
            }

            var breakingCount = result.Changes.Count(c => c.Breaking);
            writer.WriteLine($"{result.Changes.Count} change(s) between '{oldFile}' and '{newFile}' ({breakingCount} breaking):");

            foreach (var group in result.Changes
                         .GroupBy(c => c.Category)
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                writer.WriteLine($"  {group.Key}:");
                foreach (var change in group.OrderBy(c => c.Path, StringComparer.Ordinal))
                {
                    var marker = change.Breaking ? "BREAKING" : "ok";
                    writer.WriteLine($"    [{marker}] {change.Kind} {change.Path} — {change.Detail}");
                }
            }

            if (result.HasBreaking)
            {
                writer.WriteLine($"Breaking changes detected ({breakingCount}).");
                return 1;
            }

            writer.WriteLine("All changes are non-breaking (additive).");
            return 0;
        }
        catch (Exception ex)
        {
            writer.WriteLine($"Failed to diff '{oldFile}' and '{newFile}': {ex.Message}");
            return 1;
        }
    }
}
