using NimBus.Core.Events;
using NimBus.Core.Messages.PII;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace NimBus.Events.Customers;

/// <summary>
/// Instructs the billing system to suspend further credit for a customer.
/// </summary>
/// <remarks>
/// A <see cref="Command"/>, not an event: imperative intent addressed to a single
/// recipient, so exactly one endpoint may declare it consumed. Zero consumers would
/// dead-letter every send; more than one would silently turn the instruction into a
/// broadcast. <c>PlatformValidation.ValidateCommandConsumers</c> enforces this at
/// provisioning time.
/// </remarks>
[Description("Instructs billing to place a customer on credit hold pending review.")]
[SessionKey(nameof(CustomerId))]
public class PlaceCustomerOnCreditHold : Command
{
    public static readonly PlaceCustomerOnCreditHold Example = new PlaceCustomerOnCreditHold
    {
        CustomerId = Guid.Parse("7b1f2765-3a3e-4ed6-9de1-e54fd6914aa5"),
        CustomerEmail = "alex@example.com",
        Reason = "PaymentDisputed",
        CaseNotes = "Customer disputed invoice 4471; contacted on +4512345678 by Jane Caseworker.",
        RequestedBy = "billing-review",
        EffectiveFrom = DateTimeOffset.Parse("2026-08-12T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
    };

    [Required]
    [Description("The customer to place on credit hold.")]
    public Guid CustomerId { get; set; }

    [Sensitive(Mode = MaskMode.Hash)]
    [Description("The customer's email address, carried so billing can notify them of the hold.")]
    public string CustomerEmail { get; set; } = null!;

    [Required]
    [Description("Coded reason for the hold. Not free text, so it is safe to read without PII access.")]
    public string Reason { get; set; } = null!;

    // Free-text written by caseworkers; in practice it quotes names, phone numbers and
    // dispute details, so it is redacted wholesale rather than trusted to stay clean.
    [Sensitive]
    [Description("Free-text case notes recorded by the reviewing agent.")]
    public string CaseNotes { get; set; } = null!;

    [Description("Identifier of the process or team that requested the hold.")]
    public string RequestedBy { get; set; } = null!;

    [Description("When the hold takes effect.")]
    public DateTimeOffset EffectiveFrom { get; set; }
}
