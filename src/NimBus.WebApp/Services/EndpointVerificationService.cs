using NimBus.Core;
using NimBus.Core.Endpoints;
using Microsoft.AspNetCore.Mvc;
using System.Linq;

namespace NimBus.WebApp.Services;

public class EndpointVerificationService
{
    public static bool EndpointExists(IPlatform platform, string endpointId)
    {
        if (platform == null || platform.Endpoints == null || string.IsNullOrEmpty(endpointId)) {
            return false;
        }
        IEndpoint? endpoint = platform.Endpoints.FirstOrDefault(e => e.Id.Equals(endpointId, System.StringComparison.OrdinalIgnoreCase));
        return endpoint != null;
    }
}
