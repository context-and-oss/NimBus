#pragma warning disable CA1707, CA2007
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NimBus.Adapter.Mcp.Tests.Infrastructure;

namespace NimBus.Adapter.Mcp.Tests;

/// <summary>The tool contract, through the official SDK client against a real host.</summary>
[TestClass]
public sealed class AdapterMcpEndToEndTests
{
    private static readonly string[] ToolNames = ["adapter_get_health", "adapter_get_last_call", "adapter_get_mapping", "adapter_get_record"];

    private static Customer Ada() => new()
    {
        Id = "42",
        Name = "Ada Lovelace",
        Email = "ada@example.com",
        Iban = "DK5000400440116243",
        Address = new CustomerAddress { Street = "1 Analytical Way", City = "London" },
        CreditLimit = 5000,
    };

    /// <summary>The structured result, after checking the text block carries the same JSON.</summary>
    private static JsonObject Structured(CallToolResult result)
    {
        Assert.AreNotEqual(true, result.IsError, Text(result));
        var structured = JsonSerializer.SerializeToNode(result.StructuredContent)!.AsObject();
        Assert.IsTrue(JsonNode.DeepEquals(structured, JsonNode.Parse(Text(result))), "The text block must carry the structured JSON.");
        return structured;
    }

    private static string Text(CallToolResult result)
        => result.Content.OfType<TextContentBlock>().Single().Text;

    private static string Error(CallToolResult result)
    {
        Assert.IsTrue(result.IsError);
        Assert.HasCount(1, result.Content);
        return Text(result);
    }

    private static Dictionary<string, object?> Args(params (string Name, object? Value)[] args)
        => args.ToDictionary(arg => arg.Name, arg => arg.Value);

    [TestMethod]
    [DataRow(HostStyle.AspNetCore)]
    [DataRow(HostStyle.FunctionsTrigger)]
    public async Task The_client_connects_and_lists_four_read_only_tools(HostStyle style)
    {
        await using var host = await AdapterTestHost.StartAsync(style);
        await using var client = await host.CreateClientAsync();

        var tools = await client.ListToolsAsync();

        CollectionAssert.AreEqual(ToolNames, tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray());
        foreach (var tool in tools)
        {
            Assert.IsTrue(tool.ProtocolTool.Annotations?.ReadOnlyHint, tool.Name);
            Assert.AreNotEqual(true, tool.ProtocolTool.Annotations?.DestructiveHint, tool.Name);
            Assert.IsFalse(string.IsNullOrWhiteSpace(tool.Description), tool.Name);
        }

        Assert.IsNotNull(client.ServerCapabilities?.Tools);
        Assert.AreEqual("nimbus-adapter", client.ServerInfo?.Name);
    }

    [TestMethod]
    public async Task The_input_schemas_require_the_contract_arguments()
    {
        await using var host = await AdapterTestHost.StartAsync();
        await using var client = await host.CreateClientAsync();

        var tools = (await client.ListToolsAsync()).ToDictionary(tool => tool.Name, tool => JsonSerializer.SerializeToNode(tool.ProtocolTool.InputSchema)!);

        string[] Required(string name) => tools[name]["required"]?.AsArray().Select(node => (string)node!).Order(StringComparer.Ordinal).ToArray() ?? [];
        CollectionAssert.AreEqual(new[] { "entity", "key" }, Required("adapter_get_record"));
        CollectionAssert.AreEqual(new[] { "entity", "key" }, Required("adapter_get_last_call"));
        CollectionAssert.AreEqual(new[] { "eventTypeId" }, Required("adapter_get_mapping"));
        CollectionAssert.AreEqual(Array.Empty<string>(), Required("adapter_get_health"));
    }

    [TestMethod]
    [DataRow(HostStyle.AspNetCore)]
    [DataRow(HostStyle.FunctionsTrigger)]
    public async Task Get_record_returns_the_masked_record(HostStyle style)
    {
        await using var host = await AdapterTestHost.StartAsync(style);
        host.Data.Records[("customer", "42")] = Ada();
        await using var client = await host.CreateClientAsync();

        var result = Structured(await client.CallToolAsync("adapter_get_record", Args(("entity", "customer"), ("key", "42"))));

        Assert.AreEqual("customer", (string?)result["entity"]);
        Assert.AreEqual("42", (string?)result["key"]);
        Assert.IsTrue((bool?)result["found"]);
        var record = result["record"]!;
        Assert.AreEqual("Ada Lovelace", (string?)record["name"]);
        Assert.AreEqual(5000, (int?)record["creditLimit"]);
        Assert.AreEqual("London", (string?)record["address"]!["city"]);
        Assert.AreEqual("***", (string?)record["email"]);
        Assert.AreEqual("**************6243", (string?)record["iban"]);
        Assert.AreEqual("***", (string?)record["address"]!["street"]);
        Assert.DoesNotContain("ada@example.com", Text(await client.CallToolAsync("adapter_get_record", Args(("entity", "customer"), ("key", "42")))));
    }

    [TestMethod]
    public async Task Get_record_for_a_missing_key_is_not_found()
    {
        await using var host = await AdapterTestHost.StartAsync();
        await using var client = await host.CreateClientAsync();

        var result = Structured(await client.CallToolAsync("adapter_get_record", Args(("entity", "customer"), ("key", "nope"))));

        Assert.IsFalse((bool?)result["found"]);
        Assert.IsTrue(result.ContainsKey("record"));
        Assert.IsNull(result["record"]);
    }

    [TestMethod]
    public async Task Entity_names_match_case_insensitively_and_answer_with_the_served_name()
    {
        await using var host = await AdapterTestHost.StartAsync();
        host.Data.Records[("customer", "42")] = Ada();
        await using var client = await host.CreateClientAsync();

        var result = Structured(await client.CallToolAsync("adapter_get_record", Args(("entity", "Customer"), ("key", "42"))));

        Assert.AreEqual("customer", (string?)result["entity"]);
        Assert.IsTrue((bool?)result["found"]);
    }

    [TestMethod]
    [DataRow("adapter_get_record")]
    [DataRow("adapter_get_last_call")]
    public async Task An_entity_the_adapter_does_not_serve_is_refused_without_reading(string tool)
    {
        await using var host = await AdapterTestHost.StartAsync();
        await using var client = await host.CreateClientAsync();

        var error = Error(await client.CallToolAsync(tool, Args(("entity", "invoice"), ("key", "42"))));

        Assert.StartsWith("unknown-entity: ", error);
        Assert.Contains("invoice", error);
        Assert.AreEqual(0, host.Data.RecordReads);
    }

    [TestMethod]
    [DataRow("adapter_get_record", "{}")]
    [DataRow("adapter_get_record", """{"entity":"customer"}""")]
    [DataRow("adapter_get_record", """{"entity":"customer","key":""}""")]
    [DataRow("adapter_get_record", """{"entity":"customer","key":42}""")]
    [DataRow("adapter_get_last_call", """{"key":"42"}""")]
    [DataRow("adapter_get_mapping", "{}")]
    [DataRow("adapter_get_mapping", """{"eventTypeId":"  "}""")]
    public async Task Missing_or_malformed_arguments_are_invalid(string tool, string arguments)
    {
        await using var host = await AdapterTestHost.StartAsync();
        await using var client = await host.CreateClientAsync();
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments)!.ToDictionary(pair => pair.Key, pair => (object?)pair.Value);

        var error = Error(await client.CallToolAsync(tool, args));

        Assert.StartsWith("invalid-arguments: ", error);
    }

    [TestMethod]
    public async Task An_adapter_that_throws_is_a_system_error_naming_only_the_exception_type()
    {
        await using var host = await AdapterTestHost.StartAsync();
        host.Data.Failure = new InvalidOperationException("Login failed for user 'erp_admin' with password hunter2");
        await using var client = await host.CreateClientAsync();

        foreach (var (tool, args) in new[]
        {
            ("adapter_get_record", Args(("entity", "customer"), ("key", "42"))),
            ("adapter_get_mapping", Args(("eventTypeId", "CustomerChanged"))),
            ("adapter_get_health", Args()),
        })
        {
            var error = Error(await client.CallToolAsync(tool, args));

            Assert.StartsWith("system-error: ", error, tool);
            Assert.Contains(nameof(InvalidOperationException), error, tool);
            Assert.DoesNotContain("hunter2", error, tool);
            Assert.DoesNotContain("erp_admin", error, tool);
        }

        // The host's own log gets the failure, for the adapter's operator.
        Assert.HasCount(3, host.Logs.Entries.Where(entry => entry.EventId.Name == "AdapterToolFailed" && entry.Level == LogLevel.Warning));
    }

    [TestMethod]
    public async Task Get_mapping_returns_fields_and_settings()
    {
        await using var host = await AdapterTestHost.StartAsync();
        await using var client = await host.CreateClientAsync();

        var result = Structured(await client.CallToolAsync("adapter_get_mapping", Args(("eventTypeId", "CustomerChanged"))));

        Assert.AreEqual("CustomerChanged", (string?)result["eventTypeId"]);
        Assert.IsTrue((bool?)result["known"]);
        var fields = result["fields"]!.AsArray();
        Assert.HasCount(2, fields);
        Assert.AreEqual("customerId", (string?)fields[0]!["eventPath"]);
        Assert.AreEqual("accountnumber", (string?)fields[0]!["systemField"]);
        Assert.IsTrue(fields[0]!.AsObject().ContainsKey("note"));
        Assert.IsNull(fields[0]!["note"]);
        Assert.AreEqual("Lower-cased before it is written", (string?)fields[1]!["note"]);
        Assert.AreEqual("CRONUS", (string?)result["settings"]!["company"]);
    }

    [TestMethod]
    public async Task Get_mapping_for_an_unknown_event_type_is_not_known()
    {
        await using var host = await AdapterTestHost.StartAsync();
        await using var client = await host.CreateClientAsync();

        var result = Structured(await client.CallToolAsync("adapter_get_mapping", Args(("eventTypeId", "OrderPlaced"))));

        Assert.AreEqual("OrderPlaced", (string?)result["eventTypeId"]);
        Assert.IsFalse((bool?)result["known"]);
        Assert.HasCount(0, result["fields"]!.AsArray());
        Assert.HasCount(0, result["settings"]!.AsObject());
    }

    [TestMethod]
    public async Task Get_last_call_returns_what_the_system_answered()
    {
        await using var host = await AdapterTestHost.StartAsync();
        host.Get<AdapterCallLog>().Record("customer", "42", 409, AdapterCallOutcome.Failed, "Record locked");
        await using var client = await host.CreateClientAsync();

        var result = Structured(await client.CallToolAsync("adapter_get_last_call", Args(("entity", "customer"), ("key", "42"))));

        Assert.AreEqual("customer", (string?)result["entity"]);
        Assert.AreEqual("42", (string?)result["key"]);
        Assert.IsTrue((bool?)result["found"]);
        Assert.IsTrue(DateTimeOffset.TryParse((string?)result["at"], System.Globalization.CultureInfo.InvariantCulture, out _));
        Assert.AreEqual(409, (int?)result["statusCode"]);
        Assert.AreEqual("failed", (string?)result["outcome"]);
        Assert.AreEqual("Record locked", (string?)result["message"]);
    }

    [TestMethod]
    public async Task Get_last_call_without_a_call_is_not_found_with_null_fields()
    {
        await using var host = await AdapterTestHost.StartAsync();
        await using var client = await host.CreateClientAsync();

        var result = Structured(await client.CallToolAsync("adapter_get_last_call", Args(("entity", "customer"), ("key", "42"))));

        Assert.IsFalse((bool?)result["found"]);
        foreach (var field in new[] { "at", "statusCode", "outcome", "message" })
        {
            Assert.IsTrue(result.ContainsKey(field), field);
            Assert.IsNull(result[field], field);
        }
    }

    [TestMethod]
    public async Task Get_health_returns_reachability_version_and_check_time()
    {
        await using var host = await AdapterTestHost.StartAsync();
        await using var client = await host.CreateClientAsync();

        var result = Structured(await client.CallToolAsync("adapter_get_health", Args()));

        Assert.IsTrue((bool?)result["reachable"]);
        Assert.AreEqual("ERP answered in 12 ms", (string?)result["message"]);
        Assert.IsTrue(result.ContainsKey("version"));
        Assert.AreEqual(AdapterVersion.FromAssembly(System.Reflection.Assembly.GetEntryAssembly()), (string?)result["version"]);
        Assert.IsTrue(DateTimeOffset.TryParse((string?)result["checkedAt"], System.Globalization.CultureInfo.InvariantCulture, out var checkedAt));
        Assert.IsLessThan(TimeSpan.FromMinutes(1), (DateTimeOffset.UtcNow - checkedAt).Duration());
    }

    [TestMethod]
    public async Task Every_tool_call_writes_one_read_log_entry_without_record_values()
    {
        await using var host = await AdapterTestHost.StartAsync();
        host.Data.Records[("customer", "42")] = Ada();
        await using var client = await host.CreateClientAsync();

        await client.CallToolAsync("adapter_get_record", Args(("entity", "customer"), ("key", "42")));
        await client.CallToolAsync("adapter_get_last_call", Args(("entity", "invoice"), ("key", "7")));
        await client.CallToolAsync("adapter_get_health", Args());

        var reads = host.Logs.Entries.Where(entry => entry.EventId.Name == "AdapterRead").ToList();
        Assert.HasCount(3, reads);

        var first = reads[0];
        Assert.AreEqual(LogLevel.Information, first.Level);
        Assert.AreEqual("adapter_get_record", first.State["Tool"]);
        Assert.AreEqual("customer", first.State["Entity"]);
        Assert.AreEqual("42", first.State["Key"]);
        Assert.AreEqual(TestTokens.CallerObjectId, first.State["CallerObjectId"]);
        Assert.AreEqual(TestTokens.CallerAppId, first.State["CallerAppId"]);
        Assert.AreEqual("adapter_get_last_call", reads[1].State["Tool"]);
        Assert.AreEqual("invoice", reads[1].State["Entity"]);
        Assert.AreEqual("adapter_get_health", reads[2].State["Tool"]);

        foreach (var entry in host.Logs.Entries)
        {
            var text = entry.Message + string.Join(' ', entry.State.Values);
            Assert.DoesNotContain("Ada Lovelace", text);
            Assert.DoesNotContain("ada@example.com", text);
            Assert.DoesNotContain("Analytical", text);
        }
    }
}
