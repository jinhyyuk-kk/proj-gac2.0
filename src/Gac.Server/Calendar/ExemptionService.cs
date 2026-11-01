using Dapper;
using Gac.Server.Accounts;
using Gac.Server.Data;
using Gac.Shared;
using Gac.Shared.Contracts;

namespace Gac.Server.Calendar;

/// <summary>
/// Lock exemption days (CHANGE-002 §2). Stored as rules; agents receive the expanded dates for the next 60 days plus the
/// server's own time with every poll, so they can decide "today" offline and without trusting the PC clock.
/// Every add/edit/delete/skip is audited.
/// </summary>
public sealed class ExemptionService(Db db, AuditLog audit, TimeProvider time)
{
    private sealed class Row
    {
        public string id = ""; public string name = ""; public string recurrence = ""; public string date = "";
        public long? dom; public long? nth; public long? weekday; public string skipped = "[]"; public string created_by = ""; public long created_at;
    }

    private static ExemptionDay Map(Row r) => new(r.id, r.name, Enum.Parse<ExemptionRecurrence>(r.recurrence), DateOnly.Parse(r.date),
        (int?)r.dom, (int?)r.nth, r.weekday is { } w ? (DayOfWeek)w : null,
        GacJson.Deserialize<List<DateOnly>>(r.skipped) ?? [], r.created_by, Time.At(r.created_at));

    public IReadOnlyList<ExemptionDay> List()
    {
        using var c = db.Open();
        return c.Query<Row>("SELECT * FROM exemption_days ORDER BY name").Select(Map).ToList();
    }

    public ExemptionDay Save(Session actor, string? id, SaveExemptionDayRequest r)
    {
        if (ExemptionCalendar.Validate(r) is { } err) throw new GacException(err);
        using var c = db.Open();
        var existing = id == null ? null : c.QuerySingleOrDefault<Row>("SELECT * FROM exemption_days WHERE id = @id", new { id });
        if (id != null && existing == null) throw new GacException("잠금 해제일을 찾을 수 없습니다.", 404);
        var day = new ExemptionDay(id ?? Guid.NewGuid().ToString("N"), r.Name.Trim(), r.Recurrence, r.Date,
            r.Recurrence == ExemptionRecurrence.MonthlyDay ? r.DayOfMonth : null,
            r.Recurrence == ExemptionRecurrence.MonthlyNthWeekday ? r.Nth : null,
            r.Recurrence == ExemptionRecurrence.MonthlyNthWeekday ? r.Weekday : null,
            existing == null ? [] : Map(existing).Skipped, actor.UserName, time.GetUtcNow());
        c.Execute("""
            INSERT INTO exemption_days(id, name, recurrence, date, dom, nth, weekday, skipped, created_by, created_at)
            VALUES(@id, @name, @rec, @date, @dom, @nth, @wd, @skipped, @by, @at)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, recurrence = excluded.recurrence, date = excluded.date,
              dom = excluded.dom, nth = excluded.nth, weekday = excluded.weekday
            """, new
        {
            id = day.Id, name = day.Name, rec = day.Recurrence.ToString(), date = day.Date.ToString("yyyy-MM-dd"), dom = day.DayOfMonth, nth = day.Nth,
            wd = day.Weekday is { } w ? (int?)w : null, skipped = GacJson.Serialize(day.Skipped), by = actor.UserName, at = Time.Ms(time.GetUtcNow()),
        });
        audit.Write(actor.UserName, existing == null ? "calendar.exemption.add" : "calendar.exemption.edit", day.Name, ExemptionCalendar.Describe(day));
        return day;
    }

    public void Delete(Session actor, string id)
    {
        var d = Get(id);
        using var c = db.Open();
        c.Execute("DELETE FROM exemption_days WHERE id = @id", new { id });
        audit.Write(actor.UserName, "calendar.exemption.delete", d.Name, ExemptionCalendar.Describe(d));
    }

    public void Skip(Session actor, string id, DateOnly date)
    {
        var d = Get(id);
        var skipped = d.Skipped.Append(date).Distinct().Order().ToList();
        using var c = db.Open();
        c.Execute("UPDATE exemption_days SET skipped = @s WHERE id = @id", new { s = GacJson.Serialize(skipped), id });
        audit.Write(actor.UserName, "calendar.exemption.skip", d.Name, $"{date:yyyy-MM-dd} 건너뜀");
    }

    private ExemptionDay Get(string id)
    {
        using var c = db.Open();
        var r = c.QuerySingleOrDefault<Row>("SELECT * FROM exemption_days WHERE id = @id", new { id }) ?? throw new GacException("잠금 해제일을 찾을 수 없습니다.", 404);
        return Map(r);
    }

    /// <summary>What every poll carries (CHANGE-002 §2.3): server time + exemption dates from today (Seoul) for 60 days.</summary>
    public AgentCalendar ForAgent()
    {
        var now = time.GetUtcNow();
        var today = ExemptionCalendar.SeoulDate(now);
        return new AgentCalendar(now, ExemptionCalendar.ExpandAll(List(), today, today.AddDays(ExemptionCalendar.AgentHorizonDays)));
    }
}
