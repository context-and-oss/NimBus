using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace NimBus.WebApp.Versioning;

/// <summary>
/// Applies <see cref="ApiVersionAliasConvention"/> to one controller. Declared on the
/// NSwag-generated controllers' partial classes (<c>Controllers/ApiVersionAliases.cs</c>), so
/// every host that loads them serves the deprecated unversioned routes, not only the WebApp's.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class ApiVersionAliasesAttribute : Attribute, IControllerModelConvention
{
    private static readonly ApiVersionAliasConvention Convention = new();

    /// <inheritdoc />
    public void Apply(ControllerModel controller) => Convention.Apply(controller);
}
