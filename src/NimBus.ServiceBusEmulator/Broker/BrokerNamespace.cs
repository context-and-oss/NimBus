namespace NimBus.ServiceBusEmulator.Broker;

internal sealed class BrokerNamespace
{
    private readonly object _gate = new();
    private readonly BrokerOptions _options;
    private readonly Dictionary<string, TopicState> _topics = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ScheduledMessage> _scheduled = [];
    private readonly List<AdminOperation> _operationLog = [];
    private long _nextScheduleSequence;
    private long _storedBytes;
    private DateTimeOffset _nextExpiry = DateTimeOffset.MaxValue;
    private DateTimeOffset _nextActivation = DateTimeOffset.MaxValue;
    private bool _activationWaitingForSpace;

    public BrokerNamespace(BrokerOptions options)
    {
        _options = options;
    }

    public event Action<string>? TopicCreated;

    public event Action<string, string>? SubscriptionCreated;

    public TopologySnapshot GetTopologySnapshot()
    {
        lock (_gate)
        {
            return GetTopologySnapshotCore();
        }
    }

    public PreparedTopologyMutation PrepareCreateTopic(TopicDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            if (_topics.ContainsKey(definition.Name))
            {
                throw new InvalidOperationException($"Topic '{definition.Name}' already exists.");
            }

            var state = new TopicState(definition, _options.TimeProvider.GetUtcNow());
            var snapshot = GetTopologySnapshotCore();
            var candidate = snapshot with { Topics = [.. snapshot.Topics, new TopologyTopic(definition, [])] };
            return new PreparedTopologyMutation(candidate, () => ApplyCreateTopic(definition.Name, state));
        }
    }

    public PreparedTopologyMutation PrepareUpdateTopic(TopicDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            var state = GetTopic(definition.Name);
            var candidate = ReplaceTopicDefinition(GetTopologySnapshotCore(), definition);
            return new PreparedTopologyMutation(candidate, () => ApplyUpdateTopic(state, definition));
        }
    }

    public PreparedTopologyMutation PrepareDeleteTopic(string topicName)
    {
        lock (_gate)
        {
            GetTopic(topicName);
            var snapshot = GetTopologySnapshotCore();
            var candidate = snapshot with
            {
                Topics = snapshot.Topics.Where(topic => !NamesEqual(topic.Definition.Name, topicName)).ToArray(),
            };
            return new PreparedTopologyMutation(candidate, () => DeleteTopic(topicName));
        }
    }

    /// <summary>
    /// Prepares creating a subscription seeded with <paramref name="initialRule"/>, or with
    /// the true-filter <c>$Default</c> Service Bus seeds when none is given.
    /// </summary>
    public PreparedTopologyMutation PrepareCreateSubscription(
        string topicName,
        SubscriptionDefinition definition,
        RuleDefinition? initialRule = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            var topic = GetTopic(topicName);
            if (topic.Subscriptions.ContainsKey(definition.Name))
            {
                throw new InvalidOperationException($"Subscription '{topicName}/{definition.Name}' already exists.");
            }

            var state = new SubscriptionState(definition, _options.TimeProvider.GetUtcNow(), initialRule);
            var candidate = ReplaceTopicSubscriptions(
                GetTopologySnapshotCore(),
                topicName,
                subscriptions => [.. subscriptions, ToTopologySubscription(state)]);
            return new PreparedTopologyMutation(candidate, () => ApplyCreateSubscription(topicName, state));
        }
    }

    public PreparedTopologyMutation PrepareUpdateSubscription(string topicName, SubscriptionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            var state = GetSubscription(topicName, definition.Name);
            var candidate = ReplaceTopicSubscriptions(
                GetTopologySnapshotCore(),
                topicName,
                subscriptions => subscriptions.Select(subscription =>
                    NamesEqual(subscription.Definition.Name, definition.Name)
                        ? subscription with { Definition = definition }
                        : subscription).ToArray());
            return new PreparedTopologyMutation(candidate, () => ApplyUpdateSubscription(topicName, state, definition));
        }
    }

    public PreparedTopologyMutation PrepareDeleteSubscription(string topicName, string subscriptionName)
    {
        lock (_gate)
        {
            GetSubscription(topicName, subscriptionName);
            var candidate = ReplaceTopicSubscriptions(
                GetTopologySnapshotCore(),
                topicName,
                subscriptions => subscriptions.Where(subscription =>
                    !NamesEqual(subscription.Definition.Name, subscriptionName)).ToArray());
            return new PreparedTopologyMutation(candidate, () => DeleteSubscription(topicName, subscriptionName));
        }
    }

    public PreparedTopologyMutation PrepareCreateRule(
        string topicName,
        string subscriptionName,
        RuleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            var subscription = GetSubscription(topicName, subscriptionName);
            if (subscription.Rules.Any(rule => NamesEqual(rule.Definition.Name, definition.Name)))
            {
                throw new InvalidOperationException($"Rule '{topicName}/{subscriptionName}/{definition.Name}' already exists.");
            }

            var state = new RuleState(definition);
            var candidate = ReplaceSubscriptionRules(
                GetTopologySnapshotCore(),
                topicName,
                subscriptionName,
                rules => [.. rules, definition]);
            return new PreparedTopologyMutation(candidate, () => ApplyCreateRule(topicName, subscription, state));
        }
    }

    public PreparedTopologyMutation PrepareDeleteRule(string topicName, string subscriptionName, string ruleName)
    {
        lock (_gate)
        {
            var subscription = GetSubscription(topicName, subscriptionName);
            if (!subscription.Rules.Any(rule => NamesEqual(rule.Definition.Name, ruleName)))
            {
                throw new KeyNotFoundException($"Rule '{topicName}/{subscriptionName}/{ruleName}' does not exist.");
            }

            var candidate = ReplaceSubscriptionRules(
                GetTopologySnapshotCore(),
                topicName,
                subscriptionName,
                rules => rules.Where(rule => !NamesEqual(rule.Name, ruleName)).ToArray());
            return new PreparedTopologyMutation(candidate, () => DeleteRule(topicName, subscriptionName, ruleName));
        }
    }

    public void CreateTopic(TopicDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            if (!_topics.TryAdd(definition.Name, new TopicState(definition, _options.TimeProvider.GetUtcNow())))
            {
                throw new InvalidOperationException($"Topic '{definition.Name}' already exists.");
            }

            Log("PUT", definition.Name, "Create");
            TopicCreated?.Invoke(definition.Name);
        }
    }

    public IReadOnlyList<TopicDefinition> GetTopics()
    {
        lock (_gate)
        {
            return _topics.Values.Select(topic => topic.Definition).ToArray();
        }
    }

    public TopicDefinition GetTopicDefinition(string topicName)
    {
        lock (_gate)
        {
            return GetTopic(topicName).Definition;
        }
    }

    public void UpdateTopic(TopicDefinition definition)
    {
        lock (_gate)
        {
            var topic = GetTopic(definition.Name);
            topic.Definition = definition;
            topic.UpdatedAt = _options.TimeProvider.GetUtcNow();
            Log("PUT", definition.Name, "Update");
        }
    }

    public void DeleteTopic(string topicName)
    {
        lock (_gate)
        {
            if (!_topics.Remove(topicName, out var removed))
            {
                throw new KeyNotFoundException($"Topic '{topicName}' does not exist.");
            }

            ChangeStoredBytes(-removed.Subscriptions.Values.Sum(subscription => subscription.StoredBytes));
            _scheduled.RemoveAll(message =>
            {
                if (!NamesEqual(message.TopicName, topicName))
                    return false;
                ChangeStoredBytes(-message.Message.Body.Length);
                return true;
            });
            Log("DELETE", topicName, "Delete");
        }
    }

    public bool TopicExists(string topicName)
    {
        lock (_gate)
        {
            return _topics.ContainsKey(topicName);
        }
    }

    public bool CanSend(string topicName)
    {
        lock (_gate)
        {
            return GetTopic(topicName).Definition.Status != BrokerEntityStatus.SendDisabled;
        }
    }

    public bool CanReceive(string topicName, string subscriptionName)
    {
        lock (_gate)
        {
            var definition = GetSubscription(topicName, subscriptionName).Definition;
            return definition.Status != BrokerEntityStatus.ReceiveDisabled && string.IsNullOrEmpty(definition.ForwardTo);
        }
    }

    public bool SubscriptionExists(string topicName, string subscriptionName)
    {
        lock (_gate)
        {
            return _topics.TryGetValue(topicName, out var topic) && topic.Subscriptions.ContainsKey(subscriptionName);
        }
    }

    /// <summary>
    /// Creates a subscription seeded with <paramref name="initialRule"/>, or with the
    /// true-filter <c>$Default</c> Service Bus seeds when none is given.
    /// </summary>
    public void CreateSubscription(string topicName, SubscriptionDefinition definition, RuleDefinition? initialRule = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            var topic = GetTopic(topicName);
            if (!topic.Subscriptions.TryAdd(definition.Name, new SubscriptionState(definition, _options.TimeProvider.GetUtcNow(), initialRule)))
            {
                throw new InvalidOperationException($"Subscription '{topicName}/{definition.Name}' already exists.");
            }

            Log("PUT", $"{topicName}/Subscriptions/{definition.Name}", "Create");
            SubscriptionCreated?.Invoke(topicName, definition.Name);
        }
    }

    public IReadOnlyList<SubscriptionDefinition> GetSubscriptions(string topicName)
    {
        lock (_gate)
        {
            return GetTopic(topicName).Subscriptions.Values.Select(subscription => subscription.Definition).ToArray();
        }
    }

    public SubscriptionDefinition GetSubscriptionDefinition(string topicName, string subscriptionName)
    {
        lock (_gate)
        {
            return GetSubscription(topicName, subscriptionName).Definition;
        }
    }

    public void UpdateSubscription(string topicName, SubscriptionDefinition definition)
    {
        lock (_gate)
        {
            var subscription = GetSubscription(topicName, definition.Name);
            subscription.Definition = definition;
            subscription.UpdatedAt = _options.TimeProvider.GetUtcNow();
            if (definition.Status == BrokerEntityStatus.Active && !string.IsNullOrEmpty(definition.ForwardTo))
            {
                ForwardPending(subscription);
            }
            Log("PUT", $"{topicName}/Subscriptions/{definition.Name}", "Update");
        }
    }

    public void DeleteSubscription(string topicName, string subscriptionName)
    {
        lock (_gate)
        {
            var topic = GetTopic(topicName);
            if (!topic.Subscriptions.Remove(subscriptionName, out var removed))
            {
                throw new KeyNotFoundException($"Subscription '{topicName}/{subscriptionName}' does not exist.");
            }

            ChangeStoredBytes(-removed.StoredBytes);
            Log("DELETE", $"{topicName}/Subscriptions/{subscriptionName}", "Delete");
        }
    }

    public void ReplaceRule(string topicName, string subscriptionName, RuleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            var subscription = GetSubscription(topicName, subscriptionName);
            var rule = new RuleState(definition);
            subscription.Rules.Clear();
            subscription.Rules.Add(rule);
            if (_activationWaitingForSpace)
                _nextActivation = DateTimeOffset.MinValue;
        }
    }

    public void CreateRule(string topicName, string subscriptionName, RuleDefinition definition)
    {
        lock (_gate)
        {
            var subscription = GetSubscription(topicName, subscriptionName);
            if (subscription.Rules.Any(rule => string.Equals(rule.Definition.Name, definition.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Rule '{topicName}/{subscriptionName}/{definition.Name}' already exists.");
            }

            subscription.Rules.Add(new RuleState(definition));
            Log("PUT", $"{topicName}/Subscriptions/{subscriptionName}/Rules/{definition.Name}", "Create");
        }
    }

    public IReadOnlyList<RuleDefinition> GetRules(string topicName, string subscriptionName)
    {
        lock (_gate)
        {
            return GetSubscription(topicName, subscriptionName).Rules.Select(rule => rule.Definition).ToArray();
        }
    }

    public RuleDefinition GetRule(string topicName, string subscriptionName, string ruleName)
    {
        lock (_gate)
        {
            return GetSubscription(topicName, subscriptionName).Rules
                       .FirstOrDefault(rule => string.Equals(rule.Definition.Name, ruleName, StringComparison.OrdinalIgnoreCase))?.Definition
                   ?? throw new KeyNotFoundException($"Rule '{topicName}/{subscriptionName}/{ruleName}' does not exist.");
        }
    }

    public void DeleteRule(string topicName, string subscriptionName, string ruleName)
    {
        lock (_gate)
        {
            var subscription = GetSubscription(topicName, subscriptionName);
            var removed = subscription.Rules.RemoveAll(rule =>
                string.Equals(rule.Definition.Name, ruleName, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                throw new KeyNotFoundException($"Rule '{topicName}/{subscriptionName}/{ruleName}' does not exist.");
            }

            Log("DELETE", $"{topicName}/Subscriptions/{subscriptionName}/Rules/{ruleName}", "Delete");
        }
    }

    public IReadOnlyList<AdminOperation> GetOperationLog()
    {
        lock (_gate)
        {
            return _operationLog.ToArray();
        }
    }

    public long Publish(string topicName, BrokerMessage message) => PublishBatch(topicName, [message])[0];

    public long[] PublishBatch(string topicName, IReadOnlyList<BrokerMessage> messages)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var topic = GetTopic(topicName);
            if (topic.Definition.Status == BrokerEntityStatus.SendDisabled)
                throw new InvalidOperationException($"Topic '{topicName}' is send-disabled.");

            var now = _options.TimeProvider.GetUtcNow();
            var batch = new PendingWrites(this);
            var sequences = new long[messages.Count];
            for (var index = 0; index < messages.Count; index++)
            {
                var message = messages[index];
                if (message.ScheduledEnqueueTime is { } due && due > now)
                {
                    sequences[index] = _nextScheduleSequence + batch.Schedules.Count + 1;
                    batch.Reserve(message.Body.Length);
                    batch.Schedules.Add(new ScheduledMessage(sequences[index], topicName, message.Copy(), due));
                }
                else
                {
                    sequences[index] = Enqueue(topic, message, now, batch);
                }
            }

            batch.Commit();
            return sequences;
        }
    }

    public void CancelScheduled(string topicName, long scheduleSequence)
    {
        lock (_gate)
        {
            var index = _scheduled.FindIndex(message =>
                message.SequenceNumber == scheduleSequence &&
                string.Equals(message.TopicName, topicName, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new KeyNotFoundException($"Scheduled message '{scheduleSequence}' does not exist.");
            }

            ChangeStoredBytes(-_scheduled[index].Message.Body.Length);
            _scheduled.RemoveAt(index);
        }
    }

    public BrokerDelivery? TryAcquire(
        string topicName,
        string subscriptionName,
        string? sessionId,
        string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        lock (_gate)
        {
            ProcessDueWorkCore();
            var subscription = GetSubscription(topicName, subscriptionName);
            if (subscription.Definition.Status == BrokerEntityStatus.ReceiveDisabled ||
                !string.IsNullOrEmpty(subscription.Definition.ForwardTo))
            {
                return null;
            }

            if (subscription.Definition.RequiresSession)
            {
                if (sessionId is null || !subscription.Sessions.TryGetValue(sessionId, out var session) ||
                    !string.Equals(session.Owner, owner, StringComparison.Ordinal))
                {
                    return null;
                }
            }

            var now = _options.TimeProvider.GetUtcNow();
            var stored = subscription.Messages.FirstOrDefault(candidate =>
                candidate.State == MessageState.Active &&
                (!subscription.Definition.RequiresSession || string.Equals(candidate.Message.SessionId, sessionId, StringComparison.Ordinal)));
            if (stored is null)
            {
                return null;
            }

            stored.State = MessageState.Locked;
            stored.Owner = owner;
            stored.LockToken = Guid.NewGuid();
            stored.LockedUntil = now.Add(subscription.Definition.LockDuration);
            stored.Message.DeliveryCount = stored.PriorFailedAttempts + 1;
            stored.Message.LockToken = stored.LockToken;
            stored.Message.LockedUntil = stored.LockedUntil;
            WatchExpiry(subscription, stored.LockedUntil);
            return new BrokerDelivery(stored.Message.CloneForDelivery(), stored.LockToken);
        }
    }

    public AcceptedSession? TryAcceptSession(
        string topicName,
        string subscriptionName,
        string sessionId,
        string owner)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var subscription = GetSubscription(topicName, subscriptionName);
            if (!subscription.Definition.RequiresSession)
            {
                return null;
            }

            var now = _options.TimeProvider.GetUtcNow();
            if (subscription.Sessions.TryGetValue(sessionId, out var existing) && existing.LockedUntil > now)
            {
                throw new SessionCannotBeLockedException($"Session '{sessionId}' is already locked.");
            }

            var lockedUntil = now.Add(subscription.Definition.LockDuration);
            WatchExpiry(subscription, lockedUntil);
            subscription.Sessions[sessionId] = new SessionLock(owner, lockedUntil);
            return new AcceptedSession(sessionId, lockedUntil);
        }
    }

    public AcceptedSession? TryAcceptNextSession(string topicName, string subscriptionName, string owner)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var subscription = GetSubscription(topicName, subscriptionName);
            var sessionId = subscription.Messages
                .Where(message => message.State == MessageState.Active && message.Message.SessionId is not null)
                .Select(message => message.Message.SessionId!)
                .FirstOrDefault(candidate =>
                    !subscription.Sessions.TryGetValue(candidate, out var session) ||
                    session.LockedUntil <= _options.TimeProvider.GetUtcNow());
            return sessionId is null ? null : TryAcceptSession(topicName, subscriptionName, sessionId, owner);
        }
    }

    public void ReleaseSession(string topicName, string subscriptionName, string sessionId, string owner)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var subscription = GetSubscription(topicName, subscriptionName);
            if (!subscription.Sessions.TryGetValue(sessionId, out var session) ||
                !string.Equals(session.Owner, owner, StringComparison.Ordinal))
            {
                return;
            }

            subscription.Sessions.Remove(sessionId);
            foreach (var message in subscription.Messages.Where(message =>
                         message.State == MessageState.Locked &&
                         string.Equals(message.Owner, owner, StringComparison.Ordinal) &&
                         string.Equals(message.Message.SessionId, sessionId, StringComparison.Ordinal)))
            {
                message.ReleaseWithoutFailure();
                WatchExpiry(subscription, message.ExpiresAt);
            }
        }
    }

    public DateTimeOffset RenewSessionLock(string topicName, string subscriptionName, string sessionId, string owner)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var subscription = GetSubscription(topicName, subscriptionName);
            var session = FindOwnedSession(subscription, sessionId, owner);

            var expiration = _options.TimeProvider.GetUtcNow().Add(subscription.Definition.LockDuration);
            WatchExpiry(subscription, expiration);
            subscription.Sessions[sessionId] = session with { LockedUntil = expiration };
            foreach (var message in subscription.Messages.Where(message =>
                         message.State == MessageState.Locked &&
                         string.Equals(message.Message.SessionId, sessionId, StringComparison.Ordinal)))
            {
                message.LockedUntil = expiration;
                message.Message.LockedUntil = expiration;
            }

            return expiration;
        }
    }

    public ReadOnlyMemory<byte>? GetSessionState(
        string topicName,
        string subscriptionName,
        string sessionId,
        string owner)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var subscription = GetSubscription(topicName, subscriptionName);
            FindOwnedSession(subscription, sessionId, owner);
            return subscription.SessionState.TryGetValue(sessionId, out var state) ? state : null;
        }
    }

    public void SetSessionState(
        string topicName,
        string subscriptionName,
        string sessionId,
        string owner,
        ReadOnlyMemory<byte>? state)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            if (state is { Length: > 256 * 1024 })
            {
                throw new InvalidOperationException("Session state exceeds the 256 KiB limit.");
            }

            var subscription = GetSubscription(topicName, subscriptionName);
            FindOwnedSession(subscription, sessionId, owner);
            var delta = (state?.Length ?? 0) - subscription.SessionState.GetValueOrDefault(sessionId).Length;
            EnsureCapacity(delta);
            if (state is null || state.Value.IsEmpty)
            {
                subscription.SessionState.Remove(sessionId);
            }
            else
            {
                subscription.SessionState[sessionId] = state.Value.ToArray();
            }
            ChangeStoredBytes(delta);
        }
    }

    public DateTimeOffset RenewLock(string topicName, string subscriptionName, Guid lockToken)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var subscription = GetSubscription(topicName, subscriptionName);
            var stored = subscription.Messages.FirstOrDefault(message =>
                message.State == MessageState.Locked && message.LockToken == lockToken)
                ?? throw new KeyNotFoundException($"Lock token '{lockToken}' is not active.");
            if (subscription.Definition.RequiresSession)
            {
                throw new KeyNotFoundException("Session deliveries use the umbrella session lock.");
            }

            stored.LockedUntil = _options.TimeProvider.GetUtcNow().Add(subscription.Definition.LockDuration);
            stored.Message.LockedUntil = stored.LockedUntil;
            WatchExpiry(subscription, stored.LockedUntil);
            return stored.LockedUntil;
        }
    }

    public IReadOnlyList<BrokerMessage> Peek(string topicName, string subscriptionName, long fromSequenceNumber, int count, string? sessionId)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            return GetSubscription(topicName, subscriptionName).Messages
                .Where(message => message.State == MessageState.Active &&
                                  message.Message.SequenceNumber >= fromSequenceNumber &&
                                  (sessionId is null || string.Equals(message.Message.SessionId, sessionId, StringComparison.Ordinal)))
                .OrderBy(message => message.Message.SequenceNumber)
                .Take(count)
                .Select(message => message.Message.CloneForDelivery())
                .ToArray();
        }
    }

    public IReadOnlyList<BrokerMessage> PeekDeadLetter(
        string topicName,
        string subscriptionName,
        long fromSequenceNumber,
        int count)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            return GetSubscription(topicName, subscriptionName).DeadLetter
                .Where(message => message.State == MessageState.DeadLetter && message.Message.SequenceNumber >= fromSequenceNumber)
                .OrderBy(message => message.Message.SequenceNumber)
                .Take(count)
                .Select(message => message.Message.CloneForDelivery())
                .ToArray();
        }
    }

    public BrokerDelivery? TryAcquireDeadLetter(string topicName, string subscriptionName, string owner)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var subscription = GetSubscription(topicName, subscriptionName);
            var stored = subscription.DeadLetter
                .FirstOrDefault(candidate => candidate.State == MessageState.DeadLetter);
            if (stored is null)
            {
                return null;
            }

            stored.State = MessageState.Locked;
            stored.Owner = owner;
            stored.LockToken = Guid.NewGuid();
            stored.LockedUntil = _options.TimeProvider.GetUtcNow().Add(GetSubscription(topicName, subscriptionName).Definition.LockDuration);
            stored.Message.DeliveryCount = stored.PriorFailedAttempts + 1;
            stored.Message.LockToken = stored.LockToken;
            stored.Message.LockedUntil = stored.LockedUntil;
            WatchExpiry(subscription, stored.LockedUntil);
            return new BrokerDelivery(stored.Message.CloneForDelivery(), stored.LockToken);
        }
    }

    public void CompleteDeadLetter(string topicName, string subscriptionName, Guid lockToken, string owner)
    {
        lock (_gate)
        {
            var subscription = GetSubscription(topicName, subscriptionName);
            ProcessDueWorkCore();
            var stored = FindLockedIn(subscription.DeadLetter, lockToken, owner);
            subscription.DeadLetter.Remove(stored);
            ChangeStoredBytes(-stored.Message.Body.Length);
        }
    }

    public void ReleaseDeadLetter(string topicName, string subscriptionName, Guid lockToken, string owner)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var stored = FindLockedIn(GetSubscription(topicName, subscriptionName).DeadLetter, lockToken, owner);
            stored.ReleaseWithoutFailure();
            stored.State = MessageState.DeadLetter;
        }
    }

    public DateTimeOffset RenewDeadLetterLock(string topicName, string subscriptionName, Guid lockToken)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var subscription = GetSubscription(topicName, subscriptionName);
            var stored = subscription.DeadLetter.FirstOrDefault(message =>
                message.State == MessageState.Locked && message.LockToken == lockToken)
                ?? throw new KeyNotFoundException($"Lock token '{lockToken}' is not active.");
            stored.LockedUntil = _options.TimeProvider.GetUtcNow().Add(subscription.Definition.LockDuration);
            stored.Message.LockedUntil = stored.LockedUntil;
            WatchExpiry(subscription, stored.LockedUntil);
            return stored.LockedUntil;
        }
    }

    public void CommitDeadLetterReplay(
        string sourceTopic,
        string sourceSubscription,
        Guid lockToken,
        string owner,
        string destinationTopic,
        BrokerMessage message)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var subscription = GetSubscription(sourceTopic, sourceSubscription);
            var source = FindLockedIn(subscription.DeadLetter, lockToken, owner);
            var topic = GetTopic(destinationTopic);
            if (topic.Definition.Status == BrokerEntityStatus.SendDisabled)
                throw new InvalidOperationException($"Topic '{destinationTopic}' is send-disabled.");
            var batch = new PendingWrites(this, source.Message.Body.Length);
            Enqueue(topic, message, _options.TimeProvider.GetUtcNow(), batch);
            subscription.DeadLetter.Remove(source);
            batch.Commit();
        }
    }

    public void Complete(string topicName, string subscriptionName, Guid lockToken, string owner)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var subscription = GetSubscription(topicName, subscriptionName);
            var stored = FindLocked(subscription, lockToken, owner);
            subscription.Messages.Remove(stored);
            ChangeStoredBytes(-stored.Message.Body.Length);
        }
    }

    public void Abandon(string topicName, string subscriptionName, Guid lockToken, string owner)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var subscription = GetSubscription(topicName, subscriptionName);
            var stored = FindLocked(subscription, lockToken, owner);
            FailDelivery(subscription, stored, "MaxDeliveryCountExceeded", null);
        }
    }

    public void DeadLetter(
        string topicName,
        string subscriptionName,
        Guid lockToken,
        string owner,
        string? reason,
        string? description)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var subscription = GetSubscription(topicName, subscriptionName);
            var stored = FindLocked(subscription, lockToken, owner);
            MoveToDeadLetter(subscription, stored, reason, description);
        }
    }

    public void Release(string topicName, string subscriptionName, Guid lockToken, string owner)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var subscription = GetSubscription(topicName, subscriptionName);
            var stored = FindLocked(subscription, lockToken, owner);
            stored.ReleaseWithoutFailure();
            WatchExpiry(subscription, stored.ExpiresAt);
        }
    }

    public void CompleteByLockToken(string topicName, string subscriptionName, Guid lockToken)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var subscription = GetSubscription(topicName, subscriptionName);
            var stored = FindLocked(subscription, lockToken);
            subscription.Messages.Remove(stored);
            ChangeStoredBytes(-stored.Message.Body.Length);
        }
    }

    public void AbandonByLockToken(string topicName, string subscriptionName, Guid lockToken)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var subscription = GetSubscription(topicName, subscriptionName);
            FailDelivery(subscription, FindLocked(subscription, lockToken), "MaxDeliveryCountExceeded", null);
        }
    }

    public void DeadLetterByLockToken(
        string topicName,
        string subscriptionName,
        Guid lockToken,
        string? reason,
        string? description)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var subscription = GetSubscription(topicName, subscriptionName);
            MoveToDeadLetter(subscription, FindLocked(subscription, lockToken), reason, description);
        }
    }

    public SubscriptionRuntimeProperties GetSubscriptionRuntimeProperties(string topicName, string subscriptionName)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var subscription = GetSubscription(topicName, subscriptionName);
            return new SubscriptionRuntimeProperties(
                subscription.Messages.LongCount(message => message.State is MessageState.Active or MessageState.Locked),
                subscription.DeadLetter.Count,
                0,
                subscription.TransferDeadLetter.Count);
        }
    }

    public TopicRuntimeProperties GetTopicRuntimeProperties(string topicName)
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
            var topic = GetTopic(topicName);
            var size = topic.Subscriptions.Values.Sum(subscription =>
                subscription.Messages.Sum(message => (long)message.Message.Body.Length) +
                subscription.DeadLetter.Sum(message => (long)message.Message.Body.Length));
            return new TopicRuntimeProperties(
                topic.Definition.Name,
                topic.Subscriptions.Count,
                _scheduled.LongCount(message => string.Equals(message.TopicName, topicName, StringComparison.OrdinalIgnoreCase)),
                size,
                topic.CreatedAt,
                topic.UpdatedAt,
                topic.AccessedAt);
        }
    }

    public void ProcessDueWork()
    {
        lock (_gate)
        {
            ProcessDueWorkCore();
        }
    }

    private long Enqueue(TopicState topic, BrokerMessage source, DateTimeOffset now, PendingWrites batch)
    {
        var sequence = batch.NextSequence(topic);
        foreach (var subscription in topic.Subscriptions.Values)
        {
            var actionMatches = new List<RuleState>();
            var matchedWithoutAction = false;
            foreach (var rule in subscription.Rules)
            {
                try
                {
                    if (!rule.Compiled.IsMatch(source.ApplicationProperties))
                    {
                        continue;
                    }

                    if (rule.Compiled.HasActions)
                    {
                        actionMatches.Add(rule);
                    }
                    else
                    {
                        matchedWithoutAction = true;
                    }
                }
                catch (Exception) when (subscription.Definition.DeadLetterOnFilterEvaluationExceptions)
                {
                    var failed = NewStoredCopy(topic, subscription, source, sequence, now);
                    MarkDeadLetter(failed, "FilterEvaluationException", "A rule filter could not be evaluated.");
                    batch.Store(subscription, subscription.DeadLetter, failed);
                }
            }

            if (matchedWithoutAction)
            {
                EnqueueOrForward(subscription, NewStoredCopy(topic, subscription, source, sequence, now), batch);
            }

            foreach (var rule in actionMatches)
            {
                var copy = NewStoredCopy(topic, subscription, source, sequence, now);
                rule.Compiled.Apply(copy.Message.ApplicationProperties);
                copy.Message.ApplicationProperties["RuleName"] = rule.Definition.Name;
                EnqueueOrForward(subscription, copy, batch);
            }
        }

        return sequence;
    }

    private void EnsureCapacity(long delta)
    {
        if (delta > _options.MaxStoredBytes - _storedBytes)
            throw new BrokerQuotaExceededException($"The emulator's {_options.MaxStoredBytes}-byte broker memory budget would be exceeded.");
    }

    private void ChangeStoredBytes(long delta)
    {
        _storedBytes += delta;
        if (delta < 0 && _activationWaitingForSpace)
            _nextActivation = DateTimeOffset.MinValue;
    }

    // Prepare fan-out and forwarding under the namespace lock. Replacing an existing
    // body (activation, replay or resume) credits its bytes in the same commit.
    private sealed class PendingWrites(BrokerNamespace broker, int replacedBytes = 0)
    {
        private long _delta = -replacedBytes;
        private readonly List<(SubscriptionState Subscription, List<StoredMessage> Target, StoredMessage Message)> _writes = [];
        private readonly Dictionary<TopicState, long> _sequences = [];

        public List<ScheduledMessage> Schedules { get; } = [];

        public long NextSequence(TopicState topic)
        {
            var next = _sequences.GetValueOrDefault(topic, topic.NextSequence) + 1;
            _sequences[topic] = next;
            return next;
        }

        public void Reserve(int bytes)
        {
            _delta += bytes;
            broker.EnsureCapacity(_delta);
        }

        public void Store(SubscriptionState subscription, List<StoredMessage> target, StoredMessage message)
        {
            Reserve(message.Message.Body.Length);
            _writes.Add((subscription, target, message));
        }

        public void Commit()
        {
            foreach (var (subscription, target, message) in _writes)
            {
                target.Add(message);
                if (message.State == MessageState.Active)
                    broker.WatchExpiry(subscription, message.ExpiresAt);
            }

            foreach (var (topic, sequence) in _sequences)
                topic.NextSequence = sequence;
            broker._nextScheduleSequence += Schedules.Count;
            foreach (var scheduled in Schedules)
            {
                broker._scheduled.Add(scheduled);
                if (scheduled.Due < broker._nextActivation)
                    broker._nextActivation = scheduled.Due;
            }
            broker.ChangeStoredBytes(_delta);
        }
    }

    private void EnqueueOrForward(SubscriptionState subscription, StoredMessage message, PendingWrites batch)
    {
        if (subscription.Definition.Status != BrokerEntityStatus.Active || string.IsNullOrEmpty(subscription.Definition.ForwardTo))
        {
            batch.Store(subscription, subscription.Messages, message);
            return;
        }

        Forward(subscription, message, batch);
    }

    private void Forward(SubscriptionState source, StoredMessage stored, PendingWrites batch)
    {
        var targetName = source.Definition.ForwardTo!;
        if (stored.Message.ForwardHopCount >= 4 ||
            !_topics.TryGetValue(targetName, out var target) ||
            target.Definition.Status == BrokerEntityStatus.SendDisabled)
        {
            stored.State = MessageState.DeadLetter;
            batch.Store(source, source.TransferDeadLetter, stored);
            return;
        }

        var forwarded = stored.Message.Copy();
        forwarded.ForwardHopCount = stored.Message.ForwardHopCount + 1;
        Enqueue(target, forwarded, _options.TimeProvider.GetUtcNow(), batch);
    }

    private void ForwardPending(SubscriptionState subscription)
    {
        var pending = subscription.Messages.Where(message => message.State == MessageState.Active).ToArray();
        foreach (var message in pending)
        {
            subscription.Messages.Remove(message);
            var batch = new PendingWrites(this, message.Message.Body.Length);
            try
            {
                Forward(subscription, message, batch);
                batch.Commit();
            }
            catch (BrokerQuotaExceededException)
            {
                MarkDeadLetter(message, "QuotaExceeded", "The forwarding destination exceeds the broker budget.");
                subscription.TransferDeadLetter.Add(message);
            }
        }
    }

    private static StoredMessage NewStoredCopy(
        TopicState topic,
        SubscriptionState subscription,
        BrokerMessage source,
        long sequence,
        DateTimeOffset now)
    {
        var copy = source.Copy();
        copy.SequenceNumber = sequence;
        copy.EnqueuedTime = now;
        copy.DeliveryCount = 0;
        var ttl = Min(copy.TimeToLive, subscription.Definition.DefaultMessageTimeToLive, topic.Definition.DefaultMessageTimeToLive);
        return new StoredMessage(copy, ttl is null ? null : now.Add(ttl.Value));
    }

    private void WatchExpiry(SubscriptionState subscription, DateTimeOffset? deadline)
    {
        if (deadline is not { } due)
            return;
        if (due < subscription.NextExpiry)
            subscription.NextExpiry = due;
        if (due < _nextExpiry)
            _nextExpiry = due;
    }

    private void ProcessDueWorkCore()
    {
        var now = _options.TimeProvider.GetUtcNow();
        if (now >= _nextExpiry)
        {
            _nextExpiry = DateTimeOffset.MaxValue;
            foreach (var topic in _topics.Values)
            {
                foreach (var subscription in topic.Subscriptions.Values)
                {
                    if (now >= subscription.NextExpiry)
                        ProcessExpiry(subscription, now);
                    if (subscription.NextExpiry < _nextExpiry)
                        _nextExpiry = subscription.NextExpiry;
                }
            }
        }

        if (now >= _nextActivation)
        {
            _nextActivation = DateTimeOffset.MaxValue;
            _activationWaitingForSpace = false;
            _scheduled.RemoveAll(scheduled =>
            {
                if (scheduled.Due > now)
                {
                    if (scheduled.Due < _nextActivation)
                        _nextActivation = scheduled.Due;
                    return false;
                }

                var batch = new PendingWrites(this, scheduled.Message.Body.Length);
                try
                {
                    Enqueue(GetTopic(scheduled.TopicName), scheduled.Message, now, batch);
                    batch.Commit();
                    return true;
                }
                catch (BrokerQuotaExceededException)
                {
                    // Keep the accepted schedule cancellable and retry after space or topology changes.
                    _activationWaitingForSpace = true;
                    return false;
                }
            });
        }
    }

    private void ProcessExpiry(SubscriptionState subscription, DateTimeOffset now)
    {
        subscription.NextExpiry = DateTimeOffset.MaxValue;
        foreach (var session in subscription.Sessions.ToArray())
        {
            if (session.Value.LockedUntil <= now)
                subscription.Sessions.Remove(session.Key);
            else
                WatchExpiry(subscription, session.Value.LockedUntil);
        }

        // Expiry may move a message to the DLQ. Compact the active list in one pass.
        subscription.Messages.RemoveAll(message =>
        {
            if (message.State == MessageState.Locked)
            {
                if (subscription.Definition.RequiresSession)
                {
                    if (message.Message.SessionId is { } id && subscription.Sessions.ContainsKey(id))
                        return false;
                }
                else if (message.LockedUntil > now)
                {
                    WatchExpiry(subscription, message.LockedUntil);
                    return false;
                }

                if (message.ExpiresAt is null || message.ExpiresAt > now)
                {
                    message.PriorFailedAttempts++;
                    if (message.PriorFailedAttempts >= subscription.Definition.MaxDeliveryCount)
                    {
                        MarkDeadLetter(message, "MaxDeliveryCountExceeded", null);
                        subscription.DeadLetter.Add(message);
                        return true;
                    }
                    message.ReleaseWithoutFailure();
                }
            }

            if (message.ExpiresAt <= now)
            {
                ChangeStoredBytes(-message.Message.Body.Length);
                return true;
            }

            WatchExpiry(subscription, message.ExpiresAt);
            return false;
        });

        foreach (var message in subscription.DeadLetter)
        {
            if (message.State != MessageState.Locked)
                continue;
            if (message.LockedUntil > now)
            {
                WatchExpiry(subscription, message.LockedUntil);
                continue;
            }
            message.ReleaseWithoutFailure();
            message.State = MessageState.DeadLetter;
        }
    }

    private void FailDelivery(
        SubscriptionState subscription,
        StoredMessage stored,
        string reason,
        string? description)
    {
        stored.PriorFailedAttempts++;
        if (stored.PriorFailedAttempts >= subscription.Definition.MaxDeliveryCount)
        {
            MoveToDeadLetter(subscription, stored, reason, description);
        }
        else
        {
            stored.ReleaseWithoutFailure();
            WatchExpiry(subscription, stored.ExpiresAt);
        }
    }

    private static void MoveToDeadLetter(
        SubscriptionState subscription,
        StoredMessage stored,
        string? reason,
        string? description)
    {
        subscription.Messages.Remove(stored);
        MarkDeadLetter(stored, reason, description);
        subscription.DeadLetter.Add(stored);
    }

    private static void MarkDeadLetter(StoredMessage stored, string? reason, string? description)
    {
        stored.State = MessageState.DeadLetter;
        stored.Message.DeliveryCount = Math.Max(1, stored.PriorFailedAttempts);
        if (!string.IsNullOrEmpty(reason))
        {
            stored.Message.ApplicationProperties["DeadLetterReason"] = reason;
        }

        if (!string.IsNullOrEmpty(description))
        {
            stored.Message.ApplicationProperties["DeadLetterErrorDescription"] = description;
        }
    }

    private static StoredMessage FindLocked(SubscriptionState subscription, Guid lockToken, string owner)
    {
        return subscription.Messages.FirstOrDefault(message =>
                   message.State == MessageState.Locked &&
                   message.LockToken == lockToken &&
                   string.Equals(message.Owner, owner, StringComparison.Ordinal))
               ?? throw new KeyNotFoundException($"Lock token '{lockToken}' is not owned by this receiver.");
    }

    private static StoredMessage FindLocked(SubscriptionState subscription, Guid lockToken)
    {
        return subscription.Messages.FirstOrDefault(message =>
                   message.State == MessageState.Locked && message.LockToken == lockToken)
               ?? throw new KeyNotFoundException($"Lock token '{lockToken}' is not active.");
    }

    private static StoredMessage FindLockedIn(
        IEnumerable<StoredMessage> messages,
        Guid lockToken,
        string owner) =>
        messages.FirstOrDefault(message =>
            message.State == MessageState.Locked
            && message.LockToken == lockToken
            && string.Equals(message.Owner, owner, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Lock token '{lockToken}' is not owned by this receiver.");

    private static SessionLock FindOwnedSession(SubscriptionState subscription, string sessionId, string owner)
    {
        return subscription.Sessions.TryGetValue(sessionId, out var session) &&
               string.Equals(session.Owner, owner, StringComparison.Ordinal)
            ? session
            : throw new KeyNotFoundException($"Session '{sessionId}' is not locked by this receiver.");
    }

    private TopicState GetTopic(string topicName) =>
        _topics.TryGetValue(topicName, out var topic)
            ? topic
            : throw new KeyNotFoundException($"Topic '{topicName}' does not exist.");

    private void Log(string verb, string entityPath, string kind)
    {
        if (_activationWaitingForSpace)
            _nextActivation = DateTimeOffset.MinValue;
        _operationLog.Add(new AdminOperation(verb, entityPath, kind, _options.TimeProvider.GetUtcNow()));
    }

    private SubscriptionState GetSubscription(string topicName, string subscriptionName)
    {
        var topic = GetTopic(topicName);
        return topic.Subscriptions.TryGetValue(subscriptionName, out var subscription)
            ? subscription
            : throw new KeyNotFoundException($"Subscription '{topicName}/{subscriptionName}' does not exist.");
    }

    private static TimeSpan? Min(params TimeSpan?[] values)
    {
        var present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Min();
    }

    private TopologySnapshot GetTopologySnapshotCore() => new(
        _topics.Values.Select(topic => new TopologyTopic(
            topic.Definition,
            topic.Subscriptions.Values.Select(ToTopologySubscription).ToArray())).ToArray());

    private static TopologySubscription ToTopologySubscription(SubscriptionState subscription) => new(
        subscription.Definition,
        subscription.Rules.Select(rule => rule.Definition).ToArray());

    private static TopologySnapshot ReplaceTopicDefinition(
        TopologySnapshot snapshot,
        TopicDefinition definition) => snapshot with
    {
        Topics = snapshot.Topics.Select(topic =>
            NamesEqual(topic.Definition.Name, definition.Name)
                ? topic with { Definition = definition }
                : topic).ToArray(),
    };

    private static TopologySnapshot ReplaceTopicSubscriptions(
        TopologySnapshot snapshot,
        string topicName,
        Func<IReadOnlyList<TopologySubscription>, IReadOnlyList<TopologySubscription>> replace) => snapshot with
    {
        Topics = snapshot.Topics.Select(topic =>
            NamesEqual(topic.Definition.Name, topicName)
                ? topic with { Subscriptions = replace(topic.Subscriptions) }
                : topic).ToArray(),
    };

    private static TopologySnapshot ReplaceSubscriptionRules(
        TopologySnapshot snapshot,
        string topicName,
        string subscriptionName,
        Func<IReadOnlyList<RuleDefinition>, IReadOnlyList<RuleDefinition>> replace) =>
        ReplaceTopicSubscriptions(
            snapshot,
            topicName,
            subscriptions => subscriptions.Select(subscription =>
                NamesEqual(subscription.Definition.Name, subscriptionName)
                    ? subscription with { Rules = replace(subscription.Rules) }
                    : subscription).ToArray());

    private void ApplyCreateTopic(string topicName, TopicState state)
    {
        lock (_gate)
        {
            _topics.Add(topicName, state);
            Log("PUT", topicName, "Create");
            TopicCreated?.Invoke(topicName);
        }
    }

    private void ApplyUpdateTopic(TopicState state, TopicDefinition definition)
    {
        lock (_gate)
        {
            state.Definition = definition;
            state.UpdatedAt = _options.TimeProvider.GetUtcNow();
            Log("PUT", definition.Name, "Update");
        }
    }

    private void ApplyCreateSubscription(string topicName, SubscriptionState state)
    {
        lock (_gate)
        {
            GetTopic(topicName).Subscriptions.Add(state.Definition.Name, state);
            Log("PUT", $"{topicName}/Subscriptions/{state.Definition.Name}", "Create");
            SubscriptionCreated?.Invoke(topicName, state.Definition.Name);
        }
    }

    private void ApplyUpdateSubscription(
        string topicName,
        SubscriptionState state,
        SubscriptionDefinition definition)
    {
        lock (_gate)
        {
            state.Definition = definition;
            state.UpdatedAt = _options.TimeProvider.GetUtcNow();
            if (definition.Status == BrokerEntityStatus.Active && !string.IsNullOrEmpty(definition.ForwardTo))
            {
                ForwardPending(state);
            }

            Log("PUT", $"{topicName}/Subscriptions/{definition.Name}", "Update");
        }
    }

    private void ApplyCreateRule(string topicName, SubscriptionState subscription, RuleState state)
    {
        lock (_gate)
        {
            subscription.Rules.Add(state);
            Log("PUT", $"{topicName}/Subscriptions/{subscription.Definition.Name}/Rules/{state.Definition.Name}", "Create");
        }
    }

    private static bool NamesEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private sealed class TopicState(TopicDefinition definition, DateTimeOffset createdAt)
    {
        public TopicDefinition Definition { get; set; } = definition;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public DateTimeOffset UpdatedAt { get; set; } = createdAt;

        public DateTimeOffset AccessedAt { get; set; } = createdAt;

        public Dictionary<string, SubscriptionState> Subscriptions { get; } = new(StringComparer.OrdinalIgnoreCase);

        public long NextSequence { get; set; }
    }

    private sealed class SubscriptionState(SubscriptionDefinition definition, DateTimeOffset createdAt, RuleDefinition? initialRule = null)
    {
        public SubscriptionDefinition Definition { get; set; } = definition;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public DateTimeOffset UpdatedAt { get; set; } = createdAt;

        public DateTimeOffset AccessedAt { get; set; } = createdAt;

        public DateTimeOffset NextExpiry { get; set; } = DateTimeOffset.MaxValue;

        public long StoredBytes => Messages.Sum(item => (long)item.Message.Body.Length)
            + DeadLetter.Sum(item => (long)item.Message.Body.Length)
            + TransferDeadLetter.Sum(item => (long)item.Message.Body.Length)
            + SessionState.Values.Sum(state => (long)state.Length);

        public List<RuleState> Rules { get; } = [new(initialRule ?? new RuleDefinition("$Default", "1=1"))];

        public List<StoredMessage> Messages { get; } = [];

        public List<StoredMessage> DeadLetter { get; } = [];

        public List<StoredMessage> TransferDeadLetter { get; } = [];

        public Dictionary<string, SessionLock> Sessions { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ReadOnlyMemory<byte>> SessionState { get; } = new(StringComparer.Ordinal);
    }

    private sealed class RuleState(RuleDefinition definition)
    {
        public RuleDefinition Definition { get; } = definition;

        public CompiledSqlRule Compiled { get; } = SqlRuleEngine.Compile(definition.FilterExpression, definition.ActionExpression);
    }

    private sealed class StoredMessage(BrokerMessage message, DateTimeOffset? expiresAt)
    {
        public BrokerMessage Message { get; } = message;

        public DateTimeOffset? ExpiresAt { get; } = expiresAt;

        public MessageState State { get; set; } = MessageState.Active;

        public int PriorFailedAttempts { get; set; }

        public Guid LockToken { get; set; }

        public string? Owner { get; set; }

        public DateTimeOffset LockedUntil { get; set; }

        public void ReleaseWithoutFailure()
        {
            State = MessageState.Active;
            LockToken = Guid.Empty;
            Owner = null;
            LockedUntil = default;
            Message.LockToken = Guid.Empty;
            Message.LockedUntil = default;
        }
    }

    private sealed record ScheduledMessage(long SequenceNumber, string TopicName, BrokerMessage Message, DateTimeOffset Due);

    private sealed record SessionLock(string Owner, DateTimeOffset LockedUntil);

    private enum MessageState
    {
        Active,
        Locked,
        DeadLetter,
    }
}

internal static class BrokerMessageExtensions
{
    public static BrokerMessage CloneForDelivery(this BrokerMessage message)
    {
        var clone = message.Copy();
        clone.SequenceNumber = message.SequenceNumber;
        clone.EnqueuedTime = message.EnqueuedTime;
        clone.DeliveryCount = message.DeliveryCount;
        clone.LockToken = message.LockToken;
        clone.LockedUntil = message.LockedUntil;
        return clone;
    }
}
