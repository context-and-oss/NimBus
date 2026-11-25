#pragma warning disable CA1707, CA2007

namespace NimBus.Adapter.Mcp.Tests;

[TestClass]
public sealed class AdapterVersionTests
{
    [TestMethod]
    [DataRow("4.18.0+3f2a9c1d0b", "3f2a9c1d0b")]
    [DataRow("0.0.0+abc+def", "abc+def")]
    [DataRow("4.18.0", "4.18.0")]
    [DataRow("4.18.0+", "4.18.0+")]
    [DataRow(null, null)]
    [DataRow("  ", null)]
    public void The_version_is_the_commit_after_the_plus_else_the_whole_version(string? informational, string? expected)
    {
        Assert.AreEqual(expected, AdapterVersion.FromInformationalVersion(informational));
    }

    [TestMethod]
    public void No_entry_assembly_has_no_version()
    {
        Assert.IsNull(AdapterVersion.FromAssembly(null));
    }
}
