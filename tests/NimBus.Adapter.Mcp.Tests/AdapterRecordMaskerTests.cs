#pragma warning disable CA1707, CA2007
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NimBus.Adapter.Mcp.Tests.Infrastructure;
using NimBus.Core.Messages.PII;

namespace NimBus.Adapter.Mcp.Tests;

[TestClass]
public sealed class AdapterRecordMaskerTests
{
    private const string Salt = "test-salt";

    private static Customer Ada() => new()
    {
        Id = "42",
        Name = "Ada Lovelace",
        Email = "ada@example.com",
        Iban = "DK5000400440116243",
        NationalId = "010203-1234",
        Address = new CustomerAddress { Street = "1 Analytical Way", City = "London" },
        Contacts = [new CustomerContact { Role = "billing", Phone = "+44 20 7946 0000" }],
        CreditLimit = 5000,
    };

    private static JsonObject Mask(object record) => new AdapterRecordMasker(Salt).Mask(record)!.AsObject();

    [TestMethod]
    public void Unmarked_fields_stay_readable_with_their_json_types()
    {
        var masked = Mask(Ada());

        Assert.AreEqual("42", (string?)masked["id"]);
        Assert.AreEqual("Ada Lovelace", (string?)masked["name"]);
        Assert.AreEqual("London", (string?)masked["address"]!["city"]);
        Assert.AreEqual("billing", (string?)masked["contacts"]![0]!["role"]);
        Assert.AreEqual(5000, (int?)masked["creditLimit"]);
    }

    [TestMethod]
    public void Redact_replaces_the_value_with_the_NimBus_token()
    {
        Assert.AreEqual(EventJsonMasker.DefaultRedactToken, (string?)Mask(Ada())["email"]);
    }

    [TestMethod]
    public void Partial_reveal_keeps_the_last_characters()
    {
        Assert.AreEqual("**************6243", (string?)Mask(Ada())["iban"]);
    }

    [TestMethod]
    public void Hash_is_the_salted_sha256_NimBus_uses()
    {
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Salt + "010203-1234"))).ToLower(CultureInfo.InvariantCulture);

        Assert.AreEqual(expected, (string?)Mask(Ada())["nationalId"]);
    }

    [TestMethod]
    public void Nested_objects_and_collection_elements_are_masked()
    {
        var masked = Mask(Ada());

        Assert.AreEqual("***", (string?)masked["address"]!["street"]);
        Assert.AreEqual("***", (string?)masked["contacts"]![0]!["phone"]);
    }

    [TestMethod]
    public void A_class_marked_sensitive_masks_every_value()
    {
        var masked = Mask(new Secret { Value = "hidden", Number = 7 });

        Assert.AreEqual("***", (string?)masked["value"]);
        Assert.AreEqual("***", (string?)masked["number"]);
    }

    [TestMethod]
    public void A_null_sensitive_value_stays_null()
    {
        var masked = Mask(new Customer { Id = "1", Email = null });

        Assert.IsTrue(masked.ContainsKey("email"));
        Assert.IsNull(masked["email"]);
    }

    [TestMethod]
    public void The_masked_record_carries_no_marker_and_no_original_value()
    {
        var json = Mask(Ada()).ToJsonString();

        Assert.DoesNotContain(EventJsonMasker.PiiMaskedMarker, json);
        Assert.DoesNotContain("ada@example.com", json);
        Assert.DoesNotContain("1 Analytical Way", json);
        Assert.DoesNotContain("7946", json);
        Assert.DoesNotContain("010203", json);
    }

    [TestMethod]
    public void It_agrees_with_the_NimBus_event_masker()
    {
        var platform = new SingleTypePlatform(typeof(Customer));
        var nimbus = new EventJsonMasker(platform, Salt);
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(Ada(), new Newtonsoft.Json.JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        });
        var expected = JsonNode.Parse(nimbus.StripMaskedMarker(nimbus.Mask(nameof(Customer), json)));

        Assert.IsTrue(JsonNode.DeepEquals(expected, Mask(Ada())), Mask(Ada()).ToJsonString());
    }

    [TestMethod]
    public void A_null_record_is_null()
    {
        Assert.IsNull(new AdapterRecordMasker(Salt).Mask(null));
    }

    [TestMethod]
    public void An_untyped_json_record_passes_through_unchanged()
    {
        var record = JsonNode.Parse("""{"email":"ada@example.com"}""");

        var masked = new AdapterRecordMasker(Salt).Mask(record);

        Assert.IsTrue(JsonNode.DeepEquals(record, masked));
    }

    [TestMethod]
    public void A_list_of_typed_records_is_masked_element_by_element()
    {
        var masked = new AdapterRecordMasker(Salt).Mask(new List<Customer> { Ada(), Ada() })!.AsArray();

        Assert.HasCount(2, masked);
        Assert.AreEqual("***", (string?)masked[1]!["email"]);
        Assert.AreEqual("Ada Lovelace", (string?)masked[1]!["name"]);
    }

    [TestMethod]
    public void A_dictionary_of_typed_records_is_masked_value_by_value()
    {
        var masked = new AdapterRecordMasker(Salt).Mask(new Dictionary<string, Customer> { ["primary"] = Ada() })!.AsObject();

        Assert.AreEqual("***", (string?)masked["primary"]!["email"]);
        Assert.DoesNotContain("ada@example.com", masked.ToJsonString());
    }

    [TestMethod]
    public void A_record_with_an_invalid_marker_is_refused()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => new AdapterRecordMasker(Salt).Mask(new BadReveal { Value = "x" }));
    }

    private sealed class BadReveal
    {
        [Sensitive(Mode = MaskMode.PartialReveal)]
        public string? Value { get; set; }
    }

    private sealed class SingleTypePlatform(Type type) : NimBus.Core.IPlatform
    {
        public IEnumerable<NimBus.Core.Endpoints.IEndpoint> Endpoints => [];

        public IEnumerable<NimBus.Core.Events.IEventType> EventTypes => [new NimBus.Core.Events.EventType(type)];

        public IEnumerable<NimBus.Core.Endpoints.IEndpoint> GetConsumers(NimBus.Core.Events.IEventType eventType) => [];

        public IEnumerable<NimBus.Core.Endpoints.IEndpoint> GetProducers(NimBus.Core.Events.IEventType eventType) => [];
    }
}
