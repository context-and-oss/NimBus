#pragma warning disable CA1707, CA2007

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class AdminResolverDeadLetterTests
{
    [TestMethod]
    public async Task Inspect_requires_owner_and_is_not_audited()
    {
        var service = new RecordingSubscriptionAdminService();
        var audit = new RecordingAuditLogService();
        var sut = CreateController(service, audit, authorized: true);

        var response = await sut.GetAdminServicebusResolverDeadlettersAsync("Resolver");

        Assert.IsInstanceOfType<OkObjectResult>(response.Result);
        Assert.AreEqual(1, service.InspectCalls);
        Assert.AreEqual(0, audit.Entries.Count);
    }

    [TestMethod]
    public async Task Denied_replay_audits_only_the_target()
    {
        var service = new RecordingSubscriptionAdminService();
        var audit = new RecordingAuditLogService();
        var sut = CreateController(service, audit, authorized: false);

        var response = await sut.PostAdminServicebusResolverDeadlettersResubmitAsync(
            new DeadLetterResubmitRequest
            {
                Scope = DeadLetterResubmitRequestScope.Reason,
                Reason = "private broker detail",
            },
            "Resolver");

        Assert.IsInstanceOfType<ForbidResult>(response.Result);
        Assert.AreEqual(0, service.ReplayCalls);
        Assert.AreEqual(1, audit.Entries.Count);
        Assert.IsTrue(audit.Entries[0].AccessDenied);
        StringAssert.Contains(audit.Entries[0].Data, "Resolver", StringComparison.Ordinal);
        Assert.IsFalse(audit.Entries[0].Data.Contains("private broker detail", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Missing_scope_is_rejected_before_the_broker_call()
    {
        var service = new RecordingSubscriptionAdminService();
        var sut = CreateController(service, new RecordingAuditLogService(), authorized: true);

        var response = await sut.PostAdminServicebusResolverDeadlettersResubmitAsync(
            new DeadLetterResubmitRequest(),
            "Resolver");

        Assert.IsInstanceOfType<BadRequestObjectResult>(response.Result);
        Assert.AreEqual(0, service.ReplayCalls);
    }

    [TestMethod]
    public async Task Transport_failure_returns_sanitized_service_unavailable_and_audits_failure()
    {
        var service = new RecordingSubscriptionAdminService
        {
            ReplayException = new InvalidOperationException("secret broker response"),
        };
        var audit = new RecordingAuditLogService();
        var sut = CreateController(service, audit, authorized: true);

        var response = await sut.PostAdminServicebusResolverDeadlettersResubmitAsync(
            new DeadLetterResubmitRequest { Scope = DeadLetterResubmitRequestScope.All },
            "Resolver");

        var result = Assert.IsInstanceOfType<ObjectResult>(response.Result);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.AreEqual("Resolver dead letters could not be replayed.", result.Value);
        Assert.AreEqual(1, audit.Entries.Count);
        Assert.IsFalse(audit.Entries[0].Data.Contains("secret broker response", StringComparison.Ordinal));
        StringAssert.Contains(audit.Entries[0].Data, "\"success\":false", StringComparison.Ordinal);
    }

    private static AdminImplementation CreateController(
        ISubscriptionAdminService service,
        IAuditLogService audit,
        bool authorized)
    {
        var context = new DefaultHttpContext();
        var accessor = new TestHttpContextAccessor(context);
        return new AdminImplementation(
            accessor,
            adminService: null!,
            service,
            platform: null!,
            new ConfigurationBuilder().Build(),
            audit,
            new FixedAuthorizationService(authorized),
            heartbeatService: null!);
    }

    private sealed class TestHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private sealed class FixedAuthorizationService(bool authorized) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) =>
            Task.FromResult(authorized);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() =>
            Task.FromResult(new CurrentUserAccess());

        public string? GetCurrentUserName() => "test-user";
    }

    private sealed class RecordingAuditLogService : IAuditLogService
    {
        public List<(bool AccessDenied, string Data)> Entries { get; } = [];

        public Task LogAuditAsync(
            MessageAuditType type,
            HttpContext context,
            bool accessDenied = false,
            string? data = null,
            string? eventId = null,
            string? endpointId = null,
            string? eventTypeId = null,
            string? auditorNameOverride = null,
            CancellationToken cancellationToken = default)
        {
            Entries.Add((accessDenied, data ?? string.Empty));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSubscriptionAdminService : ISubscriptionAdminService
    {
        public int InspectCalls { get; private set; }

        public int ReplayCalls { get; private set; }

        public Exception? ReplayException { get; init; }

        public Task<DeadLetterOverview> GetResolverDeadLettersAsync(
            string subscriptionName,
            CancellationToken cancellationToken = default)
        {
            InspectCalls++;
            return Task.FromResult(new DeadLetterOverview());
        }

        public Task<BulkOperationResult> ResubmitResolverDeadLettersAsync(
            string subscriptionName,
            bool all,
            string? reason,
            CancellationToken cancellationToken = default)
        {
            ReplayCalls++;
            return ReplayException is null
                ? Task.FromResult(new BulkOperationResult())
                : Task.FromException<BulkOperationResult>(ReplayException);
        }

        public Task<IEnumerable<ServiceBusTopicOverview>> GetTopicOverviewAsync() => throw Unexpected();
        public Task<SubscriptionActionResult> DeleteTopicAsync(string topicName) => throw Unexpected();

        public Task<IEnumerable<ServiceBusSubscriptionInfo>> GetSubscriptionsAsync(string topicName) => throw Unexpected();

        public Task<SubscriptionActionResult> SetSubscriptionStatusAsync(string topicName, string subscriptionName, bool enable) => throw Unexpected();

        public Task<BulkOperationResult> PurgeSubscriptionAsync(string topicName, string subscriptionName) => throw Unexpected();
        public Task<BulkOperationResult> PurgeDeadLetterQueueAsync(string topicName, string subscriptionName) => throw Unexpected();

        public Task<SubscriptionActionResult> RecreateSubscriptionAsync(string topicName, string subscriptionName) => throw Unexpected();

        public Task<SubscriptionActionResult> DeleteSubscriptionAsync(string topicName, string subscriptionName) => throw Unexpected();

        public Task<SubscriptionActionResult> DeleteRuleAsync(string topicName, string subscriptionName, string ruleName) => throw Unexpected();

        public Task<SubscriptionActionResult> RestoreRulesAsync(string topicName, string subscriptionName) => throw Unexpected();

        private static Exception Unexpected() => new AssertFailedException("Unexpected subscription admin call.");
    }
}
