using System;
using System.Linq;
using NimBus.MessageStore;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services.ApplicationInsights;
using NimBus.Core.Events;
using Event = NimBus.WebApp.ManagementApi.Event;
using NimBus.MessageStore.States;
using System.Collections;
using System.Collections.Generic;
using TechnicalContact = NimBus.WebApp.ManagementApi.TechnicalContact;
using ResolutionStatus = NimBus.MessageStore.ResolutionStatus;
using HeartbeatSettings = NimBus.WebApp.ManagementApi.HeartbeatSettings;
using StoreHeartbeatSettings = NimBus.MessageStore.States.HeartbeatSettings;

namespace NimBus.WebApp.Controllers;

public static class Mapper
{
    public static Event EventFromMessageStoreEvent(UnresolvedEvent @event)
    {
        var valid = Enum.TryParse(@event.ResolutionStatus.ToString(), true, out ResolutionStatus status);
        if (!valid)
        {
            throw new ArgumentOutOfRangeException(
                $"Value '{@event.ResolutionStatus}' is unknown for type of {nameof(Event.ResolutionStatus)}");
        }

        return new Event()
        {
            UpdatedAt = @event.UpdatedAt,
            EnqueuedTimeUtc = @event.EnqueuedTimeUtc,
            EventId = @event.EventId,
            SessionId = @event.SessionId,
            CorrelationId = @event.CorrelationId,
            ResolutionStatus = @event.ResolutionStatus.ToString(),
            EndpointRole = @event.EndpointRole.ToString(),
            EndpointId = @event.EndpointId,
            RetryCount = @event.RetryCount,
            RetryLimit = @event.RetryLimit,
            QueueTimeMs = @event.QueueTimeMs,
            ProcessingTimeMs = @event.ProcessingTimeMs,
            MessageType = @event.MessageType.ToString(),
            DeadLetterReason = @event.DeadLetterReason,
            DeadLetterErrorDescription = @event.DeadLetterErrorDescription,
            LastMessageId = @event.LastMessageId,
            OriginatingMessageId = @event.OriginatingMessageId,
            ParentMessageId = @event.ParentMessageId,
            Reason = @event.Reason,
            OriginatingFrom = @event.OriginatingFrom,
            EventTypeId = @event.EventTypeId,
            To = @event.To,
            From = @event.From,
            MessageContent = new MessageContent
            {
                ErrorContent = new ErrorContent
                {
                    ErrorText = @event.MessageContent?.ErrorContent?.ErrorText,
                    ErrorType = @event.MessageContent?.ErrorContent?.ErrorType,
                    ExceptionSource = @event.MessageContent?.ErrorContent?.ExceptionSource,
                    ExceptionStackTrace = @event.MessageContent?.ErrorContent?.ExceptionStackTrace,
                },
                EventContent = new EventContent
                {
                    EventJson = @event.MessageContent?.EventContent?.EventJson,
                    EventTypeId = @event.MessageContent?.EventContent?.EventTypeId,
                }
            },
            PendingSubStatus = @event.PendingSubStatus,
            HandoffReason = @event.HandoffReason,
            ExternalJobId = @event.ExternalJobId,
            ExpectedBy = @event.ExpectedBy,
        };
    }

    public static Message MessageFromMessageEntity(MessageEntity messageEntity)
    {
        var message = new Message
        {
            EventId = messageEntity.EventId,
            MessageId = messageEntity.MessageId,
            SessionId = messageEntity.SessionId,
            CorrelationId = messageEntity.CorrelationId,
            OriginatingMessageId = messageEntity.OriginatingMessageId,
            ParentMessageId = messageEntity.ParentMessageId,
            EndpointId = messageEntity.EndpointId,
            EndpointRole = messageEntity.EndpointRole == EndpointRole.Publisher
                ? MessageEndpointRole.Publisher
                : MessageEndpointRole.Subscriber,
            To = messageEntity.To,
            From = messageEntity.From,
            OriginatingFrom = messageEntity.OriginatingFrom,
            EnqueuedTimeUtc = messageEntity.EnqueuedTimeUtc,

            EventTypeId = !string.IsNullOrWhiteSpace(messageEntity.EventTypeId)
                ? messageEntity.EventTypeId
                : messageEntity.MessageContent?.EventContent?.EventTypeId,
        };
        //Temporary fix, when messageType is not present
        try
        {
            message.MessageType = Enum.Parse<MessageType>(messageEntity.MessageType.ToString());
        }
        catch (Exception)
        {
            message.MessageType = MessageType.Unknown;
        }


        if (!string.IsNullOrEmpty(messageEntity.MessageContent?.EventContent?.EventJson))
            message.EventContent = messageEntity.MessageContent?.EventContent?.EventJson;

        if (messageEntity.MessageContent?.ErrorContent != null)
            message.ErrorContent = MessageErrorContentFromErroryContent(messageEntity.MessageContent.ErrorContent);

        message.PendingSubStatus = messageEntity.PendingSubStatus;
        message.HandoffReason = messageEntity.HandoffReason;
        message.ExternalJobId = messageEntity.ExternalJobId;
        message.ExpectedBy = messageEntity.ExpectedBy;

        return message;
    }

    /// <param name="includeExamplePayload">
    /// Serialize the authored example into the response. Off by default: only the
    /// single event-type details page reads it, while the same mapper feeds the
    /// catalog list, the per-endpoint grouping, topology and the command palette —
    /// responses that would otherwise carry every event's full example, repeatedly,
    /// for nothing.
    /// </param>
    public static ManagementApi.EventType EventTypeFromIEventType(
        IEventType eventType,
        bool includeExamplePayload = false)
    {
        return new ManagementApi.EventType()
        {
            Id = eventType.Id,
            Name = eventType.Name,
            Description = eventType.Description,
            Namespace = eventType.Namespace,
            Properties = (eventType.Properties ?? Enumerable.Empty<IProperty>())
                .Select(x => EventPropertyFromEventTypeProperty(x))
                .ToList(),
            ExamplePayload = includeExamplePayload ? ExamplePayloadFromIEventType(eventType) : null,
        };
    }

    /// <summary>
    /// The authored <c>static Example</c> on the event class as indented JSON, or null when
    /// the class declares none. A class whose example cannot be read or serialized is treated
    /// as having none: the catalog page must still render, and the client has a placeholder.
    /// </summary>
    public static string? ExamplePayloadFromIEventType(IEventType eventType)
    {
        try
        {
            var example = eventType.GetEventExample();
            return example is null
                ? null
                : Newtonsoft.Json.JsonConvert.SerializeObject(example, Newtonsoft.Json.Formatting.Indented);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <inheritdoc cref="EventTypeFromIEventType(IEventType, bool)" path="/param[@name='includeExamplePayload']"/>
    public static ManagementApi.EventType EventTypeFromIEventType(
        IEventType eventType,
        int producerCount,
        int consumerCount,
        IEnumerable<string>? producers = null,
        IEnumerable<string>? consumers = null,
        bool includeExamplePayload = false)
    {
        return new ManagementApi.EventType()
        {
            Id = eventType.Id,
            Name = eventType.Name,
            Description = eventType.Description,
            Namespace = eventType.Namespace,
            Properties = (eventType.Properties ?? Enumerable.Empty<IProperty>())
                .Select(x => EventPropertyFromEventTypeProperty(x))
                .ToList(),
            ExamplePayload = includeExamplePayload ? ExamplePayloadFromIEventType(eventType) : null,
            ProducerCount = producerCount,
            ConsumerCount = consumerCount,
            Producers = (producers ?? Enumerable.Empty<string>()).ToList(),
            Consumers = (consumers ?? Enumerable.Empty<string>()).ToList(),
        };
    }

    private static EventTypeProperty EventPropertyFromEventTypeProperty(IProperty property)
    {
        return new EventTypeProperty()
        {
            Name = property.Name,
            Description = property.Description,
            IsRequired = property.IsRequired,
            TypeFullName = property.TypeFullName,
            TypeName = property.TypeName
        };
    }

    internal static MessageAudit MessageAuditFromMessageAuditEntity(MessageAuditEntity audit)
    {
        return new MessageAudit()
        {
            AuditorName = audit.AuditorName,
            AuditTimestamp = audit.AuditTimestamp,
            AuditType = Enum.Parse<MessageAuditAuditType>(audit.AuditType.ToString())
        };
    }

    internal static MessageAuditEntity MessageAuditEntityFromMessageAudit(MessageAudit audit)
    {
        return new MessageAuditEntity()
        {
            AuditorName = audit.AuditorName,
            AuditTimestamp = audit.AuditTimestamp,
            AuditType = Enum.Parse<MessageAuditType>(audit.AuditType.ToString()),
            Comment = audit.Comment
        };
    }

    internal static SessionStatus SessionStatusFromSessionState(SessionStateCount sessionState)
    {
        return new SessionStatus
        {
            SessionId = sessionState.SessionId,
            DeferredEvents = (sessionState.DeferredEvents ?? Enumerable.Empty<string>()).ToList(),
            PendingEvents = (sessionState.PendingEvents ?? Enumerable.Empty<string>()).ToList()
        };
    }

    internal static MessageErrorContent MessageErrorContentFromErroryContent(MessageEntity message)
    {
        return new MessageErrorContent
        {
            ErrorText = message.MessageType == Core.Messages.MessageType.UnsupportedResponse
                ? "This message is unsupported by the Subscriber"
                : "Deadlettered Event",
            ErrorType = message.MessageType.ToString(),
            ExceptionSource = !string.IsNullOrEmpty(message.DeadLetterErrorDescription)
                ? message.DeadLetterErrorDescription
                : "Mismatching nuget versions. Subscriber does not have the event created from the Publisher",
            ExceptionStackTrace = !string.IsNullOrEmpty(message.DeadLetterReason)
                ? message.DeadLetterReason
                : "In order to fix this issue: upgrade EET.EIP nuget, to the same version as the Publisher"
        };
    }

    private static MessageErrorContent MessageErrorContentFromErroryContent(Core.Messages.ErrorContent errorContent)
    {
        return new MessageErrorContent
        {
            ErrorText = errorContent.ErrorText ?? "",
            ErrorType = errorContent.ErrorType ?? "",
            ExceptionSource = !string.IsNullOrEmpty(errorContent.ExceptionSource)
                ? errorContent.ExceptionSource
                : "",
            ExceptionStackTrace = !string.IsNullOrEmpty(errorContent.ExceptionStackTrace)
                ? errorContent.ExceptionStackTrace
                : ""
        };
    }

    // Pending+Handoff entries (PendingSubStatus = "Handoff") roll into PendingCount
    // automatically because their ResolutionStatus is Pending — no change needed
    // here for the FR-040 dashboard-count requirement.
    internal static EndpointStatusCount EndpointStatusCountFromEndpointStateCount(EndpointStateCount state)
    {
        return new EndpointStatusCount
        {
            EndpointId = state.EndpointId,
            EventTime = state.EventTime,
            OldestFailureAt = state.OldestFailureAt,
            DeferredCount = state.DeferredCount,
            PendingCount = state.PendingCount + state.UnsupportedCount,
            UnsupportedCount = state.UnsupportedCount,
            DeadletterCount = state.DeadletterCount,
            FailedCount = state.FailedCount + state.DeadletterCount,
            StorageStatus = "ok"
        };
    }

    internal static EventLogEntry EventLogEntryFromLogEntry(LogEntry log)
    {
        return new EventLogEntry
        {
            EventId = log.EventId,
            MessageId = log.MessageId,
            SessionId = log.SessionId,
            CorrelationId = log.CorrelationId,
            To = log.To,
            From = log.From,
            Text = log.Text,
            EventType = log.EventType,
            IsDeferred = log.IsDeferred,
            MessageType = log.MessageType,
            Payload = log.Payload,
            SeverityLevel = (EventLogEntrySeverityLevel)log.SeverityLevel,
            TimeStamp = log.Timestamp
        };
    }

    public static EndpointStatus EndpointStatusFromEndpointState(EndpointState state)
    {
        var events = new UnresolvedEvents();
        foreach (var e in state.EnrichedUnresolvedEvents ?? Enumerable.Empty<UnresolvedEvent>())
        {
            events.Add(EventFromMessageStoreEvent(e));
        }

        return new EndpointStatus
        {
            EndpointId = state.EndpointId,
            EventTime = state.EventTime,
            DeferredEvents = (state.DeferredEvents ?? Enumerable.Empty<string>()).ToList(),
            FailedEvents = (state.FailedEvents ?? Enumerable.Empty<string>()).ToList(),
            PendingEvents = (state.PendingEvents ?? Enumerable.Empty<string>()).ToList(),
            EnrichedUnresolvedEvents = events,
            UnsupportedEvents = (state.UnsupportedEvents ?? Enumerable.Empty<string>()).ToList(),
            DeadletteredEvents = (state.DeadletteredEvents ?? Enumerable.Empty<string>()).ToList(),
            ContinuationToken = String.IsNullOrEmpty(state.ContinuationToken) ? "" : state.ContinuationToken
        };
    }

    public static BlockedEvent BlockedEventFromBlockedMessageEvent(BlockedMessageEvent blockedMessageEvent)
    {
        return new BlockedEvent
        {
            EventId = blockedMessageEvent.EventId,
            OriginatingId = blockedMessageEvent.OriginatingId,
            Status = blockedMessageEvent.Status
        };
    }

    public static PendingEvent PendingEventFromBlockedMessageEvent(BlockedMessageEvent pendingMessageEvent)
    {
        return new PendingEvent
        {
            EventId = pendingMessageEvent.EventId,
            OriginatingId = pendingMessageEvent.OriginatingId,
            Status = pendingMessageEvent.Status
        };
    }

    public static List<ManagementApi.EndpointSubscription> SubscriptionsFromEndpointsubscriptions(
        IEnumerable<MessageStore.States.EndpointSubscription>? endpointSubscriptions)
    {
        var returnList = new List<ManagementApi.EndpointSubscription>();
        foreach (var sub in endpointSubscriptions ?? Enumerable.Empty<MessageStore.States.EndpointSubscription>())
        {
            returnList.Add(new ManagementApi.EndpointSubscription()
            {
                Type = sub.Type,
                NotificationSeverity = sub.NotificationSeverity,
                Mail = sub.Mail,
                AuthorId = sub.AuthorId,
                Url = sub.Url,
                EventTypes = sub.EventTypes,
                Payload = sub.Payload,
                Frequency = sub.Frequency,
                Id = sub.Id,
            });
        }

        return returnList;
    }

    public static MessageStore.EventFilter MapFilter(ManagementApi.EventFilter? filter)
    {
        if (filter == null)
        {
            return new MessageStore.EventFilter();
        }

        List<string>? status = null;
        if (filter.ResolutionStatus != null)
        {
            status = new List<string>();
            filter.ResolutionStatus.ForEach(rs => status.Add(rs.ToString()));
        }


        return new MessageStore.EventFilter
        {
            EndPointId = filter.EndpointId,
            EventId = filter.EventId,
            EventTypeId = filter.EventTypeId,
            SessionId = filter.SessionId,
            ResolutionStatus = status,
            EnqueuedAtFrom = filter.EnqueuedAtFrom,
            EnqueuedAtTo = filter.EnqueuedAtTo,
            UpdatedAtFrom = filter.UpdateAtFrom,
            UpdatedAtTo = filter.UpdatedAtTo,

            Payload = filter.Payload,
        };
    }

    public static Metadata MetadataFromEndpointMetadata(EndpointMetadata endpointMetadata)
    {
        var technicalContacts = new List<TechnicalContact>();
        if (endpointMetadata.TechnicalContacts != null)
        {
            technicalContacts = endpointMetadata.TechnicalContacts
                .Select(technicalContact => new TechnicalContact()
                    { Name = technicalContact.Name, Email = technicalContact.Email }).ToList();
        }

        return new Metadata
        {
            Id = endpointMetadata.EndpointId,
            EndpointOwner = endpointMetadata.EndpointOwner,
            EndpointOwnerTeam = endpointMetadata.EndpointOwnerTeam,
            EndpointOwnerEmail = endpointMetadata.EndpointOwnerEmail,
            TechnicalContacts = technicalContacts,
        };
    }

    public static IEnumerable<MetadataShort> MetadataShortFromList(List<EndpointMetadata>? endpointMetadatas)
    {
        return (endpointMetadatas ?? Enumerable.Empty<EndpointMetadata>()).Select(metadata => new MetadataShort()
        {
            EndpointId = metadata.EndpointId,
            SubscriptionStatus = SubscriptionStatusFromMetadata(metadata),
        });
    }

    public static MetadataShort MetadataShortFromMetadata(EndpointMetadata metadata)
    {
        return new MetadataShort()
        {
            EndpointId = metadata.EndpointId,
            SubscriptionStatus = SubscriptionStatusFromMetadata(metadata),
        };
    }

    /// <summary>Projects the stored heartbeat schedule onto the API contract.</summary>
    public static HeartbeatSettings HeartbeatSettingsFromState(StoreHeartbeatSettings settings)
    {
        return new HeartbeatSettings
        {
            Enabled = settings.Enabled,
            IntervalSeconds = settings.IntervalSeconds,
            TimeoutSeconds = settings.TimeoutSeconds,
            LastSentAtUtc = settings.LastSentAtUtc,
        };
    }

    /// <summary>
    /// Reads a schedule out of a request body. <c>LastSentAtUtc</c> is deliberately
    /// not carried across: it is owned by the send claim, and the store keeps its
    /// own value whenever a write omits one — so a client cannot rewind the
    /// schedule by echoing back a stale timestamp.
    /// </summary>
    public static StoreHeartbeatSettings HeartbeatSettingsToState(HeartbeatSettings body)
    {
        return new StoreHeartbeatSettings
        {
            Enabled = body.Enabled,
            IntervalSeconds = body.IntervalSeconds,
            TimeoutSeconds = body.TimeoutSeconds,
        };
    }

    /// <summary>Projects one heartbeat overview row onto the API contract.</summary>
    public static HeartbeatOverviewRow HeartbeatOverviewRowFromItem(HeartbeatOverviewItem item)
    {
        return new HeartbeatOverviewRow
        {
            EndpointId = item.EndpointId,
            IsHeartbeatEnabled = item.IsHeartbeatEnabled,
            Status = item.Status.ToString(),
            LastStartTime = item.LastStartTime,
            LastReceivedTime = item.LastReceivedTime,
            LastEndTime = item.LastEndTime,
            RoundTripMs = item.RoundTripMs,
            SdkVersion = item.SdkVersion,
        };
    }

    /// <summary>Projects one platform-service liveness row onto the API contract.</summary>
    public static ServiceHealthRow ServiceHealthRowFromState(ServiceHealth serviceHealth)
    {
        return new ServiceHealthRow
        {
            ServiceId = serviceHealth.ServiceId,
            Status = serviceHealth.Status.ToString(),
            Version = serviceHealth.Version,
            LastProbeSentUtc = serviceHealth.LastProbeSentUtc,
            LastSeenUtc = serviceHealth.LastSeenUtc,
            RoundTripMs = serviceHealth.RoundTripMs,
            // Status only ever holds a settled outcome, so "waiting on an answer"
            // has to be read off the outstanding claim rather than off Status.
            ProbeInFlight = serviceHealth.LastProbeMessageId != null,
        };
    }

    internal static string SubscriptionStatusFromMetadata(EndpointMetadata metadata)
    {
        if (!metadata.SubscriptionStatus.HasValue)
        {
            return "not-found";
        }

        return metadata.SubscriptionStatus.Value ? "active" : "disabled";
    }
}
