using Gac.Shared;
using Gac.Shared.ClassMode;
using Gac.Shared.Pipe;

namespace Gac.Shared.Tests;

public class PresetValidatorTests
{
    private static ClassPreset P(params PresetAction[] actions) => new() { Name = "수업 시작", Actions = [.. actions] };

    [Fact]
    public void Valid_teacher_preset_passes()
    {
        var p = P(new() { Type = "set_volume", Value = 40 }, new() { Type = "display_mode", Mode = "clone" },
            new() { Type = "open_url", Url = "https://school.example/class" }, new() { Type = "wait", Seconds = 5 },
            new() { Type = "launch_app", Path = @"C:\Program Files\App\app.exe" }, new() { Type = "close_app", ProcessName = "chrome" },
            new() { Type = "set_resolution", Width = 1920, Height = 1080 }, new() { Type = "mute" });
        Assert.True(PresetValidator.Validate(p, allowRunCommand: false).IsValid);
    }

    [Theory]
    [InlineData("set_volume", 101)]
    [InlineData("set_volume", -1)]
    [InlineData("wait", 31)]
    [InlineData("wait", 0)]
    public void Out_of_range_values_fail(string type, int value)
    {
        var a = new PresetAction { Type = type, Value = type == "set_volume" ? value : null, Seconds = type == "wait" ? value : null };
        var r = PresetValidator.Validate(P(a), false);
        Assert.False(r.IsValid);
        Assert.All(r.Errors, e => Assert.Matches("[가-힣]", e)); // Korean messages
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://x")]
    public void Only_http_urls_allowed(string url) =>
        Assert.False(PresetValidator.Validate(P(new PresetAction { Type = "open_url", Url = url }), false).IsValid);

    [Fact]
    public void Run_command_rejected_for_teachers_allowed_for_signed_shared()
    {
        var p = P(new PresetAction { Type = "run_command", Command = "echo hi" });
        Assert.False(PresetValidator.Validate(p, allowRunCommand: false).IsValid);
        Assert.True(PresetValidator.Validate(p, allowRunCommand: true).IsValid);
    }

    [Fact]
    public void Unknown_action_and_relative_app_path_fail()
    {
        Assert.False(PresetValidator.Validate(P(new PresetAction { Type = "format_disk" }), true).IsValid);
        Assert.False(PresetValidator.Validate(P(new PresetAction { Type = "launch_app", Path = "app.exe" }), false).IsValid);
        Assert.False(PresetValidator.Validate(P(new PresetAction { Type = "launch_app", Path = @"C:\x\script.bat" }), false).IsValid);
    }

    [Fact]
    public void Parse_rejects_invalid_json_with_korean_message()
    {
        var (file, r) = PresetValidator.Parse("{not json", false);
        Assert.Null(file);
        Assert.Contains("형식", r.ErrorText);
    }

    [Fact]
    public void Parse_round_trips()
    {
        var f = new PresetFile { Presets = [P(new PresetAction { Type = "unmute" })] };
        var (file, r) = PresetValidator.Parse(GacJson.Serialize(f), false);
        Assert.True(r.IsValid);
        Assert.Equal("unmute", file!.Presets[0].Actions[0].Type);
    }
}

public class PipeFramingTests
{
    [Fact]
    public async Task Frames_round_trip()
    {
        using var ms = new MemoryStream();
        var m = PipeMessage.Request(TrayRequest.NetRepair, new NetRepairBody(NetRepairAction.FlushDns));
        await PipeFraming.WriteAsync(ms, m, default);
        ms.Position = 0;
        var back = await PipeFraming.ReadAsync(ms, default);
        Assert.Equal("NetRepair", back!.Type);
        Assert.Equal(NetRepairAction.FlushDns, back.Body<NetRepairBody>()!.Action);
    }

    [Fact]
    public async Task Oversized_frame_is_rejected()
    {
        using var ms = new MemoryStream(BitConverter.GetBytes(PipeFraming.MaxFrameBytes + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeFraming.ReadAsync(ms, default));
    }
}
