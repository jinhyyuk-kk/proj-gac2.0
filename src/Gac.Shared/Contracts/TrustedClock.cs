namespace Gac.Shared.Contracts;

/// <summary>
/// Decides "now" for lock exemption without trusting the local wall clock (CHANGE-002 §2.3). A student could set the
/// PC clock to an exemption day to dodge a lock. Online, the agent anchors on the server's time; offline it uses the last
/// server time plus the monotonic time elapsed since (<see cref="Environment.TickCount64"/>), never the wall clock.
/// If the wall clock differs from that estimate by more than <see cref="TamperTolerance"/>, the wall clock is ignored
/// and the event is reported so it can be logged.
/// </summary>
public sealed class TrustedClock(Func<long> monotonicMs, Func<DateTimeOffset> wallClock)
{
    public static readonly TimeSpan TamperTolerance = TimeSpan.FromMinutes(10);
    private readonly Lock _lock = new();
    private DateTimeOffset? _anchorServer;
    private long _anchorTick;

    public TrustedClock() : this(() => Environment.TickCount64, () => DateTimeOffset.UtcNow) { }

    /// <summary>Restores a persisted anchor (last server time) after a restart. The monotonic counter restarts at boot,
    /// so the elapsed time since the anchor is unknown; until the next server contact we use the anchor itself, unless the
    /// wall clock is within tolerance of it and later (normal time passing), see <see cref="Now"/>.</summary>
    public void Restore(DateTimeOffset? lastServerTime)
    {
        lock (_lock) { if (_anchorServer == null && lastServerTime != null) { _anchorServer = lastServerTime; _anchorTick = long.MinValue; } }
    }

    public void OnServerTime(DateTimeOffset serverTime)
    {
        lock (_lock) { _anchorServer = serverTime; _anchorTick = monotonicMs(); }
    }

    public DateTimeOffset? LastServerTime { get { lock (_lock) return _anchorServer; } }

    /// <summary>The trusted current time, and whether the local clock was ignored because it looked tampered.</summary>
    public (DateTimeOffset Now, bool WallClockIgnored) Now()
    {
        var wall = wallClock();
        lock (_lock)
        {
            if (_anchorServer is not { } anchor) return (wall, false); // never contacted a server: nothing better to use
            if (_anchorTick == long.MinValue)
            {
                // Restored after reboot: the real time is at least the anchor. Accept the wall clock only if it is not
                // before the anchor (minus tolerance); a far-future jump cannot be checked without a server, so we
                // conservatively keep the anchor date (locks stay in force rather than being dodged).
                if (wall >= anchor - TamperTolerance && wall <= anchor + TimeSpan.FromDays(1)) return (wall, false);
                return (anchor, true);
            }
            var estimate = anchor + TimeSpan.FromMilliseconds(monotonicMs() - _anchorTick);
            var drift = (wall - estimate).Duration();
            return drift > TamperTolerance ? (estimate, true) : (estimate, false);
        }
    }
}
