using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;

namespace NimBus.ServiceBusEmulator.AspireHosting;

/// <summary>
/// Groups the emulator, run from its project file, with the connection-string resource exposed to
/// consumers.
/// </summary>
/// <param name="Project">The emulator project.</param>
/// <param name="ConnectionString">The connection string to reference from the provisioner, Resolver, WebApp and adapters.</param>
public readonly record struct NimBusServiceBusEmulatorProjectHandle(
    IResourceBuilder<DotnetProjectResource> Project,
    IResourceBuilder<IResourceWithConnectionString> ConnectionString);
