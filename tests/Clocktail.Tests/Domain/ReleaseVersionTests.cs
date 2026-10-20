using Clocktail.Domain.Platform;
using Xunit;

namespace Clocktail.Tests.Domain;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.1.5", "0.1.4", true)]
    [InlineData("0.1.10", "0.1.9", true)]                       // by number, not by text
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.1.4", "0.1.4+cf6f1aaa0b85af7f", false)]      // the build stamp is not part of the version
    [InlineData("0.1.3", "0.1.4", false)]                       // a newer local build is not behind
    [InlineData("0.0.0-stage", "0.1.4", false)]                 // npm's placeholder while a publish is processed
    [InlineData("0.2.0-beta.1", "0.1.4", false)]                // a pre-release is never offered
    [InlineData(null, "0.1.4", false)]
    [InlineData("0.1.5", "unknown", false)]
    public void Only_a_newer_release_counts(string? latest, string current, bool newer) =>
        Assert.Equal(newer, ReleaseVersion.IsNewer(latest, current));
}
