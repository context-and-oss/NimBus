using NimBus.Core.Events;
using NimBus.Core.Messages.PII;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace NimBus.Events.Orders;

/// <summary>
/// Carries the personal contact and delivery details attached to an order.
/// </summary>
/// <remarks>
/// Demonstrates every <see cref="MaskMode"/>: operators without the PiiReader role
/// see the annotated values masked while the operational fields (order id, carrier,
/// delivery window) stay readable, so a failure can still be triaged without PII access.
/// </remarks>
[Description("Published when a customer supplies contact and delivery details for an order.")]
[SessionKey(nameof(OrderId))]
public class OrderDeliveryDetailsCaptured : Event
{
    public static readonly OrderDeliveryDetailsCaptured Example = new OrderDeliveryDetailsCaptured
    {
        OrderId = Guid.Parse("2bb7d0b3-840f-4e54-a0d4-fb31a7cabf82"),
        CustomerEmail = "alex@example.com",
        CustomerPhone = "+4512345678",
        NationalIdentificationNumber = "010101-1234",
        DeliveryAddress = new DeliveryAddress
        {
            Street = "Vestergade 12, 3. th",
            PostalCode = "8000",
            City = "Aarhus",
            CountryCode = "DK",
        },
        CarrierPreference = "standard",
        DeliveryWindow = "2026-08-12T09:00:00Z/2026-08-12T17:00:00Z",
    };

    [Required]
    [Description("The order these delivery details belong to.")]
    public Guid OrderId { get; set; }

    // Hash keeps the value correlatable across records (same input, same salted hash)
    // without revealing it — useful for spotting repeat failures for one customer.
    [Sensitive(Mode = MaskMode.Hash)]
    [Required]
    [Description("The email address used for delivery notifications.")]
    public string CustomerEmail { get; set; } = null!;

    // PartialReveal keeps the last four digits so an operator can match the number
    // against what a customer reads out, without exposing the full line.
    [Sensitive(Mode = MaskMode.PartialReveal, Reveal = 4)]
    [Description("The phone number the carrier uses for delivery contact.")]
    public string CustomerPhone { get; set; } = null!;

    // Full redaction: there is no operational reason to read this back.
    [Sensitive]
    [Description("National identification number, where the destination country requires it for customs.")]
    public string NationalIdentificationNumber { get; set; } = null!;

    [Description("The address the order ships to.")]
    public DeliveryAddress DeliveryAddress { get; set; } = null!;

    [Description("The carrier service level requested for the shipment.")]
    public string CarrierPreference { get; set; } = null!;

    [Description("The ISO 8601 interval the customer selected for delivery.")]
    public string DeliveryWindow { get; set; } = null!;
}

/// <summary>
/// A postal address. Marked sensitive at class level, so every member — present and
/// future — is masked for non-PiiReaders without needing a per-property annotation.
/// </summary>
[Sensitive]
[Description("A customer postal address.")]
public class DeliveryAddress
{
    [Description("Street name, number and any apartment designation.")]
    public string Street { get; set; } = null!;

    [Description("Postal code.")]
    public string PostalCode { get; set; } = null!;

    [Description("City or town.")]
    public string City { get; set; } = null!;

    [Description("ISO 3166-1 alpha-2 country code.")]
    public string CountryCode { get; set; } = null!;
}
