#pragma warning disable CA1707, CA2007

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Reprocess and purge rewrite the session state instead of wiping it, so the endpoint's
/// replay guard keeps its position (Spec 041 §2.7, bug 1).
/// </summary>
[TestClass]
public class AdminSessionStateClearTests
{
    [TestMethod]
    public async Task ClearSessionState_Reprocess_ClearsTheBlockAndKeepsTheReplayPosition()
    {
        var receiver = new StateRecordingReceiver(new SessionState
        {
            BlockedByEventId = "evt-1",
            DeferredCount = 1,
            NextDeferralSequence = 1,
            ReplayedThroughSequence = -1,
        }.ToBinaryData());

        await AdminService.ClearSessionState(receiver, state => state.ClearForReprocess());

        var written = SessionState.FromBinaryData(receiver.Written);
        Assert.IsNull(written.BlockedByEventId);
        Assert.AreEqual(1, written.NextDeferralSequence);
        Assert.IsTrue(written.HasUnreplayedMessages());
    }

    [TestMethod]
    public async Task ClearSessionState_UnreadableState_IsClearedOutright()
    {
        var receiver = new StateRecordingReceiver(new BinaryData(Encoding.UTF8.GetBytes("not json")));

        await AdminService.ClearSessionState(receiver, state => state.ClearForReprocess());

        Assert.IsTrue(receiver.WasWritten);
        Assert.IsNull(receiver.Written);
    }

    private sealed class StateRecordingReceiver : ServiceBusSessionReceiver
    {
        private readonly BinaryData? _stored;

        public StateRecordingReceiver(BinaryData? stored) => _stored = stored;

        public bool WasWritten { get; private set; }

        public BinaryData? Written { get; private set; }

        public override Task<BinaryData> GetSessionStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_stored!);

        public override Task SetSessionStateAsync(BinaryData sessionState, CancellationToken cancellationToken = default)
        {
            WasWritten = true;
            Written = sessionState;
            return Task.CompletedTask;
        }
    }
}
