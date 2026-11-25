#pragma warning disable CA1707, CA2007
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace NimBus.Adapter.Mcp.Tests;

[TestClass]
public sealed class AdapterCallLogTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void The_latest_call_for_a_key_is_kept()
    {
        var clock = new FakeTimeProvider(Start);
        var log = new AdapterCallLog(10, clock);

        log.Record("customer", "42", 500, AdapterCallOutcome.Failed, "Internal Server Error");
        clock.Advance(TimeSpan.FromMinutes(1));
        log.Record("customer", "42", 200, AdapterCallOutcome.Succeeded, "OK");

        Assert.IsTrue(log.TryGet("customer", "42", out var call));
        Assert.AreEqual(new AdapterCallRecord("customer", "42", Start.AddMinutes(1), 200, AdapterCallOutcome.Succeeded, "OK"), call);
        Assert.AreEqual(1, log.Count);
    }

    [TestMethod]
    public void Keys_are_per_entity()
    {
        var log = new AdapterCallLog();
        log.Record("customer", "42", 200, AdapterCallOutcome.Succeeded, null);

        Assert.IsFalse(log.TryGet("order", "42", out _));
        Assert.IsFalse(log.TryGet("customer", "43", out _));
    }

    [TestMethod]
    public void The_log_is_bounded_and_drops_the_oldest_key()
    {
        var log = new AdapterCallLog(2, TimeProvider.System);

        log.Record("customer", "1", 200, AdapterCallOutcome.Succeeded, null);
        log.Record("customer", "2", 200, AdapterCallOutcome.Succeeded, null);
        log.Record("customer", "1", 200, AdapterCallOutcome.Succeeded, null);
        log.Record("customer", "3", 200, AdapterCallOutcome.Succeeded, null);

        Assert.AreEqual(2, log.Count);
        Assert.IsTrue(log.TryGet("customer", "1", out _));
        Assert.IsFalse(log.TryGet("customer", "2", out _));
        Assert.IsTrue(log.TryGet("customer", "3", out _));
    }

    [TestMethod]
    public void A_capacity_below_one_is_refused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AdapterCallLog(0, TimeProvider.System));
    }

    [TestMethod]
    public async Task The_handler_records_a_successful_call()
    {
        var (client, log) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK));

        using var response = await client.GetAsync(new Uri("https://erp.example/customers/42"));

        Assert.IsTrue(log.TryGet("customer", "42", out var call));
        Assert.AreEqual(200, call.StatusCode);
        Assert.AreEqual(AdapterCallOutcome.Succeeded, call.Outcome);
        Assert.AreEqual("OK", call.Message);
    }

    [TestMethod]
    public async Task The_handler_records_a_failed_status()
    {
        var (client, log) = Build(_ => new HttpResponseMessage(HttpStatusCode.Conflict) { ReasonPhrase = "Record locked" });

        using var response = await client.GetAsync(new Uri("https://erp.example/customers/42"));

        Assert.IsTrue(log.TryGet("customer", "42", out var call));
        Assert.AreEqual(409, call.StatusCode);
        Assert.AreEqual(AdapterCallOutcome.Failed, call.Outcome);
        Assert.AreEqual("Record locked", call.Message);
    }

    [TestMethod]
    public async Task The_handler_records_an_exception_by_type_and_rethrows_it()
    {
        var (client, log) = Build(_ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known (erp.example:443) secret=abc"));

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetAsync(new Uri("https://erp.example/customers/42")));

        Assert.IsTrue(log.TryGet("customer", "42", out var call));
        Assert.IsNull(call.StatusCode);
        Assert.AreEqual(AdapterCallOutcome.Failed, call.Outcome);
        Assert.AreEqual("HttpRequestException: NameResolutionError", call.Message);
    }

    [TestMethod]
    public async Task The_handler_records_a_timeout()
    {
        var (client, log) = Build(_ => throw new TaskCanceledException("timed out", new TimeoutException()));

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => client.GetAsync(new Uri("https://erp.example/customers/42")));

        Assert.IsTrue(log.TryGet("customer", "42", out var call));
        Assert.AreEqual(AdapterCallOutcome.Failed, call.Outcome);
        Assert.AreEqual("TaskCanceledException", call.Message);
    }

    [TestMethod]
    public async Task A_request_without_a_key_is_not_recorded()
    {
        var (client, log) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK));

        using var response = await client.GetAsync(new Uri("https://erp.example/health"));

        Assert.AreEqual(0, log.Count);
    }

    [TestMethod]
    public void Two_clients_share_the_one_log()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("customers").AddAdapterCallLog("customer", _ => "1");
        services.AddHttpClient("orders").AddAdapterCallLog("order", _ => "1");
        using var provider = services.BuildServiceProvider();

        Assert.AreSame(provider.GetRequiredService<AdapterCallLog>(), provider.GetRequiredService<AdapterCallLog>());
        Assert.HasCount(1, provider.GetServices<AdapterCallLog>());
    }

    private static (HttpClient Client, AdapterCallLog Log) Build(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("erp")
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(answer))
            .AddAdapterCallLog("customer", request =>
                request.RequestUri!.AbsolutePath.StartsWith("/customers/", StringComparison.Ordinal) ? request.RequestUri.Segments[^1] : null);
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IHttpClientFactory>().CreateClient("erp"), provider.GetRequiredService<AdapterCallLog>());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(answer(request));
    }
}
