using SteamPluginManager.Services.Steam;

namespace SteamPluginManager.Tests;

public class SteamtoolsVersionHelperTests
{
    [Theory]
    [InlineData("v1.2.3.0", "1.2.3", false)]
    [InlineData("1.2.9", "v1.2.10", true)]
    [InlineData("1.2.10-beta", "1.2.10", false)]
    [InlineData("", "1.2.10", false)]
    public void IsLess_ComparesNormalizedVersionComponents(
        string installedVersion,
        string latestVersion,
        bool expected)
    {
        Assert.Equal(expected, SteamtoolsVersionHelper.IsLess(installedVersion, latestVersion));
    }
}
