#pragma warning disable CA1707, CA2007

using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages.PII;

namespace NimBus.Core.Tests.Messages.PII;

[TestClass]
public class SensitiveAttributeTests
{
    private class Decorated
    {
        [Sensitive]
        public string? Default { get; set; }

        [Sensitive(Mode = MaskMode.PartialReveal, Reveal = 4)]
        public string? Partial { get; set; }

        [Sensitive(Mode = MaskMode.Hash)]
        public string? Hashed { get; set; }

        public string? NotMarked { get; set; }
    }

    [Sensitive]
    private class FullyMarked
    {
        public string? A { get; set; }
        public string? B { get; set; }
    }

    [TestMethod]
    public void Defaults_Mode_Redact_Reveal_Zero()
    {
        var attr = typeof(Decorated).GetProperty(nameof(Decorated.Default))!
            .GetCustomAttribute<SensitiveAttribute>();

        Assert.AreEqual(MaskMode.Redact, attr!.Mode);
        Assert.AreEqual(0, attr.Reveal);
    }

    [TestMethod]
    public void NamedArgs_PartialReveal_Carries_Reveal()
    {
        var attr = typeof(Decorated).GetProperty(nameof(Decorated.Partial))!
            .GetCustomAttribute<SensitiveAttribute>();

        Assert.AreEqual(MaskMode.PartialReveal, attr!.Mode);
        Assert.AreEqual(4, attr.Reveal);
    }

    [TestMethod]
    public void Unmarked_Property_Returns_Null_Attribute()
    {
        var attr = typeof(Decorated).GetProperty(nameof(Decorated.NotMarked))!
            .GetCustomAttribute<SensitiveAttribute>();

        Assert.IsNull(attr);
    }

    [TestMethod]
    public void Class_Level_Attribute_Discoverable()
    {
        var attr = typeof(FullyMarked).GetCustomAttribute<SensitiveAttribute>();

        Assert.IsNotNull(attr);
        Assert.AreEqual(MaskMode.Redact, attr.Mode);
    }
}
