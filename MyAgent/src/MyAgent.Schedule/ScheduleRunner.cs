using MyAgent.Core;

namespace MyAgent.Schedule;

/// <summary>
/// Background runner that evaluates due schedules and delivers their prompt into the target session
/// via the host dispatcher. Mirrors dsh-schedule's runtime + jobs pump: one-shot tasks end after
/// delivery; recurring tasks advance to the next occurrence.
/// </summary>
public sealed class ScheduleRunner : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private readonly ScheduleStore _store;
    private readonly ISessionDispatcher _dispatcher;
    private readonly Timer _timer;
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public event Action<string, string>? Delivered;

    public ScheduleRunner(ScheduleStore store, ISessionDispatcher dispatcher)
    {
        _store = store;
        _dispatcher = dispatcher;
        _timer = new Timer(_ => OnTick(), null, Tick, Tick);
    }

    private void OnTick()
    {
        var now = DateTimeOffset.UtcNow;
        ScheduleRecord[] due;
        lock (_gate)
            due = _store.List(active: true, limit: 500)
                .Where(r => r.ScheduledAt <= now && !_inFlight.Contains(r.Id))
                .ToArray();

        foreach (var record in due)
        {
            lock (_gate) if (!_inFlight.Add(record.Id)) continue;
            _ = Task.Run(() => DeliverAsync(record, now));
        }
    }

    private async Task DeliverAsync(ScheduleRecord record, DateTimeOffset now)
    {
        try
        {
            var target = string.IsNullOrEmpty(record.TargetSessionId)
                ? _dispatcher.CreateSession(null)
                : record.TargetSessionId;

            _dispatcher.Deliver(target, record.Prompt, "schedule");
            Delivered?.Invoke(record.Id, target);

            _store.Update(record.Id, r =>
            {
                r.LastDeliveredAt = now;
                r.DeliveryCount++;
                if (IsOneShot(r.Kind))
                {
                    r.Status = ScheduleStatus.Ended;
                }
                else
                {
                    var next = NextOccurrence(r, now);
                    r.ScheduledAt = next ?? now.AddYears(100); // effectively never if uncomputable
                    if (next is null) r.Status = ScheduleStatus.Ended;
                }
                r.UpdatedAt = DateTimeOffset.UtcNow;
            });
        }
        catch (Exception)
        {
            // Keep the task active so a transient host failure does not lose a reminder; retry next tick.
        }
        finally
        {
            lock (_gate) _inFlight.Remove(record.Id);
        }

        await Task.CompletedTask;
    }

    private static bool IsOneShot(string kind) => kind is "after" or "at";

    /// <summary>Compute the next occurrence strictly after <paramref name="from"/>.</summary>
    public static DateTimeOffset? NextOccurrence(ScheduleRecord r, DateTimeOffset from)
    {
        switch (r.Kind)
        {
            case "every":
                var seconds = Math.Max(60, r.EverySeconds ?? 60);
                var ticks = (from - r.ScheduledAt).Ticks / TimeSpan.FromSeconds(seconds).Ticks + 1;
                return r.ScheduledAt.AddSeconds(seconds * Math.Max(1, ticks));

            case "daily":
                if (!TimeSpan.TryParse(r.Time, out var daily)) return null;
                var local = TimeZoneInfo.ConvertTime(from, LocalZone(r.TimeZone));
                var d = local.Date.Add(daily);
                if (d <= local) d = d.AddDays(1);
                return new DateTimeOffset(d, LocalZone(r.TimeZone).GetUtcOffset(d));

            case "weekly":
                if (!TimeSpan.TryParse(r.Time, out var weekly) || r.Weekdays is not { Count: > 0 }) return null;
                var zone = LocalZone(r.TimeZone);
                var wlocal = TimeZoneInfo.ConvertTime(from, zone);
                for (var i = 0; i < 8; i++)
                {
                    var day = wlocal.Date.AddDays(i);
                    var iso = day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek;
                    if (!r.Weekdays.Contains(iso)) continue;
                    var candidate = day.Add(weekly);
                    if (candidate <= wlocal) continue;
                    return new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));
                }
                return null;

            case "cron":
                return CronNext(r.Expression, from);

            default:
                return null;
        }
    }

    private static TimeZoneInfo LocalZone(string? id)
    {
        if (string.IsNullOrEmpty(id)) return TimeZoneInfo.Local;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Local; }
    }

    /// <summary>Minimal 5-field cron evaluator: minute hour dom month dow (dow: 0-7, both 0 and 7 = Sunday).</summary>
    public static DateTimeOffset? CronNext(string? expression, DateTimeOffset from)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return null;

        var minute = ParseField(parts[0], 0, 59);
        var hour = ParseField(parts[1], 0, 23);
        var dom = ParseField(parts[2], 1, 31);
        var month = ParseField(parts[3], 1, 12);
        var dow = ParseField(parts[4], 0, 7);

        var cursor = from.AddMinutes(1);
        cursor = new DateTimeOffset(cursor.Year, cursor.Month, cursor.Day, cursor.Hour, cursor.Minute, 0, cursor.Offset);
        for (var i = 0; i < 60 * 24 * 366; i++)
        {
            if (!month.Contains(cursor.Month)) { cursor = cursor.AddHours(1).AddMinutes(-cursor.Minute); continue; }
            var isoDow = cursor.DayOfWeek == DayOfWeek.Sunday ? 0 : (int)cursor.DayOfWeek;
            if (hour.Contains(cursor.Hour) && minute.Contains(cursor.Minute)
                && dom.Contains(cursor.Day) && (dow.Contains(isoDow) || dow.Contains(isoDow + 7)))
                return cursor;
            cursor = cursor.AddMinutes(1);
        }
        return null;
    }

    private static HashSet<int> ParseField(string field, int min, int max)
    {
        var set = new HashSet<int>();
        foreach (var part in field.Split(','))
        {
            if (part == "*") { for (var v = min; v <= max; v++) set.Add(v); continue; }
            var step = 1;
            var body = part;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                step = int.TryParse(part[(slash + 1)..], out var s) ? Math.Max(1, s) : 1;
                body = part[..slash];
            }
            if (body == "*")
            {
                for (var v = min; v <= max; v += step) set.Add(v);
            }
            else if (body.Contains('-'))
            {
                var range = body.Split('-');
                if (int.TryParse(range[0], out var a) && int.TryParse(range[1], out var b))
                    for (var v = a; v <= b; v += step) if (v >= min && v <= max) set.Add(v);
            }
            else if (int.TryParse(body, out var single) && single >= min && single <= max)
            {
                set.Add(single);
            }
        }
        return set;
    }

    public void Dispose() => _timer.Dispose();
}
