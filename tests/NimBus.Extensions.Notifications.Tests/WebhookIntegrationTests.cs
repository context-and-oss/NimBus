#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace NimBus.Extensions.Notifications.Tests;

[TestClass]
public class WebhookIntegrationTests
{
    [TestMethod]
    public async Task Webhook_RoutedThroughFluentApi_DeliversTemplatedPayloadToLocalReceiver()
    {
        var port = GetFreeTcpPort();
        var prefix = $"http://localhost:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        string? receivedBody = null;
        string? receivedContentType = null;
        var receiveTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            receivedContentType = context.Request.ContentType;
            using (var reader = new StreamReader(context.Request.InputStream))
            {
                receivedBody = await reader.ReadToEndAsync();
            }

            context.Response.StatusCode = 200;
            context.Response.Close();
        });

        try
        {
            var services = new ServiceCollection();
            services.AddNimBus();
            services.AddNimBusNotifications(n => n.AddWebhook(opts =>
            {
                opts.Url = prefix + "nimbus";
                opts.MinSeverity = NotificationSeverity.Warning;
                opts.Template = "{\"title\":\"{Title}\",\"severity\":\"{Severity}\",\"event\":\"{EventId}\"}";
            }));

            var sp = services.BuildServiceProvider();
            var router = sp.GetRequiredService<INotificationRouter>();

            await router.RouteAsync(TestNotifications.Build(
                severity: NotificationSeverity.Critical, title: "Integration", eventId: "e-int"));

            await receiveTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.IsNotNull(receivedBody, "The webhook receiver should have received a POST body.");
            StringAssert.Contains(receivedContentType, "application/json");
            var json = JObject.Parse(receivedBody);
            Assert.AreEqual("Integration", (string?)json["title"]);
            Assert.AreEqual("Critical", (string?)json["severity"]);
            Assert.AreEqual("e-int", (string?)json["event"]);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static int GetFreeTcpPort()
    {
        var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        tcpListener.Stop();
        return port;
    }
}
