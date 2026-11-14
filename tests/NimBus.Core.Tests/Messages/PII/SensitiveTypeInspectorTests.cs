#pragma warning disable CA1707, CA2007

using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;

namespace NimBus.Core.Tests.Messages.PII;

[TestClass]
public class SensitiveTypeInspectorTests
{
    // -------- Test fixtures --------

    public class PlainEvent : Event
    {
        public string EmployeeNumber { get; set; } = null!;
    }

    public class DirectSensitiveEvent : Event
    {
        [Sensitive]
        public string Cpr { get; set; } = null!;
    }

    [Sensitive]
    public class ClassLevelSensitiveEvent : Event
    {
        public string Anything { get; set; } = null!;
    }

    [Sensitive]
    public class PrivateAddress
    {
        public string Street { get; set; } = null!;
    }

    public class NestedSensitiveEvent : Event
    {
        public string EmployeeNumber { get; set; } = null!;
        public PrivateAddress Address { get; set; } = null!;
    }

    public class CollectionItem
    {
        [Sensitive]
        public string Secret { get; set; } = null!;
    }

    public class CollectionSensitiveEvent : Event
    {
        public List<CollectionItem> Items { get; set; } = null!;
    }

    public class PlainNested
    {
        public string Street { get; set; } = null!;
    }

    public class PlainNestedEvent : Event
    {
        public PlainNested Address { get; set; } = null!;
        public List<PlainNested> History { get; set; } = null!;
    }

    public class SelfReferencing
    {
        public SelfReferencing Parent { get; set; } = null!;
        public string Name { get; set; } = null!;
    }

    public class CyclicPlainEvent : Event
    {
        public SelfReferencing Node { get; set; } = null!;
    }

    // -------- Tests --------

    [TestMethod]
    public void Null_Type_Is_Not_Sensitive()
    {
        Assert.IsFalse(SensitiveTypeInspector.ContainsSensitiveData(null!));
    }

    [TestMethod]
    public void Plain_Event_Is_Not_Sensitive()
    {
        Assert.IsFalse(SensitiveTypeInspector.ContainsSensitiveData(typeof(PlainEvent)));
    }

    [TestMethod]
    public void Property_Level_Attribute_Is_Detected()
    {
        Assert.IsTrue(SensitiveTypeInspector.ContainsSensitiveData(typeof(DirectSensitiveEvent)));
    }

    [TestMethod]
    public void Class_Level_Attribute_Is_Detected()
    {
        Assert.IsTrue(SensitiveTypeInspector.ContainsSensitiveData(typeof(ClassLevelSensitiveEvent)));
    }

    [TestMethod]
    public void Sensitive_Nested_Object_Is_Detected()
    {
        Assert.IsTrue(SensitiveTypeInspector.ContainsSensitiveData(typeof(NestedSensitiveEvent)));
    }

    [TestMethod]
    public void Sensitive_Collection_Element_Is_Detected()
    {
        Assert.IsTrue(SensitiveTypeInspector.ContainsSensitiveData(typeof(CollectionSensitiveEvent)));
    }

    [TestMethod]
    public void Plain_Nested_Types_Are_Not_Sensitive()
    {
        Assert.IsFalse(SensitiveTypeInspector.ContainsSensitiveData(typeof(PlainNestedEvent)));
    }

    [TestMethod]
    public void Self_Referencing_Types_Do_Not_Loop()
    {
        Assert.IsFalse(SensitiveTypeInspector.ContainsSensitiveData(typeof(CyclicPlainEvent)));
    }
}
