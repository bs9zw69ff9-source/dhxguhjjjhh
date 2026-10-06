using Microsoft.Extensions.Configuration;
using PavlovBot.Host.Configuration;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>MASTER_NAMES: a space separates two names, as a comma does.</summary>
public class MasterNamesConfigTests
{
    private static FeatureOptions Bind(string value) =>
        FeatureOptions.Bind(new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("MASTER_NAMES", value)])
            .Build());

    [Theory]
    [InlineData("UserOne UserTwo")]
    [InlineData("UserOne,UserTwo")]
    [InlineData(" UserOne ,  UserTwo ")]
    [InlineData("UserOne\tUserTwo,userone")]
    public void SpacesAndCommasBothSeparateNames(string value)
    {
        // "UserOne UserTwo" read as one name granted a player who does not exist.
        Assert.Equal(["UserOne", "UserTwo"], Bind(value).MasterNames);
    }

    [Fact]
    public void TheReportedLineIsTwoNames()
    {
        Assert.Equal(["fki6", "Holosight1"], Bind("fki6 Holosight1").MasterNames);
    }

    [Fact]
    public void BlankIsNoMasters()
    {
        Assert.Empty(Bind("").MasterNames);
    }
}
