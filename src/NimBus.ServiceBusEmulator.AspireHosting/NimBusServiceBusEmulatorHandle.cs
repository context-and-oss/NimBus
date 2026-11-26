using Aspire.Hosting.ApplicationModel;

namespace NimBus.ServiceBusEmulator.AspireHosting;

/// <summary>
/// Groups the runnable emulator project with the connection-string resource exposed to consumers.
/// </summary>
[Obsolete("Returned by the obsolete AddNimBusServiceBusEmulator overloads. AddNimBusServiceBusEmulatorProject returns NimBusServiceBusEmulatorProjectHandle. This type will be removed in the next major version.")]
public readonly record struct NimBusServiceBusEmulatorHandle(
    IResourceBuilder<ProjectResource> Project,
    IResourceBuilder<IResourceWithConnectionString> ConnectionString);
