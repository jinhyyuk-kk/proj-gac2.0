namespace Gac.Shared.Contracts;

/// <summary>One weekday of the Admin PC auto-shutdown schedule (CHANGE-002 §4.2). Time is "HH:mm" local time.</summary>
public sealed record ServerShutdownDay(DayOfWeek Day, bool Enabled, string Time);

/// <summary>When the Admin PC (the server PC itself) turns off automatically. Same setting from the Console and the Admin PC.</summary>
public sealed record ServerShutdownSettings(bool Enabled, IReadOnlyList<ServerShutdownDay> Days, IReadOnlyList<DateOnly> ExceptionDates)
{
    public const int WarningMinutes = 10;
    public const int ExtendMinutes = 30;
    public const int MaxJobWaitMinutes = 30;

    /// <summary>Default: Mon–Fri 18:00, weekends off, disabled until someone turns it on.</summary>
    public static ServerShutdownSettings Default() => new(false,
        [.. Enum.GetValues<DayOfWeek>().Select(d => new ServerShutdownDay(d, d is not (DayOfWeek.Saturday or DayOfWeek.Sunday), "18:00"))], []);

    public static string? Validate(ServerShutdownSettings s)
    {
        if (s.Days.Count != 7 || s.Days.Select(d => d.Day).Distinct().Count() != 7) return "요일 설정이 올바르지 않습니다.";
        foreach (var d in s.Days)
            if (!TimeOnly.TryParseExact(d.Time, "HH:mm", out _)) return $"{ExemptionCalendar.WeekdayKorean(d.Day)} 시간 형식이 올바르지 않습니다 (예: 18:00).";
        return null;
    }

    /// <summary>The scheduled shutdown time for a local date, or null when that day is off or an exception date.</summary>
    public DateTime? ScheduledAt(DateOnly date)
    {
        if (!Enabled || ExceptionDates.Contains(date)) return null;
        var d = Days.FirstOrDefault(x => x.Day == date.DayOfWeek);
        if (d is not { Enabled: true } || !TimeOnly.TryParseExact(d.Time, "HH:mm", out var t)) return null;
        return date.ToDateTime(t);
    }
}

/// <summary>Live state of today's server PC shutdown (drives the T−10 popup on the Admin PC and the Console notice).</summary>
public sealed record ServerShutdownStatus(
    DateTimeOffset? NextShutdown,
    bool Warning,
    bool SkippedToday,
    int ExtendedMinutes,
    string? WaitingFor,
    string? LastEvent);

/// <summary>Overload blocking limits (CHANGE-002 §4.3). Defaults per the spec.</summary>
public sealed record OverloadSettings(
    int MaxRequestsPerAgentPerMinute = 60,
    int MaxFailedAuthPerSourcePer10Min = 10,
    int MaxEnrollPerSourcePerHour = 5,
    int BlockMinutes = 10,
    int MaxBlockHours = 24)
{
    public static string? Validate(OverloadSettings s) =>
        s.MaxRequestsPerAgentPerMinute is < 5 or > 10000 ? "분당 최대 요청 수는 5~10000 사이여야 합니다."
        : s.MaxFailedAuthPerSourcePer10Min is < 1 or > 1000 ? "10분당 인증 실패 한도는 1~1000 사이여야 합니다."
        : s.MaxEnrollPerSourcePerHour is < 1 or > 1000 ? "시간당 등록 시도 한도는 1~1000 사이여야 합니다."
        : s.BlockMinutes is < 1 or > 1440 ? "차단 시간은 1~1440분 사이여야 합니다."
        : s.MaxBlockHours is < 1 or > 720 ? "최대 차단 시간은 1~720시간 사이여야 합니다."
        : TimeSpan.FromMinutes(s.BlockMinutes) > TimeSpan.FromHours(s.MaxBlockHours) ? "차단 시간이 최대 차단 시간보다 길 수 없습니다."
        : null;

    /// <summary>Block length for the Nth offence within 24 hours: doubles each time, capped at the maximum.</summary>
    public TimeSpan BlockFor(int offence)
    {
        var minutes = BlockMinutes * Math.Pow(2, Math.Clamp(offence - 1, 0, 20));
        return TimeSpan.FromMinutes(Math.Min(minutes, MaxBlockHours * 60.0));
    }
}

public enum OverloadLimit { Requests, FailedAuth, Enrollment }

public static class OverloadText
{
    public static string Korean(OverloadLimit l) => l switch
    {
        OverloadLimit.Requests => "분당 요청 수 초과",
        OverloadLimit.FailedAuth => "인증 실패 횟수 초과",
        OverloadLimit.Enrollment => "등록 시도 횟수 초과",
        _ => l.ToString(),
    };
}

/// <summary>An overload alert for the operator (CHANGE-002 §4.3/§4.5): shown as a Windows notification and in the 알림 list.</summary>
public sealed record OverloadAlert(
    string Id,
    string? PcAlias,
    string? WindowsName,
    string Ip,
    OverloadLimit Limit,
    int RequestCount,
    DateTimeOffset StartedAt,
    DateTimeOffset BlockedUntil,
    bool Read);

/// <summary>A client currently blocked by overload protection (listed in 과부하 차단 with 차단 해제 / 영구 차단).</summary>
public sealed record OverloadBlock(
    string Key,
    string? PcAlias,
    string? WindowsName,
    string Ip,
    string? Fingerprint,
    OverloadLimit Limit,
    DateTimeOffset BlockedUntil,
    int Offences);

public sealed record MarkAlertsReadRequest(IReadOnlyList<string> Ids);
