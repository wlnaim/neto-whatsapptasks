namespace WhatsAppTasks.Core;

/// <summary>Pure local-calendar recurrence calculations. Returned occurrences are strictly after <paramref name="afterLocal"/>.</summary>
public static class ScheduleCalculator
{
    public static DateTime? GetNextLocal(ScheduleDefinition schedule, DateTime afterLocal)
    {
        Validate(schedule);
        afterLocal = DateTime.SpecifyKind(afterLocal, DateTimeKind.Unspecified);
        var today = DateOnly.FromDateTime(afterLocal);
        var atTime = schedule.LocalTime;
        DateTime At(DateOnly day) => day.ToDateTime(atTime, DateTimeKind.Unspecified);
        bool Future(DateOnly day) => At(day) > afterLocal;

        switch (schedule.Kind)
        {
            case ScheduleKind.OneTime:
            case ScheduleKind.FutureDate:
                return Future(schedule.AnchorDate) ? At(schedule.AnchorDate) : null;
            case ScheduleKind.Daily:
            case ScheduleKind.EveryDay:
                return Future(today) ? At(today) : At(today.AddDays(1));
            case ScheduleKind.WeeklyOnDays:
                for (var i = 0; i < 8; i++) { var day = today.AddDays(i); if (schedule.DaysOfWeek.Contains(day.DayOfWeek) && Future(day)) return At(day); }
                return null;
            case ScheduleKind.Monthly:
                var firstMonth = today < new DateOnly(schedule.AnchorDate.Year, schedule.AnchorDate.Month, 1) ? schedule.AnchorDate : today;
                for (var i = 0; ; i++)
                {
                    var month = firstMonth.AddMonths(i);
                    var day = new DateOnly(month.Year, month.Month, Math.Min(schedule.AnchorDate.Day, DateTime.DaysInMonth(month.Year, month.Month)));
                    if (day >= schedule.AnchorDate && Future(day)) return At(day);
                }
            case ScheduleKind.AlternatingWeeks:
                return NextAnchoredWeek(schedule, afterLocal, 2);
            case ScheduleKind.EveryNWeeks:
                return NextAnchoredWeek(schedule, afterLocal, schedule.IntervalWeeks);
            default: throw new ArgumentOutOfRangeException(nameof(schedule));
        }
    }

    /// <summary>
    /// Finds a strictly future UTC occurrence in a supplied Windows time zone.  In a fall-back fold,
    /// <see cref="LocalToUtc"/> deliberately chooses the first wall-clock occurrence; if that instant
    /// has passed, this advances the recurrence instead of replaying it late.
    /// </summary>
    public static DateTimeOffset? GetNextUtc(ScheduleDefinition schedule, DateTimeOffset afterUtc, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var localAfter = TimeZoneInfo.ConvertTime(afterUtc, zone).DateTime;
        while (true)
        {
            var nextLocal = GetNextLocal(schedule, localAfter);
            if (nextLocal is null) return null;
            var nextUtc = LocalToUtc(nextLocal.Value, zone);
            if (nextUtc > afterUtc) return nextUtc;
            // The local candidate was ambiguous and mapped to its already-past first UTC instant.
            localAfter = nextLocal.Value;
        }
    }

    private static DateTime? NextAnchoredWeek(ScheduleDefinition schedule, DateTime afterLocal, int weeks)
    {
        var candidate = schedule.AnchorDate.ToDateTime(schedule.LocalTime, DateTimeKind.Unspecified);
        checked { while (candidate <= afterLocal) candidate = candidate.AddDays(7 * weeks); }
        return candidate;
    }

    public static void Validate(ScheduleDefinition schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (!Enum.IsDefined(schedule.Kind)) throw new ArgumentOutOfRangeException(nameof(schedule));
        if (schedule.DaysOfWeek is not null && schedule.DaysOfWeek.Any(day => !Enum.IsDefined(day)))
            throw new ArgumentException("The weekday is invalid.", nameof(schedule));
        if (schedule.Kind == ScheduleKind.WeeklyOnDays && (schedule.DaysOfWeek is null || schedule.DaysOfWeek.Count == 0))
            throw new ArgumentException("Weekly schedules require at least one weekday.", nameof(schedule));
        if (schedule.Kind == ScheduleKind.EveryNWeeks && schedule.IntervalWeeks < 1)
            throw new ArgumentException("Every-N-weeks schedules require an interval of at least one.", nameof(schedule));
    }

    public static DateTimeOffset LocalToUtc(DateTime local, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // A nonexistent wall time is advanced to the first valid minute; ambiguous time chooses the earlier UTC occurrence.
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local))
        {
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(local, offset).ToUniversalTime();
        }
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
