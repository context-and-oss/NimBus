#pragma warning disable CA1707, CA2007
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using NimBus.Core.Messages;
using NimBus.Core.Messages.PII;
using NimBus.Extensions.IntegrationIntelligence.Controllers;
using NimBus.Extensions.IntegrationIntelligence.Evidence;
using NimBus.Extensions.IntegrationIntelligence.Storage;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Extensions.IntegrationIntelligence.Tests;

[TestClass]
public sealed class ServiceAndEvidenceTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Ambiguous_Completion_Reads_Back_Or_Fences_Unknown_Without_Replay(bool commit)
    {
        var store = new AmbiguousCompletionStore(commit);
        var fixture = new ServiceFixture(store);
        var key = Guid.NewGuid().ToString("D");
        if (commit)
        {
            var result = await fixture.Service.AnalyzeAsync("event", "failure", key, false, default);
            Assert.AreEqual(1, result.Result.Revision);
            var replay = await fixture.Service.AnalyzeAsync("event", "failure", key, false, default);
            Assert.IsTrue(replay.Cached);
        }
        else
        {
            var error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => fixture.Service.AnalyzeAsync("event", "failure", key, false, default));
            Assert.AreEqual(503, error.StatusCode);
            error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => fixture.Service.AnalyzeAsync("event", "failure", Guid.NewGuid().ToString("D"), false, default));
            Assert.AreEqual("AnalysisOutcomeUnknown", error.Code);
            Assert.AreEqual(409, error.StatusCode);
        }
        Assert.AreEqual(1, fixture.Provider.Calls);
    }

    [TestMethod]
    public async Task Retention_Retries_After_Outage_And_Fences_Reservations_Without_Results()
    {
        var store = new InMemoryClassificationStore();
        var fixture = new ServiceFixture(store);
        var reservation = await store.ReserveAsync("failure", Guid.NewGuid().ToString("D"), false, DateTimeOffset.UtcNow,
            new ClassificationScope("failure", "event", "endpoint", null));
        fixture.Source.Throw = true;
        await ClassificationRetentionWorker.ReconcileAsync(store, fixture.Messages, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, default);
        Assert.AreEqual("Active", (await store.GetLatestOperationAsync("failure"))!.Status);
        fixture.Source.Throw = false;
        fixture.Source.Deleted = true;
        await ClassificationRetentionWorker.ReconcileAsync(store, fixture.Messages, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, default);
        await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => store.CompleteAsync(reservation.OperationId, DurableStoreConformanceTests.Result("failure", 1)));
        Assert.HasCount(0, await store.GetHistoryAsync("failure"));
    }

    [TestMethod]
    public async Task Evidence_Redacts_Before_Truncation_And_Withholds_Unverifiable_Text()
    {
        var fixture = new ServiceFixture();
        fixture.Options.MaximumErrorTextLength = 8;
        fixture.Source.Message.MessageContent.ErrorContent!.ErrorText = "sensitive-long-value";
        var input = await fixture.Evidence.BuildAsync("event", "failure");
        Assert.IsFalse(input!.Exception.Message!.Contains("sensitiv", StringComparison.Ordinal));
        fixture.Source.Message.MessageContent.EventContent = null!;
        input = await fixture.Evidence.BuildAsync("event", "failure");
        Assert.IsNull(input!.Exception.Message);
    }

    [TestMethod]
    public async Task Disabled_History_Is_Not_Read_And_Required_State_Has_Hard_Limit()
    {
        var fixture = new ServiceFixture();
        fixture.Options.Data.IncludeRecentFailureHistory = false;
        await fixture.Evidence.BuildAsync("event", "failure");
        Assert.AreEqual(0, fixture.Source.HistoryReads);
        fixture.Options.MaximumStateCharacters = 1000;
        fixture.Source.Message.EventTypeId = new string('x', 2000);
        var error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => fixture.Evidence.BuildAsync("event", "failure"));
        Assert.AreEqual("EvidenceTooLarge", error.Code);
    }

    [TestMethod]
    public async Task Ineligible_Post_Is_409_Audited_And_Never_Calls_Provider()
    {
        var fixture = new ServiceFixture();
        fixture.Source.Current.ResolutionStatus = ResolutionStatus.Completed;
        var error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => fixture.Service.AnalyzeAsync("event", "failure", Guid.NewGuid().ToString("D"), false, default));
        Assert.AreEqual(409, error.StatusCode);
        Assert.HasCount(1, fixture.Host.Audits);
        Assert.AreEqual(0, fixture.Provider.Calls);
    }

    [TestMethod]
    public async Task Reader_Can_Read_Saved_Result_After_Recovery_But_Cannot_Analyze()
    {
        var fixture = new ServiceFixture();
        var analyzed = await fixture.Service.AnalyzeAsync("event", "failure", Guid.NewGuid().ToString("D"), false, default);
        fixture.Host.Contributor = false;
        fixture.Source.Current.ResolutionStatus = ResolutionStatus.Completed;
        Assert.AreEqual(analyzed.Result.Id, (await fixture.Service.GetLatestAsync("event", "failure", default))!.Id);
        var error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => fixture.Service.AnalyzeAsync("event", "failure", Guid.NewGuid().ToString("D"), false, default));
        Assert.AreEqual(403, error.StatusCode);
        Assert.HasCount(2, fixture.Host.Audits);
        Assert.AreEqual(1, fixture.Provider.Calls);
    }

    [TestMethod]
    public async Task Reader_Is_Required_For_Status_And_Store_Errors_Are_Sanitized_503()
    {
        var fixture = new ServiceFixture();
        fixture.Host.Reader = false;
        var status = new IntegrationIntelligenceStatusService(fixture.Options, new(true, true, []), fixture.Host);
        var error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => status.GetAsync("endpoint", default));
        Assert.AreEqual(403, error.StatusCode);
        fixture.Host.Reader = true;
        fixture.Source.Throw = true;
        var response = await new IntegrationIntelligenceController(fixture.Service).GetClassification("event", "failure", default);
        Assert.AreEqual(503, ((ObjectResult)response).StatusCode);
        Assert.DoesNotContain("credential", System.Text.Json.JsonSerializer.Serialize(((ObjectResult)response).Value));
    }

    [TestMethod]
    public async Task Provider_Timeout_Persists_Unknown_And_Only_New_Force_Can_Retry()
    {
        var fixture = new ServiceFixture();
        fixture.Provider.Throw = true;
        var key = Guid.NewGuid().ToString("D");
        var error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => fixture.Service.AnalyzeAsync("event", "failure", key, false, default));
        Assert.AreEqual(503, error.StatusCode);
        fixture.Provider.Throw = false;
        error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => fixture.Service.AnalyzeAsync("event", "failure", Guid.NewGuid().ToString("D"), false, default));
        Assert.AreEqual("AnalysisOutcomeUnknown", error.Code);
        Assert.AreEqual(409, error.StatusCode);
        Assert.AreEqual(1, fixture.Provider.Calls);
        await fixture.Service.AnalyzeAsync("event", "failure", Guid.NewGuid().ToString("D"), true, default);
        Assert.AreEqual(2, fixture.Provider.Calls);
    }

    [TestMethod]
    public async Task Invalid_Provider_Response_Is_Sanitized_503_And_Audited_Without_Replay()
    {
        var fixture = new ServiceFixture();
        fixture.Provider.Failure = new NimBus.Extensions.IntegrationIntelligence.Providers.IntelligenceProviderException(
            "ProviderInvalidResponse", false, "raw provider detail and secret-value");

        var error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() =>
            fixture.Service.AnalyzeAsync("event", "failure", Guid.NewGuid().ToString("D"), false, default));

        Assert.AreEqual(503, error.StatusCode);
        Assert.AreEqual("ProviderUnavailable", error.Code);
        Assert.HasCount(1, fixture.Host.Audits);
        Assert.DoesNotContain("raw provider detail", string.Join(" ", fixture.Host.Audits));
        StringAssert.Contains(fixture.Host.Audits[0], "ProviderError", StringComparison.Ordinal);
        Assert.AreEqual(1, fixture.Provider.Calls);
    }

    [TestMethod]
    public void Extension_Does_Not_Reference_Workflow_Mutation_Assemblies_Or_Contracts()
    {
        var assembly = typeof(FailureClassificationService).Assembly;
        foreach (var reference in assembly.GetReferencedAssemblies())
            Assert.IsFalse(reference.Name!.StartsWith("NimBus.Resolver", StringComparison.Ordinal)
                || reference.Name.StartsWith("NimBus.Manager", StringComparison.Ordinal)
                || reference.Name.StartsWith("NimBus.WebApp", StringComparison.Ordinal)
                || reference.Name.StartsWith("NimBus.ServiceBus", StringComparison.Ordinal));
        var forbidden = new HashSet<string>(StringComparer.Ordinal) { "ISender", "IManagerClient", "IResolver", "IAdminService", "IEndpointStateStore", "ISubscriptionStore" };
        foreach (var type in assembly.GetTypes())
            foreach (var constructor in type.GetConstructors())
                foreach (var parameter in constructor.GetParameters())
                    Assert.IsFalse(forbidden.Contains(parameter.ParameterType.Name), $"{type.Name} takes {parameter.ParameterType.Name}");
    }
}

internal sealed class AmbiguousCompletionStore(bool commit) : IFailureClassificationStore
{
    private readonly InMemoryClassificationStore _inner = new();
    public Task<FailureClassification?> GetLatestAsync(string id, CancellationToken cancellationToken = default) => _inner.GetLatestAsync(id, cancellationToken);
    public Task<IReadOnlyList<FailureClassification>> GetHistoryAsync(string id, CancellationToken cancellationToken = default) => _inner.GetHistoryAsync(id, cancellationToken);
    public Task<ClassificationOperation?> GetLatestOperationAsync(string id, CancellationToken cancellationToken = default) => _inner.GetLatestOperationAsync(id, cancellationToken);
    public Task<ClassificationReservation> ReserveAsync(string id, string key, bool force, DateTimeOffset now, ClassificationScope? scope = null, CancellationToken cancellationToken = default) => _inner.ReserveAsync(id, key, force, now, scope, cancellationToken);
    public Task FailAsync(ClassificationReservation reservation, string code, CancellationToken cancellationToken = default) => _inner.FailAsync(reservation, code, cancellationToken);
    public async Task CompleteAsync(string id, FailureClassification result, CancellationToken cancellationToken = default)
    {
        if (commit) await _inner.CompleteAsync(id, result, cancellationToken);
        throw new IOException("Simulated lost acknowledgement");
    }
}

internal sealed class ServiceFixture
{
    public FailureClassificationOptions Options { get; } = new();
    public TestIntelligenceHost Host { get; } = new();
    public TestIntelligenceProvider Provider { get; } = new();
    public IMessageTrackingStore Messages { get; }
    public MessageSource Source => (MessageSource)(object)Messages;
    public FailureEvidenceBuilder Evidence { get; }
    public FailureClassificationService Service { get; }
    public ServiceFixture(IFailureClassificationStore? store = null)
    {
        Messages = DispatchProxy.Create<IMessageTrackingStore, MessageSource>();
        Evidence = new(Messages, new TestMasker(), null, new IntelligenceDataRedactor(), Options);
        Service = new(Options, Host, store ?? new InMemoryClassificationStore(), Evidence, Provider, Messages);
    }
}

public class MessageSource : DispatchProxy
{
    public MessageEntity Message { get; } = new()
    {
        MessageId = "failure", EventId = "event", EndpointId = "endpoint", EventTypeId = "type", MessageType = MessageType.ErrorResponse,
        MessageContent = new() { EventContent = new() { EventJson = "{}" }, ErrorContent = new() { ErrorType = "BusinessException", ErrorText = "A business failure" } },
    };
    public UnresolvedEvent Current { get; } = new() { ResolutionStatus = ResolutionStatus.Failed, EndpointId = "endpoint", EventId = "event" };
    public bool Throw { get; set; }
    public bool Deleted { get; set; }
    public int HistoryReads { get; private set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (Throw) throw new InvalidOperationException("credential must never leak");
        if (targetMethod!.Name == "GetMessage") return Task.FromResult(Message);
        if (targetMethod.Name == "GetEvent") return Task.FromResult(Deleted ? null! : Current);
        if (targetMethod.Name == "GetEventHistory") { HistoryReads++; return Task.FromResult<IEnumerable<MessageEntity>>([]); }
        throw new NotSupportedException(targetMethod.Name);
    }
}

internal sealed class TestMasker : IEventJsonMasker
{
    public string Mask(string eventTypeId, string eventJson) => eventJson;
    public bool ContainsRedactPlaceholder(string eventTypeId, string eventJson) => false;
    public string StripMaskedMarker(string eventJson) => eventJson;
    public bool TryCollectSensitiveValues(string eventTypeId, string eventJson, out IReadOnlyCollection<string> values) { values = ["sensitive-long-value"]; return true; }
}

internal sealed class TestIntelligenceHost : IIntegrationIntelligenceHost
{
    public bool Reader { get; set; } = true;
    public bool Contributor { get; set; } = true;
    public string? CurrentActor => "operator";
    public List<string> Audits { get; } = [];
    public bool AuditThrows { get; set; }
    public Task<bool> EndpointExistsAsync(string endpointId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<bool> HasReaderAsync(string endpointId, CancellationToken cancellationToken = default) => Task.FromResult(Reader);
    public Task<bool> HasContributorAsync(string endpointId, CancellationToken cancellationToken = default) => Task.FromResult(Contributor);
    public Task AuditAsync(MessageAuditType type, string? eventId, string? endpointId, string data, bool accessDenied, CancellationToken cancellationToken = default)
    {
        if (AuditThrows) throw new InvalidOperationException("audit unavailable");
        Audits.Add(data);
        return Task.CompletedTask;
    }
}

internal sealed class TestIntelligenceProvider : IFailureIntelligenceProvider
{
    public int Calls { get; private set; }
    public bool Throw { get; set; }
    public Exception? Failure { get; set; }
    public string Name => "TypeSafe";
    public Task<FailureIntelligenceProviderResult> ClassifyAsync(FailureClassificationInput input, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Failure is not null) throw Failure;
        if (Throw) throw new TaskCanceledException();
        return Task.FromResult(new FailureIntelligenceProviderResult("test", "unknown", 1, new Dictionary<string, double> { ["unknown"] = 1 }, 0, 0, 0, null, null));
    }
}
