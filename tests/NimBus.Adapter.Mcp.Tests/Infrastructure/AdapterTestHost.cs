#pragma warning disable CA1707, CA2007
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace NimBus.Adapter.Mcp.Tests.Infrastructure;

/// <summary>How the test host serves the adapter MCP endpoint.</summary>
public enum HostStyle
{
    /// <summary>ASP.NET Core endpoint routing with <c>MapAdapterMcp()</c> at <c>/mcp</c>.</summary>
    AspNetCore,

    /// <summary>
    /// No routing at all: a terminal delegate hands the raw <see cref="HttpContext"/> to
    /// <see cref="AdapterMcpHandler.HandleAsync"/> at <c>/api/mcp</c>, as an Azure Functions
    /// HttpTrigger with ASP.NET Core integration does.
    /// </summary>
    FunctionsTrigger,
}

/// <summary>
/// The package's production registration (<c>AddAdapterMcp</c>) on a TestServer, with the fake
/// system and a token validator that trusts the locally signed test tokens.
/// </summary>
internal sealed class AdapterTestHost : IAsyncDisposable
{
    private readonly IHost _host;

    private AdapterTestHost(IHost host, HostStyle style, CapturingLoggerProvider logs)
    {
        _host = host;
        Server = host.GetTestServer();
        Path = style == HostStyle.AspNetCore ? "/mcp" : "/api/mcp";
        Logs = logs;
    }

    public TestServer Server { get; }

    public string Path { get; }

    public CapturingLoggerProvider Logs { get; }

    public FakeAdapterData Data => Get<FakeAdapterData>();

    public T Get<T>() where T : notnull => _host.Services.GetRequiredService<T>();

    public static async Task<AdapterTestHost> StartAsync(HostStyle style = HostStyle.AspNetCore)
    {
        var logs = new CapturingLoggerProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(TestTokens.Settings()).Build();

        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(builder => builder.ClearProviders().AddProvider(logs).SetMinimumLevel(LogLevel.Debug));
                    services.AddSingleton<FakeAdapterData>();
                    services.AddAdapterMcp<FakeAdapterSystem>(configuration);
                    // Offline metadata: the same validator, trusting the local signing key.
                    services.Replace(ServiceDescriptor.Singleton(TestTokens.Validator()));
                    if (style == HostStyle.AspNetCore)
                        services.AddRouting();
                });
                web.Configure(app =>
                {
                    if (style == HostStyle.AspNetCore)
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapAdapterMcp());
                        return;
                    }

                    app.Run(context => context.Request.Path == "/api/mcp"
                        ? context.RequestServices.GetRequiredService<AdapterMcpHandler>().HandleAsync(context)
                        : Task.CompletedTask);
                });
            })
            .StartAsync();

        return new AdapterTestHost(host, style, logs);
    }

    /// <summary>The official SDK client over Streamable HTTP, signed in with <paramref name="token"/>.</summary>
    public async Task<McpClient> CreateClientAsync(string? token = null)
    {
        var http = Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token ?? TestTokens.Create());
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(Server.BaseAddress, Path),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            http,
            NullLoggerFactory.Instance,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    /// <summary>A raw request to the endpoint, for status codes and headers the client hides.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string? body = null, string? token = null, bool withToken = true, string accept = "application/json, text/event-stream")
    {
        using var request = new HttpRequestMessage(method, Path);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("Accept", accept);
        if (withToken)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? TestTokens.Create());
        using var client = Server.CreateClient();
        return await client.SendAsync(request);
    }

    /// <summary>
    /// The JSON-RPC message of a POST response. The SDK frames a POST's answer as one server-sent
    /// event; a plain JSON body is read as is.
    /// </summary>
    public static async Task<JsonNode> ReadRpcAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            text = string.Join('\n', text.Split('\n')
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line["data:".Length..].Trim()));
        }

        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty JSON-RPC response.");
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
