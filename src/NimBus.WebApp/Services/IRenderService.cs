using System.Threading.Tasks;

namespace NimBus.WebApp.Services;

public interface IRenderService
{
    Task<string> RenderAsync(string url, object? props);
}