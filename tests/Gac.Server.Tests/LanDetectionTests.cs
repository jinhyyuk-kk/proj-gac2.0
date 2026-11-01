using System.Net;
using Gac.Server.Hosting;

namespace Gac.Server.Tests;

public class LanDetectionTests
{
    [Theory]
    [InlineData("10.20.3.7", 22, "10.20.0.0/22")]
    [InlineData("192.168.1.50", 24, "192.168.1.0/24")]
    [InlineData("172.16.200.9", 16, "172.16.0.0/16")]
    [InlineData("10.0.0.1", 8, "10.0.0.0/8")]
    public void ToCidr_computes_network_address(string ip, int prefix, string expected) =>
        Assert.Equal(expected, LanDetection.ToCidr(IPAddress.Parse(ip), prefix));

    [Theory]
    [InlineData("10.20.0.0/22", true)]
    [InlineData("192.168.0.0/24", true)]
    [InlineData("10.0.0.0/33", false)]
    [InlineData("notanip/24", false)]
    [InlineData("10.0.0.0", false)]
    [InlineData("", false)]
    public void IsValidCidr(string cidr, bool expected) => Assert.Equal(expected, LanDetection.IsValidCidr(cidr));
}
