#pragma warning disable CA1707, CA2007
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Tests;

/// <summary>
/// The API must answer with the enum values api-spec.yaml declares. NSwag puts each
/// spec value on an [EnumMember] attribute, but System.Text.Json's stock converter
/// ignores it and writes the CLR member name — so thirteen of the seventeen contract
/// enums went out misspelt, the role ladder among them.
/// </summary>
[TestClass]
public sealed class EnumMemberJsonConverterTests
{
    private static JsonSerializerOptions Options()
    {
        // The same order Startup registers, so these tests exercise the real
        // precedence between the two converters.
        var options = new JsonSerializerOptions();
        options.Converters.Add(new EnumMemberJsonConverterFactory());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    [TestMethod]
    public void Lowercase_spec_values_are_written_as_declared()
    {
        Assert.AreEqual("\"owner\"", JsonSerializer.Serialize(RoleEntryRole.Owner, Options()));
        Assert.AreEqual("\"piiReader\"", JsonSerializer.Serialize(RoleEntryRole.PiiReader, Options()));
        Assert.AreEqual("\"contributor\"", JsonSerializer.Serialize(EndpointRoleInfoRole.Contributor, Options()));
        Assert.AreEqual("\"none\"", JsonSerializer.Serialize(CurrentUserAccessInfoSiteRole.None, Options()));
    }

    [TestMethod]
    public void PascalCase_spec_values_are_left_exactly_as_they_were()
    {
        // Four contract enums declare PascalCase values, and the UI compares these as
        // raw strings. A blanket camelCase naming policy would have broken them, which
        // is why the fix honours each declared value rather than imposing a convention.
        Assert.AreEqual("\"Pending\"", JsonSerializer.Serialize(AdminDeleteStatus.Pending, Options()));
        Assert.AreEqual("\"DeadLettered\"", JsonSerializer.Serialize(AdminDeleteStatus.DeadLettered, Options()));
        Assert.AreEqual("\"TooManyRequests\"", JsonSerializer.Serialize(AdminSkipSourceStatus.TooManyRequests, Options()));
    }

    [TestMethod]
    public void A_camelCase_multiword_value_keeps_its_casing()
    {
        Assert.AreEqual(
            "\"resubmitWithChanges\"",
            JsonSerializer.Serialize(AuditEntryAuditType.ResubmitWithChanges, Options()));
    }

    [TestMethod]
    public void The_declared_value_round_trips()
    {
        var json = JsonSerializer.Serialize(CurrentUserAccessInfoSiteRole.Owner, Options());
        Assert.AreEqual(
            CurrentUserAccessInfoSiteRole.Owner,
            JsonSerializer.Deserialize<CurrentUserAccessInfoSiteRole>(json, Options()));
    }

    [TestMethod]
    public void Reading_still_accepts_the_CLR_name_callers_were_sent_before()
    {
        // The API answered "Owner" for as long as the stock converter was in place, so
        // anything that stored or echoes that value must keep deserializing.
        Assert.AreEqual(
            CurrentUserAccessInfoSiteRole.Owner,
            JsonSerializer.Deserialize<CurrentUserAccessInfoSiteRole>("\"Owner\"", Options()));
        Assert.AreEqual(
            AuditEntryAuditType.ResubmitWithChanges,
            JsonSerializer.Deserialize<AuditEntryAuditType>("\"RESUBMITWITHCHANGES\"", Options()));
    }

    [TestMethod]
    public void An_unknown_value_is_rejected_rather_than_silently_defaulting()
    {
        Assert.ThrowsExactly<JsonException>(
            () => JsonSerializer.Deserialize<RoleEntryRole>("\"archivist\"", Options()));
    }

    [TestMethod]
    public void Malformed_input_raises_JsonException_rather_than_a_server_fault()
    {
        // Utf8JsonReader.GetString() throws InvalidOperationException for any token
        // that is not a string, and ASP.NET reports that as a 500. Bad request bodies
        // must reach the caller as a 400, which is what JsonException produces.
        foreach (var json in new[] { "true", "{}", "[]", "1e400", "99999999999999999999" })
        {
            Assert.ThrowsExactly<JsonException>(
                () => JsonSerializer.Deserialize<RoleEntryRole>(json, Options()),
                $"Expected JsonException for {json}.");
        }
    }

    [TestMethod]
    public void An_ordinal_still_deserializes()
    {
        // Payloads written before the contract settled carry the numeric value.
        Assert.AreEqual(RoleEntryRole.Owner, JsonSerializer.Deserialize<RoleEntryRole>("2", Options()));
    }

    [TestMethod]
    public void A_nullable_contract_enum_uses_the_declared_value_too()
    {
        CurrentUserAccessInfoSiteRole? role = CurrentUserAccessInfoSiteRole.Reader;
        Assert.AreEqual("\"reader\"", JsonSerializer.Serialize(role, Options()));
        Assert.AreEqual("null", JsonSerializer.Serialize((CurrentUserAccessInfoSiteRole?)null, Options()));
    }
}
