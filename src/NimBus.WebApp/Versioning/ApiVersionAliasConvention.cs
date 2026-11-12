using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace NimBus.WebApp.Versioning;

/// <summary>
/// Serves every REST API route at both <c>/api/v1/…</c> (the versioned contract) and
/// <c>/api/…</c> (the unversioned route callers used before 1.0.0, deprecated). The unversioned
/// selector carries <see cref="LegacyApiRouteMetadata"/>, which
/// <see cref="LegacyApiRouteHeadersMiddleware"/> turns into <c>Deprecation</c> and
/// <c>Link</c> headers.
/// <para>
/// The NSwag-generated controllers declare <c>api/v1/…</c> (from <c>api-spec.yaml</c>), so
/// they gain an unversioned alias. Hand-written API controllers that still declare
/// <c>api/…</c> (intelligence settings, the Identity extension's <c>api/auth</c>, the
/// integration-intelligence controllers) gain a <c>/api/v1/…</c> route instead. Both
/// controller-level and action-level attribute routes are handled, and the convention is
/// idempotent, so it can run both globally and from <see cref="ApiVersionAliasesAttribute"/>.
/// </para>
/// <para>
/// The copy is made from the existing selector, so its route constraints and any endpoint
/// metadata already attached travel with it. Conventions that run later and attach metadata to
/// every selector of an action (rate limiting) cover both routes too. Filters belong to the
/// action, not the selector, and therefore apply to both.
/// </para>
/// </summary>
public sealed class ApiVersionAliasConvention : IApplicationModelConvention, IControllerModelConvention
{
    /// <summary>The path segment that names the current API version.</summary>
    public const string CurrentVersionSegment = "v1";

    /// <inheritdoc />
    public void Apply(ApplicationModel application)
    {
        ArgumentNullException.ThrowIfNull(application);
        foreach (var controller in application.Controllers)
            Apply(controller);
    }

    /// <inheritdoc />
    public void Apply(ControllerModel controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        Alias(controller.Selectors);
        foreach (var action in controller.Actions)
            Alias(action.Selectors);
    }

    private static void Alias(IList<SelectorModel> selectors)
    {
        foreach (var selector in selectors.ToList())
        {
            var template = selector.AttributeRouteModel?.Template;
            if (template is null || !TrySplit(template, out var prefix, out var rest, out var versioned))
                continue;

            var counterpart = prefix + (versioned ? "api" : "api/" + CurrentVersionSegment) + rest;
            var existing = selectors.FirstOrDefault(s => string.Equals(s.AttributeRouteModel?.Template, counterpart, StringComparison.OrdinalIgnoreCase));

            if (versioned)
            {
                // api/v1/x → add the deprecated api/x, unless it is already there.
                if (existing is null)
                    selectors.Add(Copy(selector, counterpart, legacy: true));
                else
                    MarkLegacy(existing);
            }
            else
            {
                // api/x → this is the deprecated form; add api/v1/x ahead of it.
                MarkLegacy(selector);
                if (existing is null)
                    selectors.Insert(selectors.IndexOf(selector), Copy(selector, counterpart, legacy: false));
            }
        }
    }

    // "api/x", "/api/x" and "~/api/x" are API routes; prefix keeps the leading "/" or "~/".
    private static bool TrySplit(string template, out string prefix, out string rest, out bool versioned)
    {
        var start = template.StartsWith("~/", StringComparison.Ordinal) ? 2 : template.StartsWith('/') ? 1 : 0;
        prefix = template[..start];
        var path = template[start..];
        versioned = false;
        rest = string.Empty;

        if (!IsSegmentPrefix(path, "api"))
            return false;

        var afterApi = path[3..];
        var versionPrefix = "/" + CurrentVersionSegment;
        if (afterApi.StartsWith(versionPrefix, StringComparison.OrdinalIgnoreCase)
            && (afterApi.Length == versionPrefix.Length || afterApi[versionPrefix.Length] == '/'))
        {
            versioned = true;
            rest = afterApi[versionPrefix.Length..];
        }
        else
        {
            rest = afterApi;
        }

        return true;
    }

    private static bool IsSegmentPrefix(string path, string segment)
        => path.StartsWith(segment, StringComparison.OrdinalIgnoreCase)
           && (path.Length == segment.Length || path[segment.Length] == '/');

    private static SelectorModel Copy(SelectorModel source, string template, bool legacy)
    {
        var copy = new SelectorModel(source)
        {
            AttributeRouteModel = new AttributeRouteModel(source.AttributeRouteModel!)
            {
                Template = template,
                // Route names must be unique; the copy is reachable but never named.
                Name = null,
            },
        };

        copy.EndpointMetadata.Remove(LegacyApiRouteMetadata.Instance);
        if (legacy)
            copy.EndpointMetadata.Add(LegacyApiRouteMetadata.Instance);
        return copy;
    }

    private static void MarkLegacy(SelectorModel selector)
    {
        if (!selector.EndpointMetadata.Contains(LegacyApiRouteMetadata.Instance))
            selector.EndpointMetadata.Add(LegacyApiRouteMetadata.Instance);
    }
}
