using Gac.Shared;
using Gac.Shared.ClassMode;
using Gac.Shared.Contracts;
using Gac.Shared.Security;

namespace Gac.Modules.ClassMode;

/// <summary>
/// Per-user preset file (%APPDATA%\GAC\presets.json) plus console-distributed shared presets.
/// Shared presets are only accepted if their ECDSA signature verifies with the server's public key.
/// </summary>
public sealed class PresetStore(string userFilePath)
{
    public static string DefaultUserPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GAC", "presets.json");

    /// <summary>Loads user presets. Invalid files are not used; the Korean error is returned instead.</summary>
    public (PresetFile File, string? Error) LoadUser()
    {
        if (!File.Exists(userFilePath)) return (new PresetFile(), null);
        string json;
        try { json = File.ReadAllText(userFilePath); }
        catch (IOException e) { return (new PresetFile(), "프리셋 파일을 읽지 못했습니다: " + e.Message); }
        var (file, result) = PresetValidator.Parse(json, allowRunCommand: false);
        return file == null ? (new PresetFile(), "저장된 프리셋에 문제가 있어 불러오지 않았습니다.\n" + result.ErrorText) : (file, null);
    }

    public void SaveUser(PresetFile file)
    {
        var result = PresetValidator.ValidateFile(file, allowRunCommand: false);
        if (!result.IsValid) throw new InvalidOperationException(result.ErrorText);
        Directory.CreateDirectory(Path.GetDirectoryName(userFilePath)!);
        var tmp = userFilePath + ".tmp";
        File.WriteAllText(tmp, GacJson.Serialize(file));
        File.Move(tmp, userFilePath, overwrite: true);
    }

    public static (IReadOnlyList<ClassPreset> Presets, string? Error) LoadShared(SignedPresetBundle? bundle, string? publicKeyPem)
    {
        if (bundle == null) return ([], null);
        if (string.IsNullOrEmpty(publicKeyPem) || !PresetSigner.Verify(bundle, publicKeyPem))
            return ([], "관리자 배포 프리셋의 서명이 올바르지 않아 사용하지 않습니다.");
        var (file, result) = PresetValidator.Parse(bundle.PresetsJson, allowRunCommand: true);
        if (file == null) return ([], "관리자 배포 프리셋에 문제가 있습니다.\n" + result.ErrorText);
        foreach (var p in file.Presets) p.Shared = true;
        return (file.Presets, null);
    }
}
