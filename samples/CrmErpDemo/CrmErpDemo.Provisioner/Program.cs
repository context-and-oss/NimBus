using Azure.Identity;
using Azure.Messaging.ServiceBus.Administration;
using CrmErpDemo.Contracts;
using NimBus.ServiceBus.Provisioning;

// Partner-facing entities for the CloudEvents interop showcase (created here for both the
// NimBus emulator and real Azure — the AppHost runs this provisioner in either mode):
//  - PartnerInbound topic + session-required catch-all "CrmEndpoint" subscription: the
//    external PartnerPortal publishes raw CloudEvents here, and Crm.Adapter drains it
//    with a second receiver. Sessions are required because NimBus receivers are always
//    ServiceBusSessionProcessors.
//  - PartnerPortalCapture subscription on the ErpEndpoint topic: a plain subscription
//    the NimBus-free PartnerPortal reads ERP CloudEvents from. The rule keeps only
//    original publishes (excludes response/retry/continuation envelopes).
const string partnerInboundTopic = "PartnerInbound";
const string partnerInboundSubscription = "CrmEndpoint";
const string erpTopic = "ErpEndpoint";
const string partnerCaptureSubscription = "PartnerPortalCapture";

// A connection string locally (emulator or user-secrets). In the Azure deployment the
// namespace has local auth disabled, so Aspire supplies its endpoint URL and the job
// authenticates with its managed identity.
var serviceBus = Environment.GetEnvironmentVariable("ConnectionStrings__servicebus")
    ?? throw new InvalidOperationException("ConnectionStrings__servicebus is required.");

ServiceBusAdministrationClient adminClient;
ServiceBusTopologyProvisioner provisioner;
if (serviceBus.Contains("Endpoint=", StringComparison.OrdinalIgnoreCase))
{
    adminClient = new ServiceBusAdministrationClient(serviceBus);
    provisioner = new ServiceBusTopologyProvisioner(serviceBus, () => new CrmErpPlatformConfiguration());
}
else
{
    adminClient = new ServiceBusAdministrationClient(new Uri(serviceBus).Host, new DefaultAzureCredential());
    provisioner = new ServiceBusTopologyProvisioner(adminClient, () => new CrmErpPlatformConfiguration());
}

Console.WriteLine("Provisioning CRM/ERP demo Service Bus topology...");
await provisioner.ApplyAsync(CancellationToken.None);
Console.WriteLine("Topology provisioning complete.");

// Keep broker-redelivery regressions bounded in the isolated E2E profile.
// This changes neither normal demo provisioning nor the NimBus retry policy.
if (Environment.GetEnvironmentVariable("E2E__Enabled") == "true" &&
    Environment.GetEnvironmentVariable("E2E__Key") is { Length: >= 32 })
{
    foreach (var endpoint in new[] { "CrmEndpoint", "ErpEndpoint" })
    {
        var subscription = (await adminClient.GetSubscriptionAsync(endpoint, endpoint)).Value;
        subscription.MaxDeliveryCount = 3;
        subscription.LockDuration = TimeSpan.FromSeconds(5);
        await adminClient.UpdateSubscriptionAsync(subscription);
    }
}

if (!await adminClient.TopicExistsAsync(partnerInboundTopic))
{
    await adminClient.CreateTopicAsync(partnerInboundTopic);
    Console.WriteLine($"Created partner ingress topic '{partnerInboundTopic}'.");
}

if (!await adminClient.SubscriptionExistsAsync(partnerInboundTopic, partnerInboundSubscription))
{
    await adminClient.CreateSubscriptionAsync(
        new CreateSubscriptionOptions(partnerInboundTopic, partnerInboundSubscription)
        {
            RequiresSession = true,
            MaxDeliveryCount = NimBus.Core.Messages.Constants.ServiceBusMaxDeliveryCount,
        });
    Console.WriteLine($"Created session subscription '{partnerInboundSubscription}' on topic '{partnerInboundTopic}'.");
}

if (!await adminClient.SubscriptionExistsAsync(erpTopic, partnerCaptureSubscription))
{
    await adminClient.CreateSubscriptionAsync(
        new CreateSubscriptionOptions(erpTopic, partnerCaptureSubscription)
        {
            MaxDeliveryCount = 5,
        },
        new CreateRuleOptions(
            "cloudevents-capture",
            new SqlRuleFilter("user.MessageType = 'EventRequest' AND user.From IS NULL")));
    Console.WriteLine($"Created raw-capture subscription '{partnerCaptureSubscription}' on topic '{erpTopic}' for the PartnerPortal.");
}

Console.WriteLine("Partner interop entities provisioned.");
