#pragma warning disable CA1707, CA2007
using System.Reflection;
using NimBus.Extensions.IntegrationIntelligence.Evidence;
using NimBus.Extensions.IntegrationIntelligence.Storage;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Extensions.IntegrationIntelligence.Tests;

/// <summary>Spec 044 §6.4: an agent records a classification; NimBus validates, scrubs and composes guidance.</summary>
[TestClass]
public sealed class RecorderTests
{
    [TestMethod]
    public async Task Records_An_Agent_Result_With_Provenance_And_Server_Composed_Guidance()
    {
        var fixture = new RecorderFixture();
        var request = Answers() with { Model = "claude-opus-5-5", ClientId = "client-1", Rationale = "Field customerId is empty; rule: required." };

        var (result, existing) = await fixture.Recorder.RecordAsync(request, default);

        Assert.IsFalse(existing);
        Assert.AreEqual("Agent", result.Provider);
        Assert.AreEqual("claude-opus-5-5", result.Model);
        Assert.AreEqual("client-1", result.ClientId);
        Assert.AreEqual("operator", result.RequestedBy);
        Assert.AreEqual(FailureClassificationQuestionSet.Version, result.QuestionSetVersion);
        Assert.AreEqual("source_data", result.FaultOrigin);
        Assert.AreEqual(0.8, result.FaultOriginConfidence);
        Assert.AreEqual("Field customerId is empty; rule: required.", result.Rationale);
        Assert.IsNull(result.InputTokens);
        Assert.IsNull(result.OutputTokens);
        Assert.HasCount(1, result.CategoryProbabilities);
        Assert.AreEqual(0.9, result.CategoryProbabilities["business_rule"]);
        Assert.AreEqual(
            FailureGuidanceRules.Compose(request.Category, request.CategoryConfidence, request.RetryLikelihood, request.ChangeRequiredLikelihood, fixture.Options),
            result.Guidance);
        Assert.AreEqual(FailureGuidance.ChangeLikelyRequired, result.Guidance);
        Assert.AreEqual(result.Id, (await fixture.Store.GetLatestAsync("failure"))!.Id);
        Assert.HasCount(1, fixture.Host.Audits);
        StringAssert.Contains(fixture.Host.Audits[0], "\"provider\":\"Agent\"", StringComparison.Ordinal);
        StringAssert.Contains(fixture.Host.Audits[0], "\"clientId\":\"client-1\"", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task A_Missing_Model_Is_Stored_As_Unspecified()
    {
        var fixture = new RecorderFixture();
        var (result, _) = await fixture.Recorder.RecordAsync(Answers() with { Model = "  " }, default);
        Assert.AreEqual("unspecified", result.Model);
    }

    [TestMethod]
    [DataRow("category")]
    [DataRow("origin")]
    [DataRow("confidence-high")]
    [DataRow("confidence-nan")]
    [DataRow("retry-negative")]
    [DataRow("change-infinite")]
    [DataRow("external-high")]
    [DataRow("origin-confidence")]
    [DataRow("question-set")]
    [DataRow("model-long")]
    [DataRow("model-control")]
    [DataRow("rationale-long")]
    [DataRow("rationale-control")]
    [DataRow("rationale-stack-trace")]
    [DataRow("idempotency-key")]
    public async Task Refuses_An_Invalid_Answer_And_Stores_Nothing(string invalid)
    {
        var fixture = new RecorderFixture();
        var request = invalid switch
        {
            "category" => Answers() with { Category = "made_up" },
            "origin" => Answers() with { FaultOrigin = "the_moon" },
            "confidence-high" => Answers() with { CategoryConfidence = 1.01 },
            "confidence-nan" => Answers() with { CategoryConfidence = double.NaN },
            "retry-negative" => Answers() with { RetryLikelihood = -0.1 },
            "change-infinite" => Answers() with { ChangeRequiredLikelihood = double.PositiveInfinity },
            "external-high" => Answers() with { ExternalDependencyLikelihood = 2 },
            "origin-confidence" => Answers() with { FaultOriginConfidence = -1 },
            "question-set" => Answers() with { QuestionSetVersion = 1 },
            "model-long" => Answers() with { Model = new string('m', 101) },
            "model-control" => Answers() with { Model = "model\u0007" },
            "rationale-long" => Answers() with { Rationale = new string('r', 1001) },
            "rationale-control" => Answers() with { Rationale = "bad\u0000text" },
            "rationale-stack-trace" => Answers() with { Rationale = "Boom\n   at Contoso.Handler.Run()" },
            "idempotency-key" => Answers() with { IdempotencyKey = "not-a-guid" },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid)),
        };

        var error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => fixture.Recorder.RecordAsync(request, default));

        Assert.AreEqual(400, error.StatusCode);
        Assert.AreEqual(invalid switch
        {
            "question-set" => "QuestionSetOutdated",
            "idempotency-key" => "InvalidIdempotencyKey",
            _ => "InvalidRecording",
        }, error.Code);
        Assert.IsNull(await fixture.Store.GetLatestOperationAsync("failure"));
    }

    [TestMethod]
    public async Task Refuses_A_Message_That_Is_No_Longer_Failed()
    {
        var fixture = new RecorderFixture();
        fixture.Source.Current.ResolutionStatus = ResolutionStatus.Completed;

        var error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => fixture.Recorder.RecordAsync(Answers(), default));

        Assert.AreEqual("FailureNotEligible", error.Code);
        Assert.IsNull(await fixture.Store.GetLatestOperationAsync("failure"));
    }

    [TestMethod]
    public async Task Refuses_A_Message_On_Another_Endpoint()
    {
        var fixture = new RecorderFixture();

        var error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() =>
            fixture.Recorder.RecordAsync(Answers() with { EndpointId = "other" }, default));

        Assert.AreEqual("FailureNotFound", error.Code);
        Assert.IsNull(await fixture.Store.GetLatestOperationAsync("failure"));
    }

    [TestMethod]
    public async Task Refuses_A_Caller_Without_Contributor_And_Audits_The_Denial()
    {
        var fixture = new RecorderFixture();
        fixture.Host.Contributor = false;

        var error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => fixture.Recorder.RecordAsync(Answers(), default));

        Assert.AreEqual(403, error.StatusCode);
        Assert.IsNull(await fixture.Store.GetLatestOperationAsync("failure"));
        Assert.HasCount(1, fixture.Host.Audits);
        Assert.IsTrue(fixture.Host.DeniedAudits[0]);
    }

    [TestMethod]
    public async Task Refuses_An_Endpoint_Outside_The_Allow_List()
    {
        var fixture = new RecorderFixture();
        fixture.Options.AllowedEndpoints = ["elsewhere"];

        var error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => fixture.Recorder.RecordAsync(Answers(), default));

        Assert.AreEqual("EndpointNotAllowed", error.Code);
        Assert.IsNull(await fixture.Store.GetLatestOperationAsync("failure"));
    }

    [TestMethod]
    public async Task Repeating_An_Idempotency_Key_Returns_The_Stored_Result()
    {
        var fixture = new RecorderFixture();
        var request = Answers();

        var first = await fixture.Recorder.RecordAsync(request, default);
        var second = await fixture.Recorder.RecordAsync(request with { Category = "unknown" }, default);

        Assert.AreEqual(first.Result.Id, second.Result.Id);
        Assert.AreEqual("business_rule", second.Result.Category);
        Assert.IsTrue(second.Existing);
        Assert.HasCount(1, await fixture.Store.GetHistoryAsync("failure"));
    }

    [TestMethod]
    public async Task Without_Supersede_An_Existing_Result_Is_Returned_And_No_Revision_Is_Written()
    {
        var fixture = new RecorderFixture();
        var first = await fixture.Recorder.RecordAsync(Answers(), default);

        var again = await fixture.Recorder.RecordAsync(Answers() with { IdempotencyKey = Guid.NewGuid().ToString("D"), Category = "unknown" }, default);

        Assert.IsTrue(again.Existing);
        Assert.AreEqual(first.Result.Id, again.Result.Id);
        Assert.HasCount(1, await fixture.Store.GetHistoryAsync("failure"));
    }

    [TestMethod]
    public async Task Supersede_Writes_A_New_Revision_And_Keeps_The_Old_One()
    {
        var fixture = new RecorderFixture();
        await fixture.Recorder.RecordAsync(Answers(), default);

        var superseded = await fixture.Recorder.RecordAsync(
            Answers() with { IdempotencyKey = Guid.NewGuid().ToString("D"), Category = "transient_dependency", RetryLikelihood = 0.9, Supersede = true }, default);

        Assert.IsFalse(superseded.Existing);
        Assert.AreEqual(2, superseded.Result.Revision);
        Assert.AreEqual(FailureGuidance.RetryMayHelp, superseded.Result.Guidance);
        var history = await fixture.Store.GetHistoryAsync("failure");
        Assert.HasCount(2, history);
        Assert.AreEqual(1, history[1].Revision);
    }

    [TestMethod]
    public async Task A_Sensitive_Value_In_The_Rationale_Is_Replaced()
    {
        var fixture = new RecorderFixture();

        var (result, _) = await fixture.Recorder.RecordAsync(
            Answers() with { Rationale = "Customer sensitive-long-value has no address. Password=hunter2" }, default);

        Assert.DoesNotContain("sensitive-long-value", result.Rationale!);
        Assert.DoesNotContain("hunter2", result.Rationale!);
        StringAssert.Contains(result.Rationale, "has no address", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task A_Running_Provider_Analysis_Refuses_The_Recording()
    {
        var fixture = new RecorderFixture();
        await fixture.Store.ReserveAsync("failure", Guid.NewGuid().ToString("D"), false, DateTimeOffset.UtcNow,
            new ClassificationScope("failure", "event", "endpoint", null));

        var error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => fixture.Recorder.RecordAsync(Answers(), default));

        Assert.AreEqual("AnalysisInProgress", error.Code);
    }

    [TestMethod]
    public async Task Evidence_Carries_The_Input_Senders_And_Latest_Result_For_A_Reader()
    {
        var fixture = new RecorderFixture();
        fixture.Host.Contributor = false;
        fixture.Source.Current.From = "CrmEndpoint";
        fixture.Source.Current.OriginatingFrom = "PartnerPortal";

        var evidence = await fixture.Recorder.GetEvidenceAsync("endpoint", "event", "failure", default);

        Assert.AreEqual("failure", evidence.Input.MessageId);
        Assert.AreEqual("CrmEndpoint", evidence.Input.SourceEndpoint);
        Assert.AreEqual("PartnerPortal", evidence.Input.OriginatingEndpoint);
        Assert.IsNull(evidence.Input.EventPayloadJson, "The payload switch is off by default.");
        Assert.IsNull(evidence.Latest);
    }

    [TestMethod]
    public async Task Evidence_Refuses_A_Caller_Without_Reader()
    {
        var fixture = new RecorderFixture();
        fixture.Host.Reader = false;

        var error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() =>
            fixture.Recorder.GetEvidenceAsync("endpoint", "event", "failure", default));

        Assert.AreEqual(403, error.StatusCode);
    }

    [TestMethod]
    public async Task Evidence_Refuses_A_Message_That_Is_No_Longer_Failed()
    {
        var fixture = new RecorderFixture();
        fixture.Source.Current.ResolutionStatus = ResolutionStatus.Skipped;

        var error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() =>
            fixture.Recorder.GetEvidenceAsync("endpoint", "event", "failure", default));

        Assert.AreEqual("FailureNotEligible", error.Code);
    }

    [TestMethod]
    public void Version_Two_Adds_Fault_Origin_And_The_Provider_Keeps_Version_One()
    {
        Assert.AreEqual(2, FailureClassificationQuestionSet.Version);
        Assert.IsTrue(FailureClassificationQuestionSet.Items.ContainsKey("fault_origin"));
        CollectionAssert.AreEquivalent(
            new[] { "source_data", "adapter_code", "target_system", "configuration", "platform", "unknown" },
            FailureClassificationQuestionSet.FaultOrigins.ToArray());
        Assert.AreEqual(1, FailureClassificationQuestionSet.ProviderVersion);
        Assert.IsFalse(FailureClassificationQuestionSet.ProviderItems.ContainsKey("fault_origin"));
        Assert.HasCount(4, FailureClassificationQuestionSet.ProviderItems);
    }

    private static FailureClassificationRecording Answers() => new()
    {
        EndpointId = "endpoint",
        EventId = "event",
        MessageId = "failure",
        IdempotencyKey = Guid.NewGuid().ToString("D"),
        QuestionSetVersion = FailureClassificationQuestionSet.Version,
        Category = "business_rule",
        CategoryConfidence = 0.9,
        RetryLikelihood = 0.1,
        ChangeRequiredLikelihood = 0.9,
        ExternalDependencyLikelihood = 0.2,
        FaultOrigin = "source_data",
        FaultOriginConfidence = 0.8,
    };
}

internal sealed class RecorderFixture
{
    public FailureClassificationOptions Options { get; } = new();
    public RecordingHost Host { get; } = new();
    public InMemoryClassificationStore Store { get; } = new();
    public IMessageTrackingStore Messages { get; }
    public MessageSource Source => (MessageSource)(object)Messages;
    public FailureClassificationRecorder Recorder { get; }

    public RecorderFixture()
    {
        Messages = DispatchProxy.Create<IMessageTrackingStore, MessageSource>();
        var redactor = new IntelligenceDataRedactor();
        var evidence = new FailureEvidenceBuilder(Messages, new TestMasker(), null, redactor, Options);
        Recorder = new FailureClassificationRecorder(Options, Host, Store, evidence, Messages);
    }
}

internal sealed class RecordingHost : IIntegrationIntelligenceHost
{
    public bool Reader { get; set; } = true;
    public bool Contributor { get; set; } = true;
    public string? CurrentActor => "operator";
    public List<string> Audits { get; } = [];
    public List<bool> DeniedAudits { get; } = [];
    public Task<bool> EndpointExistsAsync(string endpointId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<bool> HasReaderAsync(string endpointId, CancellationToken cancellationToken = default) => Task.FromResult(Reader);
    public Task<bool> HasContributorAsync(string endpointId, CancellationToken cancellationToken = default) => Task.FromResult(Contributor);
    public Task AuditAsync(MessageAuditType type, string? eventId, string? endpointId, string data, bool accessDenied, CancellationToken cancellationToken = default)
    {
        Audits.Add(data);
        DeniedAudits.Add(accessDenied);
        return Task.CompletedTask;
    }
}
