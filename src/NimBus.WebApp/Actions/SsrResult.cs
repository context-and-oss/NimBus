using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Actions;

public class SsrResult : IActionResult
{
    private readonly string _url;
    private readonly object? _props;

    public SsrResult(string url, object? props = null)
    {
        _url = url;
        _props = props;
    }

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var renderService = context.HttpContext.RequestServices
            .GetRequiredService<IRenderService>();
        var renderResult = await renderService.RenderAsync(_url, _props);
        var contentResult = new ContentResult
        {
            Content = renderResult,
            ContentType = "text/html"
        };
        await contentResult.ExecuteResultAsync(context);
    }
}