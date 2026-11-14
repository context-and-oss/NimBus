#pragma warning disable CA1707, CA2007
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NimBus.Core;
using NimBus.Core.Messages.PII;
using NimBus.Events.Customers;
using NimBus.Events.Orders;
using Xunit;

namespace NimBus.CommandLine.Tests;

// The shipped catalog's PII annotations, exercised against the real
// PlatformConfiguration rather than a test fixture — so an event type that ships
// without the annotations it needs fails here, not in production.
public sealed class PlatformPiiCatalogTests
{
    private static EventJsonMasker Masker() => new(new PlatformConfiguration(), "test-salt");

    [Fact]
    public void Credit_hold_command_has_exactly_one_consumer()
    {
        // A command with zero consumers dead-letters every send; with two it silently
        // becomes a broadcast. Provisioning enforces this, so the catalog must satisfy it.
        var errors = PlatformValidation.ValidateCommandConsumers(new PlatformConfiguration());

        Assert.True(errors.Count == 0, string.Join(" | ", errors));
    }

    [Fact]
    public void Credit_hold_command_is_registered_as_a_command_type()
    {
        var platform = new PlatformConfiguration();
        var commandType = platform.EventTypes.Single(t => t.Id == nameof(PlaceCustomerOnCreditHold));

        Assert.True(
            typeof(NimBus.Core.Events.Command).IsAssignableFrom(commandType.GetEventClassType()),
            "PlaceCustomerOnCreditHold must derive from Command for the single-consumer rule to apply.");
    }

    [Fact]
    public void Delivery_details_mask_each_mode_and_leave_operational_fields_readable()
    {
        var json = JsonConvert.SerializeObject(OrderDeliveryDetailsCaptured.Example);

        var masked = JObject.Parse(Masker().Mask(nameof(OrderDeliveryDetailsCaptured), json));

        // Redact
        Assert.Equal("***", (string?)masked[nameof(OrderDeliveryDetailsCaptured.NationalIdentificationNumber)]);

        // PartialReveal keeps the last 4 characters of "+4512345678"
        Assert.Equal("*******5678", (string?)masked[nameof(OrderDeliveryDetailsCaptured.CustomerPhone)]);

        // Hash is not the plaintext, and is stable for the same salt
        var hashed = (string?)masked[nameof(OrderDeliveryDetailsCaptured.CustomerEmail)];
        Assert.NotEqual(OrderDeliveryDetailsCaptured.Example.CustomerEmail, hashed);
        Assert.Equal(
            hashed,
            (string?)JObject.Parse(Masker().Mask(nameof(OrderDeliveryDetailsCaptured), json))
                [nameof(OrderDeliveryDetailsCaptured.CustomerEmail)]);

        // Operational fields stay readable — the whole point of field-level masking.
        Assert.Equal("standard", (string?)masked[nameof(OrderDeliveryDetailsCaptured.CarrierPreference)]);
        Assert.Equal(
            OrderDeliveryDetailsCaptured.Example.OrderId.ToString(),
            (string?)masked[nameof(OrderDeliveryDetailsCaptured.OrderId)]);
    }

    [Fact]
    public void Class_level_sensitive_cascades_over_the_whole_delivery_address()
    {
        var json = JsonConvert.SerializeObject(OrderDeliveryDetailsCaptured.Example);

        var address = JObject.Parse(Masker().Mask(nameof(OrderDeliveryDetailsCaptured), json))
            [nameof(OrderDeliveryDetailsCaptured.DeliveryAddress)]!;

        Assert.Equal("***", (string?)address[nameof(DeliveryAddress.Street)]);
        Assert.Equal("***", (string?)address[nameof(DeliveryAddress.PostalCode)]);
        Assert.Equal("***", (string?)address[nameof(DeliveryAddress.City)]);
        Assert.Equal("***", (string?)address[nameof(DeliveryAddress.CountryCode)]);
    }

    [Fact]
    public void Credit_hold_masks_free_text_notes_but_keeps_the_coded_reason()
    {
        var json = JsonConvert.SerializeObject(PlaceCustomerOnCreditHold.Example);

        var masked = JObject.Parse(Masker().Mask(nameof(PlaceCustomerOnCreditHold), json));

        // Case notes quote names and phone numbers, so they are redacted wholesale.
        Assert.Equal("***", (string?)masked[nameof(PlaceCustomerOnCreditHold.CaseNotes)]);
        Assert.NotEqual(
            PlaceCustomerOnCreditHold.Example.CustomerEmail,
            (string?)masked[nameof(PlaceCustomerOnCreditHold.CustomerEmail)]);

        // Coded/operational fields remain readable so the hold can still be triaged.
        Assert.Equal("PaymentDisputed", (string?)masked[nameof(PlaceCustomerOnCreditHold.Reason)]);
        Assert.Equal("billing-review", (string?)masked[nameof(PlaceCustomerOnCreditHold.RequestedBy)]);
    }

    [Fact]
    public void Masked_catalog_payloads_are_detectable_by_the_resubmit_gate()
    {
        var masker = Masker();
        var masked = masker.Mask(
            nameof(OrderDeliveryDetailsCaptured),
            JsonConvert.SerializeObject(OrderDeliveryDetailsCaptured.Example));

        Assert.True(masker.ContainsRedactPlaceholder(nameof(OrderDeliveryDetailsCaptured), masked));
    }

    [Fact]
    public void OrderPlaced_carries_no_sensitive_fields_and_is_unaffected()
    {
        // Guards the deliberate decision to leave OrderPlaced unannotated: its fields are
        // identifiers and amounts, not PII. If that changes, this test should change with it.
        var json = JsonConvert.SerializeObject(OrderPlaced.Example);

        var masked = Masker().Mask(nameof(OrderPlaced), json);

        Assert.False(SensitiveTypeInspector.ContainsSensitiveData(typeof(OrderPlaced)));

        // Nothing was masked, so no sidecar marker should be added.
        Assert.Null(JObject.Parse(masked)[EventJsonMasker.PiiMaskedMarker]);
    }
}
