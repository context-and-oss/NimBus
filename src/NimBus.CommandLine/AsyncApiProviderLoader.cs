using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NimBus.Core.Events;

namespace NimBus.CommandLine;

/// <summary>
/// Loads an <see cref="IAsyncApiDocumentProvider"/> from a host assembly so
/// <c>nb asyncapi export --assembly &lt;path&gt;</c> can emit a document enriched with fluent
/// <c>Publish&lt;T&gt;(o =&gt; o.AsyncApi…)</c> metadata. Fluent enrichment is a runtime construct the
/// standalone CLI cannot otherwise observe; a host exposes it either as a public parameterless
/// <see cref="IAsyncApiDocumentProvider"/> or — the usual case, since <c>AddNimBusAsyncApiDocument</c>
/// registers a private, DI-backed provider — as a public parameterless
/// <see cref="IAsyncApiDocumentProviderFactory"/> whose <c>Create()</c> builds the container and returns
/// the DI-resolved provider. Mirrors the reflection convention the WebApp already uses to load an
/// <c>IPlatform</c> by type/assembly.
/// </summary>
public static class AsyncApiProviderLoader
{
    /// <summary>
    /// Loads the assembly at <paramref name="assemblyPath"/> and resolves its document provider,
    /// optionally selecting the type named <paramref name="providerTypeName"/>.
    /// </summary>
    public static IAsyncApiDocumentProvider Load(string assemblyPath, string? providerTypeName = null)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath))
            throw new ArgumentException("Assembly path is required.", nameof(assemblyPath));

        var fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Assembly not found: '{fullPath}'.", fullPath);

        var assembly = Assembly.LoadFrom(fullPath);
        return Resolve(assembly, providerTypeName);
    }

    /// <summary>
    /// Resolves the AsyncAPI document provider a host assembly exposes: a public, concrete, parameterless
    /// <see cref="IAsyncApiDocumentProvider"/>, or a public parameterless
    /// <see cref="IAsyncApiDocumentProviderFactory"/> (invoked to build the DI-backed provider). When
    /// <paramref name="providerTypeName"/> is null the assembly must expose exactly one candidate;
    /// otherwise the type is matched by full or simple name.
    /// </summary>
    public static IAsyncApiDocumentProvider Resolve(Assembly assembly, string? providerTypeName = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        // A candidate is either a direct provider type or a factory type — both surface a
        // parameterless way to obtain an IAsyncApiDocumentProvider, so they compete for --provider.
        var candidates = assembly.GetExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetConstructor(Type.EmptyTypes) != null)
            .Where(t => typeof(IAsyncApiDocumentProvider).IsAssignableFrom(t)
                        || typeof(IAsyncApiDocumentProviderFactory).IsAssignableFrom(t))
            .ToList();

        Type providerType;
        if (!string.IsNullOrWhiteSpace(providerTypeName))
        {
            providerType = candidates.FirstOrDefault(t =>
                string.Equals(t.FullName, providerTypeName, StringComparison.Ordinal)
                || string.Equals(t.Name, providerTypeName, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"No public parameterless IAsyncApiDocumentProvider or IAsyncApiDocumentProviderFactory named '{providerTypeName}' was found in '{assembly.GetName().Name}'.");
        }
        else if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No public parameterless IAsyncApiDocumentProvider or IAsyncApiDocumentProviderFactory was found in '{assembly.GetName().Name}'. Expose one, or pass --provider <type>.");
        }
        else if (candidates.Count > 1)
        {
            throw new InvalidOperationException(
                $"Multiple AsyncAPI provider types were found in '{assembly.GetName().Name}': "
                + string.Join(", ", candidates.Select(t => t.FullName))
                + ". Disambiguate with --provider <type>.");
        }
        else
        {
            providerType = candidates[0];
        }

        return Instantiate(providerType);
    }

    private static IAsyncApiDocumentProvider Instantiate(Type providerType)
    {
        var instance = Activator.CreateInstance(providerType)!;

        // A factory is the common case (AddNimBusAsyncApiDocument registers a private, DI-backed provider):
        // build it through Create(). A type that is both a provider and a factory is treated as a factory.
        if (instance is IAsyncApiDocumentProviderFactory factory)
        {
            return factory.Create()
                ?? throw new InvalidOperationException(
                    $"'{providerType.FullName}'.Create() returned null.");
        }

        return (IAsyncApiDocumentProvider)instance;
    }
}
