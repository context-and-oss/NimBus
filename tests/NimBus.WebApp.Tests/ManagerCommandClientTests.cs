#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Manager;
using NimBus.MessageStore;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Journaled operator commands publish under the operation id so the WebApp can find the
/// command and its response in the Resolver's history (Spec 044 Phase 2, decision D1).
/// </summary>
[TestClass]
public sealed class ManagerCommandClientTests
{
    private static MessageEntity Failure() => new()
    {
        EventId = "event-1",
        SessionId = "session-1",
        MessageId = "failed-attempt",
        CorrelationId = "correlation-1",
    };

    [TestMethod]
    public async Task A_resubmit_with_a_command_id_is_published_under_that_id()
    {
        var bus = new RecordingServiceBusClient();
        var client = new ManagerClient(bus);

        await client.Resubmit(Failure(), "Crm", "CrmAccountCreated", "{}", "op-1");

        Assert.AreEqual("op-1", bus.Sent.Single().MessageId);
        Assert.AreEqual("Crm", bus.Entity);
    }

    [TestMethod]
    public async Task A_skip_with_a_command_id_is_published_under_that_id()
    {
        var bus = new RecordingServiceBusClient();
        var client = new ManagerClient(bus);

        await client.Skip(Failure(), "Crm", "CrmAccountCreated", "op-2");

        Assert.AreEqual("op-2", bus.Sent.Single().MessageId);
    }

    [TestMethod]
    public async Task The_existing_overloads_leave_the_message_id_to_the_broker()
    {
        var bus = new RecordingServiceBusClient();
        var client = new ManagerClient(bus);

        await client.Resubmit(Failure(), "Crm", "CrmAccountCreated", "{}");
        await client.Skip(Failure(), "Crm", "CrmAccountCreated");

        // ServiceBusMessage assigns no id of its own until the SDK sends it.
        Assert.IsTrue(bus.Sent.TrueForAll(m => string.IsNullOrEmpty(m.MessageId)));
    }

    [TestMethod]
    [DataRow("", DisplayName = "empty")]
    [DataRow("   ", DisplayName = "blank")]
    public async Task A_blank_command_id_is_refused(string id)
    {
        var client = new ManagerClient(new RecordingServiceBusClient());

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.Resubmit(Failure(), "Crm", "T", "{}", id));
    }

    [TestMethod]
    public async Task A_command_id_longer_than_Service_Bus_allows_is_refused()
    {
        var client = new ManagerClient(new RecordingServiceBusClient());

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.Skip(Failure(), "Crm", "T", new string('x', 129)));
    }

    private sealed class RecordingServiceBusClient : ServiceBusClient
    {
        public List<ServiceBusMessage> Sent { get; } = [];
        public string? Entity { get; private set; }

        public override ServiceBusSender CreateSender(string queueOrTopicName)
        {
            Entity = queueOrTopicName;
            return new RecordingSender(Sent);
        }
    }

    private sealed class RecordingSender(List<ServiceBusMessage> sent) : ServiceBusSender
    {
        public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            sent.Add(message);
            return Task.CompletedTask;
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
