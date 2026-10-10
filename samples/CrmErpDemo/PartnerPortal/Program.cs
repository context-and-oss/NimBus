using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;

// PartnerPortal — a simulated EXTERNAL third-party system with no NimBus dependency.
// It demonstrates both directions of CloudEvents 1.0 interoperability:
//
//  INBOUND  — publishes raw CloudEvents (binary content mode, "ce-*" application
//             properties) to the plain PartnerInbound topic. Crm.Adapter's AutoDetect
//             subscriber maps the CloudEvents `type` attribute's last dot-segment to
//             the PartnerLeadSubmitted contract and creates a Partner-origin contact.
//             The "ce-" prefix (instead of NimBus's own "cloudEvents:") is deliberate:
//             it proves the reader's AcceptedPrefixes handle non-Microsoft producers.
//
//  OUTBOUND — reads ERP events from ErpEndpoint/PartnerPortalCapture, a plain
//             subscription whose rule keeps only original publishes. Erp.Api publishes
//             with UseCloudEvents (binary), so this loop parses "cloudEvents:*"
//             application properties with zero NimBus knowledge.
//
// See samples/CrmErpDemo/README.md (CloudEvents partner interop) and docs/cloudevents.md.

const string partnerInboundTopic = "PartnerInbound";
const string erpTopic = "ErpEndpoint";
const string captureSubscription = "PartnerPortalCapture";
const string leadEventType = "com.partnerportal.crm.PartnerLeadSubmitted";
const string structuredContentType = "application/cloudevents+json";
const string cloudEventsPropertyPrefix = "cloudEvents:";

var serviceBus = Environment.GetEnvironmentVariable("ConnectionStrings__servicebus")
    ?? throw new InvalidOperationException(
        "ConnectionStrings__servicebus is required -- a Service Bus namespace connection string " +
        "(or 'Endpoint=sb://localhost;...;UseDevelopmentEmulator=true' for the local emulator), " +
        "or the namespace endpoint URL to authenticate with a managed identity.");

var leadIntervalSeconds = int.TryParse(Environment.GetEnvironmentVariable("PARTNER_LEAD_INTERVAL_SECONDS"), out var s) && s > 0 ? s : 45;
var sendInvalid = string.Equals(Environment.GetEnvironmentVariable("PARTNER_SEND_INVALID"), "true", StringComparison.OrdinalIgnoreCase);

await using var client = serviceBus.Contains("Endpoint=", StringComparison.OrdinalIgnoreCase)
    ? new ServiceBusClient(serviceBus)
    : new ServiceBusClient(new Uri(serviceBus).Host, new DefaultAzureCredential());
using var shutdown = new CancellationTokenSource();

ConsoleCancelEventHandler cancelHandler = (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};
Console.CancelKeyPress += cancelHandler;

Console.WriteLine($"PartnerPortal started (no NimBus reference). Publishing a lead to '{partnerInboundTopic}' every {leadIntervalSeconds}s; listening on {erpTopic}/{captureSubscription}. Ctrl+C to stop.");

try
{
    await Program.RunSupervisedAsync(PublishLeadsAsync, ConsumeErpCloudEventsAsync, shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    // Expected on Ctrl+C.
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

async Task PublishLeadsAsync(CancellationToken cancellationToken)
{
    var firstNames = new[] { "Ada", "Grace", "Alan", "Edsger", "Barbara", "Donald" };
    var lastNames = new[] { "Lovelace", "Hopper", "Turing", "Dijkstra", "Liskov", "Knuth" };
    var companies = new[] { "Contoso Ltd", "Fabrikam Inc", "Northwind Traders", "Adventure Works" };

    await using var sender = client.CreateSender(partnerInboundTopic);

    if (sendInvalid)
    {
        // Deliberately broken CloudEvent: no ce-source. NimBus's validating handler
        // rejects it as a permanent failure, so it lands in the subscription's
        // dead-letter queue with a clear InvalidCloudEventException reason.
        var invalid = new ServiceBusMessage(BinaryData.FromString("{\"broken\":true}"))
        {
            MessageId = Guid.NewGuid().ToString(),
            SessionId = Guid.NewGuid().ToString(),
            ContentType = "application/json",
        };
        invalid.ApplicationProperties["ce-specversion"] = "1.0";
        invalid.ApplicationProperties["ce-id"] = invalid.MessageId;
        invalid.ApplicationProperties["ce-type"] = leadEventType;
        try
        {
            await sender.SendMessageAsync(invalid, cancellationToken);
            Console.WriteLine("[publish] Sent one INVALID CloudEvent (missing ce-source) to demo dead-lettering.");
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            Console.WriteLine($"[publish] Could not send the invalid-CloudEvent demo message: {ex.Message}");
        }
    }

    var iteration = 0;
    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            var lead = new
            {
                LeadId = Guid.NewGuid(),
                FirstName = firstNames[iteration % firstNames.Length],
                LastName = lastNames[iteration % lastNames.Length],
                Email = $"lead{iteration}@partnerportal.example",
                Phone = $"+1-555-01{iteration % 100:D2}",
                CompanyName = companies[iteration % companies.Length],
            };
            iteration++;

            var message = new ServiceBusMessage(BinaryData.FromString(JsonSerializer.Serialize(lead)))
            {
                MessageId = Guid.NewGuid().ToString(),
                // PartnerInbound/CrmEndpoint requires sessions (NimBus receivers are session
                // processors); SessionId is a plain Service Bus property, no NimBus needed.
                SessionId = lead.LeadId.ToString(),
                ContentType = "application/json",
            };
            message.ApplicationProperties["ce-specversion"] = "1.0";
            message.ApplicationProperties["ce-id"] = message.MessageId;
            message.ApplicationProperties["ce-source"] = "urn:partnerportal";
            message.ApplicationProperties["ce-type"] = leadEventType;
            message.ApplicationProperties["ce-time"] = DateTimeOffset.UtcNow.ToString("o");

            await sender.SendMessageAsync(message, cancellationToken);
            Console.WriteLine($"[publish] Sent lead {lead.LeadId} ({lead.FirstName} {lead.LastName}, {lead.CompanyName}) as CloudEvent '{leadEventType}' to {partnerInboundTopic}.");
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            // The emulator drops AMQP connections while warming up; the client
            // self-heals, so just wait and retry the next tick.
            Console.WriteLine($"[publish] Transient Service Bus error, retrying next tick: {ex.Message}");
        }

        await Task.Delay(TimeSpan.FromSeconds(leadIntervalSeconds), cancellationToken);
    }
}

async Task ConsumeErpCloudEventsAsync(CancellationToken cancellationToken)
{
    await using var receiver = client.CreateReceiver(erpTopic, captureSubscription);

    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30), cancellationToken);

            if (message is null)
            {
                continue;
            }

            if (string.Equals(message.ContentType, structuredContentType, StringComparison.OrdinalIgnoreCase))
            {
                // Structured content mode: the entire CloudEvent (context attributes + data)
                // is the JSON body.
                Console.WriteLine("[consume] Structured CloudEvent received:");
                Console.WriteLine(message.Body.ToString());
            }
            else if (message.ApplicationProperties.ContainsKey($"{cloudEventsPropertyPrefix}specversion")
                || message.ApplicationProperties.ContainsKey("ce-specversion"))
            {
                // Binary content mode: CloudEvents context attributes ride as "cloudEvents:*"
                // application properties (the AMQP CloudEvents binding); the body is the raw
                // domain-event JSON and message.ContentType carries datacontenttype.
                Console.WriteLine($"[consume] Binary CloudEvent received (datacontenttype={message.ContentType}):");
                foreach (var property in message.ApplicationProperties)
                {
                    if (property.Key.StartsWith(cloudEventsPropertyPrefix, StringComparison.Ordinal)
                        || property.Key.StartsWith("ce-", StringComparison.Ordinal))
                    {
                        Console.WriteLine($"  {property.Key} = {property.Value}");
                    }
                }
                Console.WriteLine($"  data = {Encoding.UTF8.GetString(message.Body)}");
            }
            else
            {
                // Belt-and-braces: the capture rule already excludes response/retry envelopes,
                // but if a non-CloudEvents message slips through, don't poison the subscription.
                Console.WriteLine($"[consume] Skipping non-CloudEvents message {message.MessageId}.");
            }

            await receiver.CompleteMessageAsync(message, cancellationToken);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            // Receive and completion share this retry boundary. The emulator can drop
            // either operation while warming up, and the client reconnects itself.
            Console.WriteLine($"[consume] Transient Service Bus error, retrying in 5s: {ex.Message}");
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }
}

static bool IsTransient(Exception ex) =>
    ex is ServiceBusException or System.IO.IOException or System.Net.Sockets.SocketException or TimeoutException;

internal partial class Program
{
    internal static async Task RunSupervisedAsync(
        Func<CancellationToken, Task> publisher,
        Func<CancellationToken, Task> consumer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(consumer);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var publisherTask = RunLoopAsync(publisher, linkedCancellation.Token);
        var consumerTask = RunLoopAsync(consumer, linkedCancellation.Token);

        await Task.WhenAny(publisherTask, consumerTask);
        linkedCancellation.Cancel();

        // WhenAll observes both tasks, gives faults precedence over the sibling's expected
        // cancellation, and does not return until the canceled sibling has shut down.
        await Task.WhenAll(publisherTask, consumerTask);
    }

    private static async Task RunLoopAsync(
        Func<CancellationToken, Task> loop,
        CancellationToken cancellationToken) =>
        await loop(cancellationToken);
}
