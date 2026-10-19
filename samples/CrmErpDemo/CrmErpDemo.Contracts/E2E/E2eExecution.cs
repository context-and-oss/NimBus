using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.Core.Pipeline;
using NimBus.Core.Extensions;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Extensions;

namespace CrmErpDemo.Contracts.E2E;

/// <summary>Opt-in probes around real demo handlers; no changes to the platform pipeline.</summary>
public static class E2eExecution
{
    /// <summary>Decorates the existing typed handlers after normal subscriber registration.</summary>
    public static void AddE2eExecution(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment, string apiUrl)
    {
        if (!E2eSettings.IsEnabled(configuration, environment)) return;
        services.AddHttpClient<E2eControlClient>(client =>
        {
            client.BaseAddress = new Uri(apiUrl);
            client.DefaultRequestHeaders.Add(E2eSettings.Header, configuration["E2E:Key"]);
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddSingleton<IFailureDispositionClassifier, E2eClassifier>();
        services.AddSingleton<IRetryPolicyProvider>(new DefaultRetryPolicyProvider().AddExceptionRule(
            "E2E configured retry", new RetryPolicy
            {
                MaxRetries = 2,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.FromSeconds(2),
                MaxDelay = TimeSpan.FromSeconds(2),
            }));
        foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType.IsGenericType &&
            descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IEventHandler<>)).ToArray())
        {
            var implementation = descriptor.ImplementationType
                ?? throw new InvalidOperationException("E2E decorators require a typed sample handler registration.");
            var wrapper = typeof(E2eHandler<>).MakeGenericType(descriptor.ServiceType.GenericTypeArguments);
            services.Remove(descriptor);
            services.Add(ServiceDescriptor.Describe(descriptor.ServiceType, provider =>
                ActivatorUtilities.CreateInstance(provider, wrapper, ActivatorUtilities.CreateInstance(provider, implementation)), descriptor.Lifetime));
        }
    }
}

/// <summary>Reads a session script from the corresponding business API.</summary>
public sealed class E2eControlClient(HttpClient http)
{
    /// <summary>Unregistered or non-GUID sessions pass through without a fault.</summary>
    public async Task<string> AttemptAsync(string sessionId, string eventType, string stage, string messageId, string eventId, string? origin, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(sessionId, out var session)) return "continue";
        using var response = await http.PostAsJsonAsync($"/api/e2e/sessions/{session}/attempt",
            new E2eEndpoints.AttemptRequest(eventType, stage, messageId, eventId, origin), cancellationToken);
        response.EnsureSuccessStatusCode();
        var attempt = await response.Content.ReadFromJsonAsync<E2eAttempt>(cancellationToken);
        return attempt?.Action ?? throw new InvalidOperationException("Missing E2E attempt response.");
    }
}

internal sealed class E2eHandler<T>(IEventHandler<T> inner, E2eControlClient control) : IEventHandler<T> where T : IEvent
{
    public async Task Handle(T message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        var action = await control.AttemptAsync(context.SessionId, context.EventType, "handler", context.MessageId,
            context.EventId, context.OriginatingMessageId, cancellationToken);
        if (action.StartsWith("pending", StringComparison.Ordinal))
        {
            context.MarkPendingHandoff("E2E external work", $"e2e-{context.EventId}", TimeSpan.FromSeconds(1));
            if (action == "pending-twice") context.MarkPendingHandoff("E2E final declaration", $"final-{context.EventId}");
            if (action != "pending-throw") return;
        }
        if (action == "transient") throw new TransientException("E2E transient failure");
        if (action != "continue") throw new E2eFailureException(action);
        await inner.Handle(message, context, cancellationToken);
    }
}

internal sealed class E2eFailureException(string action)
    : Exception(action == "retry" ? "E2E configured retry" : $"E2E {action}")
{
    public string Action { get; } = action;
}

internal sealed class E2eClassifier : IFailureDispositionClassifier
{
    public FailureDisposition Classify(Exception exception, string eventTypeId, string? endpointName) => exception switch
    {
        E2eFailureException { Action: "permanent" } => FailureDisposition.DeadLetter,
        E2eFailureException { Action: "discard" } => FailureDisposition.Discard,
        _ => FailureDisposition.Retry,
    };
}

/// <summary>Session-scoped faults appended to the sample's existing pipeline in the E2E profile.</summary>
public sealed class E2eMiddleware(E2eControlClient control) : IMessagePipelineBehavior
{
    /// <inheritdoc />
    public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default)
    {
        if (context.MessageType is MessageType.EventRequest or MessageType.RetryRequest or MessageType.ResubmissionRequest)
        {
            var action = await control.AttemptAsync(context.SessionId, context.EventTypeId, "middleware", context.MessageId,
                context.EventId, context.OriginatingMessageId, cancellationToken);
            if (action == "validation")
            {
                await context.DeadLetter("E2E validation rejection", cancellationToken: cancellationToken);
                throw new MessageAlreadyDeadLetteredException("E2E validation rejection");
            }
            if (action != "continue") throw new E2eFailureException(action);
        }
        await next(context, cancellationToken);
    }
}
