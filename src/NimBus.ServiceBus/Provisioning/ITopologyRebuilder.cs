using Azure.Messaging.ServiceBus.Administration;

namespace NimBus.ServiceBus.Provisioning;

/// <summary>
/// Puts one subscription back exactly as provisioning would have created it.
/// </summary>
/// <remarks>
/// Service Bus rejects <c>receive</c> on an auto-forwarding entity, so the only way to
/// discard such a subscription's backlog is to delete and re-provision it. This is the
/// seam that lets the WebApp's subscription admin do that without reimplementing — or
/// drifting from — <see cref="ServiceBusTopologyProvisioner"/>.
/// </remarks>
public interface ITopologyRebuilder
{
    /// <summary>
    /// Creates <paramref name="expected"/> on <paramref name="topicName"/>, or brings an
    /// existing subscription up to it, including its rules.
    /// </summary>
    Task EnsureSubscriptionAsync(
        string topicName,
        ExpectedSubscription expected,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ITopologyRebuilder"/>
public sealed class ServiceBusTopologyRebuilder : ITopologyRebuilder
{
    private readonly ServiceBusAdministrationClient _client;
    private readonly Action<string> _log;

    /// <summary>
    /// Creates a rebuilder for a real Service Bus namespace: a rule whose filter or action
    /// differs from the topology is updated in place. Callers pointed at an emulator,
    /// which cannot update a rule, use
    /// <see cref="ServiceBusTopologyRebuilder(ServiceBusAdministrationClient, bool, Action{string}?)"/>
    /// with <c>isEmulator: true</c>.
    /// </summary>
    /// <param name="client">Administration client for the target namespace.</param>
    /// <param name="log">Optional progress sink; defaults to discarding the messages.</param>
    public ServiceBusTopologyRebuilder(ServiceBusAdministrationClient client, Action<string>? log = null)
        : this(client, isEmulator: false, log)
    {
    }

    /// <summary>
    /// Creates a rebuilder that reconciles mismatched rules the way
    /// <see cref="ServiceBusTopologyProvisioner"/> does for the same target: updated in place
    /// on a real namespace, deleted and recreated on an emulator.
    /// </summary>
    /// <param name="client">Administration client for the target namespace.</param>
    /// <param name="isEmulator">
    /// True when <paramref name="client"/> targets a Service Bus emulator (see
    /// <see cref="ServiceBusTopologyProvisioner.IsEmulator(string?)"/>).
    /// </param>
    /// <param name="log">Optional progress sink; defaults to discarding the messages.</param>
    public ServiceBusTopologyRebuilder(ServiceBusAdministrationClient client, bool isEmulator, Action<string>? log = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        IsEmulator = isEmulator;
        // Unlike `nb topology apply`, a rebuild runs inside an HTTP request with no
        // console to write to — the caller reports the outcome to the operator.
        _log = log ?? (_ => { });
    }

    /// <summary>
    /// True when this rebuilder targets an emulator and recreates mismatched rules rather
    /// than updating them in place.
    /// </summary>
    public bool IsEmulator { get; }

    /// <inheritdoc/>
    public Task EnsureSubscriptionAsync(
        string topicName,
        ExpectedSubscription expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);

        return ServiceBusTopologyProvisioner.EnsureSubscriptionAsync(
            _client, topicName, expected, IsEmulator, _log, cancellationToken);
    }
}
