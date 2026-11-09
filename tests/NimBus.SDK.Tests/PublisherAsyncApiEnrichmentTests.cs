#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using NimBus.CommandLine;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.SDK.Extensions;
using AsyncApiExporter = NimBus.ServiceBus.AsyncApi.AsyncApiExporter;
using AsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;

namespace NimBus.SDK.Tests;

[TestClass]
public sealed class PublisherAsyncApiEnrichmentTests
{
    private const string FakeConnection =
        "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=dGVzdC1rZXktdmFsdWU=";

    [TestMethod]
    public void FluentPublish_RecordsEnrichmentInSharedRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusPublisher("Ep", b => b.Publish<SomeEvent>(o =>
        {
            o.AsyncApi.Title = "T";
            o.AsyncApi.Name = "N";
            o.AsyncApi.Tags.Add("X");
        }));

        var registry = services.BuildServiceProvider().GetRequiredService<AsyncApiEnrichmentRegistry>();
        Assert.IsTrue(registry.TryGet(typeof(SomeEvent), out var options));
        Assert.AreEqual("T", options.Title);
        Assert.AreEqual("N", options.Name);
        CollectionAssert.Contains(options.Tags.ToList(), "X");
    }

    [TestMethod]
    public void FluentEnrichment_FlowsIntoExportedDocument_ViaProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusPublisher("Ep", b => b.Publish<SomeEvent>(o =>
        {
            o.AsyncApi.Title = "FluentTitle";
            o.AsyncApi.Name = "FluentName";
            o.AsyncApi.Tags.Add("FluentTag");
        }));

        // The composition root supplies the exporter via an adapter lambda whose parameter order
        // matches AsyncApiExporter.Serialize(IPlatform, AsyncApiFormat, AsyncApiEnrichmentRegistry?).
        services.AddNimBusAsyncApiDocument(
            new TestPlatform(new TestEndpoint("Ep", produces: new[] { typeof(SomeEvent) })),
            (p, f, r) => AsyncApiExporter.Serialize(p, f, r));

        var provider = services.BuildServiceProvider();
        var document = provider.GetRequiredService<IAsyncApiDocumentProvider>().GetDocument(AsyncApiFormat.Json);

        StringAssert.Contains(document, "FluentTitle", StringComparison.Ordinal);
        StringAssert.Contains(document, "FluentName", StringComparison.Ordinal);
        StringAssert.Contains(document, "FluentTag", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CliLoader_ResolvesSdkRegisteredProvider_ViaFactory_IncludesFluentMetadata()
    {
        // End-to-end: a host registers its provider through the real SDK path (AddNimBusPublisher fluent
        // enrichment + AddNimBusAsyncApiDocument → a private, DI-backed IAsyncApiDocumentProvider) and
        // exposes it via a public parameterless IAsyncApiDocumentProviderFactory. The CLI's
        // AsyncApiProviderLoader must reach that DI-backed provider through the factory so
        // `nb asyncapi export --assembly <host.dll>` surfaces fluent metadata.
        var provider = AsyncApiProviderLoader.Resolve(
            typeof(PublisherAsyncApiEnrichmentTests).Assembly,
            typeof(SdkFluentDocumentFactory).FullName);

        var document = provider.GetDocument(AsyncApiFormat.Json);
        StringAssert.Contains(document, SdkFluentDocumentFactory.OwnerMarker, StringComparison.Ordinal);
    }

    [TestMethod]
    public void FluentPublish_DoesNotBreakSendPath()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusPublisher("Ep", b => b.Publish<SomeEvent>(o => o.AsyncApi.Title = "T"));

        // AC#10: the send path is unchanged — the publisher client still resolves.
        Assert.IsNotNull(services.BuildServiceProvider().GetRequiredService<IPublisherClient>());
    }

    [TestMethod]
    public void MultiplePublishCalls_AccumulateInOneRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusPublisher("Ep", b => b
            .Publish<SomeEvent>(o => o.AsyncApi.Title = "One")
            .Publish<OtherEvent>(o => o.AsyncApi.Title = "Two"));

        var registry = services.BuildServiceProvider().GetRequiredService<AsyncApiEnrichmentRegistry>();
        Assert.AreEqual(2, registry.Entries.Count);
    }

    // ---- test doubles ----

    // Public parameterless factory a host exposes so `nb asyncapi export --assembly` can reach the
    // SDK-registered, DI-backed IAsyncApiDocumentProvider. Builds the same container the host would.
    public sealed class SdkFluentDocumentFactory : IAsyncApiDocumentProviderFactory
    {
        public const string OwnerMarker = "AF98-SDK-Factory-Owner";

        public IAsyncApiDocumentProvider Create()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new ServiceBusClient(FakeConnection));
            services.AddNimBusPublisher("Ep", b => b.Publish<SomeEvent>(o => o.AsyncApi.Owner = OwnerMarker));
            services.AddNimBusAsyncApiDocument(
                new TestPlatform(new TestEndpoint("Ep", produces: new[] { typeof(SomeEvent) })),
                (p, f, r) => AsyncApiExporter.Serialize(p, f, r));
            return services.BuildServiceProvider().GetRequiredService<IAsyncApiDocumentProvider>();
        }
    }

    private sealed class SomeEvent : Event
    {
        public Guid Id { get; set; }
    }

    private sealed class OtherEvent : Event
    {
        public Guid Id { get; set; }
    }

    private sealed class TestPlatform : Platform
    {
        public TestPlatform(params TestEndpoint[] endpoints)
        {
            foreach (var endpoint in endpoints) AddEndpoint(endpoint);
        }
    }

    private sealed class TestSystem : ISystem
    {
        public TestSystem(string id) => SystemId = id;

        public string SystemId { get; }
    }

    private sealed class TestEndpoint : IEndpoint
    {
        public TestEndpoint(string id, Type[]? produces = null, Type[]? consumes = null)
        {
            Id = id;
            Name = id;
            EventTypesProduced = (produces ?? Array.Empty<Type>()).Select(t => (IEventType)new EventType(t)).ToList();
            EventTypesConsumed = (consumes ?? Array.Empty<Type>()).Select(t => (IEventType)new EventType(t)).ToList();
        }

        public string Id { get; }
        public string Name { get; }
        public string Description => $"{Id} description";
        public string Namespace => "Tests";
        public string SecurityGroupName => string.Empty;
        public ISystem System => new TestSystem(Id);
        public IEnumerable<IEventType> EventTypesProduced { get; }
        public IEnumerable<IEventType> EventTypesConsumed { get; }
        public IEnumerable<IRoleAssignment> RoleAssignments => Array.Empty<IRoleAssignment>();
    }
}
