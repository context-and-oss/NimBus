using System.Reflection;

namespace NimBus.Adapter.Mcp;

/// <summary>The version <c>adapter_get_health</c> reports: the commit the deployment was built from.</summary>
internal static class AdapterVersion
{
    /// <summary>The version of the host's entry assembly, read once.</summary>
    public static readonly string? Current = FromAssembly(Assembly.GetEntryAssembly());

    /// <summary>The commit from an assembly's informational version; see <see cref="FromInformationalVersion"/>.</summary>
    public static string? FromAssembly(Assembly? assembly)
        => FromInformationalVersion(assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>
    /// The part after the first <c>+</c>, where SourceLink stamps the source revision
    /// (<c>4.18.0+3f2a9c1</c>), else the whole informational version.
    /// </summary>
    public static string? FromInformationalVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
            return null;

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 && plus < informationalVersion.Length - 1
            ? informationalVersion[(plus + 1)..]
            : informationalVersion;
    }
}
