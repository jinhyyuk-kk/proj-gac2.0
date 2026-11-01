using Gac.Shared;
using Gac.Shared.ClassMode;
using Gac.Shared.Contracts;
using Gac.Shared.Modules;
using Gac.Shared.Pipe;

namespace Gac.Agent.Runtime;

/// <summary>
/// Holds the PC-lock state (CHANGE-001 2.4) in the SYSTEM service, not the tray, so killing the tray or rebooting does
/// not clear it. On each poll it tells the tray to lock for every active period that contains <c>lock_pc</c> and has not
/// been unlocked this boot, and to clear the lock when the period ends or is unlocked. Work PCs never receive a lock
/// because the server strips <c>lock_pc</c> from their period presets before delivery.
/// </summary>
public sealed class LockController(ITrayBridge tray, TimeProvider time)
{
    private const int CountdownMinutes = 3;
    private readonly Lock _lock = new();
    private readonly HashSet<string> _unlocked = new();   // period ids lifted until the next restart
    private readonly HashSet<string> _showing = new();    // period ids currently shown as locked

    public void MarkUnlocked(string periodId)
    {
        lock (_lock) { _unlocked.Add(periodId); _showing.Remove(periodId); }
        tray.Broadcast(ServiceEvent.Unlock, new LockBody(periodId, "", 0));
    }

    /// <summary>Reconcile the current active periods into lock / unlock events for the tray.</summary>
    public void Reconcile(IReadOnlyList<PeriodPreset> periods)
    {
        var now = time.GetUtcNow();
        var lockable = new List<(string Id, string Name)>();
        foreach (var p in periods)
        {
            if (!p.IsActive(now)) continue; // never lock for an expired/not-yet-started period (offline-persisted lists)
            ClassPreset? preset = null;
            try { preset = GacJson.Deserialize<ClassPreset>(p.PresetJson); } catch { }
            if (preset != null && preset.Actions.Any(a => a.Type == PresetActionTypes.LockPc))
                lockable.Add((p.Id, p.PeriodName));
        }
        var active = lockable.Select(x => x.Id).ToHashSet();

        List<(string Id, string Name)> toLock;
        List<string> toClear;
        lock (_lock)
        {
            // Clear periods that are no longer active (ended/stopped) from the showing + unlocked sets.
            toClear = _showing.Where(id => !active.Contains(id)).ToList();
            foreach (var id in toClear) _showing.Remove(id);
            _unlocked.RemoveWhere(id => !active.Contains(id));
            toLock = lockable.Where(x => !_unlocked.Contains(x.Id) && _showing.Add(x.Id)).ToList();
        }
        foreach (var id in toClear) tray.Broadcast(ServiceEvent.Unlock, new LockBody(id, "", 0));
        foreach (var (id, name) in toLock) tray.Broadcast(ServiceEvent.Lock, new LockBody(id, name, CountdownMinutes));
    }

    /// <summary>Re-sends lock events for everything still locked (used when a tray reconnects).</summary>
    public void Resend(IReadOnlyList<PeriodPreset> periods)
    {
        lock (_lock) _showing.Clear();
        Reconcile(periods);
    }
}
