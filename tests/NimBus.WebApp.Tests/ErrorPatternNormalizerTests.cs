#pragma warning disable CA1707, CA2007

using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

[TestClass]
public class ErrorPatternNormalizerTests
{
    [TestMethod]
    public void Normalize_GroupsDimensionValueErrorsWithDifferentValues()
    {
        var first = ErrorPatternNormalizer.Normalize(
            "Mandatory fields must be filled in to proceed. The dimension value 55890030 does not exist for dimension Afdeling.");
        var second = ErrorPatternNormalizer.Normalize(
            "Mandatory fields must be filled in to proceed. The dimension value 60000038 does not exist for dimension Afdeling.");
        var third = ErrorPatternNormalizer.Normalize(
            "Mandatory fields must be filled in to proceed. The dimension value 302 does not exist for dimension Afdeling.");

        Assert.AreEqual(first, second);
        Assert.AreEqual(first, third);
        Assert.AreEqual(
            "Mandatory fields must be filled in to proceed. The dimension value <value> does not exist for dimension Afdeling",
            first);
    }

    [TestMethod]
    public void ExtractCategory_UsesNormalizedFallbackForErrorsWithoutShortCategory()
    {
        var first = ErrorPatternNormalizer.ExtractCategory(
            "Mandatory fields must be filled in to proceed. The dimension value 55890030 does not exist for dimension Afdeling.");
        var second = ErrorPatternNormalizer.ExtractCategory(
            "Mandatory fields must be filled in to proceed. The dimension value 60000038 does not exist for dimension Afdeling.");

        Assert.AreEqual(first, second);
        StringAssert.Contains(first, "dimension value <value>", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ExtractCategory_NormalizesEarlyColonCategory()
    {
        var first = ErrorPatternNormalizer.ExtractCategory(
            "Order 'ABC123' rejected: Mandatory fields must be filled in to proceed.");
        var second = ErrorPatternNormalizer.ExtractCategory(
            "Order 'XYZ789' rejected: Mandatory fields must be filled in to proceed.");

        Assert.AreEqual("Order '<value>' rejected", first);
        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public void ExtractCategory_NormalizesTimestampPrefixBeforeEarlyColon()
    {
        var first = ErrorPatternNormalizer.ExtractCategory(
            "2025-06-14T21:33:43: dimension value 55890030 does not exist");
        var second = ErrorPatternNormalizer.ExtractCategory(
            "2026-01-02T03:04:05: dimension value 60000038 does not exist");

        Assert.AreEqual("dimension value <value> does not exist", first);
        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public void Normalize_GroupsGuidAndJobIdErrors()
    {
        Assert.AreEqual(
            "Could not find functional location <id>",
            ErrorPatternNormalizer.Normalize("Could not find functional location e19aee84-6a4a-4bf4-b75a-b0d8d0a825a9"));

        Assert.AreEqual(
            "Job with JobID [<id>] not found",
            ErrorPatternNormalizer.Normalize("Job with JobID [06E53DA9-0FA8-495B-8DE3-9187FF5F83BF] not found"));
    }

    [TestMethod]
    public void Normalize_GroupsErrorsThatDifferOnlyByTrailingUnquotedValue()
    {
        const string prefix =
            "[CRM API ERROR] Failed to update Contact 'a4b1c2d3-1111-2222-3333-444455556666': A contact with the same email address already exists. Please use a different email or update the existing contact: ";

        var first = ErrorPatternNormalizer.Normalize(prefix + "james.burton");
        var second = ErrorPatternNormalizer.Normalize(prefix + "Isabel Gsaller");
        var third = ErrorPatternNormalizer.Normalize(prefix + "support");

        Assert.AreEqual(first, second);
        Assert.AreEqual(first, third);
        Assert.AreEqual(
            "[CRM API ERROR] Failed to update Contact '<id>': A contact with the same email address already exists. Please use a different email or update the existing contact: <value>",
            first);
    }

    [TestMethod]
    public void Normalize_KeepsReasonAfterSingleCategoryColon()
    {
        Assert.AreEqual("Order rejected: timeout", ErrorPatternNormalizer.Normalize("Order rejected: timeout"));
        Assert.AreEqual(
            "Order rejected: invalid credentials",
            ErrorPatternNormalizer.Normalize("Order rejected: invalid credentials"));
    }

    [TestMethod]
    public void Normalize_StripsActionSuffix()
    {
        Assert.AreEqual(
            "[TRANSIENT ERROR] Failed to handle AliceSaidHello '<value>': the downstream system was momentarily unavailable",
            ErrorPatternNormalizer.Normalize(
                "[TRANSIENT ERROR] Failed to handle AliceSaidHello 'hello-0': the downstream system was momentarily unavailable. Action: Wait a few minutes and resubmit"));
    }
}
