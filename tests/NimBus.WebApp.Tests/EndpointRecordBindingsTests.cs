#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// A refused record binding names its row and the field, so an empty row the user added is not
/// mistaken for a filled-in one. The WebApp's adapter-mcp.ts gives the same messages.
/// </summary>
[TestClass]
public sealed class EndpointRecordBindingsTests
{
    private static readonly (string?, string?, string?) Valid = ("CrmAccountCreated", "$.AccountId", "customer");

    [TestMethod]
    public void An_empty_new_row_is_named_by_its_number()
    {
        Assert.AreEqual(
            "Binding 2 is empty: fill in the event type, the payload path and the entity, or delete it.",
            EndpointRecordBindings.Validate([Valid, ("", " ", null)]));
    }

    [TestMethod]
    [DataRow("", "$.AccountId", "customer", "Binding 1: the event type is empty.")]
    [DataRow("CrmAccountCreated", "  ", "customer", "Binding 1: the payload path is empty.")]
    [DataRow("CrmAccountCreated", "$.AccountId", "", "Binding 1: the entity is empty.")]
    [DataRow("CrmAccountCreated", "", "", "Binding 1: the payload path and the entity are empty.")]
    [DataRow("", "$.AccountId", "", "Binding 1: the event type and the entity are empty.")]
    public void The_empty_fields_are_named(string eventTypeId, string payloadPath, string entity, string message)
    {
        Assert.AreEqual(message, EndpointRecordBindings.Validate([(eventTypeId, payloadPath, entity)]));
    }

    [TestMethod]
    public void An_invalid_value_names_its_row_and_field()
    {
        Assert.AreEqual(
            "Binding 2: the payload path 'AccountId' must be a dotted path such as $.AccountId, without wildcards, indexes or filters.",
            EndpointRecordBindings.Validate([Valid, ("CrmAccountCreated", "AccountId", "customer")]));
        Assert.AreEqual(
            "Binding 1: the entity 'customer record' must be a name of letters, digits, '.', '_' or '-'.",
            EndpointRecordBindings.Validate([("CrmAccountCreated", "$.AccountId", "customer record")]));
        Assert.AreEqual(
            "Binding 1: the event type is longer than 200 characters.",
            EndpointRecordBindings.Validate([(new string('x', 201), "$.AccountId", "customer")]));
        Assert.AreEqual(
            "Binding 1: the event type contains control characters.",
            EndpointRecordBindings.Validate([("Crm\u0001Account", "$.AccountId", "customer")]));
    }

    [TestMethod]
    public void A_path_bound_twice_names_both_rows()
    {
        Assert.AreEqual(
            "Binding 2: the path $.accountId is already bound for CrmAccountCreated in binding 1.",
            EndpointRecordBindings.Validate([Valid, ("CrmAccountCreated", "$.accountId", "account")]));
    }

    [TestMethod]
    public void Valid_bindings_pass()
    {
        Assert.IsNull(EndpointRecordBindings.Validate([Valid, ("CrmContactCreated", "$.Contact.Id", "contact")]));
        Assert.IsNull(EndpointRecordBindings.Validate([]));
    }
}
