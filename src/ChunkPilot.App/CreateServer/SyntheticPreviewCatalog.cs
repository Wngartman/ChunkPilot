using ChunkPilot.Core;

namespace ChunkPilot.App.CreateServer;

/// <summary>
/// One choice the preview can offer for an intent: a version, a build, or a modpack release.
/// </summary>
/// <remarks>
/// Every instance is invented, built into the binary, and reachable only from the Create Server v2
/// preview. Nothing here was retrieved from a provider, so no download URL, hash or retrieval
/// timestamp is populated - an empty hash means "none supplied", never "verified".
/// </remarks>
public sealed record SyntheticPreviewOption
{
    public string Id { get; init; } = "";
    public CreationIntent Intent { get; init; }

    /// <summary>Modpack releases only. Empty for every other intent.</summary>
    public string ProjectId { get; init; } = "";

    public string ProjectName { get; init; } = "";
    public string Title { get; init; } = "";

    /// <summary>
    /// The one short fact that distinguishes this row from its neighbours.
    /// </summary>
    /// <remarks>
    /// A list row cannot wrap, so it carries a fact rather than a sentence; the sentence is
    /// <see cref="Summary"/> and belongs in the details panel where it has room.
    /// </remarks>
    public string RowSubtitle { get; init; } = "";

    public string Summary { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public string Implementation { get; init; } = "";
    public string Loader { get; init; } = "";
    public string LoaderVersion { get; init; } = "";
    public bool IsAvailable { get; init; } = true;
    public string AvailabilityDetail { get; init; } = "";
    public string ClientRequirementText { get; init; } = "";
    public IReadOnlyList<string> Limitations { get; init; } = [];
    public IReadOnlyList<string> UnresolvedRequirements { get; init; } = [];
    public CompatibilityEvidence Evidence { get; init; } = new();

    /// <summary>Composed name so a list row reads as one statement to a screen reader.</summary>
    public string AutomationName =>
        $"{Title}. {CompatibilityConclusionPolicy.ShortLabel(Evidence.Conclusion)}.";

    /// <summary>Projects this option into the resolved-context shape the rest of the wizard reads.</summary>
    public ResolvedCreationContext ToContext() => new()
    {
        OptionTitle = Title,
        OptionSummary = Summary,
        MinecraftVersion = MinecraftVersion,
        Implementation = Implementation,
        Loader = Loader,
        LoaderVersion = LoaderVersion,
        ProjectName = ProjectName,
        IsAvailable = IsAvailable,
        AvailabilityDetail = AvailabilityDetail,
        Compatibility = Evidence,
        ClientRequirementText = ClientRequirementText,
        ProvenanceDetail = SyntheticPreviewCatalog.ProvenanceDetail,
        Limitations = Limitations,
        UnresolvedRequirements = UnresolvedRequirements
    };
}

/// <summary>An invented modpack project with one or more invented releases.</summary>
public sealed record SyntheticPreviewProject(
    string Id,
    string Name,
    string Author,
    string Summary,
    IReadOnlyList<SyntheticPreviewOption> Versions)
{
    /// <summary>The conclusion the project's best release reaches, for the list row's badge.</summary>
    public CompatibilityConclusion HeadlineConclusion =>
        Versions.Count == 0 ? CompatibilityConclusion.Unknown : Versions[0].Evidence.Conclusion;

    /// <summary>Short row fact. The full sentence lives in <see cref="Summary"/>.</summary>
    public string RowSubtitle =>
        Versions.Count == 1 ? "Example project, 1 release" : $"Example project, {Versions.Count} releases";

    public string AutomationName =>
        $"{Name} by {Author}. {CompatibilityConclusionPolicy.ShortLabel(HeadlineConclusion)}.";
}

/// <summary>
/// The deterministic, entirely invented option set behind the Create Server v2 preview.
/// </summary>
/// <remarks>
/// <para>
/// PREVIEW DATA ONLY. This type exists so the interaction architecture and visual experience can be
/// reviewed before the wizard is connected to ChunkPilot's real creation pipeline. It contacts
/// nothing, caches nothing, persists nothing, and is reachable only from
/// <see cref="CreateServerPreviewLauncher"/>. No product control opens that path.
/// </para>
/// <para>
/// The set is chosen to exercise every compatibility conclusion, including the ones that must block
/// progression, rather than to look like a plausible catalogue. Names are obviously invented so a
/// screenshot of the preview can never be mistaken for real provider results.
/// </para>
/// </remarks>
public static class SyntheticPreviewCatalog
{
    /// <summary>The provenance line attached to every option. Shown wherever evidence is shown.</summary>
    public const string ProvenanceDetail =
        "Sample data built into ChunkPilot for this preview. No provider was contacted, nothing was "
        + "downloaded, and no compatibility was actually tested.";

    private const string SampleSource = "ChunkPilot preview sample data";

    private static readonly SyntheticPreviewOption[] VanillaOptions =
    [
        new()
        {
            Id = "synthetic-vanilla-release",
            Intent = CreationIntent.Vanilla,
            Title = "Minecraft 1.21.4",
            RowSubtitle = "Stable release",
            Summary = "The current stable release. Recommended unless you need something specific.",
            MinecraftVersion = "1.21.4",
            Implementation = "Vanilla",
            ClientRequirementText = "Any player with the matching Minecraft version can join. Nothing to install.",
            UnresolvedRequirements =
            [
                "Download the official server file and check it against the published hash.",
                "Choose and install a matching Java runtime for this Minecraft version.",
                "Ask you to accept the Minecraft EULA before any files are written."
            ],
            Evidence = new CompatibilityEvidence
            {
                Conclusion = CompatibilityConclusion.VerifiedCompatible,
                MinecraftVersion = "1.21.4",
                RequiredJavaMajor = 21,
                ServerArtifactSource = SampleSource,
                ServerPackAvailable = true,
                ClientRequirement = ClientRequirement.None
            }
        },
        new()
        {
            Id = "synthetic-vanilla-snapshot",
            Intent = CreationIntent.Vanilla,
            Title = "Minecraft 25w05a (snapshot)",
            RowSubtitle = "In development",
            Summary = "An in-development build. Worlds made here may not open in a later release.",
            MinecraftVersion = "25w05a",
            Implementation = "Vanilla",
            ClientRequirementText = "Every player must switch their launcher to the same snapshot.",
            Limitations =
            [
                "Snapshots change without notice and are not supported by most plugins or mods.",
                "A world created on a snapshot can stop loading when the next snapshot arrives."
            ],
            UnresolvedRequirements =
            [
                "Download the official snapshot server file and check it against the published hash.",
                "Choose and install a matching Java runtime.",
                "Ask you to accept the Minecraft EULA before any files are written."
            ],
            Evidence = new CompatibilityEvidence
            {
                Conclusion = CompatibilityConclusion.Inferred,
                MinecraftVersion = "25w05a",
                RequiredJavaMajor = 21,
                ServerArtifactSource = SampleSource,
                ServerPackAvailable = true,
                ClientRequirement = ClientRequirement.MatchingPackRequired,
                Assumptions =
                [
                    "Java requirement taken from the release this snapshot follows, not from the snapshot itself."
                ],
                Warnings =
                [
                    "Snapshot behaviour is not verified. Treat anything you build here as temporary."
                ]
            }
        },
        new()
        {
            Id = "synthetic-vanilla-unavailable",
            Intent = CreationIntent.Vanilla,
            Title = "Minecraft 1.21.5",
            RowSubtitle = "Cannot be obtained",
            Summary = "Listed, but its files cannot be obtained at the moment.",
            MinecraftVersion = "1.21.5",
            Implementation = "Vanilla",
            IsAvailable = false,
            AvailabilityDetail = "The sample source for this option is marked unreachable, so it cannot be chosen.",
            ClientRequirementText = "Not established. The option cannot be resolved.",
            Evidence = new CompatibilityEvidence
            {
                Conclusion = CompatibilityConclusion.TemporarilyUnavailable,
                MinecraftVersion = "1.21.5",
                ServerArtifactSource = SampleSource,
                ClientRequirement = ClientRequirement.Unknown,
                Warnings = ["Nothing about this option has been confirmed, including whether it exists."]
            }
        }
    ];

    private static readonly SyntheticPreviewOption[] PluginOptions =
    [
        new()
        {
            Id = "synthetic-paper-1214",
            Intent = CreationIntent.Plugins,
            Title = "Paper 1.21.4",
            RowSubtitle = "Runs plugins",
            Summary = "The usual choice. Runs plugins and keeps ordinary clients working unchanged.",
            MinecraftVersion = "1.21.4",
            Implementation = "Paper",
            ClientRequirementText = "Any player with Minecraft 1.21.4 can join. Plugins run on the server only.",
            Limitations =
            [
                "Plugins are added after the server exists. This step only chooses the server that can run them."
            ],
            UnresolvedRequirements =
            [
                "Download the exact Paper build for this Minecraft version and check its hash.",
                "Choose and install a matching Java runtime.",
                "Ask you to accept the Minecraft EULA before any files are written."
            ],
            Evidence = new CompatibilityEvidence
            {
                Conclusion = CompatibilityConclusion.ProviderDeclaredCompatible,
                MinecraftVersion = "1.21.4",
                RequiredJavaMajor = 21,
                ServerArtifactSource = SampleSource,
                ServerPackAvailable = true,
                ClientRequirement = ClientRequirement.None,
                Assumptions = ["The build is taken to support this Minecraft version because it is published for it."]
            }
        },
        new()
        {
            Id = "synthetic-purpur-1214",
            Intent = CreationIntent.Plugins,
            Title = "Purpur 1.21.4",
            RowSubtitle = "Runs plugins, more settings",
            Summary = "Paper plus extra tuning options. Same plugins, more settings to read.",
            MinecraftVersion = "1.21.4",
            Implementation = "Purpur",
            ClientRequirementText = "Any player with Minecraft 1.21.4 can join. Plugins run on the server only.",
            Limitations = ["More configuration means more ways to change gameplay by accident."],
            UnresolvedRequirements =
            [
                "Download the exact Purpur build for this Minecraft version and check its hash.",
                "Choose and install a matching Java runtime.",
                "Ask you to accept the Minecraft EULA before any files are written."
            ],
            Evidence = new CompatibilityEvidence
            {
                Conclusion = CompatibilityConclusion.Inferred,
                MinecraftVersion = "1.21.4",
                RequiredJavaMajor = 21,
                ServerArtifactSource = SampleSource,
                ServerPackAvailable = true,
                ClientRequirement = ClientRequirement.None,
                Assumptions = ["Plugin behaviour assumed to match Paper because Purpur is built from it."],
                Warnings = ["Derived from the Paper relationship rather than from a direct check."]
            }
        }
    ];

    private static readonly SyntheticPreviewOption[] ModOptions =
    [
        new()
        {
            Id = "synthetic-fabric-1214",
            Intent = CreationIntent.Mods,
            Title = "Fabric on Minecraft 1.21.4",
            RowSubtitle = "Fabric 0.16.9",
            Summary = "Light and quick to update. The usual choice for current versions.",
            MinecraftVersion = "1.21.4",
            Implementation = "Fabric",
            Loader = "Fabric",
            LoaderVersion = "0.16.9",
            ClientRequirementText =
                "Every player needs Fabric and the same mods. A plain Minecraft client cannot join.",
            UnresolvedRequirements =
            [
                "Resolve the exact loader build and check the download against its hash.",
                "Choose and install a matching Java runtime.",
                "Ask you to accept the Minecraft EULA before any files are written."
            ],
            Evidence = new CompatibilityEvidence
            {
                Conclusion = CompatibilityConclusion.VerifiedCompatible,
                MinecraftVersion = "1.21.4",
                Loader = "Fabric",
                LoaderVersion = "0.16.9",
                RequiredJavaMajor = 21,
                ServerArtifactSource = SampleSource,
                ServerPackAvailable = true,
                ClientRequirement = ClientRequirement.MatchingPackRequired
            }
        },
        new()
        {
            Id = "synthetic-neoforge-1214",
            Intent = CreationIntent.Mods,
            Title = "NeoForge on Minecraft 1.21.4",
            RowSubtitle = "NeoForge 21.4.48-beta",
            Summary = "The larger mod ecosystem. Bigger packs, more moving parts.",
            MinecraftVersion = "1.21.4",
            Implementation = "NeoForge",
            Loader = "NeoForge",
            LoaderVersion = "21.4.48-beta",
            ClientRequirementText =
                "Every player needs NeoForge and the same mods. A plain Minecraft client cannot join.",
            Limitations = ["Larger mod sets take longer to start and use more memory."],
            UnresolvedRequirements =
            [
                "Resolve the exact loader build and run its installer in a staging directory.",
                "Choose and install a matching Java runtime.",
                "Ask you to accept the Minecraft EULA before any files are written."
            ],
            Evidence = new CompatibilityEvidence
            {
                Conclusion = CompatibilityConclusion.ProviderDeclaredCompatible,
                MinecraftVersion = "1.21.4",
                Loader = "NeoForge",
                LoaderVersion = "21.4.48-beta",
                RequiredJavaMajor = 21,
                ServerArtifactSource = SampleSource,
                ServerPackAvailable = true,
                ClientRequirement = ClientRequirement.MatchingPackRequired,
                Assumptions = ["Taken as compatible because the loader publishes a build for this version."]
            }
        },
        new()
        {
            Id = "synthetic-fabric-1710",
            Intent = CreationIntent.Mods,
            Title = "Fabric on Minecraft 1.7.10",
            RowSubtitle = "No loader build exists",
            Summary = "Kept in this list to show what a rejected combination looks like.",
            MinecraftVersion = "1.7.10",
            Implementation = "Fabric",
            Loader = "Fabric",
            ClientRequirementText = "Not established. This combination is not built.",
            Evidence = new CompatibilityEvidence
            {
                Conclusion = CompatibilityConclusion.VerifiedIncompatible,
                MinecraftVersion = "1.7.10",
                Loader = "Fabric",
                ServerArtifactSource = SampleSource,
                ClientRequirement = ClientRequirement.Unknown,
                Warnings = ["This loader has no build for this Minecraft version, so there is nothing to install."]
            }
        }
    ];

    private static readonly SyntheticPreviewOption[] CrossplayOptions =
    [
        new()
        {
            Id = "synthetic-crossplay-paper",
            Intent = CreationIntent.Crossplay,
            Title = "Paper 1.21.4 with a crossplay layer",
            RowSubtitle = "Paper base",
            Summary = "A Java server that also accepts Bedrock players. The usual crossplay setup.",
            MinecraftVersion = "1.21.4",
            Implementation = "Paper with Geyser and Floodgate",
            ClientRequirementText =
                "Java players join on the Java address. Bedrock players - phone, console and Windows edition - "
                + "join on a separate Bedrock address.",
            Limitations =
            [
                "Java and Bedrock use different addresses and different network protocols. They are never the same line.",
                "Some Java features have no Bedrock equivalent and behave differently for those players."
            ],
            UnresolvedRequirements =
            [
                "Download the Paper build and both crossplay components, and check each against its hash.",
                "Choose and install a matching Java runtime.",
                "Ask you to accept the Minecraft EULA before any files are written.",
                "Set up how people outside your home network reach the server, which is a separate step."
            ],
            Evidence = new CompatibilityEvidence
            {
                Conclusion = CompatibilityConclusion.ProviderDeclaredCompatible,
                MinecraftVersion = "1.21.4",
                RequiredJavaMajor = 21,
                ServerArtifactSource = SampleSource,
                ServerPackAvailable = true,
                ClientRequirement = ClientRequirement.None,
                Assumptions = ["Crossplay components are taken to support this Minecraft version because builds exist for it."],
                Warnings =
                [
                    "Whether players outside your home network can reach this server is a separate networking "
                    + "question. ChunkPilot never presents a local check as proof of public reachability."
                ]
            }
        },
        new()
        {
            Id = "synthetic-crossplay-purpur",
            Intent = CreationIntent.Crossplay,
            Title = "Purpur 1.21.4 with a crossplay layer",
            RowSubtitle = "Purpur base",
            Summary = "The same arrangement on Purpur, for people who want the extra tuning options.",
            MinecraftVersion = "1.21.4",
            Implementation = "Purpur with Geyser and Floodgate",
            ClientRequirementText =
                "Java players join on the Java address. Bedrock players join on a separate Bedrock address.",
            Limitations =
            [
                "Java and Bedrock use different addresses and different network protocols.",
                "Crossplay on Purpur is assumed from its Paper lineage rather than checked directly."
            ],
            UnresolvedRequirements =
            [
                "Download the Purpur build and both crossplay components, and check each against its hash.",
                "Choose and install a matching Java runtime.",
                "Ask you to accept the Minecraft EULA before any files are written.",
                "Set up how people outside your home network reach the server, which is a separate step."
            ],
            Evidence = new CompatibilityEvidence
            {
                Conclusion = CompatibilityConclusion.Inferred,
                MinecraftVersion = "1.21.4",
                RequiredJavaMajor = 21,
                ServerArtifactSource = SampleSource,
                ServerPackAvailable = true,
                ClientRequirement = ClientRequirement.None,
                Assumptions = ["Crossplay support inferred from Purpur being built from Paper."],
                Warnings =
                [
                    "Public reachability is a separate networking concern and is not established here."
                ]
            }
        }
    ];

    private static readonly SyntheticPreviewOption[] AdvancedOptions =
    [
        new()
        {
            Id = "synthetic-advanced-preview",
            Intent = CreationIntent.Advanced,
            Title = "Expert setup",
            RowSubtitle = "You choose everything",
            Summary = "You choose the server files, the Java runtime and the launch settings.",
            Implementation = "Chosen by you",
            ClientRequirementText = "Depends entirely on what you choose. ChunkPilot cannot state it in advance.",
            Limitations =
            [
                "The expert editors are not part of this preview. This step describes them and nothing more.",
                "Once you replace what ChunkPilot resolved, it can no longer confirm the combination works."
            ],
            UnresolvedRequirements =
            [
                "Collect the server files, runtime and launch settings you intend to use.",
                "Run the same staging, verification and activation steps every other path uses.",
                "Ask you to accept the Minecraft EULA before any files are written."
            ],
            Evidence = new CompatibilityEvidence
            {
                Conclusion = CompatibilityConclusion.Unknown,
                ServerArtifactSource = SampleSource,
                ClientRequirement = ClientRequirement.Unknown,
                Assumptions = ["Nothing is assumed. The choices that would decide this have not been made."],
                Warnings =
                [
                    "Custom choices reduce what ChunkPilot can check for you. Compatibility becomes yours to verify."
                ]
            }
        }
    ];

    private static readonly SyntheticPreviewProject[] Projects =
    [
        new("synthetic-pack-skyward",
            "Sample Pack: Skyward Depths",
            "ChunkPilot preview data",
            "An invented exploration pack used to show a project that publishes a dedicated server pack.",
            [
                ModpackRelease("synthetic-pack-skyward-14", "synthetic-pack-skyward", "Sample Pack: Skyward Depths",
                    "1.4.0", "1.21.4", "Fabric", CompatibilityConclusion.ProviderDeclaredCompatible,
                    serverPack: true),
                ModpackRelease("synthetic-pack-skyward-13", "synthetic-pack-skyward", "Sample Pack: Skyward Depths",
                    "1.3.2", "1.21.1", "Fabric", CompatibilityConclusion.ProviderDeclaredCompatible,
                    serverPack: true)
            ]),

        new("synthetic-pack-lantern",
            "Sample Pack: Lantern Fields",
            "ChunkPilot preview data",
            "An invented pack that publishes a client pack only, used to show a state that must block creation.",
            [
                ModpackRelease("synthetic-pack-lantern-20", "synthetic-pack-lantern", "Sample Pack: Lantern Fields",
                    "2.0.0", "1.21.4", "NeoForge", CompatibilityConclusion.NoServerPackAvailable,
                    serverPack: false)
            ]),

        new("synthetic-pack-driftwood",
            "Sample Pack: Driftwood",
            "ChunkPilot preview data",
            "An invented pack whose server suitability was never established, used to show an honest unknown.",
            [
                ModpackRelease("synthetic-pack-driftwood-07", "synthetic-pack-driftwood", "Sample Pack: Driftwood",
                    "0.7.1", "1.20.6", "Fabric", CompatibilityConclusion.Unknown, serverPack: false)
            ]),

        new("synthetic-pack-vault",
            "Sample Pack: Ironvault",
            "ChunkPilot preview data",
            "An invented pack from a provider that needs an account key, used to show the authentication state.",
            [
                ModpackRelease("synthetic-pack-vault-31", "synthetic-pack-vault", "Sample Pack: Ironvault",
                    "3.1.0", "1.21.1", "NeoForge", CompatibilityConclusion.RequiresAuthentication,
                    serverPack: false)
            ]),

        new("synthetic-pack-expedition",
            "Sample Pack: Expedition",
            "ChunkPilot preview data",
            "An invented pack whose server files are distributed by hand, used to show a manual-artifact state.",
            [
                ModpackRelease("synthetic-pack-expedition-15", "synthetic-pack-expedition", "Sample Pack: Expedition",
                    "1.5.0", "1.20.1", "Forge", CompatibilityConclusion.RequiresUserSuppliedArtifact,
                    serverPack: false)
            ]),

        new("synthetic-pack-relic",
            "Sample Pack: Relic Age",
            "ChunkPilot preview data",
            "An invented pack from a source ChunkPilot has no supported way to read, used to show that state.",
            [
                ModpackRelease("synthetic-pack-relic-42", "synthetic-pack-relic", "Sample Pack: Relic Age",
                    "4.2.0", "1.12.2", "Forge", CompatibilityConclusion.UnsupportedByChunkPilot,
                    serverPack: false)
            ])
    ];

    /// <summary>The invented modpack projects, in a stable order.</summary>
    public static IReadOnlyList<SyntheticPreviewProject> ModpackProjects => Projects;

    /// <summary>Every option the preview knows about, across all intents.</summary>
    public static IReadOnlyList<SyntheticPreviewOption> AllOptions { get; } =
        VanillaOptions
            .Concat(PluginOptions)
            .Concat(ModOptions)
            .Concat(CrossplayOptions)
            .Concat(AdvancedOptions)
            .Concat(Projects.SelectMany(project => project.Versions))
            .ToArray();

    /// <summary>The options offered directly on an intent's setup step.</summary>
    /// <remarks>Modpack returns nothing: its releases are reached through a project first.</remarks>
    public static IReadOnlyList<SyntheticPreviewOption> OptionsFor(CreationIntent intent) => intent switch
    {
        CreationIntent.Vanilla => VanillaOptions,
        CreationIntent.Plugins => PluginOptions,
        CreationIntent.Mods => ModOptions,
        CreationIntent.Crossplay => CrossplayOptions,
        CreationIntent.Advanced => AdvancedOptions,
        _ => []
    };

    /// <summary>The releases of one invented project, or nothing when the id is unknown.</summary>
    public static IReadOnlyList<SyntheticPreviewOption> VersionsForProject(string projectId) =>
        Projects.FirstOrDefault(project => project.Id == projectId)?.Versions ?? [];

    /// <summary>Finds one option by identifier, or null.</summary>
    public static SyntheticPreviewOption? Find(string optionId) =>
        string.IsNullOrEmpty(optionId)
            ? null
            : AllOptions.FirstOrDefault(option => option.Id == optionId);

    /// <summary>Finds one project by identifier, or null.</summary>
    public static SyntheticPreviewProject? FindProject(string projectId) =>
        string.IsNullOrEmpty(projectId)
            ? null
            : Projects.FirstOrDefault(project => project.Id == projectId);

    private static SyntheticPreviewOption ModpackRelease(
        string id,
        string projectId,
        string projectName,
        string releaseName,
        string minecraftVersion,
        string loader,
        CompatibilityConclusion conclusion,
        bool serverPack)
    {
        var clientRequirement = conclusion switch
        {
            CompatibilityConclusion.ProviderDeclaredCompatible => ClientRequirement.MatchingPackRequired,
            CompatibilityConclusion.NoServerPackAvailable => ClientRequirement.MatchingPackRequired,
            _ => ClientRequirement.Unknown
        };

        var clientText = conclusion switch
        {
            CompatibilityConclusion.ProviderDeclaredCompatible =>
                "Every player installs the same pack release in their launcher. A plain Minecraft client cannot join.",
            CompatibilityConclusion.NoServerPackAvailable =>
                "Players would install the pack, but there is no server side to join.",
            _ => "Not established. This release cannot be resolved."
        };

        var warnings = conclusion switch
        {
            CompatibilityConclusion.NoServerPackAvailable =>
                new[]
                {
                    "This release publishes a client pack only. ChunkPilot will not attempt to convert it into a server."
                },
            CompatibilityConclusion.Unknown =>
                ["Nothing states whether this release works as a dedicated server."],
            CompatibilityConclusion.RequiresAuthentication =>
                ["The provider will not return files for this release without an account key you have not supplied."],
            CompatibilityConclusion.RequiresUserSuppliedArtifact =>
                ["The publisher distributes server files by hand, so ChunkPilot cannot fetch them for you."],
            CompatibilityConclusion.UnsupportedByChunkPilot =>
                ["ChunkPilot has no documented, supported way to read this source and will not guess at one."],
            _ => []
        };

        return new SyntheticPreviewOption
        {
            Id = id,
            Intent = CreationIntent.Modpack,
            ProjectId = projectId,
            ProjectName = projectName,
            Title = $"{projectName} {releaseName}",
            RowSubtitle = $"Minecraft {minecraftVersion} on {loader}",
            Summary = $"Release {releaseName} for Minecraft {minecraftVersion} on {loader}.",
            MinecraftVersion = minecraftVersion,
            Implementation = loader,
            Loader = loader,
            ClientRequirementText = clientText,
            Limitations = serverPack
                ? ["Everyone who joins has to install the same pack release themselves."]
                : ["This release cannot be installed as a server, so nothing further applies."],
            UnresolvedRequirements = serverPack
                ?
                [
                    "Download the published server pack and check it against the provider's hash.",
                    "Choose and install a Java runtime the pack accepts.",
                    "Ask you to accept the Minecraft EULA before any files are written."
                ]
                : [],
            Evidence = new CompatibilityEvidence
            {
                Conclusion = conclusion,
                MinecraftVersion = minecraftVersion,
                Loader = loader,
                ServerArtifactSource = SampleSource,
                ServerPackAvailable = serverPack,
                ClientRequirement = clientRequirement,
                Assumptions = serverPack
                    ? ["Taken as installable because the sample release is marked as publishing a server pack."]
                    : [],
                Warnings = warnings
            }
        };
    }
}
