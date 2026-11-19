using Amqp;
using Amqp.Listener;
using Microsoft.Extensions.Logging;
using NimBus.ServiceBusEmulator.Broker;

namespace NimBus.ServiceBusEmulator.Protocol;

internal sealed class AmqpFrontend : IDisposable
{
    private readonly ContainerHost _host;
    private readonly HashSet<string> _managementNodes = new(StringComparer.OrdinalIgnoreCase);

    public AmqpFrontend(int port, BrokerNamespace broker, int maxMessageSize = 262_144, ILoggerFactory? loggerFactory = null)
    {
        var sessionLinks = new SessionLinkRegistry();
        var pendingAttaches = new PendingLinkAttachRegistry();
        _host = new ContainerHost(new Address($"amqp://127.0.0.1:{port}"));
        var listener = _host.Listeners[0];
        listener.SASL.EnableMechanism("MSSBCBS", new MssbcbsSaslProfile());
        listener.HandlerFactory = _ => new GuidDeliveryTagHandler(pendingAttaches);
        _host.AddressResolver = static (_, attach) =>
        {
            if (!attach.Role && attach.Target is Amqp.Transactions.Coordinator)
            {
                // ContainerHost otherwise casts the coordinator terminus to a
                // normal Target before it reaches the custom link processor.
                return "$coordinator";
            }

            var address = attach.Role
                ? (attach.Source as Amqp.Framing.Source)?.Address
                : (attach.Target as Amqp.Framing.Target)?.Address;
            return address?.TrimStart('/');
        };
        _host.RegisterRequestProcessor("$cbs", new CbsRequestProcessor());
        broker.TopicCreated += RegisterTopicManagement;
        broker.SubscriptionCreated += RegisterSubscriptionManagement;
        foreach (var topic in broker.GetTopics())
        {
            RegisterTopicManagement(topic.Name);
            foreach (var subscription in broker.GetSubscriptions(topic.Name))
            {
                RegisterSubscriptionManagement(topic.Name, subscription.Name);
            }
        }
        _host.RegisterLinkProcessor(new BrokerLinkProcessor(broker, sessionLinks, pendingAttaches, maxMessageSize, loggerFactory?.CreateLogger<BrokerLinkProcessor>()));

        void RegisterTopicManagement(string topicName) => RegisterManagementNode(topicName);

        void RegisterSubscriptionManagement(string topicName, string subscriptionName)
        {
            var entityPath = $"{topicName}/Subscriptions/{subscriptionName}";
            RegisterManagementNode(entityPath);
            RegisterManagementNode($"{entityPath}/$DeadLetterQueue");
        }

        void RegisterManagementNode(string entityPath)
        {
            var node = $"{entityPath}/$management";
            lock (_managementNodes)
            {
                if (_managementNodes.Add(node))
                {
                    _host.RegisterRequestProcessor(node, new ManagementRequestProcessor(broker, sessionLinks, entityPath, maxMessageSize));
                }
            }
        }
    }

    public void Start() => _host.Open();

    public void Dispose() => _host.Close();
}
