using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ChunkPilot.Core;

public sealed partial class BoundedConsoleBuffer
{
    private readonly ConcurrentQueue<ConsoleLine> lines = new();
    private readonly int capacity;
    private long sequence;

    public BoundedConsoleBuffer(int capacity = 5_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 100);
        this.capacity = capacity;
    }

    public void Add(string stream, string text)
    {
        var cleaned = AnsiRegex().Replace(text, "");
        lines.Enqueue(new ConsoleLine(Interlocked.Increment(ref sequence), DateTimeOffset.Now, stream, cleaned));
        while (lines.Count > capacity)
            lines.TryDequeue(out _);
    }

    public IReadOnlyList<ConsoleLine> Snapshot(int maximum = 1_000) =>
        lines.Reverse().Take(Math.Clamp(maximum, 1, capacity)).Reverse().ToArray();

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.Compiled)]
    private static partial Regex AnsiRegex();
}

public static partial class SecretRedactor
{
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var result = KeyValueSecretRegex().Replace(value, "$1=<redacted>");
        result = BearerRegex().Replace(result, "Bearer <redacted>");
        result = UriSecretRegex().Replace(result, "$1<redacted>");
        return result;
    }

    public static IReadOnlyDictionary<string, string> RedactEnvironment(IReadOnlyDictionary<string, string> source) =>
        source.ToDictionary(
            pair => pair.Key,
            pair => SecretKeyRegex().IsMatch(pair.Key) ? "<redacted>" : Redact(pair.Value),
            StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?i)\b(password|passwd|secret|token|api[_-]?key|rcon[_-]?password)\s*[:=]\s*([^\s;]+)")]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/\-=]+")]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"(?i)(://[^:/\s]+:)[^@\s]+")]
    private static partial Regex UriSecretRegex();

    [GeneratedRegex(@"(?i)(password|passwd|secret|token|api.?key|credential|private.?key)")]
    private static partial Regex SecretKeyRegex();
}

public static class CommandLineQuoter
{
    public static string QuoteWindowsArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"'))
            return argument;

        var result = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', slashes * 2 + 1);
                result.Append('"');
                slashes = 0;
                continue;
            }

            result.Append('\\', slashes);
            slashes = 0;
            result.Append(character);
        }

        result.Append('\\', slashes * 2);
        result.Append('"');
        return result.ToString();
    }

    public static string BuildCmdArguments(string scriptPath) =>
        $"/d /s /c \"\"{scriptPath.Replace("\"", "\"\"", StringComparison.Ordinal)}\"\"";
}

public static class ScheduleCalculator
{
    public static DateTimeOffset? NextRun(ScheduleEntry schedule, DateTimeOffset after)
    {
        if (!schedule.Enabled)
            return null;

        return schedule.Kind switch
        {
            ScheduleKind.OneTime => schedule.OneTimeAt is { } time && time > after ? time : null,
            ScheduleKind.Interval => NextInterval(schedule, after),
            ScheduleKind.Daily => NextDaily(schedule, after),
            ScheduleKind.Weekly => NextWeekly(schedule, after),
            ScheduleKind.Monthly => NextMonthly(schedule, after),
            ScheduleKind.Cron => NextCron(schedule.CronExpression, after),
            _ => null
        };
    }

    private static DateTimeOffset NextInterval(ScheduleEntry schedule, DateTimeOffset after)
    {
        var minutes = Math.Max(1, schedule.IntervalMinutes);
        var origin = schedule.LastRunAt ?? after;
        var next = origin.AddMinutes(minutes);
        return next > after ? next : after.AddMinutes(minutes);
    }

    private static DateTimeOffset NextDaily(ScheduleEntry schedule, DateTimeOffset after)
    {
        var candidate = new DateTimeOffset(after.Year, after.Month, after.Day,
            schedule.TimeOfDay.Hours, schedule.TimeOfDay.Minutes, schedule.TimeOfDay.Seconds, after.Offset);
        return candidate > after ? candidate : candidate.AddDays(1);
    }

    private static DateTimeOffset NextWeekly(ScheduleEntry schedule, DateTimeOffset after)
    {
        var candidate = NextDaily(schedule, after);
        while (candidate.DayOfWeek != schedule.DayOfWeek)
            candidate = candidate.AddDays(1);
        return candidate;
    }

    private static DateTimeOffset NextMonthly(ScheduleEntry schedule, DateTimeOffset after)
    {
        var day = Math.Clamp(schedule.DayOfMonth, 1, DateTime.DaysInMonth(after.Year, after.Month));
        var candidate = new DateTimeOffset(after.Year, after.Month, day,
            schedule.TimeOfDay.Hours, schedule.TimeOfDay.Minutes, schedule.TimeOfDay.Seconds, after.Offset);
        if (candidate > after)
            return candidate;
        var nextMonth = after.AddMonths(1);
        day = Math.Clamp(schedule.DayOfMonth, 1, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
        return new DateTimeOffset(nextMonth.Year, nextMonth.Month, day,
            schedule.TimeOfDay.Hours, schedule.TimeOfDay.Minutes, schedule.TimeOfDay.Seconds, after.Offset);
    }

    private static DateTimeOffset? NextCron(string expression, DateTimeOffset after)
    {
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            return null;

        for (var candidate = after.AddMinutes(1); candidate <= after.AddYears(1); candidate = candidate.AddMinutes(1))
        {
            if (CronFieldMatches(fields[0], candidate.Minute, 0, 59) &&
                CronFieldMatches(fields[1], candidate.Hour, 0, 23) &&
                CronFieldMatches(fields[2], candidate.Day, 1, 31) &&
                CronFieldMatches(fields[3], candidate.Month, 1, 12) &&
                CronFieldMatches(fields[4], (int)candidate.DayOfWeek, 0, 6))
                return candidate;
        }

        return null;
    }

    private static bool CronFieldMatches(string field, int value, int minimum, int maximum)
    {
        if (field == "*")
            return true;
        if (field.StartsWith("*/", StringComparison.Ordinal) &&
            int.TryParse(field.AsSpan(2), out var step) && step > 0)
            return value % step == 0;
        return field.Split(',').Any(part =>
            int.TryParse(part, out var exact) && exact >= minimum && exact <= maximum && value == exact);
    }
}

public static class StatisticsDownsampler
{
    public static IReadOnlyList<StatisticsSample> Downsample(
        IReadOnlyList<StatisticsSample> samples,
        int maximumPoints)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPoints, 2);
        if (samples.Count <= maximumPoints)
            return samples.ToArray();

        var result = new List<StatisticsSample>(maximumPoints);
        var bucketSize = (double)samples.Count / maximumPoints;
        for (var bucket = 0; bucket < maximumPoints; bucket++)
        {
            var start = (int)Math.Floor(bucket * bucketSize);
            var end = Math.Min(samples.Count, (int)Math.Floor((bucket + 1) * bucketSize));
            if (end <= start)
                end = Math.Min(samples.Count, start + 1);
            var slice = samples.Skip(start).Take(end - start).ToArray();
            result.Add(new StatisticsSample
            {
                Timestamp = slice[^1].Timestamp,
                CpuPercent = slice.Average(sample => sample.CpuPercent),
                WorkingSetBytes = (long)slice.Average(sample => sample.WorkingSetBytes),
                PeakWorkingSetBytes = slice.Max(sample => sample.PeakWorkingSetBytes),
                ProcessCount = (int)Math.Round(slice.Average(sample => sample.ProcessCount)),
                ThreadCount = (int)Math.Round(slice.Average(sample => sample.ThreadCount)),
                DiskReadBytes = slice.Max(sample => sample.DiskReadBytes),
                DiskWriteBytes = slice.Max(sample => sample.DiskWriteBytes)
            });
        }
        return result;
    }
}
