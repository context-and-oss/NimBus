using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using NimBus.ServiceBusEmulator.AspireHosting;

namespace Aspire.Hosting;

/// <summary>
/// Aspire extensions for the NimBus Service Bus emulator.
/// </summary>
public static class NimBusServiceBusEmulatorBuilderExtensions
{
    /// <summary>The NuGet package that ships the emulator as a dotnet tool.</summary>
    public const string EmulatorToolPackageId = "Akaule.NimBus.ServiceBusEmulator";

    /// <summary>
    /// Adds the emulator from its project file and a connection-string resource named
    /// <paramref name="name"/>. Use it in an AppHost that has the NimBus sources; an AppHost without
    /// them uses <see cref="AddNimBusServiceBusEmulator(IDistributedApplicationBuilder, string, int?, string?)"/>.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The connection-string resource name, for example <c>servicebus</c>.</param>
    /// <param name="projectPath">
    /// The emulator's project file (<c>NimBus.ServiceBusEmulator.csproj</c>), absolute or relative to
    /// the AppHost directory.
    /// </param>
    /// <param name="port">A fixed host port, or <c>null</c> for a dynamic one.</param>
    /// <returns>The emulator project and its connection string.</returns>
    public static NimBusServiceBusEmulatorProjectHandle
        AddNimBusServiceBusEmulatorProject(
            this IDistributedApplicationBuilder builder,
            string name,
            string projectPath,
            int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        var (project, connectionString) = Configure(builder, name, builder.AddDotnetProject($"{name}-emulator", projectPath), port);
        return new(project, connectionString);
    }

    /// <summary>
    /// Adds the emulator project and a connection-string resource named <paramref name="name"/>.
    /// </summary>
    [Obsolete("Aspire 17 retires AddProject and generated project metadata. Use AddNimBusServiceBusEmulatorProject(name, projectPath). This overload will be removed in the next major version.")]
    public static NimBusServiceBusEmulatorHandle
        AddNimBusServiceBusEmulator<TProject>(
            this IDistributedApplicationBuilder builder,
            string name,
            int? port = null)
        where TProject : IProjectMetadata, new()
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var (project, connectionString) = Configure(builder, name, builder.AddProject<TProject>($"{name}-emulator"), port);
        return new(project, connectionString);
    }

    /// <summary>
    /// Adds an emulator project by project-file path for AppHosts that do not use generated project metadata.
    /// </summary>
    [Obsolete("Aspire 17 retires AddProject. Use AddNimBusServiceBusEmulatorProject(name, projectPath), which returns the Aspire 17 project resource. This overload will be removed in the next major version.")]
    public static NimBusServiceBusEmulatorHandle
        AddNimBusServiceBusEmulator(
            this IDistributedApplicationBuilder builder,
            string name,
            string projectPath,
            int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        var (project, connectionString) = Configure(builder, name, builder.AddProject($"{name}-emulator", projectPath), port);
        return new(project, connectionString);
    }

    /// <summary>
    /// Adds the emulator from its NuGet tool package (<c>Akaule.NimBus.ServiceBusEmulator</c>, run with
    /// <c>dotnet tool exec</c>) and a connection-string resource named <paramref name="name"/>. Use it in
    /// an AppHost that has no NimBus source checkout.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The connection-string resource name, for example <c>servicebus</c>.</param>
    /// <param name="port">A fixed host port, or <c>null</c> for a dynamic one.</param>
    /// <param name="version">
    /// The tool version. Defaults to the <c>NIMBUS_TOOL_VERSION</c> configuration value, then to this
    /// hosting package's own version.
    /// </param>
    /// <returns>The emulator executable and its connection string.</returns>
    public static NimBusServiceBusEmulatorToolHandle
        AddNimBusServiceBusEmulator(
            this IDistributedApplicationBuilder builder,
            string name,
            int? port = null,
            string? version = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var tool = NimBusTools.AddTool(builder, $"{name}-emulator", EmulatorToolPackageId, version);
        var (emulator, connectionString) = Configure(builder, name, tool, port);
        return new(emulator, connectionString);
    }

    private static (IResourceBuilder<TResource> Emulator, IResourceBuilder<IResourceWithConnectionString> ConnectionString)
        Configure<TResource>(
            IDistributedApplicationBuilder builder,
            string name,
            IResourceBuilder<TResource> emulatorBuilder,
            int? port)
        where TResource : class, IResourceWithEndpoints, IResourceWithEnvironment, IResourceWithWaitSupport
    {
        // The endpoint must stay proxied: DCP requires an explicit port for
        // unproxied endpoints, and a hard-coded default would collide when
        // several AppHosts run emulators side by side. The DCP proxy carries
        // both planes fine — the emulator multiplexes AMQP and HTTP admin by
        // first-byte sniffing on the single target port it gets via env.
        var emulator = emulatorBuilder
            .WithEndpoint(
                targetPort: null,
                port: port,
                scheme: "tcp",
                name: "tcp",
                env: "NIMBUS_SBEMULATOR_PORT",
                isExternal: false,
                isProxied: true)
            .WithEnvironment("NIMBUS_SBEMULATOR_RESOURCE_NAME", name);

        var endpoint = emulator.GetEndpoint("tcp");
        var connectionString = builder.AddConnectionString(
                name,
                ReferenceExpression.Create(
                    $"Endpoint=sb://{endpoint.Property(EndpointProperty.Host)}:{endpoint.Property(EndpointProperty.Port)};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=nimbus-local;UseDevelopmentEmulator=true"))
            .WaitFor(emulator);

        var healthCheckName = $"{name}-emulator-readiness";
        builder.Services.AddHttpClient();
        builder.Services.AddHealthChecks().AddCheck(
            healthCheckName,
            new EmulatorEndpointHealthCheck(() => endpoint));
        emulator.WithHealthCheck(healthCheckName);

        return (emulator, connectionString);
    }
}
