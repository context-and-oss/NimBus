using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NimBus.Core;

namespace NimBus.CommandLine;

/// <summary>
/// Loads a public parameterless <see cref="IPlatform"/> from a host assembly so
/// <c>nb catalog export --assembly &lt;path&gt;</c> documents a real integration platform instead of
/// the built-in sample. Unlike <see cref="AsyncApiProviderLoader"/> (whose provider returns an
/// already-serialized document), the catalog builder needs the platform object itself, so this
/// loader resolves the <see cref="IPlatform"/> type directly. Fluent enrichment recorded in host
/// DI is not observable through this path; attribute enrichment still applies.
/// </summary>
internal static class PlatformLoader
{
    /// <summary>
    /// Loads the assembly at <paramref name="assemblyPath"/> and resolves its platform,
    /// optionally selecting the type named <paramref name="platformTypeName"/>.
    /// </summary>
    /// <summary>
    /// Builds the platform factory the provisioners take: the catalog from
    /// <paramref name="assemblyPath"/> when one is given, otherwise the built-in platform.
    /// Loading is deferred to the factory call so an unreadable assembly fails during the
    /// command rather than while options are being parsed.
    /// </summary>
    public static Func<IPlatform> CreateFactory(string? assemblyPath, string? platformTypeName = null) =>
        string.IsNullOrWhiteSpace(assemblyPath)
            ? () => new PlatformConfiguration()
            : () => Load(assemblyPath, platformTypeName);

    public static IPlatform Load(string assemblyPath, string? platformTypeName = null)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath))
            throw new ArgumentException("Assembly path is required.", nameof(assemblyPath));

        var fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Assembly not found: '{fullPath}'.", fullPath);

        var assembly = Assembly.LoadFrom(fullPath);
        return Resolve(assembly, platformTypeName);
    }

    /// <summary>
    /// Resolves the platform a host assembly exposes: a public, concrete, parameterless
    /// <see cref="IPlatform"/> implementation. When <paramref name="platformTypeName"/> is null the
    /// assembly must expose exactly one candidate; otherwise the type is matched by full or simple name.
    /// </summary>
    public static IPlatform Resolve(Assembly assembly, string? platformTypeName = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var candidates = assembly.GetExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetConstructor(Type.EmptyTypes) != null)
            .Where(t => typeof(IPlatform).IsAssignableFrom(t))
            .ToList();

        Type platformType;
        if (!string.IsNullOrWhiteSpace(platformTypeName))
        {
            platformType = candidates.FirstOrDefault(t =>
                string.Equals(t.FullName, platformTypeName, StringComparison.Ordinal)
                || string.Equals(t.Name, platformTypeName, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"No public parameterless IPlatform named '{platformTypeName}' was found in '{assembly.GetName().Name}'.");
        }
        else if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No public parameterless IPlatform was found in '{assembly.GetName().Name}'. Expose one, or pass --platform <type>.");
        }
        else if (candidates.Count > 1)
        {
            throw new InvalidOperationException(
                $"Multiple IPlatform types were found in '{assembly.GetName().Name}': "
                + string.Join(", ", candidates.Select(t => t.FullName))
                + ". Disambiguate with --platform <type>.");
        }
        else
        {
            platformType = candidates[0];
        }

        return (IPlatform)Activator.CreateInstance(platformType)!;
    }
}
