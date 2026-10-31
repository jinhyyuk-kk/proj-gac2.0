using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gac.Shared.ClassMode;

public enum PresetTrigger { Manual, Login }

/// <summary>Action type names from CLAUDE.md 4.3. Teachers can only pick from <see cref="TeacherAllowed"/>.</summary>
public static class PresetActionTypes
{
    public const string SetVolume = "set_volume";
    public const string Mute = "mute";
    public const string Unmute = "unmute";
    public const string DisplayMode = "display_mode";
    public const string SetResolution = "set_resolution";
    public const string OpenUrl = "open_url";
    public const string LaunchApp = "launch_app";
    public const string CloseApp = "close_app";
    public const string Wait = "wait";
    /// <summary>Only allowed in console-distributed presets signed by the server key.</summary>
    public const string RunCommand = "run_command";

    public static readonly IReadOnlyList<string> TeacherAllowed =
        [SetVolume, Mute, Unmute, DisplayMode, SetResolution, OpenUrl, LaunchApp, CloseApp, Wait];

    public static readonly IReadOnlyList<string> DisplayModes = ["clone", "extend", "internal", "external"];

    public static string KoreanName(string type) => type switch
    {
        SetVolume => "볼륨 설정",
        Mute => "음소거",
        Unmute => "음소거 해제",
        DisplayMode => "화면 모드",
        SetResolution => "해상도 변경",
        OpenUrl => "웹 페이지 열기",
        LaunchApp => "프로그램 실행",
        CloseApp => "프로그램 종료",
        Wait => "대기",
        RunCommand => "명령 실행(관리자 배포)",
        _ => type,
    };

    public static string KoreanDisplayMode(string mode) => mode switch
    {
        "clone" => "복제",
        "extend" => "확장",
        "internal" => "PC 화면만",
        "external" => "두 번째 화면만",
        _ => mode,
    };
}

public sealed class PresetAction
{
    public string Type { get; set; } = "";
    public int? Value { get; set; }
    public string? Mode { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? Url { get; set; }
    public string? Path { get; set; }
    public string? Arguments { get; set; }
    public string? ProcessName { get; set; }
    public int? Seconds { get; set; }
    public string? Command { get; set; }

    public string Describe() => Type switch
    {
        PresetActionTypes.SetVolume => $"볼륨 {Value}%",
        PresetActionTypes.Mute => "음소거",
        PresetActionTypes.Unmute => "음소거 해제",
        PresetActionTypes.DisplayMode => $"화면 모드: {PresetActionTypes.KoreanDisplayMode(Mode ?? "")}",
        PresetActionTypes.SetResolution => $"해상도 {Width}×{Height}",
        PresetActionTypes.OpenUrl => $"웹 페이지 열기: {Url}",
        PresetActionTypes.LaunchApp => $"프로그램 실행: {System.IO.Path.GetFileName(Path ?? "")} {Arguments}".TrimEnd(),
        PresetActionTypes.CloseApp => $"프로그램 종료: {ProcessName}",
        PresetActionTypes.Wait => $"{Seconds}초 대기",
        PresetActionTypes.RunCommand => $"명령 실행: {Command}",
        _ => Type,
    };
}

public sealed class ClassPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public PresetTrigger Trigger { get; set; } = PresetTrigger.Manual;
    public List<PresetAction> Actions { get; set; } = [];

    /// <summary>Set at load time for console-distributed presets; never read from user files.</summary>
    [JsonIgnore] public bool Shared { get; set; }
}

public sealed class PresetFile
{
    public int Version { get; set; } = 1;
    public List<ClassPreset> Presets { get; set; } = [];
}

public sealed record PresetValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public string ErrorText => string.Join(Environment.NewLine, Errors);
}

/// <summary>Validates preset files on load and before saving. All messages are Korean (shown to teachers).</summary>
public static class PresetValidator
{
    public const int MaxPresets = 50;
    public const int MaxActions = 30;
    public const int MaxNameLength = 40;

    public static PresetValidationResult Validate(ClassPreset preset, bool allowRunCommand)
    {
        var errors = new List<string>();
        var name = string.IsNullOrWhiteSpace(preset.Name) ? "(이름 없음)" : preset.Name;
        if (string.IsNullOrWhiteSpace(preset.Name)) errors.Add("프리셋 이름을 입력하세요.");
        else if (preset.Name.Length > MaxNameLength) errors.Add($"프리셋 이름은 {MaxNameLength}자 이하여야 합니다.");
        if (!Enum.IsDefined(preset.Trigger)) errors.Add($"'{name}': 실행 시점 값이 올바르지 않습니다.");
        if (preset.Actions.Count == 0) errors.Add($"'{name}': 동작을 하나 이상 추가하세요.");
        if (preset.Actions.Count > MaxActions) errors.Add($"'{name}': 동작은 최대 {MaxActions}개까지 넣을 수 있습니다.");

        for (var i = 0; i < preset.Actions.Count; i++)
        {
            var a = preset.Actions[i];
            var p = $"'{name}' {i + 1}번 동작";
            switch (a.Type)
            {
                case PresetActionTypes.SetVolume:
                    if (a.Value is not (>= 0 and <= 100)) errors.Add($"{p}: 볼륨은 0~100 사이여야 합니다.");
                    break;
                case PresetActionTypes.Mute:
                case PresetActionTypes.Unmute:
                    break;
                case PresetActionTypes.DisplayMode:
                    if (a.Mode == null || !PresetActionTypes.DisplayModes.Contains(a.Mode)) errors.Add($"{p}: 화면 모드는 복제/확장/PC 화면만/두 번째 화면만 중 하나여야 합니다.");
                    break;
                case PresetActionTypes.SetResolution:
                    if (a.Width is not (>= 640 and <= 7680) || a.Height is not (>= 480 and <= 4320)) errors.Add($"{p}: 해상도 값이 올바르지 않습니다.");
                    break;
                case PresetActionTypes.OpenUrl:
                    if (!IsHttpUrl(a.Url)) errors.Add($"{p}: 웹 주소는 http:// 또는 https:// 로 시작해야 합니다.");
                    break;
                case PresetActionTypes.LaunchApp:
                    if (string.IsNullOrWhiteSpace(a.Path) || !System.IO.Path.IsPathFullyQualified(a.Path)) errors.Add($"{p}: 실행할 프로그램의 전체 경로를 입력하세요.");
                    else if (!a.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) errors.Add($"{p}: 실행 파일(.exe)만 지정할 수 있습니다.");
                    if (a.Arguments is { Length: > 500 }) errors.Add($"{p}: 실행 인수가 너무 깁니다.");
                    break;
                case PresetActionTypes.CloseApp:
                    if (string.IsNullOrWhiteSpace(a.ProcessName) || a.ProcessName.IndexOfAny(['\\', '/', ':']) >= 0) errors.Add($"{p}: 종료할 프로그램 이름을 입력하세요 (예: chrome).");
                    break;
                case PresetActionTypes.Wait:
                    if (a.Seconds is not (>= 1 and <= 30)) errors.Add($"{p}: 대기 시간은 1~30초여야 합니다.");
                    break;
                case PresetActionTypes.RunCommand:
                    if (!allowRunCommand) errors.Add($"{p}: '명령 실행'은 관리자가 배포한 프리셋에서만 사용할 수 있습니다.");
                    else if (string.IsNullOrWhiteSpace(a.Command)) errors.Add($"{p}: 실행할 명령을 입력하세요.");
                    break;
                default:
                    errors.Add($"{p}: 알 수 없는 동작 종류입니다 ({a.Type}).");
                    break;
            }
        }
        return new PresetValidationResult(errors.Count == 0, errors);
    }

    public static PresetValidationResult ValidateFile(PresetFile file, bool allowRunCommand)
    {
        var errors = new List<string>();
        if (file.Presets.Count > MaxPresets) errors.Add($"프리셋은 최대 {MaxPresets}개까지 만들 수 있습니다.");
        if (file.Presets.Select(p => p.Id).Distinct().Count() != file.Presets.Count) errors.Add("프리셋 ID가 중복되었습니다.");
        foreach (var p in file.Presets) errors.AddRange(Validate(p, allowRunCommand).Errors);
        return new PresetValidationResult(errors.Count == 0, errors);
    }

    /// <summary>Parses and validates JSON. Returns null file plus Korean errors when invalid.</summary>
    public static (PresetFile? File, PresetValidationResult Result) Parse(string json, bool allowRunCommand)
    {
        PresetFile? file;
        try { file = GacJson.Deserialize<PresetFile>(json); }
        catch (JsonException) { return (null, new PresetValidationResult(false, ["프리셋 파일 형식이 올바르지 않습니다."])); }
        if (file == null) return (null, new PresetValidationResult(false, ["프리셋 파일이 비어 있습니다."]));
        file.Presets ??= [];
        foreach (var p in file.Presets) p.Actions ??= [];
        var result = ValidateFile(file, allowRunCommand);
        return (result.IsValid ? file : null, result);
    }

    public static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
}
