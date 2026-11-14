using System.Threading.Tasks;
using Jering.Javascript.NodeJS;
using Microsoft.AspNetCore.Hosting;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NimBus.WebApp.Services;

public class RenderService : IRenderService
{
    private const string ServerJsRelPath = "./ClientApp/build/server.js";
    private readonly INodeJSService nodeJSService;
    private readonly string serverJsPath;

    public RenderService(INodeJSService nodeJSServ, IWebHostEnvironment webHostEnvironment)
    {
        nodeJSService = nodeJSServ;
        serverJsPath = webHostEnvironment.ContentRootFileProvider
                .GetFileInfo(ServerJsRelPath).PhysicalPath!;
    }

    public async Task<string> RenderAsync(string url, object? props)
    {
        (bool success, string? result) = await nodeJSService.TryInvokeFromCacheAsync<string>(serverJsPath, args: new object[] { url, JavaScriptEncoder.Default.Encode(JsonSerializer.Serialize(props)) });
        if (!success)
        {
            result = await nodeJSService.InvokeFromFileAsync<string>(serverJsPath, args: new object[] { url, JavaScriptEncoder.Default.Encode(JsonSerializer.Serialize(props)) });
        }
        return result!;
    }
}