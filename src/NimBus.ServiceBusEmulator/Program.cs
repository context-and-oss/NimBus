using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using NimBus.ServiceBusEmulator.Admin;
using NimBus.ServiceBusEmulator.Broker;
using NimBus.ServiceBusEmulator.Hosting;
using NimBus.ServiceBusEmulator.Protocol;
using NimBus.ServiceBusEmulator.Storage;

var builder = WebApplication.CreateBuilder(args);
var publicPort = builder.Configuration.GetValue<int?>("port")
    ?? builder.Configuration.GetValue<int?>("NIMBUS_SBEMULATOR_PORT")
    ?? 5672;
if (publicPort is < 0 or > 65535)
{
    throw new InvalidOperationException("The emulator port must be between 0 and 65535.");
}

var httpPort = GetAvailableLoopbackPort();
var amqpPort = GetAvailableLoopbackPort();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, httpPort);
    options.Limits.MaxRequestBodySize = 1024 * 1024;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    options.Limits.MinRequestBodyDataRate = new MinDataRate(1024, TimeSpan.FromSeconds(5));
});

var maxStoredBytes = builder.Configuration.GetValue<long?>("NIMBUS_SBEMULATOR_MAX_STORED_BYTES")
    ?? 512L * 1024 * 1024;
var maxMessageSize = builder.Configuration.GetValue<int?>("NIMBUS_SBEMULATOR_MAX_MESSAGE_SIZE")
    ?? 262_144;
if (maxStoredBytes < 1 || maxMessageSize is < 1 or > 1_048_576)
{
    throw new InvalidOperationException("The emulator storage and message-size limits must be positive; message size is capped at 1 MiB.");
}

var broker = new BrokerNamespace(new BrokerOptions { MaxStoredBytes = maxStoredBytes });
var resourceName = builder.Configuration["NIMBUS_SBEMULATOR_RESOURCE_NAME"] ?? "standalone";
var topologyPath = builder.Configuration["NIMBUS_SBEMULATOR_TOPOLOGY_PATH"]
    ?? TopologyJournal.DefaultPath(resourceName);
using var topologyJournal = new TopologyJournal(topologyPath);
await topologyJournal.ReplayAsync(broker, CancellationToken.None).ConfigureAwait(false);
builder.Services.AddSingleton(broker);
var app = builder.Build();
// A launcher may choose the instance id, so it can tell this emulator's /health answer apart
// from another process that holds the port this emulator failed to bind.
var instanceId = Guid.TryParse(builder.Configuration["NIMBUS_SBEMULATOR_INSTANCE_ID"], out var configuredInstanceId)
    ? configuredInstanceId
    : Guid.NewGuid();
app.MapServiceBusAdmin(broker, instanceId, topologyJournal);

await app.StartAsync().ConfigureAwait(false);
using var amqp = new AmqpFrontend(amqpPort, broker, maxMessageSize, app.Services.GetRequiredService<ILoggerFactory>());
amqp.Start();
await using var multiplexer = new TcpMultiplexer(
    publicPort,
    new IPEndPoint(IPAddress.Loopback, amqpPort),
    new IPEndPoint(IPAddress.Loopback, httpPort));
multiplexer.Start();

try
{
    await app.WaitForShutdownAsync().ConfigureAwait(false);
}
finally
{
    await app.StopAsync().ConfigureAwait(false);
    await app.DisposeAsync().ConfigureAwait(false);
}

static int GetAvailableLoopbackPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}
