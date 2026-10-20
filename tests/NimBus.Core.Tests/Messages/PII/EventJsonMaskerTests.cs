#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace NimBus.Core.Tests.Messages.PII;

[TestClass]
public class EventJsonMaskerTests
{
    // -------- Test fixtures --------

    public class SimpleEvent : Event
    {
        [Sensitive]
        public string Cpr { get; set; } = null!;

        public string EmployeeNumber { get; set; } = null!;
    }

    public class PartialEvent : Event
    {
        [Sensitive(Mode = MaskMode.PartialReveal, Reveal = 4)]
        public string Phone { get; set; } = null!;
    }

    public class HashEvent : Event
    {
        [Sensitive(Mode = MaskMode.Hash)]
        public string Email { get; set; } = null!;
    }

    [Sensitive]
    public class PrivateAddress
    {
        public string Street { get; set; } = null!;
        public string City { get; set; } = null!;
        public string Zip { get; set; } = null!;
    }

    public class NestedEvent : Event
    {
        public string EmployeeNumber { get; set; } = null!;

        public PrivateAddress Address { get; set; } = null!;
    }

    public class CollectionItem
    {
        [Sensitive]
        public string Secret { get; set; } = null!;
        public string Public { get; set; } = null!;
    }

    public class CollectionEvent : Event
    {
        public List<CollectionItem> Items { get; set; } = null!;
    }

    public class JsonRenamedEvent : Event
    {
        [Sensitive]
        [JsonProperty(PropertyName = "ssn")]
        public string SocialSecurityNumber { get; set; } = null!;
    }

    // -------- Test platform --------

    private class TestEndpoint : Endpoint
    {
        public TestEndpoint()
        {
            Produces<SimpleEvent>();
            Produces<PartialEvent>();
            Produces<HashEvent>();
            Produces<NestedEvent>();
            Produces<CollectionEvent>();
            Produces<JsonRenamedEvent>();
            Produces<CombinedSensitiveEvent>();
        }
    }

    private class TestPlatform : Platform
    {
        public TestPlatform() { AddEndpoint(new TestEndpoint()); }
    }

    private static EventJsonMasker NewMasker(string salt = "")
        => new EventJsonMasker(new TestPlatform(), salt);

    // -------- Tests --------

    [TestMethod]
    public void Redact_Replaces_Sensitive_Property()
    {
        var json = JsonConvert.SerializeObject(new SimpleEvent { Cpr = "1234567890", EmployeeNumber = "E1" });

        var masked = NewMasker().Mask(nameof(SimpleEvent), json);
        var parsed = JObject.Parse(masked);

        Assert.AreEqual("***", (string?)parsed["Cpr"]);
        Assert.AreEqual("E1", (string?)parsed["EmployeeNumber"]);
    }

    [TestMethod]
    public void Full_Redaction_Overrides_Partial_And_Hash_Modes()
    {
        var json = JsonConvert.SerializeObject(new CombinedSensitiveEvent
        {
            Phone = "12345678",
            Email = "a@b.dk",
            Nested = new PrivateAddress { Street = "Vej 1", City = "Aarhus", Zip = "8000" },
        });

        var redacted = NewMasker("salt").Redact(nameof(CombinedSensitiveEvent), json);
        var parsed = JObject.Parse(redacted);

        Assert.AreEqual("***", (string?)parsed["Phone"]);
        Assert.AreEqual("***", (string?)parsed["Email"]);
        Assert.AreEqual("***", (string?)parsed["Nested"]!["Street"]);
    }

    [TestMethod]
    public void Full_Redaction_Fails_Closed_For_Unknown_And_Invalid_Input()
    {
        var masker = NewMasker();

        Assert.AreEqual(EventJsonMasker.UnknownTypeMarker, masker.Redact("missing", "{\"secret\":\"value\"}"));
        Assert.AreEqual(EventJsonMasker.InvalidJsonMarker, masker.Redact(nameof(SimpleEvent), "{bad"));
    }

    private class CombinedSensitiveEvent : Event
    {
        [Sensitive(Mode = MaskMode.PartialReveal, Reveal = 4)]
        public string Phone { get; set; } = null!;

        [Sensitive(Mode = MaskMode.Hash)]
        public string Email { get; set; } = null!;

        public PrivateAddress Nested { get; set; } = null!;
    }

    [TestMethod]
    public void PartialReveal_Keeps_Last_N_Chars()
    {
        var json = JsonConvert.SerializeObject(new PartialEvent { Phone = "12345678" });

        var masked = NewMasker().Mask(nameof(PartialEvent), json);

        Assert.AreEqual("****5678", (string?)JObject.Parse(masked)["Phone"]);
    }

    [TestMethod]
    public void PartialReveal_Falls_Back_To_Redact_When_Value_Shorter_Than_Reveal()
    {
        var json = JsonConvert.SerializeObject(new PartialEvent { Phone = "12" });

        var masked = NewMasker().Mask(nameof(PartialEvent), json);

        Assert.AreEqual("***", (string?)JObject.Parse(masked)["Phone"]);
    }

    [TestMethod]
    public void Hash_Is_Deterministic_With_Same_Salt()
    {
        var json = JsonConvert.SerializeObject(new HashEvent { Email = "a@b.dk" });

        var first = NewMasker("salt-x").Mask(nameof(HashEvent), json);
        var second = NewMasker("salt-x").Mask(nameof(HashEvent), json);

        Assert.AreEqual(first, second);
        Assert.AreNotEqual("a@b.dk", (string?)JObject.Parse(first)["Email"]);
    }

    [TestMethod]
    public void Hash_Differs_With_Different_Salt()
    {
        var json = JsonConvert.SerializeObject(new HashEvent { Email = "a@b.dk" });

        var first = NewMasker("salt-x").Mask(nameof(HashEvent), json);
        var second = NewMasker("salt-y").Mask(nameof(HashEvent), json);

        Assert.AreNotEqual(first, second);
    }

    [TestMethod]
    public void Class_Level_Sensitive_Cascades_To_Every_Member()
    {
        var json = JsonConvert.SerializeObject(new NestedEvent
        {
            EmployeeNumber = "E1",
            Address = new PrivateAddress { Street = "Vej 1", City = "Aarhus", Zip = "8000" }
        });

        var masked = NewMasker().Mask(nameof(NestedEvent), json);
        var parsed = JObject.Parse(masked);

        Assert.AreEqual("E1", (string?)parsed["EmployeeNumber"]);
        Assert.AreEqual("***", (string?)parsed["Address"]!["Street"]);
        Assert.AreEqual("***", (string?)parsed["Address"]!["City"]);
        Assert.AreEqual("***", (string?)parsed["Address"]!["Zip"]);
    }

    [TestMethod]
    public void Collection_Of_Items_Masks_Each_Sensitive_Property()
    {
        var json = JsonConvert.SerializeObject(new CollectionEvent
        {
            Items = new List<CollectionItem>
            {
                new CollectionItem { Secret = "s1", Public = "p1" },
                new CollectionItem { Secret = "s2", Public = "p2" },
            }
        });

        var masked = NewMasker().Mask(nameof(CollectionEvent), json);
        var arr = (JArray)JObject.Parse(masked)["Items"]!;

        Assert.AreEqual("***", (string?)arr[0]!["Secret"]);
        Assert.AreEqual("p1", (string?)arr[0]!["Public"]);
        Assert.AreEqual("***", (string?)arr[1]!["Secret"]);
        Assert.AreEqual("p2", (string?)arr[1]!["Public"]);
    }

    [TestMethod]
    public void JsonProperty_Renamed_Property_Is_Recognized()
    {
        // Serialize via Newtonsoft so the JSON uses "ssn" not "SocialSecurityNumber"
        var json = JsonConvert.SerializeObject(new JsonRenamedEvent { SocialSecurityNumber = "secret" });
        StringAssert.Contains(json, "\"ssn\"");

        var masked = NewMasker().Mask(nameof(JsonRenamedEvent), json);

        Assert.AreEqual("***", (string?)JObject.Parse(masked)["ssn"]);
    }

    [TestMethod]
    public void Unknown_EventTypeId_Returns_Marker()
    {
        var masked = NewMasker().Mask("UnknownType", "{\"x\":1}");

        Assert.AreEqual(EventJsonMasker.UnknownTypeMarker, masked);
    }

    [TestMethod]
    public void Malformed_Json_Returns_Marker()
    {
        var masked = NewMasker().Mask(nameof(SimpleEvent), "{not json");

        Assert.AreEqual(EventJsonMasker.InvalidJsonMarker, masked);
    }

    [TestMethod]
    public void Null_Or_Empty_Json_Passes_Through()
    {
        var m = NewMasker();

        Assert.IsNull(m.Mask(nameof(SimpleEvent), null!));
        Assert.AreEqual("", m.Mask(nameof(SimpleEvent), ""));
    }

    [TestMethod]
    public void Null_Sensitive_Value_Stays_Null()
    {
        var json = JsonConvert.SerializeObject(new SimpleEvent { Cpr = null!, EmployeeNumber = "E1" });

        var masked = NewMasker().Mask(nameof(SimpleEvent), json);
        var parsed = JObject.Parse(masked);

        Assert.AreEqual(JTokenType.Null, parsed["Cpr"]!.Type);
        Assert.AreEqual("E1", (string?)parsed["EmployeeNumber"]);
    }

    [TestMethod]
    public void Redact_Is_Idempotent()
    {
        var json = JsonConvert.SerializeObject(new SimpleEvent { Cpr = "1234567890", EmployeeNumber = "E1" });

        var once = NewMasker().Mask(nameof(SimpleEvent), json);
        var twice = NewMasker().Mask(nameof(SimpleEvent), once);

        Assert.AreEqual(once, twice);
    }

    [TestMethod]
    public void Empty_Sensitive_Value_Becomes_Redact_Token()
    {
        var json = JsonConvert.SerializeObject(new SimpleEvent { Cpr = "", EmployeeNumber = "E1" });

        var masked = NewMasker().Mask(nameof(SimpleEvent), json);
        var parsed = JObject.Parse(masked);

        Assert.AreEqual("***", (string?)parsed["Cpr"]);
    }

    [TestMethod]
    public void ContainsRedactPlaceholder_Detects_Masked_Sensitive_Field()
    {
        var json = JsonConvert.SerializeObject(new SimpleEvent { Cpr = "1234567890", EmployeeNumber = "E1" });
        var masked = NewMasker().Mask(nameof(SimpleEvent), json);

        Assert.IsTrue(NewMasker().ContainsRedactPlaceholder(nameof(SimpleEvent), masked));
    }

    [TestMethod]
    public void ContainsRedactPlaceholder_False_For_Plaintext_Payload()
    {
        var json = JsonConvert.SerializeObject(new SimpleEvent { Cpr = "1234567890", EmployeeNumber = "E1" });

        Assert.IsFalse(NewMasker().ContainsRedactPlaceholder(nameof(SimpleEvent), json));
    }

    [TestMethod]
    public void ContainsRedactPlaceholder_Ignores_Three_Stars_In_Non_Sensitive_Field()
    {
        // Operator legitimately puts "***" in a non-sensitive field — must not trigger.
        var json = JsonConvert.SerializeObject(new SimpleEvent { Cpr = "real", EmployeeNumber = "***" });

        Assert.IsFalse(NewMasker().ContainsRedactPlaceholder(nameof(SimpleEvent), json));
    }

    [TestMethod]
    public void ContainsRedactPlaceholder_Detects_Masked_Field_In_Class_Level_Sensitive_Cascade()
    {
        var json = JsonConvert.SerializeObject(new NestedEvent
        {
            EmployeeNumber = "E1",
            Address = new PrivateAddress { Street = "Vej 1", City = "Aarhus", Zip = "8000" }
        });
        var masked = NewMasker().Mask(nameof(NestedEvent), json);

        Assert.IsTrue(NewMasker().ContainsRedactPlaceholder(nameof(NestedEvent), masked));
    }

    // -------- Sidecar marker --------

    [TestMethod]
    public void Mask_Adds_PiiMasked_Marker_When_Field_Is_Sensitive()
    {
        var json = JsonConvert.SerializeObject(new SimpleEvent { Cpr = "1234567890", EmployeeNumber = "E1" });

        var masked = NewMasker().Mask(nameof(SimpleEvent), json);
        var parsed = JObject.Parse(masked);

        Assert.AreEqual(JTokenType.Boolean, parsed[EventJsonMasker.PiiMaskedMarker]!.Type);
        Assert.IsTrue((bool)parsed[EventJsonMasker.PiiMaskedMarker]!);
    }

    public class NoSensitiveEvent : Event
    {
        public string PublicField { get; set; } = null!;
    }

    private class NoSensitiveEndpoint : Endpoint
    {
        public NoSensitiveEndpoint() { Produces<NoSensitiveEvent>(); }
    }

    private class NoSensitivePlatform : Platform
    {
        public NoSensitivePlatform() { AddEndpoint(new NoSensitiveEndpoint()); }
    }

    [TestMethod]
    public void Mask_Does_Not_Add_Marker_When_No_Sensitive_Fields_Touched()
    {
        var masker = new EventJsonMasker(new NoSensitivePlatform());
        var json = JsonConvert.SerializeObject(new NoSensitiveEvent { PublicField = "ok" });

        var masked = masker.Mask(nameof(NoSensitiveEvent), json);
        var parsed = JObject.Parse(masked);

        Assert.IsNull(parsed[EventJsonMasker.PiiMaskedMarker]);
    }

    [TestMethod]
    public void ContainsRedactPlaceholder_Detects_PartialReveal_Output_Via_Marker()
    {
        // Without the marker, ****5678 would slip past the per-field "***" check.
        var json = JsonConvert.SerializeObject(new PartialEvent { Phone = "12345678" });
        var masked = NewMasker().Mask(nameof(PartialEvent), json);

        Assert.IsTrue(NewMasker().ContainsRedactPlaceholder(nameof(PartialEvent), masked));
    }

    [TestMethod]
    public void ContainsRedactPlaceholder_Detects_Hash_Output_Via_Marker()
    {
        // Hash output is 64 hex chars — without the marker, no per-field signal exists.
        var json = JsonConvert.SerializeObject(new HashEvent { Email = "a@b.dk" });
        var masked = NewMasker("salt").Mask(nameof(HashEvent), json);

        Assert.IsTrue(NewMasker("salt").ContainsRedactPlaceholder(nameof(HashEvent), masked));
    }

    [TestMethod]
    public void ContainsRedactPlaceholder_Detects_Redact_When_Marker_Stripped()
    {
        // Defense in depth: even if a client strips the sidecar marker, "***" left in a
        // sensitive Redact-mode field is still caught.
        var json = JsonConvert.SerializeObject(new SimpleEvent { Cpr = "1234567890", EmployeeNumber = "E1" });
        var masked = NewMasker().Mask(nameof(SimpleEvent), json);
        var stripped = NewMasker().StripMaskedMarker(masked);

        Assert.IsFalse(stripped.Contains(EventJsonMasker.PiiMaskedMarker, StringComparison.Ordinal));
        Assert.IsTrue(NewMasker().ContainsRedactPlaceholder(nameof(SimpleEvent), stripped));
    }

    [TestMethod]
    public void StripMaskedMarker_Removes_Sidecar_Key()
    {
        var json = JsonConvert.SerializeObject(new SimpleEvent { Cpr = "1234567890", EmployeeNumber = "E1" });
        var masked = NewMasker().Mask(nameof(SimpleEvent), json);
        StringAssert.Contains(masked, EventJsonMasker.PiiMaskedMarker);

        var stripped = NewMasker().StripMaskedMarker(masked);

        Assert.IsFalse(stripped.Contains(EventJsonMasker.PiiMaskedMarker, StringComparison.Ordinal));
        // Other fields are untouched.
        var parsed = JObject.Parse(stripped);
        Assert.AreEqual("***", (string?)parsed["Cpr"]);
        Assert.AreEqual("E1", (string?)parsed["EmployeeNumber"]);
    }

    [TestMethod]
    public void StripMaskedMarker_Is_Noop_When_Marker_Absent()
    {
        var json = "{\"Cpr\":\"plain\",\"EmployeeNumber\":\"E1\"}";

        var stripped = NewMasker().StripMaskedMarker(json);

        Assert.AreEqual(json, stripped);
    }

    // -------- Camel-case contract resolver --------

    [TestMethod]
    public void Mask_Works_With_CamelCase_Contract_Resolver()
    {
        var settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };
        var json = JsonConvert.SerializeObject(
            new SimpleEvent { Cpr = "1234567890", EmployeeNumber = "E1" },
            settings);
        StringAssert.Contains(json, "\"cpr\"");

        var masked = NewMasker().Mask(nameof(SimpleEvent), json);
        var parsed = JObject.Parse(masked);

        Assert.AreEqual("***", (string?)parsed["cpr"]);
        Assert.AreEqual("E1", (string?)parsed["employeeNumber"]);
    }

    // -------- PartialReveal validation --------

    public class BadPartialEvent : Event
    {
        // Reveal defaults to 0 — this would silently degrade to Redact at runtime.
        // The masker constructor should reject this at startup.
        [Sensitive(Mode = MaskMode.PartialReveal)]
        public string Phone { get; set; } = null!;
    }

    private class BadPartialEndpoint : Endpoint
    {
        public BadPartialEndpoint() { Produces<BadPartialEvent>(); }
    }

    private class BadPartialPlatform : Platform
    {
        public BadPartialPlatform() { AddEndpoint(new BadPartialEndpoint()); }
    }

    [TestMethod]
    public void Constructor_Throws_When_PartialReveal_Has_Reveal_Zero()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => new EventJsonMasker(new BadPartialPlatform()));

        StringAssert.Contains(ex.Message, "PartialReveal");
        StringAssert.Contains(ex.Message, nameof(BadPartialEvent.Phone));
    }

    public class NegativeRevealEvent : Event
    {
        [Sensitive(Mode = MaskMode.PartialReveal, Reveal = -2)]
        public string? Phone { get; set; }
    }

    private sealed class NegativeRevealEndpoint : Endpoint
    {
        public NegativeRevealEndpoint() { Produces<NegativeRevealEvent>(); }
    }

    private sealed class NegativeRevealPlatform : Platform
    {
        public NegativeRevealPlatform() { AddEndpoint(new NegativeRevealEndpoint()); }
    }

    [TestMethod]
    public void Constructor_Error_Formats_Reveal_Independently_Of_The_Host_Culture()
    {
        // sv-SE formats a negative int with U+2212 (minus sign) instead of an ASCII hyphen.
        var hostCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        try
        {
            var ex = Assert.ThrowsExactly<InvalidOperationException>(
                () => new EventJsonMasker(new NegativeRevealPlatform()));

            StringAssert.Contains(ex.Message, "Reveal=-2.", StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = hostCulture;
        }
    }

    // -------- TryCollectSensitiveValues (diagnostic scrubbing) --------

    [TestMethod]
    public void CollectSensitiveValues_Returns_Only_Flagged_Leaf_Values()
    {
        var json = JsonConvert.SerializeObject(new SimpleEvent { Cpr = "1234567890", EmployeeNumber = "E1" });

        Assert.IsTrue(NewMasker().TryCollectSensitiveValues(nameof(SimpleEvent), json, out var values));

        CollectionAssert.Contains(values.ToList(), "1234567890");
        CollectionAssert.DoesNotContain(values.ToList(), "E1");
    }

    [TestMethod]
    public void CollectSensitiveValues_Walks_Class_Level_Cascade_And_Collections()
    {
        var nestedJson = JsonConvert.SerializeObject(new NestedEvent
        {
            EmployeeNumber = "E1",
            Address = new PrivateAddress { Street = "Vej 1", City = "Aarhus", Zip = "8000" }
        });
        var collectionJson = JsonConvert.SerializeObject(new CollectionEvent
        {
            Items = new List<CollectionItem>
            {
                new CollectionItem { Secret = "s1", Public = "p1" },
                new CollectionItem { Secret = "s2", Public = "p2" },
            }
        });

        Assert.IsTrue(NewMasker().TryCollectSensitiveValues(nameof(NestedEvent), nestedJson, out var nestedValues));
        Assert.IsTrue(NewMasker().TryCollectSensitiveValues(nameof(CollectionEvent), collectionJson, out var collectionValues));

        var nested = nestedValues.ToList();
        var collection = collectionValues.ToList();

        CollectionAssert.Contains(nested, "Vej 1");
        CollectionAssert.Contains(nested, "Aarhus");
        CollectionAssert.Contains(nested, "8000");
        CollectionAssert.DoesNotContain(nested, "E1");
        CollectionAssert.Contains(collection, "s1");
        CollectionAssert.Contains(collection, "s2");
        CollectionAssert.DoesNotContain(collection, "p1");
    }

    [TestMethod]
    public void CollectSensitiveValues_Recognizes_JsonProperty_Rename()
    {
        var json = JsonConvert.SerializeObject(new JsonRenamedEvent { SocialSecurityNumber = "secret" });

        Assert.IsTrue(NewMasker().TryCollectSensitiveValues(nameof(JsonRenamedEvent), json, out var values));

        CollectionAssert.Contains(values.ToList(), "secret");
    }

    [TestMethod]
    public void CollectSensitiveValues_Fails_Closed_On_Unknown_Type()
    {
        Assert.IsFalse(NewMasker().TryCollectSensitiveValues("UnknownType", "{\"x\":\"1\"}", out var values));
        Assert.IsNull(values);
    }

    [TestMethod]
    public void CollectSensitiveValues_Fails_Closed_On_Malformed_Json()
    {
        Assert.IsFalse(NewMasker().TryCollectSensitiveValues(nameof(SimpleEvent), "{not json", out var values));
        Assert.IsNull(values);
    }

    [TestMethod]
    public void CollectSensitiveValues_Empty_Payload_Yields_Empty_Set()
    {
        var masker = NewMasker();

        Assert.IsTrue(masker.TryCollectSensitiveValues(nameof(SimpleEvent), null!, out var nullValues));
        Assert.IsTrue(masker.TryCollectSensitiveValues(nameof(SimpleEvent), "", out var emptyValues));

        Assert.AreEqual(0, nullValues.Count);
        Assert.AreEqual(0, emptyValues.Count);
    }
}
