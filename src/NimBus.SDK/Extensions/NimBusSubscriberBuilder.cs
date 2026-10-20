using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NimBus.Core.Events;
using NimBus.Core.CircuitBreaker;
using NimBus.Core.Inbox;
using NimBus.Core.Messages;
using NimBus.SDK.EventHandlers;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;

namespace NimBus.SDK.Extensions;

/// <summary>
/// Builder for configuring NimBus subscriber handlers and retry policies via DI.
/// </summary>
public class NimBusSubscriberBuilder
{
    internal readonly IServiceCollection Services;
    internal readonly List<HandlerRegistration> HandlerRegistrations = new();
    internal Action<DefaultRetryPolicyProvider>? RetryPolicyConfigurator;
    internal InboxOptions? InboxConfiguration { get; private set; }
    internal CircuitBreakerOptions? CircuitBreakerConfiguration { get; private set; }

    public NimBusSubscriberBuilder(IServiceCollection services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>
    /// Registers an event handler. The handler is resolved from DI per message.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <typeparam name="THandler">The handler implementation type.</typeparam>
    public NimBusSubscriberBuilder AddHandler<TEvent, THandler>()
        where TEvent : IEvent
        where THandler : class, IEventHandler<TEvent>
    {
        AddHandlerRegistration(typeof(TEvent), typeof(THandler), explicitRegistration: true);

        return this;
    }

    /// <summary>
    /// Registers a request handler for the request/reply pattern. The handler is resolved
    /// from DI per message; its response is JSON-serialized and sent to the requester's
    /// <c>{endpoint}-reply</c> subscription when the inbound message carries a
    /// <c>ReplyTo</c> address (see <see cref="Core.Messages.IReplyDispatcher"/>).
    /// When the request type is published as a plain event (no <c>ReplyTo</c>), the
    /// handler still runs and the response is discarded.
    /// </summary>
    /// <typeparam name="TRequest">The request event type.</typeparam>
    /// <typeparam name="TResponse">The response type (serialized as JSON).</typeparam>
    /// <typeparam name="THandler">The handler implementation type.</typeparam>
    public NimBusSubscriberBuilder AddRequestHandler<TRequest, TResponse, THandler>()
        where TRequest : IEvent
        where TResponse : class
        where THandler : class, IRequestHandler<TRequest, TResponse>
    {
        var eventTypeId = new EventType(typeof(TRequest)).Id;
        var existing = HandlerRegistrations.SingleOrDefault(r => r.EventTypeId == eventTypeId);
        if (existing != null)
        {
            throw new InvalidOperationException(
                $"EventTypeId '{eventTypeId}' is already registered as a " +
                $"{(existing.EventType is null ? "dynamic" : "typed")} handler " +
                $"('{existing.HandlerType?.FullName ?? "<dynamic>"}'); " +
                $"cannot also register a request handler ('{typeof(THandler).FullName}') for it. " +
                "Register only one handler per EventTypeId.");
        }

        Services.AddTransient<IRequestHandler<TRequest, TResponse>, THandler>();

        HandlerRegistrations.Add(new HandlerRegistration
        {
            EventTypeId = eventTypeId,
            EventType = typeof(TRequest),
            HandlerType = typeof(THandler),
            IsExplicit = true,
            Register = (provider, handlerProvider) =>
            {
                var replyDispatcher = provider.GetRequiredService<IReplyDispatcher>();
                handlerProvider.RegisterHandler(
                    eventTypeId,
                    (IServiceProvider? scopedProvider) => new RequestJsonHandler<TRequest, TResponse>(
                        (scopedProvider ?? provider).GetRequiredService<IRequestHandler<TRequest, TResponse>>(),
                        replyDispatcher));
            }
        });

        return this;
    }

    /// <summary>
    /// Registers all concrete <see cref="IEventHandler{T}"/> implementations from
    /// the assembly containing <typeparamref name="TMarker"/>.
    /// </summary>
    public NimBusSubscriberBuilder AddHandlersFromAssemblyContaining<TMarker>()
    {
        return AddHandlersFromAssembly(typeof(TMarker).Assembly);
    }

    /// <summary>
    /// Registers all concrete <see cref="IEventHandler{T}"/> implementations from the specified assembly.
    /// </summary>
    public NimBusSubscriberBuilder AddHandlersFromAssembly(Assembly assembly)
    {
        if (assembly == null) throw new ArgumentNullException(nameof(assembly));

        foreach (var registration in DiscoverHandlerRegistrations(assembly))
        {
            AddHandlerRegistration(registration.EventType, registration.HandlerType, explicitRegistration: false);
        }

        return this;
    }

    /// <summary>
    /// Registers all concrete <see cref="IEventHandler{T}"/> implementations from the specified assemblies.
    /// </summary>
    public NimBusSubscriberBuilder AddHandlersFromAssemblies(params Assembly[] assemblies)
    {
        if (assemblies == null) throw new ArgumentNullException(nameof(assemblies));

        foreach (var assembly in assemblies)
        {
            AddHandlersFromAssembly(assembly);
        }

        return this;
    }

    /// <summary>
    /// Registers a handler for a dynamically-typed event keyed by its string EventTypeId
    /// (e.g. "crm.contact.enriched.v1") with no compiled IEvent class. The factory typically
    /// returns a DelegateEventJsonHandler reading context.MessageContent.EventContent.EventJson.
    /// Spec 022 — the hosted-subscriber counterpart to AddHandler&lt;TEvent,THandler&gt;.
    /// </summary>
    /// <param name="eventTypeId">The wire EventTypeId string (e.g. "crm.contact.enriched.v1"). Must not be null or whitespace.</param>
    /// <param name="handlerFactory">Factory invoked per message to create the handler. Must not be null.</param>
    public NimBusSubscriberBuilder AddDynamicHandler(string eventTypeId, Func<IEventJsonHandler> handlerFactory)
    {
        return AddDynamicRegistration(
            eventTypeId,
            handlerFactory,
            (_, handlerProvider) => handlerProvider.RegisterHandler(eventTypeId, handlerFactory));
    }

    /// <summary>
    /// Registers a handler for a dynamically-typed event keyed by its string EventTypeId,
    /// with a DI-aware factory that receives the root <see cref="IServiceProvider"/> at
    /// registration time. This is the DI-integrated counterpart to
    /// <see cref="AddDynamicHandler(string, Func{IEventJsonHandler})"/> — the
    /// <paramref name="handlerFactory"/> is called once when the
    /// <see cref="ISubscriberClient"/> singleton is resolved, so the same handler instance
    /// processes every message. Use
    /// <see cref="AddScopedDynamicHandler(string, Func{IServiceProvider, IEventJsonHandler})"/>
    /// when the handler requires scoped dependencies.
    /// </summary>
    /// <param name="eventTypeId">The wire EventTypeId string (e.g. "crm.contact.enriched.v1"). Must not be null or whitespace.</param>
    /// <param name="handlerFactory">Factory that receives the DI container and returns the handler. Must not be null.</param>
    public NimBusSubscriberBuilder AddDynamicHandler(string eventTypeId, Func<IServiceProvider, IEventJsonHandler> handlerFactory)
    {
        return AddDynamicRegistration(
            eventTypeId,
            handlerFactory,
            (provider, handlerProvider) =>
            {
                var handler = handlerFactory(provider);
                handlerProvider.RegisterHandler(eventTypeId, () => handler);
            });
    }

    /// <summary>
    /// Registers a handler for a dynamically-typed event keyed by its string EventTypeId,
    /// resolving the handler from a new dependency-injection scope for each message. The
    /// scope and its dependencies are disposed after the handler completes.
    /// </summary>
    /// <param name="eventTypeId">The wire EventTypeId string (e.g. "crm.contact.enriched.v1"). Must not be null or whitespace.</param>
    /// <param name="handlerFactory">Factory that receives the per-message scoped service provider and returns the handler. Must not be null.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown during dispatch when the event handler provider has no dependency-injection scope factory.
    /// </exception>
    public NimBusSubscriberBuilder AddScopedDynamicHandler(
        string eventTypeId,
        Func<IServiceProvider, IEventJsonHandler> handlerFactory)
    {
        return AddDynamicRegistration(
            eventTypeId,
            handlerFactory,
            (_, handlerProvider) =>
                handlerProvider.RegisterHandler(
                    eventTypeId,
                    scopedProvider => handlerFactory(RequireScopedProvider(scopedProvider))));
    }

    /// <summary>
    /// Registers a single fallback handler invoked for any event type with no specific
    /// handler. The factory is called once when the <see cref="ISubscriberClient"/> singleton
    /// is resolved, so the same handler instance processes every unmatched message. The
    /// Mapping Executor uses this to consult the mapping registry per message (spec 023).
    /// Use <see cref="AddScopedDynamicFallbackHandler(Func{IServiceProvider, IEventJsonHandler})"/>
    /// when the handler requires scoped dependencies.
    /// </summary>
    /// <param name="handlerFactory">Factory that receives the DI container and returns the handler. Must not be null.</param>
    public NimBusSubscriberBuilder AddDynamicFallbackHandler(Func<IServiceProvider, IEventJsonHandler> handlerFactory)
    {
        if (handlerFactory == null) throw new ArgumentNullException(nameof(handlerFactory));

        HandlerRegistrations.Add(new HandlerRegistration
        {
            EventTypeId = null,
            EventType = null,
            HandlerType = null,
            IsExplicit = true,
            Register = (provider, handlerProvider) =>
            {
                var handler = handlerFactory(provider);
                handlerProvider.RegisterFallbackHandler(() => handler);
            }
        });

        return this;
    }

    /// <summary>
    /// Registers a single fallback handler invoked for any event type with no specific
    /// handler, resolving it from a new dependency-injection scope for each message. The
    /// scope and its dependencies are disposed after the handler completes.
    /// </summary>
    /// <param name="handlerFactory">Factory that receives the per-message scoped service provider and returns the handler. Must not be null.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown during dispatch when the event handler provider has no dependency-injection scope factory.
    /// </exception>
    public NimBusSubscriberBuilder AddScopedDynamicFallbackHandler(
        Func<IServiceProvider, IEventJsonHandler> handlerFactory)
    {
        if (handlerFactory == null) throw new ArgumentNullException(nameof(handlerFactory));

        HandlerRegistrations.Add(new HandlerRegistration
        {
            EventTypeId = null,
            EventType = null,
            HandlerType = null,
            IsExplicit = true,
            Register = (_, handlerProvider) =>
            {
                handlerProvider.RegisterFallbackHandler(
                    scopedProvider => handlerFactory(RequireScopedProvider(scopedProvider)));
            }
        });

        return this;
    }

    private NimBusSubscriberBuilder AddDynamicRegistration<THandlerFactory>(
        string eventTypeId,
        THandlerFactory handlerFactory,
        Action<IServiceProvider, EventHandlerProvider> register)
        where THandlerFactory : class
    {
        if (string.IsNullOrWhiteSpace(eventTypeId))
            throw new ArgumentException("Event type id must not be null or empty.", nameof(eventTypeId));
        if (handlerFactory == null) throw new ArgumentNullException(nameof(handlerFactory));

        // Symmetric to AddHandlerRegistration's dedup: two handlers (dynamic or
        // typed) claiming the same wire EventTypeId can't both win the dispatch
        // table, so fail loudly rather than let the later Register() overwrite
        // the earlier one at provider-construction time.
        var existing = HandlerRegistrations.SingleOrDefault(r => r.EventTypeId == eventTypeId);
        if (existing != null)
        {
            throw new InvalidOperationException(
                $"EventTypeId '{eventTypeId}' is already registered as a " +
                $"{(existing.EventType is null ? "dynamic" : "typed")} handler " +
                $"('{existing.HandlerType?.FullName ?? "<dynamic>"}'); " +
                "cannot also register a dynamic handler for it. " +
                "Register only one handler per EventTypeId.");
        }

        HandlerRegistrations.Add(new HandlerRegistration
        {
            EventTypeId = eventTypeId,
            EventType = null,
            HandlerType = null,
            IsExplicit = true,
            Register = register
        });

        return this;
    }

    private static IServiceProvider RequireScopedProvider(IServiceProvider? scopedProvider)
    {
        return scopedProvider ?? throw new InvalidOperationException(
            $"Scoped dynamic handlers require an {nameof(IServiceScopeFactory)}-configured event handler provider.");
    }

    /// <summary>
    /// Configures retry policies for this subscriber.
    /// </summary>
    public NimBusSubscriberBuilder ConfigureRetryPolicies(Action<DefaultRetryPolicyProvider> configure)
    {
        RetryPolicyConfigurator = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }

    /// <summary>Enables the endpoint circuit breaker for this subscriber.</summary>
    /// <param name="configure">Configures failure sampling, pause duration, and probes.</param>
    /// <returns>This builder for chaining.</returns>
    public NimBusSubscriberBuilder WithCircuitBreaker(Action<CircuitBreakerOptions> configure)
    {
        if (CircuitBreakerConfiguration is not null)
            throw new InvalidOperationException("A circuit breaker is already configured for this subscriber.");

        ArgumentNullException.ThrowIfNull(configure);
        var options = new CircuitBreakerOptions();
        configure(options);
        options.Validate();
        CircuitBreakerConfiguration = options;
        return this;
    }

    /// <summary>
    /// Enables record-on-success inbox deduplication for this subscriber.
    /// </summary>
    /// <param name="configure">Configures retention, cleanup frequency, and the explicitly selected keyed provider.</param>
    /// <returns>This builder for chaining.</returns>
    [SuppressMessage("Usage", "CA2208:Instantiate argument exceptions correctly", Justification = "ParamName names the invalid InboxOptions property, which callers and tests rely on.")]
    public NimBusSubscriberBuilder UseInbox(Action<InboxOptions> configure)
    {
        if (InboxConfiguration is not null)
            throw new InvalidOperationException("Inbox deduplication is already configured for this subscriber.");

        ArgumentNullException.ThrowIfNull(configure);
        var options = new InboxOptions();
        configure(options);

        if (options.DeduplicationStore is not { } provider)
        {
            throw new InvalidOperationException(
                $"{nameof(InboxOptions.DeduplicationStore)} must be selected explicitly when enabling the inbox.");
        }

        if (!Enum.IsDefined(typeof(InboxStore), provider))
        {
            throw new ArgumentOutOfRangeException(
                nameof(InboxOptions.DeduplicationStore),
                provider,
                "The selected inbox provider is not supported.");
        }

        InboxConfiguration = options;
        return this;
    }

    /// <summary>
    /// Registers the classifier that selects retry, dead-letter, or discard behavior
    /// for exceptions thrown by this subscriber's event handlers.
    /// </summary>
    /// <param name="classifier">The failure disposition classifier to register.</param>
    /// <returns>This builder for chaining.</returns>
    public NimBusSubscriberBuilder WithFailureDispositions(IFailureDispositionClassifier classifier)
    {
        Services.AddSingleton(classifier ?? throw new ArgumentNullException(nameof(classifier)));
        return this;
    }

    /// <summary>
    /// Configures a <see cref="DefaultPermanentFailureClassifier"/> as this subscriber's failure
    /// disposition classifier. Exceptions classified as permanent are dead-lettered immediately
    /// without consuming retry budget; every other failure is retried. A classifier registered
    /// with <see cref="WithFailureDispositions"/> takes precedence, whichever is called first.
    /// </summary>
    public NimBusSubscriberBuilder ConfigurePermanentFailureClassifier(Action<DefaultPermanentFailureClassifier> configure)
    {
        var classifier = new DefaultPermanentFailureClassifier();
        (configure ?? throw new ArgumentNullException(nameof(configure)))(classifier);

        var existing = Services.LastOrDefault(d => d.ServiceType == typeof(IFailureDispositionClassifier));
        if (existing is null)
            Services.AddSingleton<IFailureDispositionClassifier>(classifier);
        else if (existing.ImplementationInstance is DefaultPermanentFailureClassifier)
            Services.Replace(ServiceDescriptor.Singleton<IFailureDispositionClassifier>(classifier));
        return this;
    }

    private void AddHandlerRegistration(Type eventType, Type handlerType, bool explicitRegistration)
    {
        if (eventType == null) throw new ArgumentNullException(nameof(eventType));
        if (handlerType == null) throw new ArgumentNullException(nameof(handlerType));

        var expectedHandlerInterface = typeof(IEventHandler<>).MakeGenericType(eventType);
        if (!typeof(IEvent).IsAssignableFrom(eventType))
            throw new ArgumentException($"Event type '{eventType.FullName}' must implement {nameof(IEvent)}.", nameof(eventType));
        if (!expectedHandlerInterface.IsAssignableFrom(handlerType))
            throw new ArgumentException(
                $"Handler type '{handlerType.FullName}' must implement IEventHandler<{eventType.Name}>.",
                nameof(handlerType));

        // Dedupe on the wire EventTypeId, not on CLR-type identity: that's
        // what EventContextHandler keys its dispatch table on, so two
        // different CLR types sharing a name (e.g. A.OrderPlaced +
        // B.OrderPlaced) collide at runtime even though the CLR-type
        // equality check would treat them as distinct registrations.
        var eventTypeId = new EventType(eventType).Id;
        var existing = HandlerRegistrations.SingleOrDefault(r => r.EventTypeId == eventTypeId);
        if (existing != null)
        {
            if (existing.EventType is null)
            {
                // The existing entry is a DYNAMIC (string-keyed) registration —
                // AddDynamicHandler stores no CLR EventType. A dynamic handler
                // and a typed handler claiming the same wire EventTypeId is a
                // genuine conflict the dispatch table can't resolve, so fail
                // loudly rather than NRE on existing.EventType below.
                throw new InvalidOperationException(
                    $"EventTypeId '{eventTypeId}' is already registered as a dynamic handler; " +
                    $"cannot also register a typed handler ('{handlerType.FullName}') for it. " +
                    "Register only one handler per EventTypeId.");
            }
            if (existing.EventType != eventType)
            {
                // Distinct CLR types collapsing onto the same wire id —
                // unsupportable because the bus dispatches on EventTypeId.
                // Fail loudly at startup instead of letting one handler
                // silently overwrite the other.
                throw new InvalidOperationException(
                    $"Two distinct event types map to the same EventTypeId '{eventTypeId}': " +
                    $"'{existing.EventType?.FullName ?? "<dynamic>"}' and '{eventType.FullName}'. " +
                    "EventTypeId is derived from the unqualified type name and must be unique across the subscriber. " +
                    "Rename one of the events to disambiguate.");
            }
            if (explicitRegistration)
            {
                HandlerRegistrations.Remove(existing);
            }
            else if (existing.IsExplicit || existing.HandlerType == handlerType)
            {
                return;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Multiple handlers were discovered for event type '{eventType.FullName}': " +
                    $"'{existing.HandlerType?.FullName ?? "<dynamic>"}' and '{handlerType.FullName}'. " +
                    "Register the chosen handler explicitly with AddHandler<TEvent,THandler>() before scanning the assembly; " +
                    "an explicit registration made after the scan is too late to resolve the conflict.");
            }
        }

        Services.AddTransient(expectedHandlerInterface, handlerType);

        HandlerRegistrations.Add(new HandlerRegistration
        {
            EventTypeId = eventTypeId,
            EventType = eventType,
            HandlerType = handlerType,
            IsExplicit = explicitRegistration,
            Register = (provider, handlerProvider) =>
            {
                handlerProvider.RegisterHandler(
                    eventType,
                    scopedProvider => (scopedProvider ?? provider).GetRequiredService(expectedHandlerInterface));
            }
        });
    }

    private static IEnumerable<(Type EventType, Type HandlerType)> DiscoverHandlerRegistrations(Assembly assembly)
    {
        return GetLoadableTypes(assembly)
            .Where(type => type is { IsClass: true, IsAbstract: false } && !type.ContainsGenericParameters)
            .SelectMany(handlerType => handlerType
                .GetInterfaces()
                .Where(IsEventHandlerInterface)
                .Select(handlerInterface => (EventType: handlerInterface.GetGenericArguments()[0], HandlerType: handlerType)));
    }

    private static bool IsEventHandlerInterface(Type type)
    {
        return type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(IEventHandler<>)
            && typeof(IEvent).IsAssignableFrom(type.GetGenericArguments()[0]);
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null).Cast<Type>();
        }
    }

    internal class HandlerRegistration
    {
        public string? EventTypeId { get; set; }

        /// <summary>
        /// The CLR event type for a typed handler, or <c>null</c> for a
        /// dynamic (string-keyed) registration added via
        /// <see cref="AddDynamicHandler(string, Func{IEventJsonHandler})"/> that has no compiled IEvent class.
        /// </summary>
        public Type? EventType { get; set; }

        /// <summary>
        /// The CLR handler type for a typed handler, or <c>null</c> for a
        /// dynamic registration whose handler is produced by a factory.
        /// </summary>
        public Type? HandlerType { get; set; }
        public bool IsExplicit { get; set; }
        public Action<IServiceProvider, EventHandlerProvider> Register { get; set; } = null!;
    }
}
