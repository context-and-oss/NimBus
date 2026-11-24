#pragma warning disable CA1707, CA2007
using System.Linq;
using CrmErpDemo.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CrmErpDemo.AppHost.Tests;

/// <summary>
/// The WebApp's Event Types page, the AsyncAPI export and the EventCatalog export all
/// read field descriptions from <c>[Description]</c>. The demo is the showcase, so a
/// field without one renders as a dash next to fields that are documented.
/// </summary>
[TestClass]
public sealed class EventCatalogDescriptionTests
{
    [TestMethod]
    public void EveryEventType_HasADescription()
    {
        var missing = new CrmErpPlatformConfiguration().EventTypes
            .Where(t => string.IsNullOrWhiteSpace(t.Description))
            .Select(t => t.Name)
            .Distinct()
            .ToList();

        Assert.AreEqual(0, missing.Count, "Event types without [Description]: " + string.Join(", ", missing));
    }

    [TestMethod]
    public void EveryEventTypeProperty_HasADescription()
    {
        var missing = new CrmErpPlatformConfiguration().EventTypes
            .SelectMany(t => t.Properties
                .Where(p => string.IsNullOrWhiteSpace(p.Description))
                .Select(p => $"{t.Name}.{p.Name}"))
            .Distinct()
            .ToList();

        Assert.AreEqual(0, missing.Count, "Properties without [Description]: " + string.Join(", ", missing));
    }
}
