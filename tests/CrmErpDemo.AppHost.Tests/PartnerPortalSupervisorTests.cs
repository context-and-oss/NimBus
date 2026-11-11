#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CrmErpDemo.AppHost.Tests;

[TestClass]
public sealed class PartnerPortalSupervisorTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RunSupervisedAsync_WhenLoopFaults_CancelsAndAwaitsSibling(bool publisherFaults)
    {
        var expected = new InvalidOperationException("loop failed");
        var siblingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingCancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSiblingToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task FaultingLoop(CancellationToken cancellationToken)
        {
            await siblingStarted.Task.WaitAsync(cancellationToken);
            throw expected;
        }

        async Task SiblingLoop(CancellationToken cancellationToken)
        {
            siblingStarted.SetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                siblingCancellationObserved.SetResult();
                await allowSiblingToFinish.Task;
                throw;
            }
        }

        var runTask = publisherFaults
            ? Program.RunSupervisedAsync(FaultingLoop, SiblingLoop, CancellationToken.None)
            : Program.RunSupervisedAsync(SiblingLoop, FaultingLoop, CancellationToken.None);

        await siblingCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(runTask.IsCompleted, "The supervisor must await the canceled sibling loop.");

        allowSiblingToFinish.SetResult();
        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runTask);

        Assert.AreSame(expected, actual);
    }

    [TestMethod]
    public async Task RunSupervisedAsync_WithoutPublisher_KeepsConsumerRunningUntilCanceled()
    {
        var consumerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdown = new CancellationTokenSource();

        async Task ConsumerLoop(CancellationToken cancellationToken)
        {
            consumerStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var runTask = Program.RunSupervisedAsync(publisher: null, ConsumerLoop, shutdown.Token);

        await consumerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.IsFalse(runTask.IsCompleted, "With lead publishing off, the consumer must keep running.");

        await shutdown.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => runTask);
    }

    [TestMethod]
    [DataRow(null, true)]
    [DataRow("", true)]
    [DataRow("true", true)]
    [DataRow("not-a-bool", true)]
    [DataRow("false", false)]
    [DataRow("FALSE", false)]
    [DataRow(" false ", false)]
    public void IsLeadPublishingEnabled_IsOffOnlyForFalse(string? value, bool expected)
    {
        Assert.AreEqual(expected, Program.IsLeadPublishingEnabled(value));
    }
}
