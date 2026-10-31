using Gac.Shared.ClassMode;

namespace Gac.Modules.ClassMode;

/// <summary>Performs one preset action. Real implementation runs in the tray (user context, no admin rights).</summary>
public interface IPresetActionExecutor
{
    Task ExecuteAsync(PresetAction action, bool sharedPreset, CancellationToken ct);
}

public sealed record PresetActionResult(int Index, string Description, bool Success, string? Error);

/// <summary>Runs a preset's actions in order. A failing action is logged and the preset continues (CLAUDE.md 4.3).</summary>
public sealed class PresetRunner(IPresetActionExecutor executor)
{
    public async Task<IReadOnlyList<PresetActionResult>> RunAsync(ClassPreset preset, IProgress<string>? progress, CancellationToken ct)
    {
        var validation = PresetValidator.Validate(preset, allowRunCommand: preset.Shared);
        if (!validation.IsValid)
            return [new PresetActionResult(0, preset.Name, false, validation.ErrorText)];

        var results = new List<PresetActionResult>();
        for (var i = 0; i < preset.Actions.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var a = preset.Actions[i];
            progress?.Report($"{i + 1}/{preset.Actions.Count} {a.Describe()}");
            try
            {
                await executor.ExecuteAsync(a, preset.Shared, ct);
                results.Add(new(i + 1, a.Describe(), true, null));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                results.Add(new(i + 1, a.Describe(), false, e.Message));
            }
        }
        return results;
    }
}
