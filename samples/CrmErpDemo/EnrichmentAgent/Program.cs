using CrmErpDemo.Contracts.Events;
using EnrichmentAgent;
using EnrichmentAgent.Classification;
using NimBus.Agents;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults(options => options.UseStandardResilienceHandler = true);

// DI: select Claude classifier when ANTHROPIC_API_KEY is available, otherwise fall back to the
// deterministic fake (used in CI, in-memory tests, and local dev without a key).
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
    builder.Services.AddSingleton<IContactClassifier, ClaudeContactClassifier>();
else
    builder.Services.AddSingleton<IContactClassifier, DeterministicContactClassifier>();

// The receive -> classify -> publish -> settle loop, provided by the NimBus.Agents SDK.
// Base address "https+http://nimbus-ops" is resolved by service discovery (AddServiceDefaults).
builder.Services.AddNimBusAgent<EnrichmentHandler, CrmContactCreated>(o =>
{
    o.AgentId = "enrichment-agent";
    // Issued by a site Owner through POST /api/admin/agent-keys; needed when nimbus-ops requires sign-in
    // (NimBus__Agent__ApiKey). The local AppHost's development sign-in bypass works without one.
    o.ApiKey = builder.Configuration["NimBus:Agent:ApiKey"];
    o.Subscribe("CrmContactCreated");
    o.DefineOutput(EnrichmentHandler.EnrichedEventTypeId, EnrichmentHandler.EnrichedSchema, "Enriched CRM Contact");
});

var host = builder.Build();
host.Run();
