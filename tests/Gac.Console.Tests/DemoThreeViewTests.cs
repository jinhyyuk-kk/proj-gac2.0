using Gac.Demo;
using Gac.Console.Services;
using Gac.Shared.Auth;
using Gac.Shared.Contracts;

namespace Gac.Console.Tests;

/// <summary>
/// CHANGE-001 §7: the demo console view starts at the sign-up screen, the first sign-up becomes the Owner and then
/// sign-up closes, and the 차단된 기기 screen has sample blocked laptops / console-first machines. None of this touches
/// the real machine (covered separately by <see cref="DemoSafetyTests"/>'s reference-closure checks).
/// </summary>
public class DemoThreeViewTests
{
    [Fact]
    public async Task Console_view_starts_needing_an_owner_then_sign_up_closes()
    {
        using var c = new DemoServerClient(delayScale: 0);
        Assert.True((await c.GetSetupAsync()).NeedsOwner);
        Assert.False(c.OwnerSignedUp);

        // Login is refused until the first sign-up (the demo client throws synchronously).
        Assert.Throws<GacClientException>(() => { _ = c.LoginAsync("owner", "whatever12"); });

        var owner = await c.CreateOwnerAsync(new CreateOwnerRequest("principal", "교장", "GoodPass1", "DEMO-2026-GAC0"));
        Assert.Equal(Role.Owner, owner.Role);
        Assert.Equal("principal", owner.UserName);
        Assert.Equal("교장", owner.DisplayName);

        Assert.True(c.OwnerSignedUp);
        Assert.False((await c.GetSetupAsync()).NeedsOwner); // 회원가입 button disappears
        var login = await c.LoginAsync("principal", "GoodPass1");
        Assert.Equal("principal", login.Account.UserName);
    }

    [Fact]
    public async Task Second_sign_up_is_refused()
    {
        using var c = new DemoServerClient(delayScale: 0);
        await c.CreateOwnerAsync(new CreateOwnerRequest("principal", "교장", "GoodPass1", "code"));
        await Assert.ThrowsAsync<GacClientException>(() =>
            c.CreateOwnerAsync(new CreateOwnerRequest("another", "다른이", "GoodPass2", "code")));
    }

    [Theory]
    [InlineData("ab", "GoodPass1", "code")]      // username too short
    [InlineData("principal", "short1", "code")]  // password too short
    [InlineData("principal", "nodigitspass", "code")] // no digit
    [InlineData("principal", "GoodPass1", "")]   // missing setup code
    public async Task Sign_up_validates_input(string user, string pw, string code)
    {
        using var c = new DemoServerClient(delayScale: 0);
        await Assert.ThrowsAsync<GacClientException>(() =>
            c.CreateOwnerAsync(new CreateOwnerRequest(user, "이름", pw, code)));
        Assert.False(c.OwnerSignedUp);
    }

    [Fact]
    public async Task Blocked_devices_list_has_sample_laptops_and_a_console_first_machine()
    {
        using var c = new DemoServerClient(delayScale: 0);
        var blocked = await c.GetBlockedDevicesAsync();
        Assert.Contains(blocked, d => d.Reason.Contains("노트북에는 설치할 수 없습니다"));
        Assert.Contains(blocked, d => d.PermanentlyBlocked && d.Reason.Contains("관리자 프로그램이 설치된 PC"));
        Assert.Contains(await c.GetPendingDevicesAsync(), d => d.Reason.Contains("확인이 필요"));
    }
}
