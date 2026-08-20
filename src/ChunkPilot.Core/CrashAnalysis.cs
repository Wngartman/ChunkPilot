namespace ChunkPilot.Core;

/// <summary>How strongly the bounded local evidence supports a crash diagnosis.</summary>
public enum CrashConfidence
{
    Unknown,
    Possible,
    HighlyLikely,
    Confirmed
}

/// <summary>One bounded, redacted excerpt used to explain a local crash diagnosis.</summary>
public sealed record CrashEvidence
{
    public string Source { get; init; } = "";
    public string Excerpt { get; init; } = "";
}

/// <summary>A safe, allowlisted next action. It never contains a shell command or native path.</summary>
public sealed record CrashRemediation
{
    public string Code { get; init; } = "";
    public string Label { get; init; } = "";
    public string Detail { get; init; } = "";
}

/// <summary>A durable diagnosis derived entirely from bounded local evidence.</summary>
public sealed record CrashAnalysisReport
{
    public Guid ReportId { get; init; } = Guid.NewGuid();
    public Guid ServerId { get; init; }
    public DateTimeOffset AnalyzedAt { get; init; } = DateTimeOffset.UtcNow;
    public int? ExitCode { get; init; }
    public string Code { get; init; } = "unknown";
    public string Title { get; init; } = "ChunkPilot could not determine the cause";
    public string Summary { get; init; } = "The process exited unexpectedly, but the bounded local evidence does not identify one reliable cause.";
    public CrashConfidence Confidence { get; init; } = CrashConfidence.Unknown;
    public bool ReachedReadiness { get; init; }
    public string ServerIdentity { get; init; } = "";
    public string RuntimeIdentity { get; init; } = "";
    public string ActiveOperation { get; init; } = "";
    public IReadOnlyList<CrashEvidence> Evidence { get; init; } = [];
    public IReadOnlyList<string> RecommendedSteps { get; init; } = [];
    public IReadOnlyList<CrashRemediation> SafeActions { get; init; } = [];
}

public sealed record CrashEvidenceInput(string Source, string Text);

public sealed record CrashAnalysisInput
{
    public Guid ServerId { get; init; }
    public int? ExitCode { get; init; }
    public int? ConfiguredPort { get; init; }
    public bool ReachedReadiness { get; init; }
    public string ServerIdentity { get; init; } = "";
    public string RuntimeIdentity { get; init; } = "";
    public string ActiveOperation { get; init; } = "";
    public IReadOnlyList<CrashEvidenceInput> Evidence { get; init; } = [];
}

/// <summary>
/// Correlates deterministic troubleshooting rules across distinct bounded evidence sources. A
/// single regular-expression match is never labelled Confirmed.
/// </summary>
public static class CrashAnalysisService
{
    private static readonly IReadOnlyList<CrashRemediation> SafeActions =
    [
        new() { Code = "open-console", Label = "Open console", Detail = "Review the surrounding server output." },
        new() { Code = "open-logs", Label = "Open logs", Detail = "Open the server's local log folder." },
        new() { Code = "support-bundle", Label = "Create support bundle", Detail = "Create a redacted local diagnostic bundle." },
        new() { Code = "retry-start", Label = "Retry start", Detail = "Retry through the authoritative lifecycle path after reviewing the evidence." }
    ];

    public static CrashAnalysisReport Analyze(CrashAnalysisInput input)
    {
        var sourceMatches = input.Evidence
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .Select(item => new
            {
                item.Source,
                Report = TroubleshootingService.Analyze(item.Text, input.ConfiguredPort)
            })
            .ToArray();
        var ranked = sourceMatches
            .SelectMany(source => source.Report.Matches.Select(match => new { source.Source, Match = match }))
            .GroupBy(item => item.Match.Code, StringComparer.Ordinal)
            .Select(group => new
            {
                Code = group.Key,
                Best = group.OrderByDescending(item => item.Match.Confidence).First().Match,
                Sources = group.Select(item => item.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Evidence = group
                    .GroupBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
                    .Select(items => items.First())
                    .Take(4)
                    .Select(item => new CrashEvidence
                    {
                        Source = item.Source,
                        Excerpt = SecretRedactor.Redact(item.Match.Evidence)
                    })
                    .ToArray()
            })
            .OrderByDescending(item => item.Sources.Length)
            .ThenByDescending(item => item.Best.Confidence)
            .ThenBy(item => item.Best.Title, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (ranked is null)
        {
            var fallback = input.Evidence
                .Where(item => !string.IsNullOrWhiteSpace(item.Text))
                .Take(2)
                .Select(item => new CrashEvidence
                {
                    Source = item.Source,
                    Excerpt = BoundedExcerpt(item.Text)
                })
                .ToArray();
            return new CrashAnalysisReport
            {
                ServerId = input.ServerId,
                ExitCode = input.ExitCode,
                ReachedReadiness = input.ReachedReadiness,
                ServerIdentity = SecretRedactor.Redact(input.ServerIdentity),
                RuntimeIdentity = SecretRedactor.Redact(input.RuntimeIdentity),
                ActiveOperation = input.ActiveOperation,
                Evidence = fallback,
                SafeActions = SafeActions
            };
        }

        var confidence = ranked.Sources.Length >= 2
            ? CrashConfidence.HighlyLikely
            : ranked.Best.Confidence >= 93
                ? CrashConfidence.HighlyLikely
                : CrashConfidence.Possible;
        return new CrashAnalysisReport
        {
            ServerId = input.ServerId,
            ExitCode = input.ExitCode,
            Code = ranked.Code,
            Title = ranked.Best.Title,
            Summary = ranked.Best.Summary,
            Confidence = confidence,
            ReachedReadiness = input.ReachedReadiness,
            ServerIdentity = SecretRedactor.Redact(input.ServerIdentity),
            RuntimeIdentity = SecretRedactor.Redact(input.RuntimeIdentity),
            ActiveOperation = input.ActiveOperation,
            Evidence = ranked.Evidence,
            RecommendedSteps = ranked.Best.Steps,
            SafeActions = SafeActions
        };
    }

    private static string BoundedExcerpt(string value)
    {
        var safe = SecretRedactor.Redact(value).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return safe.Length <= 280 ? safe : safe[..277] + "...";
    }
}
