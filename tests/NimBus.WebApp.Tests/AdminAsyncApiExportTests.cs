#pragma warning disable CA1707, CA2007

using System;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus;
using NimBus.Core;
using NimBus.ServiceBus.AsyncApi;
using NimBus.WebApp.Controllers.ApiContract;
using AsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;

namespace NimBus.WebApp.Tests;

// Covers the admin AsyncAPI export endpoint (GET /api/admin/asyncapi):
// authorization (403 before any serialization), default/explicit/invalid format
// handling, the reuse of the shared NimBus.ServiceBus.AsyncApi exporter, and the
// file-response metadata (attachment filename + content type).
[TestClass]
public sealed class AdminAsyncApiExportTests
{
    private const string ManagementGroup = "EIP_Management";

    [TestMethod]
    public async Task Export_DefaultsToYaml_WhenFormatMissing()
    {
        var platform = new PlatformConfiguration();
        var sut = BuildSut(CtxWithGroup(ManagementGroup), platform);

        var result = await sut.GetAdminAsyncapiAsync(null!);

        var file = AssertFile(result, "nimbus-asyncapi.yaml", "application/x-yaml");
        // Byte-for-byte the shared exporter's YAML output — proves reuse, not a re-implementation.
        Assert.AreEqual(AsyncApiExporter.Serialize(platform, AsyncApiFormat.Yaml), Text(file));
        StringAssert.Contains(Text(file), "asyncapi:", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Export_DefaultsToYaml_WhenFormatEmpty()
    {
        var sut = BuildSut(CtxWithGroup(ManagementGroup), new PlatformConfiguration());

        var result = await sut.GetAdminAsyncapiAsync("   ");

        AssertFile(result, "nimbus-asyncapi.yaml", "application/x-yaml");
    }

    [TestMethod]
    public async Task Export_IsCaseInsensitive_ForYaml()
    {
        var sut = BuildSut(CtxWithGroup(ManagementGroup), new PlatformConfiguration());

        var result = await sut.GetAdminAsyncapiAsync("YAML");

        AssertFile(result, "nimbus-asyncapi.yaml", "application/x-yaml");
    }

    [TestMethod]
    public async Task Export_ReturnsJson_WhenFormatJson()
    {
        var platform = new PlatformConfiguration();
        var sut = BuildSut(CtxWithGroup(ManagementGroup), platform);

        var result = await sut.GetAdminAsyncapiAsync("json");

        var file = AssertFile(result, "nimbus-asyncapi.json", "application/json");
        Assert.AreEqual(AsyncApiExporter.Serialize(platform, AsyncApiFormat.Json), Text(file));
        // Valid JSON with the AsyncAPI header.
        var root = JsonNode.Parse(Text(file))!;
        Assert.AreEqual("3.0.0", root["asyncapi"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Export_Returns400_ForInvalidFormat()
    {
        var sut = BuildSut(CtxWithGroup(ManagementGroup), new PlatformConfiguration());

        var result = await sut.GetAdminAsyncapiAsync("xml");

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
    }

    [TestMethod]
    public async Task Export_Returns403_ForUnauthorizedUser_BeforeSerialization()
    {
        // A null platform would make serialization throw (ArgumentNullException). The auth
        // check must run first, so a ForbidResult is returned without ever touching the platform.
        var sut = BuildSut(CtxWithoutGroup(), platform: null!);

        var result = await sut.GetAdminAsyncapiAsync("json");

        Assert.IsInstanceOfType<ForbidResult>(result);
    }

    // ---------------- helpers ----------------

    private static AdminImplementation BuildSut(HttpContext ctx, IPlatform platform)
    {
        var accessor = new HttpContextAccessor { HttpContext = ctx };
        return new AdminImplementation(
            accessor,
            adminService: null!,
            subscriptionAdminService: null!,
            platform,
            configuration: null!,
            auditLogService: null!,
            authorizationService: new ClaimGateAuthService(ctx),
            heartbeatService: null!);
    }

    // Claims-shaped gate double: Owner iff the context carries the marker group
    // claim. Resolution itself is covered by AccessControlServiceTests.
    private sealed class ClaimGateAuthService : NimBus.WebApp.Services.IEndpointAuthorizationService
    {
        private readonly HttpContext _ctx;
        public ClaimGateAuthService(HttpContext ctx) => _ctx = ctx;

        private bool IsAdmin => _ctx.User?.Claims != null
            && System.Linq.Enumerable.Any(_ctx.User.Claims, c => c.Type == "groups" && c.Value == ManagementGroup);

        public Task<bool> HasRoleAsync(NimBus.WebApp.Services.AccessRole required, string? endpointId = null)
            => Task.FromResult(IsAdmin);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);

        public Task<NimBus.WebApp.Services.CurrentUserAccess> GetCurrentUserAccessAsync()
            => Task.FromResult(new NimBus.WebApp.Services.CurrentUserAccess
            {
                SiteRole = IsAdmin ? NimBus.WebApp.Services.AccessRole.Owner : NimBus.WebApp.Services.AccessRole.None,
            });

        public string? GetCurrentUserName() => "test-user";
    }

    private static FileContentResult AssertFile(IActionResult result, string fileName, string contentType)
    {
        Assert.IsInstanceOfType<FileContentResult>(result);
        var file = (FileContentResult)result;
        Assert.AreEqual(fileName, file.FileDownloadName);
        Assert.AreEqual(contentType, file.ContentType);
        Assert.IsTrue(file.FileContents.Length > 0);
        return file;
    }

    private static string Text(FileContentResult file) => Encoding.UTF8.GetString(file.FileContents);

    private static HttpContext CtxWithGroup(string group)
    {
        var identity = new ClaimsIdentity(new[] { new Claim("groups", group) }, "Test");
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    private static HttpContext CtxWithoutGroup() =>
        new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
}
