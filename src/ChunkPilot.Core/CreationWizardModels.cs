namespace ChunkPilot.Core;

/// <summary>
/// What the user said they want to run. The primary concept of Create Server v2.
/// </summary>
/// <remarks>
/// Deliberately distinct from <see cref="InstallSourceType"/> (an implementation detail) and from
/// <see cref="QuickStartKind"/> (a domain-default bundle). A beginner picks an intent; ChunkPilot
/// resolves the provider, loader, runtime and launch details on their behalf.
/// </remarks>
public enum CreationIntent
{
    Vanilla,
    Plugins,
    Mods,
    Modpack,
    Crossplay,
    Advanced
}

/// <summary>
/// How much ChunkPilot actually knows about whether a chosen option will work.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Unknown"/> is deliberately the zero value: an unset conclusion must read as "no claim
/// made", never as healthy. Nothing may promote a provider assertion or a heuristic match to
/// <see cref="VerifiedCompatible"/>; that value is reserved for something ChunkPilot's own code
/// checked directly.
/// </para>
/// </remarks>
public enum CompatibilityConclusion
{
    /// <summary>No claim. Nothing established this either way.</summary>
    Unknown = 0,

    /// <summary>ChunkPilot checked this itself against a known-good plan.</summary>
    VerifiedCompatible,

    /// <summary>The provider asserts it works. ChunkPilot did not independently confirm it.</summary>
    ProviderDeclaredCompatible,

    /// <summary>Derived from version/loader matching rules rather than from a direct check.</summary>
    Inferred,

    /// <summary>Established as not working. Progression is blocked.</summary>
    VerifiedIncompatible,

    /// <summary>The source exists but could not be reached or is withdrawn for now.</summary>
    TemporarilyUnavailable,

    /// <summary>ChunkPilot has no supported, documented way to install this.</summary>
    UnsupportedByChunkPilot,

    /// <summary>A credential the user has not supplied is required before this can be resolved.</summary>
    RequiresAuthentication,

    /// <summary>The user would have to supply server files the provider does not publish.</summary>
    RequiresUserSuppliedArtifact,

    /// <summary>The project publishes a client pack only; there is no dedicated server package.</summary>
    NoServerPackAvailable
}

/// <summary>
/// Everything known about one compatibility conclusion, including how it was reached.
/// </summary>
/// <remarks>
/// The evidence travels with the conclusion so a review screen can show the reasoning rather than a
/// bare verdict. Fields ChunkPilot has not established stay empty and render as unknown; an empty
/// <see cref="HashValue"/> means no hash was supplied, never "the hash matched".
/// </remarks>
public sealed record CompatibilityEvidence
{
    public CompatibilityConclusion Conclusion { get; init; } = CompatibilityConclusion.Unknown;
    public string MinecraftVersion { get; init; } = "";
    public string Loader { get; init; } = "";
    public string LoaderVersion { get; init; } = "";
    public int? RequiredJavaMajor { get; init; }

    /// <summary>Where the conclusion came from, in words the user can check.</summary>
    public string ServerArtifactSource { get; init; } = "";

    public bool ServerPackAvailable { get; init; }
    public ClientRequirement ClientRequirement { get; init; } = ClientRequirement.Unknown;

    /// <summary>When the provider data behind this was retrieved. Null when nothing was retrieved.</summary>
    public DateTimeOffset? ProviderDataAsOf { get; init; }

    public string HashAlgorithm { get; init; } = "";
    public string HashValue { get; init; } = "";

    /// <summary>What had to be assumed to reach the conclusion.</summary>
    public IReadOnlyList<string> Assumptions { get; init; } = [];

    /// <summary>Risks the user should read before continuing.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Plain-language wording and blocking rules for each compatibility conclusion.</summary>
/// <remarks>
/// Wording lives here rather than in XAML so the same sentence appears on the option row, in the
/// details panel and on the review screen, and so a test can assert that every state carries text.
/// </remarks>
public static class CompatibilityConclusionPolicy
{
    /// <summary>
    /// The shortest honest wording, for a badge in a dense row.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ShortLabel"/> because a badge that trims mid-word tells the reader
    /// nothing, and a compatibility state is exactly the thing that must never be half-read.
    /// </remarks>
    public static string BadgeLabel(CompatibilityConclusion conclusion) => conclusion switch
    {
        CompatibilityConclusion.VerifiedCompatible => "Verified",
        CompatibilityConclusion.ProviderDeclaredCompatible => "Provider says yes",
        CompatibilityConclusion.Inferred => "Likely",
        CompatibilityConclusion.VerifiedIncompatible => "Will not work",
        CompatibilityConclusion.TemporarilyUnavailable => "Unavailable",
        CompatibilityConclusion.UnsupportedByChunkPilot => "Not supported",
        CompatibilityConclusion.RequiresAuthentication => "Needs a key",
        CompatibilityConclusion.RequiresUserSuppliedArtifact => "Needs your files",
        CompatibilityConclusion.NoServerPackAvailable => "No server pack",
        _ => "Unknown"
    };

    /// <summary>Fuller label for a review line or a details panel. Always text; never colour alone.</summary>
    public static string ShortLabel(CompatibilityConclusion conclusion) => conclusion switch
    {
        CompatibilityConclusion.VerifiedCompatible => "Verified compatible",
        CompatibilityConclusion.ProviderDeclaredCompatible => "Provider states compatible",
        CompatibilityConclusion.Inferred => "Likely compatible",
        CompatibilityConclusion.VerifiedIncompatible => "Not compatible",
        CompatibilityConclusion.TemporarilyUnavailable => "Temporarily unavailable",
        CompatibilityConclusion.UnsupportedByChunkPilot => "Not supported by ChunkPilot",
        CompatibilityConclusion.RequiresAuthentication => "Needs a provider account key",
        CompatibilityConclusion.RequiresUserSuppliedArtifact => "Needs files you supply",
        CompatibilityConclusion.NoServerPackAvailable => "No server pack",
        _ => "Unknown"
    };

    /// <summary>One sentence explaining what the conclusion means for this user.</summary>
    public static string Explanation(CompatibilityConclusion conclusion) => conclusion switch
    {
        CompatibilityConclusion.VerifiedCompatible =>
            "ChunkPilot checked this combination itself and it matched a known-good install plan.",
        CompatibilityConclusion.ProviderDeclaredCompatible =>
            "The provider states this works. ChunkPilot has not started it to confirm that.",
        CompatibilityConclusion.Inferred =>
            "ChunkPilot worked this out from version and loader rules rather than from a direct check.",
        CompatibilityConclusion.VerifiedIncompatible =>
            "This combination is known not to work, so ChunkPilot will not build it.",
        CompatibilityConclusion.TemporarilyUnavailable =>
            "This option exists but cannot be obtained right now, so it cannot be chosen.",
        CompatibilityConclusion.UnsupportedByChunkPilot =>
            "ChunkPilot has no supported way to install this and will not guess at one.",
        CompatibilityConclusion.RequiresAuthentication =>
            "This provider needs an account key before its files can be read.",
        CompatibilityConclusion.RequiresUserSuppliedArtifact =>
            "The provider does not publish server files for this, so you would have to supply them.",
        CompatibilityConclusion.NoServerPackAvailable =>
            "This project publishes a client pack only. It cannot be turned into a dedicated server.",
        _ => "ChunkPilot has not established whether this works."
    };

    /// <summary>True when the conclusion must stop the user from continuing.</summary>
    public static bool IsBlocking(CompatibilityConclusion conclusion) => conclusion
        is CompatibilityConclusion.VerifiedIncompatible
        or CompatibilityConclusion.TemporarilyUnavailable
        or CompatibilityConclusion.UnsupportedByChunkPilot
        or CompatibilityConclusion.RequiresAuthentication
        or CompatibilityConclusion.RequiresUserSuppliedArtifact
        or CompatibilityConclusion.NoServerPackAvailable;

    /// <summary>True when the conclusion is usable but must not be presented as confirmed.</summary>
    public static bool NeedsWarning(CompatibilityConclusion conclusion) => conclusion
        is CompatibilityConclusion.Inferred or CompatibilityConclusion.Unknown;
}

/// <summary>The wizard's steps, in order.</summary>
public enum CreationWizardStep
{
    Intent,
    Setup,
    Review,

    /// <summary>
    /// A real creation is running. Reached only by a wizard that can actually create something.
    /// </summary>
    /// <remarks>
    /// The synthetic preview never enters this step: it walks Review straight to
    /// <see cref="Completion"/>, because there is nothing for it to wait on.
    /// </remarks>
    Creating,

    Completion
}

/// <summary>
/// What the user has decided so far. Immutable; the wizard replaces it rather than mutating it.
/// </summary>
/// <remarks>
/// Only the user's own answers live here. Anything ChunkPilot worked out on their behalf - version
/// strings, loader versions, compatibility - lives in <see cref="ResolvedCreationContext"/>, so
/// going back a step restores exactly what was typed and nothing that was inferred.
/// </remarks>
public sealed record CreationSelection
{
    public CreationIntent? Intent { get; init; }
    public string ServerName { get; init; } = "";

    /// <summary>Identifier of the chosen version, build or modpack release.</summary>
    public string OptionId { get; init; } = "";

    /// <summary>Identifier of the chosen modpack project. Empty for every other intent.</summary>
    public string ProjectId { get; init; } = "";

    /// <summary>Set only when the user acknowledged the Advanced-path verification limits.</summary>
    public bool AdvancedAcknowledged { get; init; }

    /// <summary>
    /// Drops the answers a new intent invalidates and keeps the ones it does not.
    /// </summary>
    /// <remarks>
    /// The server name is intent-independent and survives; a version, project or acknowledgement
    /// chosen for a different intent does not, because carrying it forward would silently attach a
    /// Fabric build to a Bedrock-crossplay server.
    /// </remarks>
    public CreationSelection WithIntent(CreationIntent intent) =>
        Intent == intent
            ? this
            : new CreationSelection
            {
                Intent = intent,
                ServerName = ServerName,
                OptionId = "",
                ProjectId = "",
                AdvancedAcknowledged = false
            };
}

/// <summary>
/// What ChunkPilot resolved for the current selection, kept separate from what the user typed.
/// </summary>
public sealed record ResolvedCreationContext
{
    public string OptionTitle { get; init; } = "";
    public string OptionSummary { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";

    /// <summary>The server implementation, for example Vanilla, Paper or Fabric.</summary>
    public string Implementation { get; init; } = "";

    public string Loader { get; init; } = "";
    public string LoaderVersion { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public bool IsAvailable { get; init; } = true;

    /// <summary>Why the option is unavailable, when it is. Empty otherwise.</summary>
    public string AvailabilityDetail { get; init; } = "";

    public CompatibilityEvidence Compatibility { get; init; } = new();

    /// <summary>What the player needs on their own machine to join.</summary>
    public string ClientRequirementText { get; init; } = "";

    /// <summary>Where this information came from. Never presented as live provider evidence.</summary>
    public string ProvenanceDetail { get; init; } = "";

    /// <summary>What this option does not do, stated plainly.</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];

    /// <summary>What a real creation run would still have to resolve before installing.</summary>
    public IReadOnlyList<string> UnresolvedRequirements { get; init; } = [];

    public bool HasOption => !string.IsNullOrEmpty(OptionTitle);
}

public enum CreationIssueSeverity
{
    /// <summary>Purely informational. Never stops progression.</summary>
    Info,

    /// <summary>A real risk the user should read. Does not stop progression.</summary>
    Warning,

    /// <summary>Stops progression until it is resolved.</summary>
    Blocking
}

/// <summary>One validation finding, tied to the step and field it belongs to.</summary>
/// <param name="Severity">How much the finding matters.</param>
/// <param name="Message">Plain language, addressed to the user.</param>
/// <param name="Step">The step whose Next button this finding governs.</param>
/// <param name="Field">The field this finding is about, so a view can place it beside its control.</param>
public sealed record CreationValidationIssue(
    CreationIssueSeverity Severity,
    string Message,
    CreationWizardStep Step,
    string Field = "");

/// <summary>The complete validation state for one selection.</summary>
public sealed record CreationValidationResult
{
    public IReadOnlyList<CreationValidationIssue> Issues { get; init; } = [];

    public IReadOnlyList<CreationValidationIssue> BlockingIssues =>
        Issues.Where(issue => issue.Severity == CreationIssueSeverity.Blocking).ToArray();

    public IReadOnlyList<CreationValidationIssue> Warnings =>
        Issues.Where(issue => issue.Severity == CreationIssueSeverity.Warning).ToArray();

    public IReadOnlyList<CreationValidationIssue> Notices =>
        Issues.Where(issue => issue.Severity == CreationIssueSeverity.Info).ToArray();

    public bool HasBlockingIssues => Issues.Any(issue => issue.Severity == CreationIssueSeverity.Blocking);

    /// <summary>True when the user may move forward from <paramref name="step"/>.</summary>
    public bool CanContinueFrom(CreationWizardStep step) => step switch
    {
        CreationWizardStep.Review or CreationWizardStep.Completion => !HasBlockingIssues,
        _ => !Issues.Any(issue =>
            issue.Severity == CreationIssueSeverity.Blocking && issue.Step == step)
    };

    /// <summary>True when the final preview action may be offered.</summary>
    public bool CanFinish => !HasBlockingIssues;

    /// <summary>The blocking findings for one field, for an inline message beside its control.</summary>
    public IReadOnlyList<CreationValidationIssue> For(string field) =>
        Issues.Where(issue => string.Equals(issue.Field, field, StringComparison.Ordinal)).ToArray();
}

/// <summary>
/// Deterministic rules for a server name, expressed without touching the filesystem.
/// </summary>
/// <remarks>
/// <para>
/// This validates; it does not sanitise. <c>ManagedServerInstaller.MakeSafeInstanceName</c> remains
/// the single place a name is turned into a folder, and it stays untouched. Duplicating its
/// rewriting behaviour here would let the wizard promise a name the installer would then quietly
/// change, which is exactly the silent rename the data-safety rules prohibit.
/// </para>
/// <para>
/// The 64-character ceiling matches that installer's existing cap, so a name accepted here cannot be
/// truncated later.
/// </para>
/// </remarks>
public static class CreationNamePolicy
{
    /// <summary>Longest accepted name, matching the managed instance-folder cap.</summary>
    public const int MaximumLength = 64;

    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    /// <summary>Validates a raw name exactly as typed.</summary>
    public static IReadOnlyList<CreationValidationIssue> Validate(string? name)
    {
        var issues = new List<CreationValidationIssue>();
        var raw = name ?? "";
        var trimmed = raw.Trim();

        if (trimmed.Length == 0)
        {
            issues.Add(Blocking(raw.Length == 0
                ? "Enter a name for this server."
                : "A name made only of spaces is not a name. Type something you will recognise later."));
            return issues;
        }

        if (!string.Equals(raw, trimmed, StringComparison.Ordinal))
            issues.Add(new CreationValidationIssue(
                CreationIssueSeverity.Info,
                "Spaces at the start and end of the name are removed.",
                CreationWizardStep.Setup,
                Fields.ServerName));

        if (trimmed.Length > MaximumLength)
            issues.Add(Blocking(
                $"This name is {trimmed.Length} characters. Use {MaximumLength} or fewer so the server folder keeps the same name."));

        var invalid = trimmed
            .Where(character => Path.GetInvalidFileNameChars().Contains(character))
            .Distinct()
            .ToArray();
        if (invalid.Length > 0)
            issues.Add(Blocking(
                "Windows folder names cannot contain " + Describe(invalid) + ". Remove them from the name."));

        if (trimmed.EndsWith('.'))
            issues.Add(Blocking("Windows folder names cannot end with a full stop. Remove the one at the end."));

        var deviceName = trimmed.Split('.')[0].Trim();
        if (ReservedDeviceNames.Contains(deviceName, StringComparer.OrdinalIgnoreCase))
            issues.Add(Blocking(
                $"\"{deviceName}\" is a name Windows reserves for hardware, so a folder cannot use it. Choose another name."));

        return issues;
    }

    private static CreationValidationIssue Blocking(string message) =>
        new(CreationIssueSeverity.Blocking, message, CreationWizardStep.Setup, Fields.ServerName);

    private static string Describe(IReadOnlyList<char> characters)
    {
        var printable = characters
            .Where(character => !char.IsControl(character))
            .Select(character => $"\"{character}\"")
            .ToArray();
        if (printable.Length == 0)
            return "control characters";
        var listed = string.Join(", ", printable);
        return printable.Length == characters.Count ? listed : listed + " or control characters";
    }

    /// <summary>Field identifiers shared by the validator and the views that render its findings.</summary>
    public static class Fields
    {
        public const string Intent = "Intent";
        public const string ServerName = "ServerName";
        public const string Option = "Option";
        public const string Project = "Project";
        public const string Acknowledgement = "Acknowledgement";
    }
}

/// <summary>One label-and-value line on the review screen.</summary>
/// <param name="Label">What the value describes.</param>
/// <param name="Value">The value. Ignored when <paramref name="IsUnknown"/> is true.</param>
/// <param name="IsUnknown">True when ChunkPilot has not established a value.</param>
/// <param name="UnknownText">Wording for the unknown case.</param>
public sealed record CreationReviewRow(
    string Label,
    string Value,
    bool IsUnknown = false,
    string UnknownText = "Unknown",
    bool IsCopyable = false);

/// <summary>
/// A sentence on the review screen, introduced by what it is about.
/// </summary>
/// <remarks>
/// Prose does not belong in a label-and-value row: a row trims, and a trimmed compatibility
/// explanation is worse than none. Notes wrap.
/// </remarks>
public sealed record CreationReviewNote(string Label, string Text);

/// <summary>A titled group of review lines. Sections with nothing to say are never emitted.</summary>
public sealed record CreationReviewSection(
    string Title,
    IReadOnlyList<CreationReviewRow> Rows,
    IReadOnlyList<CreationReviewNote>? Notes = null)
{
    /// <summary>Wrapped explanations shown under the rows.</summary>
    public IReadOnlyList<CreationReviewNote> Notes { get; init; } = Notes ?? [];
}

/// <summary>Everything the review screen renders, built from the current state and nothing else.</summary>
public sealed record CreationReviewSummary
{
    public IReadOnlyList<CreationReviewSection> Sections { get; init; } = [];

    /// <summary>Technical provenance, kept behind progressive disclosure.</summary>
    public IReadOnlyList<CreationReviewRow> EvidenceRows { get; init; } = [];

    /// <summary>Provenance and assumptions, in sentences rather than trimmed rows.</summary>
    public IReadOnlyList<CreationReviewNote> EvidenceNotes { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> BlockingIssues { get; init; } = [];

    /// <summary>What a real creation run would still need to resolve. Never presented as done.</summary>
    public IReadOnlyList<string> UnresolvedRequirements { get; init; } = [];

    /// <summary>The unmissable statement that nothing is being installed.</summary>
    public string PreviewNotice { get; init; } = "";
}
