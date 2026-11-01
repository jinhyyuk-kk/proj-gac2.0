namespace Gac.Shared.Contracts;

/// <summary>How a lock exemption day repeats (CHANGE-002 §2.2).</summary>
public enum ExemptionRecurrence
{
    /// <summary>A single date.</summary>
    Once,
    /// <summary>Every month on <see cref="ExemptionDay.DayOfMonth"/> (skipped in months that do not have that day).</summary>
    MonthlyDay,
    /// <summary>Every month on the Nth <see cref="ExemptionDay.Weekday"/> (N = 1–4, 5 = the last one).</summary>
    MonthlyNthWeekday,
}

/// <summary>
/// A lock exemption day (잠금 해제일, CHANGE-002 §2.2). On such a day <c>lock_pc</c> never runs on any PC, whatever period
/// is active. <see cref="Date"/> is the first date the rule applies from (and the only date for <see cref="ExemptionRecurrence.Once"/>).
/// </summary>
public sealed record ExemptionDay(
    string Id,
    string Name,
    ExemptionRecurrence Recurrence,
    DateOnly Date,
    int? DayOfMonth,
    int? Nth,
    DayOfWeek? Weekday,
    IReadOnlyList<DateOnly> Skipped,
    string CreatedBy,
    DateTimeOffset CreatedAt);

public sealed record SaveExemptionDayRequest(string Name, ExemptionRecurrence Recurrence, DateOnly Date, int? DayOfMonth = null, int? Nth = null, DayOfWeek? Weekday = null);

public sealed record SkipOccurrenceRequest(DateOnly Date);

/// <summary>Recurrence expansion, validation and the Asia/Seoul day boundary for lock exemption days.</summary>
public static class ExemptionCalendar
{
    /// <summary>Preset name offered in the dropdown (CHANGE-002 §2.2).</summary>
    public const string PresetName = "내PC지키미 점검일";

    /// <summary>The server sends agents this many days of expanded exemption dates (CHANGE-002 §2.3).</summary>
    public const int AgentHorizonDays = 60;

    /// <summary>Korea Standard Time (UTC+9, no DST). Falls back to a fixed +9 zone if the OS has neither id.</summary>
    public static readonly TimeZoneInfo Seoul = FindSeoul();

    private static TimeZoneInfo FindSeoul()
    {
        foreach (var id in new[] { "Korea Standard Time", "Asia/Seoul" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { } catch (InvalidTimeZoneException) { }
        return TimeZoneInfo.CreateCustomTimeZone("KST", TimeSpan.FromHours(9), "Korea Standard Time", "Korea Standard Time");
    }

    /// <summary>"Today" in Asia/Seoul for an instant: the exemption day runs 00:00–23:59:59 local Seoul time.</summary>
    public static DateOnly SeoulDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Seoul).DateTime);

    public static string? Validate(SaveExemptionDayRequest r)
    {
        var name = r.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 40) return "잠금 해제일 이름을 1~40자로 입력하세요.";
        return r.Recurrence switch
        {
            ExemptionRecurrence.Once => null,
            ExemptionRecurrence.MonthlyDay when r.DayOfMonth is >= 1 and <= 31 => null,
            ExemptionRecurrence.MonthlyDay => "날짜(1~31일)를 고르세요.",
            ExemptionRecurrence.MonthlyNthWeekday when r.Nth is >= 1 and <= 5 && r.Weekday is not null => null,
            ExemptionRecurrence.MonthlyNthWeekday => "몇째 주 무슨 요일인지 고르세요.",
            _ => "반복 방법이 올바르지 않습니다.",
        };
    }

    /// <summary>Every date in [from, to] (inclusive) on which the rule falls, minus skipped occurrences.</summary>
    public static IEnumerable<DateOnly> Expand(ExemptionDay d, DateOnly from, DateOnly to)
    {
        if (to < from) yield break;
        var skipped = d.Skipped.ToHashSet();
        if (d.Recurrence == ExemptionRecurrence.Once)
        {
            if (d.Date >= from && d.Date <= to && !skipped.Contains(d.Date)) yield return d.Date;
            yield break;
        }
        var start = from < d.Date ? d.Date : from;
        for (var month = new DateOnly(start.Year, start.Month, 1); month <= to; month = month.AddMonths(1))
        {
            var date = Occurrence(d, month.Year, month.Month);
            if (date is { } x && x >= start && x <= to && !skipped.Contains(x)) yield return x;
        }
    }

    /// <summary>The rule's date in a given month, or null when the month has none (e.g. 31st in a 30-day month).</summary>
    public static DateOnly? Occurrence(ExemptionDay d, int year, int month)
    {
        switch (d.Recurrence)
        {
            case ExemptionRecurrence.MonthlyDay:
                var day = d.DayOfMonth ?? d.Date.Day;
                return day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;
            case ExemptionRecurrence.MonthlyNthWeekday:
                var wd = d.Weekday ?? d.Date.DayOfWeek;
                var n = d.Nth ?? 1;
                var first = new DateOnly(year, month, 1);
                var firstHit = first.AddDays(((int)wd - (int)first.DayOfWeek + 7) % 7);
                if (n >= 5) // the last one in the month
                {
                    var last = firstHit;
                    while (last.AddDays(7).Month == month) last = last.AddDays(7);
                    return last;
                }
                var hit = firstHit.AddDays(7 * (n - 1));
                return hit.Month == month ? hit : null;
            default:
                return d.Date.Year == year && d.Date.Month == month ? d.Date : null;
        }
    }

    /// <summary>All exemption dates (with names) in a window, sorted. Used for the calendar and for the agent list.</summary>
    public static IReadOnlyList<ExemptionDate> ExpandAll(IEnumerable<ExemptionDay> days, DateOnly from, DateOnly to) =>
        days.SelectMany(d => Expand(d, from, to).Select(x => new ExemptionDate(x, d.Name, d.Id))).OrderBy(x => x.Date).ToList();

    /// <summary>True when the trusted "now" falls on an exemption date in Asia/Seoul: lock_pc must not run at all (CHANGE-002 §2.2).</summary>
    public static bool IsExempt(IEnumerable<ExemptionDate> dates, DateTimeOffset trustedNow)
    {
        var today = SeoulDate(trustedNow);
        return dates.Any(d => d.Date == today);
    }

    private static readonly string[] WeekdayKo = ["일", "월", "화", "수", "목", "금", "토"];
    public static string WeekdayKorean(DayOfWeek d) => WeekdayKo[(int)d] + "요일";

    public static string Describe(ExemptionDay d) => d.Recurrence switch
    {
        ExemptionRecurrence.Once => $"{d.Date:yyyy-MM-dd} 하루",
        ExemptionRecurrence.MonthlyDay => $"매월 {d.DayOfMonth ?? d.Date.Day}일",
        ExemptionRecurrence.MonthlyNthWeekday => $"매월 {NthKorean(d.Nth ?? 1)} {WeekdayKorean(d.Weekday ?? d.Date.DayOfWeek)}",
        _ => "",
    } + (d.Skipped.Count > 0 ? $" (건너뜀 {d.Skipped.Count}회)" : "");

    public static string NthKorean(int n) => n switch { 1 => "첫째", 2 => "둘째", 3 => "셋째", 4 => "넷째", _ => "마지막" };
}

/// <summary>One expanded exemption date (what agents receive and what the calendar draws).</summary>
public sealed record ExemptionDate(DateOnly Date, string Name, string ExemptionId);

/// <summary>Sent to agents with every poll: authoritative server time plus the next 60 days of exemption dates (CHANGE-002 §2.3).</summary>
public sealed record AgentCalendar(DateTimeOffset ServerTime, IReadOnlyList<ExemptionDate> ExemptDates);
