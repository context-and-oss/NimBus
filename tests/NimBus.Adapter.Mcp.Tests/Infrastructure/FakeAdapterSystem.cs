#pragma warning disable CA1707, CA2007
using System.Collections.Concurrent;
using NimBus.Core.Messages.PII;

namespace NimBus.Adapter.Mcp.Tests.Infrastructure;

/// <summary>What the fake system holds; one per test host, shared by every scope.</summary>
internal sealed class FakeAdapterData
{
    public ConcurrentDictionary<(string Entity, string Key), object> Records { get; } = new();

    /// <summary>Set to make every call into the system throw it.</summary>
    public Exception? Failure { get; set; }

    public AdapterHealth Health { get; set; } = new(Reachable: true, Message: "ERP answered in 12 ms");

    public int RecordReads;
}

/// <summary>An adapter's system: customers and orders, with a mapping for CustomerChanged.</summary>
internal sealed class FakeAdapterSystem(FakeAdapterData data) : IAdapterSystem
{
    public IReadOnlyCollection<string> Entities { get; } = ["customer", "order"];

    public Task<object?> GetRecordAsync(string entity, string key, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref data.RecordReads);
        if (data.Failure is not null)
            throw data.Failure;
        return Task.FromResult(data.Records.TryGetValue((entity, key), out var record) ? record : null);
    }

    public AdapterMapping? GetMapping(string eventTypeId)
    {
        if (data.Failure is not null)
            throw data.Failure;

        return eventTypeId == "CustomerChanged"
            ? new AdapterMapping
            {
                Fields =
                [
                    new AdapterFieldMapping("customerId", "accountnumber"),
                    new AdapterFieldMapping("email", "emailaddress1", "Lower-cased before it is written"),
                ],
                Settings = new Dictionary<string, string> { ["company"] = "CRONUS", ["upsert"] = "true" },
            }
            : null;
    }

    public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        if (data.Failure is not null)
            throw data.Failure;
        return Task.FromResult(data.Health);
    }
}

/// <summary>A customer as the system returns it, with NimBus's <c>[Sensitive]</c> markers.</summary>
internal sealed class Customer
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    [Sensitive]
    public string? Email { get; set; }

    [Sensitive(Mode = MaskMode.PartialReveal, Reveal = 4)]
    public string? Iban { get; set; }

    [Sensitive(Mode = MaskMode.Hash)]
    public string? NationalId { get; set; }

    public CustomerAddress? Address { get; set; }

    public List<CustomerContact> Contacts { get; set; } = [];

    public int CreditLimit { get; set; }
}

internal sealed class CustomerAddress
{
    [Sensitive]
    public string? Street { get; set; }

    public string? City { get; set; }
}

internal sealed class CustomerContact
{
    public string? Role { get; set; }

    [Sensitive]
    public string? Phone { get; set; }
}

/// <summary>A class marked sensitive as a whole: every value is masked.</summary>
[Sensitive]
internal sealed class Secret
{
    public string? Value { get; set; }

    public int Number { get; set; }
}
