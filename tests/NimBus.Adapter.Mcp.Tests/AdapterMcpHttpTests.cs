#pragma warning disable CA1707, CA2007
using System.Net;
using NimBus.Adapter.Mcp.Tests.Infrastructure;

namespace NimBus.Adapter.Mcp.Tests;

/// <summary>Sign-in and transport at the HTTP level, on both host styles.</summary>
[TestClass]
public sealed class AdapterMcpHttpTests
{
    private const string ToolsList = """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""";
    private const string GetRecord = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"adapter_get_record","arguments":{"entity":"customer","key":"42"}}}""";

    [TestMethod]
    [DataRow(HostStyle.AspNetCore)]
    [DataRow(HostStyle.FunctionsTrigger)]
    public async Task No_token_is_401_with_a_bearer_challenge(HostStyle style)
    {
        await using var host = await AdapterTestHost.StartAsync(style);

        using var response = await host.SendAsync(HttpMethod.Post, ToolsList, withToken: false);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [TestMethod]
    public async Task An_invalid_token_is_401_naming_invalid_token()
    {
        await using var host = await AdapterTestHost.StartAsync();

        using var response = await host.SendAsync(HttpMethod.Post, ToolsList, token: "not-a-jwt");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.Single();
        Assert.AreEqual("Bearer", challenge.Scheme);
        Assert.Contains("error=\"invalid_token\"", challenge.Parameter ?? "");
    }

    [TestMethod]
    [DataRow(HostStyle.AspNetCore)]
    [DataRow(HostStyle.FunctionsTrigger)]
    public async Task A_token_for_NimBus_is_401_and_reads_nothing(HostStyle style)
    {
        await using var host = await AdapterTestHost.StartAsync(style);

        using var response = await host.SendAsync(HttpMethod.Post, GetRecord, token: TestTokens.Create(audience: TestTokens.NimBusClientId));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(0, host.Data.RecordReads);
    }

    [TestMethod]
    public async Task A_token_from_another_tenant_is_401()
    {
        await using var host = await AdapterTestHost.StartAsync();

        using var response = await host.SendAsync(HttpMethod.Post, ToolsList, token: TestTokens.Create(issuer: TestTokens.V2Issuer(TestTokens.OtherTenantId)));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    [DataRow(HostStyle.AspNetCore)]
    [DataRow(HostStyle.FunctionsTrigger)]
    public async Task A_token_without_the_read_role_is_403_and_reads_nothing(HostStyle style)
    {
        await using var host = await AdapterTestHost.StartAsync(style);

        using var response = await host.SendAsync(HttpMethod.Post, GetRecord, token: TestTokens.Create(roles: ["Nimbus.Observe"]));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreEqual(0, host.Data.RecordReads);
    }

    [TestMethod]
    [DataRow(HostStyle.AspNetCore)]
    [DataRow(HostStyle.FunctionsTrigger)]
    public async Task A_valid_token_lists_the_tools(HostStyle style)
    {
        await using var host = await AdapterTestHost.StartAsync(style);

        using var response = await host.SendAsync(HttpMethod.Post, ToolsList);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var rpc = await AdapterTestHost.ReadRpcAsync(response);
        Assert.HasCount(4, rpc["result"]!["tools"]!.AsArray());
    }

    [TestMethod]
    [DataRow(HostStyle.AspNetCore, "GET")]
    [DataRow(HostStyle.AspNetCore, "DELETE")]
    [DataRow(HostStyle.FunctionsTrigger, "GET")]
    [DataRow(HostStyle.FunctionsTrigger, "DELETE")]
    public async Task Get_and_delete_are_405_no_sessions_no_stream(HostStyle style, string method)
    {
        await using var host = await AdapterTestHost.StartAsync(style);

        using var response = await host.SendAsync(new HttpMethod(method));

        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("POST", response.Content.Headers.Allow);
    }

    [TestMethod]
    public async Task Initialize_on_a_handshake_protocol_version_advertises_tools_and_no_session()
    {
        await using var host = await AdapterTestHost.StartAsync();

        using var response = await host.SendAsync(HttpMethod.Post,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"probe","version":"1"}}}""");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse(response.Headers.Contains("Mcp-Session-Id"));
        var result = (await AdapterTestHost.ReadRpcAsync(response))["result"]!;
        Assert.AreEqual("2025-11-25", (string?)result["protocolVersion"]);
        Assert.IsNotNull(result["capabilities"]!["tools"]);
        Assert.AreEqual("nimbus-adapter", (string?)result["serverInfo"]!["name"]);
    }

    [TestMethod]
    public async Task The_initialized_notification_is_202()
    {
        await using var host = await AdapterTestHost.StartAsync();

        using var response = await host.SendAsync(HttpMethod.Post, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
    }

    [TestMethod]
    public async Task Ping_answers_an_empty_result()
    {
        await using var host = await AdapterTestHost.StartAsync();

        using var response = await host.SendAsync(HttpMethod.Post, """{"jsonrpc":"2.0","id":7,"method":"ping"}""");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var rpc = await AdapterTestHost.ReadRpcAsync(response);
        Assert.AreEqual(7, (int?)rpc["id"]);
        Assert.IsNotNull(rpc["result"]);
        Assert.IsNull(rpc["error"]);
    }
}
