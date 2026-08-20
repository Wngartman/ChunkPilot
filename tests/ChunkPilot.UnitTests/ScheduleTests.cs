using ChunkPilot.Core;

namespace ChunkPilot.UnitTests;

public sealed class ScheduleTests
{
    private static readonly DateTimeOffset Reference = new(2026, 7, 24, 10, 30, 0, TimeSpan.FromHours(-6));

    [Fact]
    public void Interval_schedule_calculates_future_time()
    {
        var schedule = new ScheduleEntry { Kind = ScheduleKind.Interval, IntervalMinutes = 15, Enabled = true };
        Assert.Equal(Reference.AddMinutes(15), ScheduleCalculator.NextRun(schedule, Reference));
    }

    [Fact]
    public void Daily_schedule_rolls_to_next_day_after_time()
    {
        var schedule = new ScheduleEntry { Kind = ScheduleKind.Daily, TimeOfDay = new TimeSpan(9, 0, 0), Enabled = true };
        Assert.Equal(new DateTimeOffset(2026, 7, 25, 9, 0, 0, Reference.Offset), ScheduleCalculator.NextRun(schedule, Reference));
    }

    [Fact]
    public void Cron_schedule_supports_steps_and_exact_fields()
    {
        var schedule = new ScheduleEntry { Kind = ScheduleKind.Cron, CronExpression = "*/10 11 * * *", Enabled = true };
        Assert.Equal(new DateTimeOffset(2026, 7, 24, 11, 0, 0, Reference.Offset), ScheduleCalculator.NextRun(schedule, Reference));
    }

    [Fact]
    public void Invalid_cron_returns_unavailable()
    {
        var schedule = new ScheduleEntry { Kind = ScheduleKind.Cron, CronExpression = "bad", Enabled = true };
        Assert.Null(ScheduleCalculator.NextRun(schedule, Reference));
    }
}

