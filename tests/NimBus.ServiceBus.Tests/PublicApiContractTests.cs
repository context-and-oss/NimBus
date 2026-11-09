#pragma warning disable CA1707
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.SDK;
using NimBus.SDK.Extensions;
using NimBus.ServiceBus;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class PublicApiContractTests
{
    [TestMethod]
    public void IMessage_ExposesFromProperty()
    {
        var property = typeof(IMessage).GetProperty(nameof(IMessage.From));

        Assert.IsNotNull(property);
        Assert.AreEqual(typeof(string), property.PropertyType);
    }

    [TestMethod]
    public void ISubscriberClient_ExtendsIServiceBusAdapter()
    {
        Assert.IsTrue(typeof(IServiceBusAdapter).IsAssignableFrom(typeof(ISubscriberClient)));
    }

    [TestMethod]
    public void IServiceBusAdapter_ExposesExpectedHandleOverloads()
    {
        var signatures = typeof(IServiceBusAdapter)
            .GetMethods()
            .Where(method => method.Name == "Handle")
            .Select(method => string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name)))
            .OrderBy(signature => signature)
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[]
            {
                $"{nameof(ServiceBusReceivedMessage)}, {nameof(ServiceBusSessionMessageActions)}, {nameof(CancellationToken)}",
                $"{nameof(ServiceBusReceivedMessage)}, {nameof(ServiceBusMessageActions)}, {nameof(ServiceBusSessionMessageActions)}, {nameof(CancellationToken)}",
                $"{nameof(ServiceBusReceivedMessage)}, {nameof(ServiceBusSessionReceiver)}, {nameof(CancellationToken)}",
                $"{nameof(ProcessSessionMessageEventArgs)}, {nameof(CancellationToken)}",
            },
            signatures);
    }

    [TestMethod]
    [DataRow(typeof(IServiceBusSession), "message", "reason", "v", "cancellationToken")]
    [DataRow(typeof(ServiceBusSession), "message", "deadLetterReason", "deadLetterErrorDescription", "cancellationToken")]
    public void DeadLetterAsync_KeepsParameterNamesForNamedArgumentCallers(System.Type type, string p0, string p1, string p2, string p3)
    {
        var method = type.GetMethod(nameof(IServiceBusSession.DeadLetterAsync));

        Assert.IsNotNull(method);
        CollectionAssert.AreEqual(
            new[] { p0, p1, p2, p3 },
            method.GetParameters().Select(parameter => parameter.Name).ToArray());
    }

    [TestMethod]
    public void ISubscriberClient_ExposesGenericRegisterHandlerMethod()
    {
        var method = typeof(ISubscriberClient).GetMethods().Single(candidate => candidate.Name == "RegisterHandler");

        Assert.IsTrue(method.IsGenericMethodDefinition);
        Assert.AreEqual(1, method.GetGenericArguments().Length);
    }

    [TestMethod]
    public void NimBusSubscriberBuilder_ExposesAssemblyScanningRegistrationMethods()
    {
        Assert.IsNotNull(typeof(NimBusSubscriberBuilder).GetMethod(nameof(NimBusSubscriberBuilder.AddHandlersFromAssemblyContaining)));
        Assert.IsNotNull(typeof(NimBusSubscriberBuilder).GetMethod(nameof(NimBusSubscriberBuilder.AddHandlersFromAssembly), [typeof(Assembly)]));
        Assert.IsNotNull(typeof(NimBusSubscriberBuilder).GetMethod(nameof(NimBusSubscriberBuilder.AddHandlersFromAssemblies), [typeof(Assembly[])]));
    }
}
