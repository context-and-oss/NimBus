using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using FailureAgent.Runner;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// Spec 044 Phase 4: a scheduled, unattended failure-handling agent. It signs in as its own
// workload, checks what it may do, fetches the handle-failures prompt from NimBus and hands it to
// a headless coding agent with the NimBus MCP server and its own repository and tracker tools.
// NimBus enforces everything the prompt says: roles, switches, the budget and the per-message cap.

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();
var options = configuration.GetSection(RunnerOptions.Section).Get<RunnerOptions>() ?? new RunnerOptions();

var problems = options.Validate();
if (problems.Count > 0)
{
    foreach (var problem in problems)
        Console.Error.WriteLine(problem);
    return 2;
}

using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(options.MaxRunMinutes));
var cancellationToken = cancellation.Token;

string? accessToken = null;
if (options.Authentication == RunnerAuthentication.Entra)
{
    // DefaultAzureCredential covers workload identity federation (GitHub Actions, AKS), managed
    // identity (Azure) and AZURE_CLIENT_ID/AZURE_TENANT_ID/AZURE_CLIENT_SECRET.
    var token = await new DefaultAzureCredential().GetTokenAsync(new TokenRequestContext([options.Scope!]), cancellationToken);
    var remaining = token.ExpiresOn - DateTimeOffset.UtcNow;
    if (remaining < TimeSpan.FromMinutes(options.MaxRunMinutes + 5))
    {
        Console.Error.WriteLine($"The token expires in {remaining.TotalMinutes:F0} minutes, less than the run may take ({options.MaxRunMinutes} minutes plus 5). Lower FailureAgent:MaxRunMinutes.");
        return 2;
    }

    accessToken = token.Token;
}

string prompt;
await using (var client = await ConnectAsync(options.McpUrl, accessToken, cancellationToken))
{
    var capabilities = await client.CallToolAsync("nimbus_get_capabilities", new Dictionary<string, object?>(), cancellationToken: cancellationToken);
    var check = CapabilityCheck.Evaluate(JsonSerializer.SerializeToElement(capabilities.StructuredContent), options.Authentication);
    foreach (var warning in check.Warnings)
        Console.WriteLine($"Note: {warning}");
    if (!check.Ok)
    {
        foreach (var error in check.Errors)
            Console.Error.WriteLine(error);
        return 3;
    }

    var arguments = new Dictionary<string, object?> { ["period"] = options.Period, ["unattended"] = "true" };
    if (!string.IsNullOrWhiteSpace(options.EndpointId))
        arguments["endpointId"] = options.EndpointId;
    var result = await client.GetPromptAsync("handle-failures", arguments, cancellationToken: cancellationToken);
    prompt = string.Join("\n\n", result.Messages.Select(m => m.Content).OfType<TextContentBlock>().Select(t => t.Text));
}

Directory.CreateDirectory(options.WorkingDirectory);
var configPath = AgentInvocation.WritePrivateFile(Path.GetTempPath(), AgentInvocation.McpConfig(options.McpUrl, accessToken));
try
{
    var start = new ProcessStartInfo(options.AgentCommand)
    {
        WorkingDirectory = options.WorkingDirectory,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        StandardOutputEncoding = Encoding.UTF8,
    };
    foreach (var argument in AgentInvocation.Arguments(options, configPath))
        start.ArgumentList.Add(argument);

    using var agent = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {options.AgentCommand}.");
    await agent.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken);
    agent.StandardInput.Close();
    var output = await agent.StandardOutput.ReadToEndAsync(cancellationToken);
    await agent.WaitForExitAsync(cancellationToken);

    var summary = AgentInvocation.Summarize(output);
    Console.WriteLine(summary.Result);
    if (summary.CostUsd is { } cost)
        Console.WriteLine($"Cost: ${cost:F2}");
    return summary.Succeeded && agent.ExitCode == 0 ? 0 : 1;
}
finally
{
    File.Delete(configPath);
}

static async Task<McpClient> ConnectAsync(string url, string? accessToken, CancellationToken cancellationToken)
{
    var http = new HttpClient();
    if (accessToken is not null)
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(url) }, http, ownsHttpClient: true);
    return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
}
