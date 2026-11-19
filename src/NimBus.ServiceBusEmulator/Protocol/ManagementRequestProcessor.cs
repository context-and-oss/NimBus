using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using Amqp.Types;
using NimBus.ServiceBusEmulator.Broker;

namespace NimBus.ServiceBusEmulator.Protocol;

internal sealed class ManagementRequestProcessor(
    BrokerNamespace broker,
    SessionLinkRegistry sessionLinks,
    string? fixedEntityPath = null,
    int maxMessageSize = 262_144) : IRequestProcessor
{
    internal const string ManagementStatusCode = "statusCode";

    public int Credit => 100;

    public void Process(RequestContext requestContext)
    {
        var operation = GetApplicationProperty(requestContext.Message, "operation")?.ToString();
        if (operation?.StartsWith("com.microsoft:", StringComparison.OrdinalIgnoreCase) == true)
        {
            operation = operation["com.microsoft:".Length..];
        }
        var entityPath = fixedEntityPath ?? (requestContext.Message.Properties?.To ?? string.Empty).TrimStart('/');
        if (entityPath.EndsWith("/$management", StringComparison.OrdinalIgnoreCase))
        {
            entityPath = entityPath[..^"/$management".Length];
        }

        try
        {
            var body = requestContext.Message.Body as Map ?? new Map();
            var owner = IsSessionOperation(operation)
                ? sessionLinks.GetOwner(
                    requestContext.Link.Session.Connection,
                    GetApplicationProperty(requestContext.Message, "associated-link-name")?.ToString())
                : null;
            var responseBody = Dispatch(operation, entityPath, body, owner);
            requestContext.Complete(Response(200, "OK", responseBody));
        }
        catch (MessageSizeExceededException exception)
        {
            requestContext.Complete(Response(413, exception.Message, errorCondition: "amqp:link:message-size-exceeded"));
        }
        catch (BrokerQuotaExceededException exception)
        {
            requestContext.Complete(Response(403, exception.Message, errorCondition: "amqp:resource-limit-exceeded"));
        }
        catch (NotSupportedException exception)
        {
            requestContext.Complete(Response(501, exception.Message));
        }
        catch (KeyNotFoundException exception)
        {
            requestContext.Complete(Response(410, exception.Message, errorCondition: ErrorCondition(operation)));
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            requestContext.Complete(Response(400, exception.Message));
        }
    }

    private Map Dispatch(string? operation, string entityPath, Map body, string? owner)
    {
        if (operation == "schedule-message")
        {
            return Schedule(entityPath, body);
        }

        if (operation == "cancel-scheduled-message")
        {
            return CancelScheduled(entityPath, body);
        }

        const string deadLetterSuffix = "/$DeadLetterQueue";
        var isDeadLetter = entityPath.EndsWith(deadLetterSuffix, StringComparison.OrdinalIgnoreCase);
        if (isDeadLetter)
        {
            entityPath = entityPath[..^deadLetterSuffix.Length];
        }

        ParseSubscriptionPath(entityPath, out var topicName, out var subscriptionName);
        return operation switch
        {
            "renew-lock" => RenewLocks(topicName, subscriptionName, body, isDeadLetter),
            "renew-session-lock" => RenewSession(topicName, subscriptionName, body, owner!),
            "get-session-state" => GetSessionState(topicName, subscriptionName, body, owner!),
            "set-session-state" => SetSessionState(topicName, subscriptionName, body, owner!),
            "peek-message" => Peek(topicName, subscriptionName, body, isDeadLetter),
            "update-disposition" => UpdateDisposition(topicName, subscriptionName, body),
            "receive-by-sequence-number" => throw new NotSupportedException("Service Bus deferral is outside Spec 027 section 3."),
            _ => throw new NotSupportedException($"Management operation '{operation}' is outside Spec 027."),
        };
    }

    private Map Schedule(string topicName, Map body)
    {
        if (GetValue(body, "messages") is not System.Collections.IEnumerable values)
        {
            throw new FormatException("schedule-message requires messages.");
        }

        var messages = new List<BrokerMessage>();
        foreach (var value in values)
        {
            if (value is not Map item || GetValue(item, "message") is not byte[] bytes)
            {
                throw new FormatException("Each scheduled item requires encoded message bytes.");
            }

            var message = Message.Decode(new ByteBuffer(bytes, 0, bytes.Length, bytes.Length));
            var decoded = AmqpMessageConverter.FromAmqp(message);
            if (decoded.Body.Length > maxMessageSize)
                throw new MessageSizeExceededException($"Message exceeds the configured {maxMessageSize}-byte limit.");
            messages.Add(decoded);
        }

        return new Map { ["sequence-numbers"] = broker.PublishBatch(topicName, messages) };
    }

    private sealed class MessageSizeExceededException(string message) : Exception(message);

    private Map CancelScheduled(string topicName, Map body)
    {
        if (GetValue(body, "sequence-numbers") is not System.Collections.IEnumerable values)
        {
            throw new FormatException("cancel-scheduled-message requires sequence-numbers.");
        }

        foreach (var value in values)
        {
            broker.CancelScheduled(topicName, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
        }

        return new Map();
    }

    private Map RenewLocks(string topicName, string subscriptionName, Map body, bool isDeadLetter)
    {
        var values = GetValue(body, "lock-tokens") as Array
            ?? throw new FormatException("renew-lock requires lock-tokens.");
        var expirations = new DateTime[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            var token = values.GetValue(index) switch
            {
                Guid guid => guid,
                byte[] bytes when bytes.Length == 16 => new Guid(bytes),
                var value => Guid.Parse(value?.ToString() ?? string.Empty),
            };
            expirations[index] = (isDeadLetter
                ? broker.RenewDeadLetterLock(topicName, subscriptionName, token)
                : broker.RenewLock(topicName, subscriptionName, token)).UtcDateTime;
        }

        return new Map { ["expirations"] = expirations };
    }

    private Map RenewSession(string topicName, string subscriptionName, Map body, string owner)
    {
        var sessionId = RequiredString(body, "session-id");
        return new Map { ["expiration"] = broker.RenewSessionLock(topicName, subscriptionName, sessionId, owner).UtcDateTime };
    }

    private Map GetSessionState(string topicName, string subscriptionName, Map body, string owner)
    {
        var state = broker.GetSessionState(topicName, subscriptionName, RequiredString(body, "session-id"), owner);
        return new Map { ["session-state"] = state?.ToArray() };
    }

    private Map SetSessionState(string topicName, string subscriptionName, Map body, string owner)
    {
        var sessionId = RequiredString(body, "session-id");
        var state = GetValue(body, "session-state") as byte[];
        broker.SetSessionState(topicName, subscriptionName, sessionId, owner, state);
        return new Map();
    }

    private Map Peek(string topicName, string subscriptionName, Map body, bool isDeadLetter)
    {
        var from = Convert.ToInt64(GetValue(body, "from-sequence-number") ?? 0, System.Globalization.CultureInfo.InvariantCulture);
        var count = Convert.ToInt32(GetValue(body, "message-count") ?? 1, System.Globalization.CultureInfo.InvariantCulture);
        var sessionId = GetValue(body, "session-id")?.ToString();
        var messages = (isDeadLetter
                ? broker.PeekDeadLetter(topicName, subscriptionName, from, count)
                : broker.Peek(topicName, subscriptionName, from, count, sessionId))
            .Select(message =>
            {
                var encoded = AmqpMessageConverter.ToAmqp(message).Encode();
                var bytes = new byte[encoded.Length];
                Array.Copy(encoded.Buffer, encoded.Offset, bytes, 0, encoded.Length);
                return (object)new Map { ["message"] = bytes };
            })
            .ToList();
        return new Map { ["messages"] = messages };
    }

    private Map UpdateDisposition(string topicName, string subscriptionName, Map body)
    {
        var status = RequiredString(body, "disposition-status");
        if (string.Equals(status, "defered", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Service Bus deferral is outside Spec 027 section 3.");
        }

        if (GetValue(body, "lock-tokens") is not System.Collections.IEnumerable values)
        {
            throw new FormatException("update-disposition requires lock-tokens.");
        }

        foreach (var value in values)
        {
            var token = value switch
            {
                Guid guid => guid,
                byte[] bytes when bytes.Length == 16 => new Guid(bytes),
                _ => Guid.Parse(value?.ToString() ?? string.Empty),
            };
            switch (status)
            {
                case "completed":
                    broker.CompleteByLockToken(topicName, subscriptionName, token);
                    break;
                case "abandoned":
                    broker.AbandonByLockToken(topicName, subscriptionName, token);
                    break;
                case "suspended":
                    broker.DeadLetterByLockToken(
                        topicName,
                        subscriptionName,
                        token,
                        GetValue(body, "deadletter-reason")?.ToString(),
                        GetValue(body, "deadletter-description")?.ToString());
                    break;
                default:
                    throw new NotSupportedException($"Disposition status '{status}' is outside Spec 027.");
            }
        }

        return new Map();
    }

    private static Message Response(int statusCode, string description, Map? body = null, string? errorCondition = null)
    {
        var response = new Message
        {
            ApplicationProperties = new ApplicationProperties(),
            BodySection = new AmqpValue { Value = body ?? new Map() },
        };
        response.ApplicationProperties[ManagementStatusCode] = statusCode;
        response.ApplicationProperties["statusDescription"] = description;
        if (errorCondition is not null)
        {
            response.ApplicationProperties["errorCondition"] = new Symbol(errorCondition);
        }
        return response;
    }

    private static string ErrorCondition(string? operation) => operation switch
    {
        "cancel-scheduled-message" => "com.microsoft:message-not-found",
        "renew-session-lock" or "get-session-state" or "set-session-state" => "com.microsoft:session-lock-lost",
        _ => "com.microsoft:message-lock-lost",
    };

    private static object? GetApplicationProperty(Message message, string key) =>
        message.ApplicationProperties?.Map.TryGetValue(key, out var value) == true ? value : null;

    private static bool IsSessionOperation(string? operation) => operation is
        "renew-session-lock" or "get-session-state" or "set-session-state";

    private static object? GetValue(Map map, string key)
    {
        foreach (var pair in map)
        {
            if (string.Equals(pair.Key.ToString(), key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static string RequiredString(Map map, string key) =>
        GetValue(map, key)?.ToString() ?? throw new FormatException($"The '{key}' value is required.");

    private static void ParseSubscriptionPath(string entityPath, out string topicName, out string subscriptionName)
    {
        const string separator = "/Subscriptions/";
        var index = entityPath.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
        if (index < 1)
        {
            throw new FormatException($"Management entity '{entityPath}' is not a subscription path.");
        }

        topicName = entityPath[..index];
        subscriptionName = entityPath[(index + separator.Length)..];
    }
}
