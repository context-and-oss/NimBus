#pragma warning disable CA1707, CA2007
extern alias AppHost;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using DemoBuildVersion = AppHost::CrmErpDemo.AppHost.DemoBuildVersion;

namespace CrmErpDemo.AppHost.Tests;

// Regression: the deployed demo's nimbus-ops footer read "NIMBUS 0.0.0". The repo keeps a
// 0.0.0 placeholder version and `aspire deploy` passed no version to the images it built.
[TestClass]
[DoNotParallelize]
public sealed class DemoBuildVersionTests
{
    [TestMethod]
    [DataRow("v4.13.0", "4.13.0")]
    [DataRow("V4.13.0\n", "4.13.0")]
    [DataRow("4.13.0", "4.13.0")]
    [DataRow("v4.13.0-1-g355cc2f2", "4.13.0-1-g355cc2f2")]
    [DataRow("v5.0.0-rc.1", "5.0.0-rc.1")]
    public void TryNormalize_TurnsGitDescribeOutputIntoAVersion(string describe, string expected)
    {
        Assert.IsTrue(DemoBuildVersion.TryNormalize(describe, out var version));
        Assert.AreEqual(expected, version);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    [DataRow("nightly")]
    [DataRow("v4")]
    [DataRow("v4.13")]
    [DataRow("v4.13.0-bad tag")]
    [DataRow("v4.13.0-1;rm")]
    public void TryNormalize_RejectsWhatIsNotAVersion(string? describe)
    {
        Assert.IsFalse(DemoBuildVersion.TryNormalize(describe, out _));
    }

    [TestMethod]
    public void Apply_KeepsAVersionTheOperatorSet()
    {
        var before = Environment.GetEnvironmentVariable(DemoBuildVersion.VariableName);
        try
        {
            Environment.SetEnvironmentVariable(DemoBuildVersion.VariableName, "9.9.9");

            var applied = DemoBuildVersion.Apply(AppContext.BaseDirectory);

            Assert.AreEqual("9.9.9", applied);
            Assert.AreEqual("9.9.9", Environment.GetEnvironmentVariable(DemoBuildVersion.VariableName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DemoBuildVersion.VariableName, before);
        }
    }

    [TestMethod]
    public void Apply_OutsideAGitCheckout_LeavesTheBuildUnversioned()
    {
        var before = Environment.GetEnvironmentVariable(DemoBuildVersion.VariableName);
        var directory = Directory.CreateTempSubdirectory("nimbus-no-git-");
        try
        {
            Environment.SetEnvironmentVariable(DemoBuildVersion.VariableName, null);

            // A drive root's temp folder is not inside a repository, so `git describe` fails.
            var applied = DemoBuildVersion.Apply(directory.FullName);

            Assert.IsNull(applied);
            Assert.IsNull(Environment.GetEnvironmentVariable(DemoBuildVersion.VariableName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DemoBuildVersion.VariableName, before);
            directory.Delete(recursive: true);
        }
    }
}
