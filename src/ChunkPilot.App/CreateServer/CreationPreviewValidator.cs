using ChunkPilot.Core;

namespace ChunkPilot.App.CreateServer;

/// <summary>
/// Turns a selection plus the option it resolved to into the complete set of validation findings.
/// </summary>
/// <remarks>
/// <para>
/// Pure and deterministic: same inputs, same findings, no clock, no filesystem, no provider. The
/// wizard's Back, Next and finish availability all read from one <see cref="CreationValidationResult"/>
/// rather than each recomputing their own idea of whether the state is usable.
/// </para>
/// <para>
/// A compatibility conclusion is never upgraded here. An inferred or unknown result produces a
/// warning and stays inferred or unknown; only <see cref="CompatibilityConclusionPolicy.IsBlocking"/>
/// states stop progression.
/// </para>
/// </remarks>
public static class CreationPreviewValidator
{
    public static CreationValidationResult Validate(
        CreationSelection selection,
        SyntheticPreviewOption? option)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var issues = new List<CreationValidationIssue>();

        if (selection.Intent is not { } intent)
        {
            issues.Add(new CreationValidationIssue(
                CreationIssueSeverity.Blocking,
                "Choose what you want to run before continuing.",
                CreationWizardStep.Intent,
                CreationNamePolicy.Fields.Intent));
            return new CreationValidationResult { Issues = issues };
        }

        issues.AddRange(CreationNamePolicy.Validate(selection.ServerName));

        if (intent == CreationIntent.Modpack && string.IsNullOrEmpty(selection.ProjectId))
            issues.Add(Blocking("Choose a modpack before continuing.", CreationNamePolicy.Fields.Project));

        if (option is null)
        {
            if (intent != CreationIntent.Modpack || !string.IsNullOrEmpty(selection.ProjectId))
                issues.Add(Blocking(MissingOptionMessage(intent), CreationNamePolicy.Fields.Option));
        }
        else
        {
            issues.AddRange(OptionIssues(option));
        }

        if (intent == CreationIntent.Advanced && !selection.AdvancedAcknowledged)
            issues.Add(Blocking(
                "Confirm that you understand ChunkPilot cannot verify a setup you assemble yourself.",
                CreationNamePolicy.Fields.Acknowledgement));

        return new CreationValidationResult { Issues = issues };
    }

    private static IEnumerable<CreationValidationIssue> OptionIssues(SyntheticPreviewOption option)
    {
        var conclusion = option.Evidence.Conclusion;

        if (!option.IsAvailable)
        {
            // The availability message is more specific than the conclusion label, so it is used
            // instead of it rather than as well as it.
            yield return Blocking(
                string.IsNullOrEmpty(option.AvailabilityDetail)
                    ? $"\"{option.Title}\" cannot be obtained at the moment, so it cannot be chosen."
                    : $"\"{option.Title}\" cannot be chosen. {option.AvailabilityDetail}",
                CreationNamePolicy.Fields.Option);
            yield break;
        }

        if (CompatibilityConclusionPolicy.IsBlocking(conclusion))
        {
            yield return Blocking(
                $"{CompatibilityConclusionPolicy.ShortLabel(conclusion)}. "
                + CompatibilityConclusionPolicy.Explanation(conclusion),
                CreationNamePolicy.Fields.Option);
            yield break;
        }

        if (CompatibilityConclusionPolicy.NeedsWarning(conclusion))
            yield return new CreationValidationIssue(
                CreationIssueSeverity.Warning,
                $"{CompatibilityConclusionPolicy.ShortLabel(conclusion)}. "
                + CompatibilityConclusionPolicy.Explanation(conclusion),
                CreationWizardStep.Setup,
                CreationNamePolicy.Fields.Option);
        else if (conclusion == CompatibilityConclusion.ProviderDeclaredCompatible)
            yield return new CreationValidationIssue(
                CreationIssueSeverity.Info,
                CompatibilityConclusionPolicy.Explanation(conclusion),
                CreationWizardStep.Setup,
                CreationNamePolicy.Fields.Option);

        foreach (var warning in option.Evidence.Warnings)
            yield return new CreationValidationIssue(
                CreationIssueSeverity.Warning,
                warning,
                CreationWizardStep.Setup,
                CreationNamePolicy.Fields.Option);
    }

    private static string MissingOptionMessage(CreationIntent intent) => intent switch
    {
        CreationIntent.Vanilla => "Choose a Minecraft version before continuing.",
        CreationIntent.Plugins => "Choose which plugin-capable server to use before continuing.",
        CreationIntent.Mods => "Choose a loader and Minecraft version before continuing.",
        CreationIntent.Modpack => "Choose a release of this modpack before continuing.",
        CreationIntent.Crossplay => "Choose the server this crossplay setup is built on before continuing.",
        _ => "Choose an option before continuing."
    };

    private static CreationValidationIssue Blocking(string message, string field) =>
        new(CreationIssueSeverity.Blocking, message, CreationWizardStep.Setup, field);
}
