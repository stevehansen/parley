using System.Net;
using Parley.Cli;
using Parley.Sharing;

namespace Parley.Tests;

public class DeviceRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parley-tests", Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_dir, "sharing.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Sharing_StartsWithAPendingCode_AndEndsWithTheLastDevice()
    {
        using var devices = new DeviceRegistry();
        var changes = 0;
        devices.SharingChanged += () => changes++;
        devices.IsSharing.ShouldBeFalse();

        var code = devices.CreatePairingCode("laptop");
        devices.IsSharing.ShouldBeTrue();
        devices.List().Single().CodeExpiresAt.ShouldNotBeNull();

        devices.Redeem(code).ShouldNotBeNull();
        devices.IsSharing.ShouldBeTrue();
        devices.Remove("laptop").ShouldBeTrue();
        devices.IsSharing.ShouldBeFalse();
        changes.ShouldBe(3);
    }

    [Fact]
    public void Redeem_GivesATokenThatAuthenticates_AndIsSingleUse()
    {
        using var devices = new DeviceRegistry();
        var code = devices.CreatePairingCode("Laptop");
        code.ShouldMatch("^[A-Z2-9]{3}-[A-Z2-9]{3}-[A-Z2-9]{3}$");

        var (device, token) = devices.Redeem(code.ToLowerInvariant().Replace("-", " ")).ShouldNotBeNull();
        device.Name.ShouldBe("laptop");
        token.ShouldStartWith("prly_");
        devices.Authenticate(token).ShouldBeSameAs(device);
        devices.Authenticate("prly_wrong").ShouldBeNull();
        devices.Authenticate(null).ShouldBeNull();
        devices.Redeem(code).ShouldBeNull();
    }

    [Fact]
    public void TooManyWrongCodes_DropPendingCodes()
    {
        using var devices = new DeviceRegistry();
        var code = devices.CreatePairingCode("laptop");
        for (var i = 0; i < 10; i++) devices.Redeem("AAA-AAA-AAA").ShouldBeNull();
        devices.Redeem(code).ShouldBeNull();
        devices.IsSharing.ShouldBeFalse();
    }

    [Fact]
    public void PairingAgain_ReplacesTheDevice_AndRevokesItsOldToken()
    {
        using var devices = new DeviceRegistry();
        var (old, oldToken) = devices.Redeem(devices.CreatePairingCode("laptop")).ShouldNotBeNull();
        var (_, newToken) = devices.Redeem(devices.CreatePairingCode("laptop")).ShouldNotBeNull();

        devices.Authenticate(oldToken).ShouldBeNull();
        old.Revoked.IsCancellationRequested.ShouldBeTrue();
        devices.Authenticate(newToken).ShouldNotBeNull();
        devices.List().Count.ShouldBe(1);
    }

    [Fact]
    public void Remove_RevokesTheDevice()
    {
        using var devices = new DeviceRegistry();
        var (device, token) = devices.Redeem(devices.CreatePairingCode("phone")).ShouldNotBeNull();
        devices.Remove("PHONE").ShouldBeTrue();
        device.Revoked.IsCancellationRequested.ShouldBeTrue();
        devices.Authenticate(token).ShouldBeNull();
        devices.Remove("phone").ShouldBeFalse();
    }

    [Fact]
    public void Devices_SurviveARestart_WithoutTheirTokensOnDisk()
    {
        string token;
        using (var devices = new DeviceRegistry(File))
            (_, token) = devices.Redeem(devices.CreatePairingCode("laptop")).ShouldNotBeNull();

        System.IO.File.ReadAllText(File).ShouldNotContain(token);
        using var reloaded = new DeviceRegistry(File);
        reloaded.Authenticate(token)!.Name.ShouldBe("laptop");
        reloaded.IsSharing.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("my laptop")]
    [InlineData("-laptop")]
    [InlineData("a-name-that-is-far-too-long-to-be-useful")]
    public void BadNames_AreRejected(string name)
    {
        using var devices = new DeviceRegistry();
        Should.Throw<ArgumentException>(() => devices.CreatePairingCode(name));
    }

    [Theory]
    [InlineData("100.67.218.227", true)]  // NetBird
    [InlineData("100.101.102.103", true)] // Tailscale
    [InlineData("192.168.1.10", false)]
    [InlineData("8.8.8.8", false)]
    public void DefaultNetworks_AreTheOverlayRange(string address, bool allowed)
    {
        using var devices = new DeviceRegistry();
        devices.IsAllowed(IPAddress.Parse(address)).ShouldBe(allowed);
        devices.IsAllowed(IPAddress.Parse(address).MapToIPv6()).ShouldBe(allowed);
    }

    [Theory]
    [InlineData("steve-beast.netbird.cloud", "http://steve-beast.netbird.cloud:19480")]
    [InlineData("100.67.218.227", "http://100.67.218.227:19480")]
    [InlineData("http://100.67.218.227:19480/", "http://100.67.218.227:19480")]
    [InlineData("https://hub.example.com", "https://hub.example.com")]
    [InlineData("http://hub:8080", "http://hub:8080")]
    [InlineData("ftp://hub", null)]
    public void JoinUrls_AreNormalized(string raw, string? expected) =>
        SharingCommands.NormalizeUrl(raw).ShouldBe(expected);
}
