using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Csrf;

/// <summary>
/// Attaches <see cref="CookieAntiforgeryFilter"/> to the NSwag-generated API controllers (#296).
/// <para>
/// Those controllers live in <c>Controllers/ApiContract.g.cs</c>, which NSwag rewrites on every
/// build, so an attribute there would be erased: the same reason
/// <see cref="AllowAnonymousActionsConvention"/> and the rate-limit convention exist. Matching on the
/// generated namespace covers a controller added later without anyone remembering this list.
/// </para>
/// </summary>
internal sealed class CookieAntiforgeryConvention : IApplicationModelConvention
{
    private static readonly HashSet<Type> Exempt = new()
    {
        // Anonymous, authenticated by the X-Webhook-Key header.
        typeof(StorageHookApiController),
        // Their [AutoValidateAntiforgeryToken] already requires the token on every write, from a
        // cookie session or not; that stricter rule stays.
        typeof(AgentKeysApiController),
        typeof(McpAccessApiController),
    };

    public void Apply(ApplicationModel application)
    {
        ArgumentNullException.ThrowIfNull(application);
        foreach (var controller in application.Controllers)
        {
            var type = controller.ControllerType.AsType();
            if (type.Namespace != typeof(AdminApiController).Namespace || Exempt.Contains(type))
                continue;

            controller.Filters.Add(new ServiceFilterAttribute(typeof(CookieAntiforgeryFilter)));
        }
    }
}
