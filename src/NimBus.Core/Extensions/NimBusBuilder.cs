using Microsoft.Extensions.DependencyInjection;
using NimBus.Core.Messages;
using System;
using System.Collections.Generic;

namespace NimBus.Core.Extensions;

/// <summary>
/// Default implementation of <see cref="INimBusBuilder"/>.
/// </summary>
public class NimBusBuilder : INimBusBuilder
{
    private readonly List<Type> _pipelineBehaviorTypes = [];
    private readonly List<Type> _lifecycleObserverTypes = [];

    public NimBusBuilder(IServiceCollection services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public IServiceCollection Services { get; }

    public INimBusBuilder AddPipelineBehavior<TBehavior>() where TBehavior : class, IMessagePipelineBehavior
    {
        _pipelineBehaviorTypes.Add(typeof(TBehavior));
        Services.AddSingleton<TBehavior>();
        return this;
    }

    public INimBusBuilder AddLifecycleObserver<TObserver>() where TObserver : class, IMessageLifecycleObserver
    {
        _lifecycleObserverTypes.Add(typeof(TObserver));
        Services.AddSingleton<IMessageLifecycleObserver, TObserver>();
        return this;
    }

    public INimBusBuilder AddExtension<TExtension>() where TExtension : class, INimBusExtension, new()
    {
        var extension = new TExtension();
        extension.Configure(this);
        return this;
    }

    public INimBusBuilder AddExtension(INimBusExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        extension.Configure(this);
        return this;
    }

    /// <summary>
    /// Builds the pipeline and registers remaining infrastructure services.
    /// Called internally by <see cref="NimBusServiceCollectionExtensions.AddNimBus"/>.
    /// </summary>
    internal void Build()
    {
        // Register the lifecycle notifier (aggregates all observers)
        Services.AddSingleton<MessageLifecycleNotifier>();

        // Register the pipeline behavior types list for the pipeline to resolve
        Services.AddSingleton(new PipelineBehaviorRegistry(_pipelineBehaviorTypes));

        // Register the message pipeline
        Services.AddSingleton<MessagePipeline>();
    }
}

/// <summary>
/// Registry of pipeline behavior types, used to resolve behaviors in order.
/// </summary>
public class PipelineBehaviorRegistry
{
    public IReadOnlyList<Type> BehaviorTypes { get; }

    public PipelineBehaviorRegistry(IEnumerable<Type> behaviorTypes)
    {
        BehaviorTypes = behaviorTypes?.ToList().AsReadOnly()
            ?? throw new ArgumentNullException(nameof(behaviorTypes));
    }
}
