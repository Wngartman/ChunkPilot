using ChunkPilot.Core;

namespace ChunkPilot.App.CreateServer;

/// <summary>
/// Builds the review screen's content from the current state, and from nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The review is rebuilt whenever the state changes, so it cannot drift from what the user actually
/// chose. Only applicable lines are emitted: a Vanilla server has no loader row and no modpack row,
/// because an empty row reads as a value ChunkPilot failed to find.
/// </para>
/// <para>
/// Nothing here may claim work was done. No file was downloaded, no hash was checked, no runtime was
/// installed and no server was registered, so those appear only under "what would still need to
/// happen".
/// </para>
/// </remarks>
public static class CreationReviewBuilder
{
    /// <summary>The statement that must appear wherever the preview could be mistaken for the real thing.</summary>
    public const string PreviewNotice =
        "This is a design preview of the new Create Server experience. Nothing is downloaded, no files "
        + "are written, no Minecraft EULA is accepted and no server is created or registered.";

    public static CreationReviewSummary Build(
        CreationSelection selection,
        ResolvedCreationContext context,
        CreationValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(validation);

        var sections = new List<CreationReviewSection>();
        var intent = selection.Intent;

        var identity = new List<CreationReviewRow>
        {
            new("Server name", selection.ServerName.Trim(),
                IsUnknown: string.IsNullOrWhiteSpace(selection.ServerName), UnknownText: "Not set")
        };
        if (intent is { } chosen)
            identity.Add(new CreationReviewRow("What it is for", CreationIntentCatalog.For(chosen).Title));
        sections.Add(new CreationReviewSection("Your server", identity));

        var build = new List<CreationReviewRow>();
        if (intent == CreationIntent.Modpack && !string.IsNullOrEmpty(context.ProjectName))
            build.Add(new CreationReviewRow("Modpack", context.ProjectName));
        if (context.HasOption)
            build.Add(new CreationReviewRow(
                intent == CreationIntent.Modpack ? "Release" : "Chosen option", context.OptionTitle));
        if (!string.IsNullOrEmpty(context.MinecraftVersion))
            build.Add(new CreationReviewRow("Minecraft version", context.MinecraftVersion));
        if (!string.IsNullOrEmpty(context.Implementation))
            build.Add(new CreationReviewRow("Server software", context.Implementation));
        if (!string.IsNullOrEmpty(context.Loader))
            build.Add(new CreationReviewRow(
                "Mod loader",
                string.IsNullOrEmpty(context.LoaderVersion)
                    ? context.Loader
                    : $"{context.Loader} {context.LoaderVersion}"));
        if (build.Count > 0)
            sections.Add(new CreationReviewSection("What ChunkPilot would build", build));

        if (context.HasOption)
        {
            var conclusion = context.Compatibility.Conclusion;
            sections.Add(new CreationReviewSection(
                "Will it work",
                [new CreationReviewRow("Compatibility", CompatibilityConclusionPolicy.ShortLabel(conclusion))],
                [
                    new CreationReviewNote("What that means", CompatibilityConclusionPolicy.Explanation(conclusion)),
                    new CreationReviewNote(
                        "Players need",
                        string.IsNullOrEmpty(context.ClientRequirementText)
                            ? "Not established."
                            : context.ClientRequirementText)
                ]));
        }

        return new CreationReviewSummary
        {
            Sections = sections,
            EvidenceRows = BuildEvidenceRows(context),
            EvidenceNotes = BuildEvidenceNotes(context),
            Warnings = validation.Warnings.Select(issue => issue.Message).ToArray(),
            BlockingIssues = validation.BlockingIssues.Select(issue => issue.Message).ToArray(),
            UnresolvedRequirements = context.UnresolvedRequirements,
            PreviewNotice = PreviewNotice
        };
    }

    private static IReadOnlyList<CreationReviewRow> BuildEvidenceRows(ResolvedCreationContext context)
    {
        if (!context.HasOption)
            return [];

        var evidence = context.Compatibility;
        return
        [
            new CreationReviewRow("Provider data retrieved", "", IsUnknown: evidence.ProviderDataAsOf is null,
                UnknownText: "Nothing was retrieved"),
            new CreationReviewRow("File hash", evidence.HashValue,
                IsUnknown: string.IsNullOrEmpty(evidence.HashValue), UnknownText: "None supplied"),
            new CreationReviewRow("Java version needed",
                evidence.RequiredJavaMajor is { } major ? $"Java {major}" : "",
                IsUnknown: evidence.RequiredJavaMajor is null, UnknownText: "Not established"),
            new CreationReviewRow("Server package published", evidence.ServerPackAvailable ? "Yes" : "No")
        ];
    }

    private static IReadOnlyList<CreationReviewNote> BuildEvidenceNotes(ResolvedCreationContext context)
    {
        if (!context.HasOption)
            return [];

        var notes = new List<CreationReviewNote>();
        if (!string.IsNullOrEmpty(context.ProvenanceDetail))
            notes.Add(new CreationReviewNote("Where this came from", context.ProvenanceDetail));
        notes.AddRange(context.Compatibility.Assumptions.Select(assumption =>
            new CreationReviewNote("Assumption made", assumption)));
        notes.AddRange(context.Limitations.Select(limitation =>
            new CreationReviewNote("Limitation", limitation)));
        return notes;
    }
}
