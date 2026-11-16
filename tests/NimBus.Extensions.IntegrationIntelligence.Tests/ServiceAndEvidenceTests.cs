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

    private const string StoredTrace =
        @"   at Crm.Adapter.CustomerMapper.MapAddress(CustomerDto dto) in C:\agent\_work\1\s\src\Crm.Adapter\CustomerMapper.cs:line 42" + "\n" +
        "   at Crm.Adapter.Handlers.Upsert(String id) in /home/runner/work/crm/src/Handlers.cs:line 7 with sensitive-long-value Password=hunter2";

    [TestMethod]
    public async Task Evidence_Carries_No_Stack_Trace_Unless_An_Owner_Allows_It()
    {
        var fixture = new ServiceFixture();
        fixture.Source.Message.MessageContent.ErrorContent!.ExceptionStackTrace = StoredTrace;

        var input = await fixture.Evidence.BuildAsync("event", "failure");

        Assert.IsNull(input!.StackTrace);
    }

    [TestMethod]
    public async Task An_Allowed_Stack_Trace_Keeps_Frames_And_File_Names_And_Drops_Paths_And_Secrets()
    {
        // Spec 044 Phase 3: frames are what the agent needs; build paths, sensitive values and
        // credentials are not.
        var fixture = new ServiceFixture();
        fixture.Options.IncludeStackTrace = true;
        fixture.Source.Message.MessageContent.ErrorContent!.ExceptionStackTrace = StoredTrace;

        var trace = (await fixture.Evidence.BuildAsync("event", "failure"))!.StackTrace!;

        StringAssert.Contains(trace, "Crm.Adapter.CustomerMapper.MapAddress(CustomerDto dto) in CustomerMapper.cs:line 42", StringComparison.Ordinal);
        StringAssert.Contains(trace, "in Handlers.cs:line 7", StringComparison.Ordinal);
        Assert.IsFalse(trace.Contains("_work", StringComparison.Ordinal) || trace.Contains("/home/runner", StringComparison.Ordinal), trace);
        Assert.IsFalse(trace.Contains("sensitive-long-value", StringComparison.Ordinal), trace);
        Assert.IsFalse(trace.Contains("hunter2", StringComparison.Ordinal), trace);
    }

    [TestMethod]
    public async Task A_Stack_Trace_Whose_Sensitive_Values_Cannot_Be_Read_Is_Withheld()
    {
        var fixture = new ServiceFixture();
        fixture.Options.IncludeStackTrace = true;
        fixture.Source.Message.MessageContent.ErrorContent!.ExceptionStackTrace = StoredTrace;
        fixture.Source.Message.MessageContent.EventContent = null!;

        Assert.IsNull((await fixture.Evidence.BuildAsync("event", "failure"))!.StackTrace);
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
    public async Task Reader_Can_Read_Saved_Result_After_Recovery()
    {
        var store = new InMemoryClassificationStore();
        var fixture = new ServiceFixture(store);
        var reservation = await store.ReserveAsync("failure", Guid.NewGuid().ToString("D"), false, DateTimeOffset.UtcNow,
            new ClassificationScope("failure", "event", "endpoint", null));
        var saved = DurableStoreConformanceTests.Result("failure", reservation.Revision);
        await store.CompleteAsync(reservation.OperationId, saved);
        fixture.Host.Contributor = false;
        fixture.Source.Current.ResolutionStatus = ResolutionStatus.Completed;

        Assert.AreEqual(saved.Id, (await fixture.Reader.GetLatestAsync("event", "failure", default))!.Id);
    }

    [TestMethod]
    public async Task Reader_Is_Required_For_Status_And_Store_Errors_Are_Sanitized_503()
    {
        var fixture = new ServiceFixture();
        fixture.Host.Reader = false;
        var status = new IntegrationIntelligenceStatusService(fixture.Options, new(true, []), fixture.Host);
        var error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => status.GetAsync("endpoint", default));
        Assert.AreEqual(403, error.StatusCode);
        fixture.Host.Reader = true;
        fixture.Source.Throw = true;
        var response = await new IntegrationIntelligenceReadController(fixture.Reader).GetClassification("event", "failure", default);
        Assert.AreEqual(503, ((ObjectResult)response).StatusCode);
        Assert.DoesNotContain("credential", System.Text.Json.JsonSerializer.Serialize(((ObjectResult)response).Value));
    }

    [TestMethod]
    public void Extension_Does_Not_Reference_Workflow_Mutation_Assemblies_Or_Contracts()
    {
        var assembly = typeof(FailureClassificationReader).Assembly;
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

internal sealed class ServiceFixture
{
    public FailureClassificationOptions Options { get; } = new();
    public TestIntelligenceHost Host { get; } = new();
    public IMessageTrackingStore Messages { get; }
    public MessageSource Source => (MessageSource)(object)Messages;
    public FailureEvidenceBuilder Evidence { get; }
    public FailureClassificationReader Reader { get; }
    public ServiceFixture(IFailureClassificationStore? store = null)
    {
        store ??= new InMemoryClassificationStore();
        Messages = DispatchProxy.Create<IMessageTrackingStore, MessageSource>();
        Evidence = new(Messages, new TestMasker(), null, new IntelligenceDataRedactor(), Options);
        Reader = new(Host, store, Messages);
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
    public List<MessageEntity> History { get; } = [];
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (Throw) throw new InvalidOperationException("credential must never leak");
        if (targetMethod!.Name == "GetMessage") return Task.FromResult(Message);
        if (targetMethod.Name == "GetEvent") return Task.FromResult(Deleted ? null! : Current);
        if (targetMethod.Name == "GetEventHistory") { HistoryReads++; return Task.FromResult<IEnumerable<MessageEntity>>(History.ToList()); }
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
