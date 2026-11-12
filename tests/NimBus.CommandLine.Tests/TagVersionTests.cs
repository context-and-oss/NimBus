#pragma warning disable CA1707, CA2007
using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// Deploy-time version stamping: release tags normalize to MSBuild Version
/// values so the WebApp footer / platformVersion reports the tag instead of
/// the 0.0.0 placeholder.
/// </summary>
public class TagVersionTests
{
    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("V1.2.0", "1.2.0")]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData(" v1.2.0 \n", "1.2.0")]
    [InlineData("v1.2.0-rc.1", "1.2.0-rc.1")]
    [InlineData("v10.20.30.40", "10.20.30.40")]
    public void Valid_tags_normalize(string tag, string expected)
    {
        Assert.True(SourceBuildArtifactSource.TryNormalizeTagVersion(tag, out var version));
        Assert.Equal(expected, version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("v1")]
    [InlineData("release-2026-07")]
    [InlineData("v1.x")]
    public void Invalid_tags_are_rejected(string? tag)
    {
        Assert.False(SourceBuildArtifactSource.TryNormalizeTagVersion(tag, out _));
    }
}
