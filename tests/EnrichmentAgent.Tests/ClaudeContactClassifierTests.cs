#pragma warning disable CA1707, CA2007

using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using EnrichmentAgent.Classification;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EnrichmentAgent.Tests;

/// <summary>
/// Drives <see cref="ClaudeContactClassifier"/> through the real Anthropic SDK against a recording
/// HTTP handler, so the request the SDK sends and the reply parsing are checked without a key or network.
/// </summary>
[TestClass]
public class ClaudeContactClassifierTests
{
    private const string Reply = """
        {
          "id": "msg_test",
          "type": "message",
          "role": "assistant",
          "model": "claude-haiku-4-5",
          "content": [{ "type": "text", "text": "{\"industry\":\"Technology\",\"leadScore\":120,\"rationale\":\"Tech email domain.\"}" }],
          "stop_reason": "end_turn",
          "stop_sequence": null,
          "usage": { "input_tokens": 10, "output_tokens": 20 }
        }
        """;

    [TestMethod]
    public async Task Classify_sends_one_user_message_with_structured_output_on_api_version_2026_10_21()
    {
        using var handler = new RecordingHandler(Reply);
        using var classifier = new ClaudeContactClassifier(new AnthropicClient(new ClientOptions
        {
            ApiKey = "test-key",
            HttpClient = new HttpClient(handler),
            MaxRetries = 0,
        }));

        var result = await classifier.Classify(new ContactInput(
            ContactId: "contact-001", FirstName: "Alice", LastName: "Smith", Email: "alice@techcorp.com", Phone: null));

        Assert.AreEqual("Technology", result.Industry);
        Assert.AreEqual(100, result.LeadScore, "Lead score is clamped to 0-100.");
        Assert.AreEqual("Tech email domain.", result.Rationale);

        Assert.AreEqual("2026-10-21", handler.Header("anthropic-version"));
        Assert.AreEqual("Bearer test-key", handler.Header("Authorization"));

        using var body = JsonDocument.Parse(handler.Body!);
        var request = body.RootElement;
        Assert.AreEqual(ClaudeContactClassifier.ModelId, request.GetProperty("model").GetString());
        Assert.AreEqual(1024, request.GetProperty("max_tokens").GetInt32());
        Assert.IsFalse(request.TryGetProperty("temperature", out _), "API version 2026-10-21 rejects sampling parameters.");
        var messages = request.GetProperty("messages");
        Assert.AreEqual(1, messages.GetArrayLength());
        Assert.AreEqual("user", messages[0].GetProperty("role").GetString());
        StringAssert.Contains(messages[0].GetProperty("content").GetString(), "alice@techcorp.com", StringComparison.Ordinal);
        var format = request.GetProperty("output_config").GetProperty("format");
        Assert.AreEqual("json_schema", format.GetProperty("type").GetString());
        Assert.AreEqual("object", format.GetProperty("schema").GetProperty("type").GetString());
    }

    private sealed class RecordingHandler(string reply) : HttpMessageHandler
    {
        private HttpRequestMessage? _request;

        public string? Body { get; private set; }

        public string? Header(string name) =>
            _request is not null && _request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply, Encoding.UTF8, "application/json"),
            };
        }
    }
}
