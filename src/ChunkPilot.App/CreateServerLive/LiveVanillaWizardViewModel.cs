using System.Collections.ObjectModel;
using System.Globalization;
using ChunkPilot.App.CreateServer;
using ChunkPilot.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChunkPilot.App.CreateServerLive;

/// <summary>How the live version list stands right now.</summary>
/// <remarks>
/// A version list that could not be refreshed is not an empty version list, and neither is one the
/// provider could not be reached for. Each value is a different sentence to the user and a different
/// set of available actions, which is why they are separate states rather than one "error".
/// </remarks>
public enum LiveCatalogState
{
    /// <summary>Nothing has been asked for yet.</summary>
    Idle,

    /// <summary>A request is in flight.</summary>
    Loading,

    /// <summary>Read from Mojang just now.</summary>
    Available,

    /// <summary>Served from ChunkPilot's saved copy, which is still within its lifetime.</summary>
    Cached,

    /// <summary>A refresh failed and the saved copy is older than ChunkPilot would like.</summary>
    StaleCache,

    /// <summary>Mojang could not be reached and there is no saved copy to fall back to.</summary>
    NoUsableMetadata,

    /// <summary>The request itself failed, for example because the Agent could not be reached.</summary>
    RequestFailed
}

/// <summary>One explicit, non-consenting creation preference shown in the networking chooser.</summary>
public sealed record VanillaNetworkingOption(
    VanillaNetworkingPreference Preference,
    string Title,
    string Description,
    bool IsRecommended);

/// <summary>One intent card as the live wizard presents it, with its live availability.</summary>
/// <param name="Card">The shared product copy for this intent.</param>
/// <param name="IsLive">True only for the intent this development build can actually create.</param>
/// <param name="Availability">What this build can do with the intent, stated plainly.</param>
public sealed record LiveIntentOption(CreationIntentCard Card, bool IsLive, string Availability)
{
    public CreationIntent Intent => Card.Intent;

    public string Title => Card.Title;

    public string Description => Card.Description;

    public DesignSystem.AppIconKind Icon => Card.Icon;

    /// <summary>Composed so a screen reader hears the availability, not only the name.</summary>
    public string AutomationName => $"{Card.Title}. {Card.Description} {Availability}";
}

/// <summary>
/// The live Vanilla creation wizard: real versions, one real operation, real outcomes.
/// </summary>
/// <remarks>
/// <para>
/// This is the authoritative beginner Vanilla workflow. Product Create server actions request it
/// semantically from the shell; <see cref="CreateServerLiveLauncher.LiveVanillaSwitch"/> remains a
/// development shortcut into the same composition.
/// </para>
/// <para>
/// The split is the same one the synthetic preview established and is the reason this is not one
/// oversized view model: what the user chose, what the Agent resolved, what validation concluded and
/// what the operation is doing are four separate pieces of state that change for four different
/// reasons. What is new here is that the last of them is owned by another process.
/// </para>
/// <para>
/// This type holds no provider client, no <c>HttpClient</c>, no installer and no store. Its entire
/// contact with the outside world is <see cref="IVanillaCreationGateway"/>, whose every method is a
/// single named-pipe request. It never writes a file, never downloads anything and never registers
/// anything: it submits one plan and then watches.
/// </para>
/// </remarks>
public sealed partial class LiveVanillaWizardViewModel : ObservableObject, IDisposable
{
    private static readonly CreationWizardStep[] Sequence =
    [
        CreationWizardStep.Intent,
        CreationWizardStep.Setup,
        CreationWizardStep.Review,
        CreationWizardStep.Creating,
        CreationWizardStep.Completion
    ];

    private readonly IVanillaCreationGateway gateway;
    private readonly ISafeLinkOpener links;
    private readonly ICreatedServerNavigator? navigator;
    private readonly IServerLocationChooser? locationChooser;
    private readonly TimeSpan pollInterval;
    private readonly CancellationTokenSource lifetime = new();

    private VanillaVersionCatalog? catalog;
    private Guid? operationId;
    private bool submitted;
    private ServerDefinition? createdServer;

    /// <summary>Raised when the wizard wants focus moved to the first control of a step.</summary>
    public event EventHandler<CreationWizardStep>? FocusRequested;

    /// <summary>Raised when the window should close. The window owns the actual close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised with text the window should place on the clipboard.</summary>
    public event EventHandler<string>? CopyRequested;

    public LiveVanillaWizardViewModel(
        IVanillaCreationGateway gateway,
        ISafeLinkOpener? links = null,
        ICreatedServerNavigator? navigator = null,
        TimeSpan? pollInterval = null,
        IServerLocationChooser? locationChooser = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        this.gateway = gateway;
        this.links = links ?? new ShellLinkOpener();
        this.navigator = navigator;
        this.locationChooser = locationChooser;
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(400);

        const string unavailable = "Not in this build";
        Intents =
        [
            new(CreationIntentCatalog.For(CreationIntent.Vanilla), true, "Available"),
            new(CreationIntentCatalog.For(CreationIntent.Plugins), false, unavailable),
            new(CreationIntentCatalog.For(CreationIntent.Mods), false, unavailable),
            new(CreationIntentCatalog.For(CreationIntent.Modpack), false, unavailable),
            new(CreationIntentCatalog.For(CreationIntent.Crossplay), false, unavailable),
            new(CreationIntentCatalog.For(CreationIntent.Advanced), false, unavailable)
        ];
        NetworkingOptions =
        [
            new(VanillaNetworkingPreference.FriendsOverInternet, "Friends over the internet",
                "Recommended. Create the server now, then ChunkPilot will guide you through connectivity setup.", true),
            new(VanillaNetworkingPreference.ThisNetworkOnly, "This network only",
                "Players on your Wi-Fi or wired network can be set up after creation.", false),
            new(VanillaNetworkingPreference.DecideLater, "Decide later",
                "Keep networking unconfigured until you choose a method in the server workspace.", false)
        ];
        Revalidate();
    }

    // ------------------------------------------------------------------ steps

    public IReadOnlyList<CreationStepItem> Steps { get; } =
    [
        new(CreationWizardStep.Intent, 1, "Type"),
        new(CreationWizardStep.Setup, 2, "Setup"),
        new(CreationWizardStep.Review, 3, "Review"),
        new(CreationWizardStep.Creating, 4, "Create"),
        new(CreationWizardStep.Completion, 5, "Result")
    ];

    [ObservableProperty]
    private CreationWizardStep currentStep = CreationWizardStep.Intent;

    public int CurrentStepNumber => Array.IndexOf(Sequence, CurrentStep) + 1;

    public string StepPosition => $"Step {CurrentStepNumber} of {Sequence.Length}";

    public string StepTitle => Steps.First(step => step.Step == CurrentStep).Title;

    public string StepAutomationName => $"{StepPosition}. {StepTitle}.";

    // ------------------------------------------------------------------ user selection

    public IReadOnlyList<LiveIntentOption> Intents { get; }

    public IReadOnlyList<LiveIntentOption> AvailableIntents => Intents.Where(intent => intent.IsLive).ToArray();

    [ObservableProperty]
    private LiveIntentOption? selectedIntent;

    [ObservableProperty]
    private string serverName = "";

    private string maximumMemoryText = "4";
    public string MaximumMemoryText
    {
        get => maximumMemoryText;
        set
        {
            if (!SetProperty(ref maximumMemoryText, value))
                return;
            var parsed = MemoryAllocationPolicy.ParseGigabytes(value, CultureInfo.CurrentCulture);
            SynchronizeMemoryPreset(parsed.Mebibytes);
            MaximumMemoryInputError = parsed.Error;
            if (parsed.Mebibytes is { } mebibytes)
                MaximumMemoryMib = mebibytes;
            if (parsed.IsValid)
                MaximumMemoryInputError = MemoryAllocationPolicy.ValidatePair(1_024, MaximumMemoryMib) ?? "";
            EulaAccepted = false;
            Revalidate();
        }
    }

    [ObservableProperty]
    private int maximumMemoryMib = 4_096;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMaximumMemoryInputError))]
    private string maximumMemoryInputError = "";

    public bool HasMaximumMemoryInputError => MaximumMemoryInputError.Length > 0;
    public IReadOnlyList<MemoryPreset> MemoryPresets => MemoryAllocationPolicy.CommonPresets;

    private string serverPortText = ServerPortPolicy.DefaultPort.ToString(CultureInfo.InvariantCulture);
    public string ServerPortText
    {
        get => serverPortText;
        set
        {
            if (!SetProperty(ref serverPortText, value))
                return;
            var parsed = ServerPortPolicy.Parse(value);
            ServerPortInputError = parsed.Error;
            if (parsed.Port is { } port)
                ServerPort = port;
            else
                ServerPort = 0;
            EulaAccepted = false;
            Revalidate();
        }
    }

    [ObservableProperty]
    private int serverPort = ServerPortPolicy.DefaultPort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPortInputError))]
    private string serverPortInputError = "";

    public bool HasServerPortInputError => ServerPortInputError.Length > 0;

    public IReadOnlyList<VanillaNetworkingOption> NetworkingOptions { get; }

    [ObservableProperty]
    private VanillaNetworkingPreference networkingPreference =
        VanillaNetworkingPreference.FriendsOverInternet;

    private MemoryPreset? selectedMemoryPreset;
    public MemoryPreset? SelectedMemoryPreset
    {
        get => selectedMemoryPreset;
        set
        {
            if (!SetProperty(ref selectedMemoryPreset, value) || value is null)
                return;
            MaximumMemoryText = MemoryAllocationPolicy.FormatGigabytes(value.Mebibytes, CultureInfo.CurrentCulture);
        }
    }

    private void SynchronizeMemoryPreset(int? maximumMib)
    {
        var matchingPreset = maximumMib is { } value
            ? MemoryPresets.FirstOrDefault(preset => preset.Mebibytes == value)
            : null;
        if (Equals(selectedMemoryPreset, matchingPreset))
            return;
        selectedMemoryPreset = matchingPreset;
        OnPropertyChanged(nameof(SelectedMemoryPreset));
    }

    [ObservableProperty]
    private VanillaVersionOption? selectedVersion;

    /// <summary>
    /// False by default. Turning it on is the only way a snapshot ever appears in the list.
    /// </summary>
    [ObservableProperty]
    private bool includeSnapshots;

    /// <summary>
    /// Starts false for every fresh session and is never set by anything except the user.
    /// </summary>
    /// <remarks>
    /// Opening the EULA does not set it, choosing a version does not set it, moving between steps does
    /// not set it, and going back to change something clears it again: what was accepted was a
    /// specific plan, so a changed plan needs a new acceptance.
    /// </remarks>
    [ObservableProperty]
    private bool eulaAccepted;

    private DateTimeOffset? eulaAcceptedAtUtc;

    /// <summary>The acceptance exactly as it will be recorded. Never fabricated.</summary>
    public VanillaEulaAcceptance Eula => new()
    {
        Accepted = EulaAccepted,
        AcceptedAtUtc = EulaAccepted ? eulaAcceptedAtUtc : null,
        SourceUrl = EulaAccepted ? VanillaEulaAcceptance.OfficialSourceUrl : ""
    };

    public string EulaSourceUrl => VanillaEulaAcceptance.OfficialSourceUrl;

    /// <summary>
    /// The acceptance state as one value. The address and the fact that only it is stored belong to
    /// the review's technical rows, not to a sentence beside the control.
    /// </summary>
    public string EulaAcceptedDetail => EulaAccepted
        ? $"Accepted {LiveVanillaReviewBuilder.FormatLocal(eulaAcceptedAtUtc)}"
        : "Not accepted";

    // ------------------------------------------------------------------ live catalogue

    public ObservableCollection<VanillaVersionOption> Versions { get; } = [];

    [ObservableProperty]
    private LiveCatalogState catalogState = LiveCatalogState.Idle;

    [ObservableProperty]
    private string catalogDetail = "";

    /// <summary>True when a refresh removed the entry the user had chosen.</summary>
    [ObservableProperty]
    private bool selectedVersionDisappeared;

    public bool IsCatalogLoading => CatalogState == LiveCatalogState.Loading;

    public bool HasVersions => Versions.Count > 0;

    public bool ShowsCatalogProblem => CatalogState
        is LiveCatalogState.NoUsableMetadata or LiveCatalogState.RequestFailed;

    public bool ShowsCatalogWarning => CatalogState == LiveCatalogState.StaleCache;

    public bool ShowsCacheNotice => CatalogState == LiveCatalogState.Cached;

    public string CatalogStateHeadline => CatalogState switch
    {
        LiveCatalogState.Idle => "Versions have not been loaded yet",
        LiveCatalogState.Loading => "Loading versions from Mojang",
        LiveCatalogState.Available => "Read from Mojang just now",
        LiveCatalogState.Cached => "Using the version list ChunkPilot saved earlier",
        LiveCatalogState.StaleCache => "Mojang could not be reached, so this list may be out of date",
        LiveCatalogState.NoUsableMetadata => "Mojang could not be reached and there is no saved list",
        _ => "ChunkPilot could not ask for the version list"
    };

    public string CatalogFreshness => LiveVanillaReviewBuilder.DescribeFreshness(catalog);

    public string ChannelDescription => IncludeSnapshots
        ? "Showing releases and snapshots."
        : "Showing releases only.";

    // ------------------------------------------------------------------ resolved detail

    [ObservableProperty]
    private VanillaDestinationPreview? destination;

    /// <summary>
    /// The folder new servers are created inside. Empty means ChunkPilot's managed root.
    /// </summary>
    /// <remarks>
    /// A parent folder, not the server's own folder. The server's folder name is still derived from
    /// the display name, so renaming keeps the chosen location and only changes the child, and
    /// choosing a location is never consent to write into it.
    /// </remarks>
    [ObservableProperty]
    private string instanceRoot = "";

    public bool IsCustomLocation => InstanceRoot.Length > 0;

    public string LocationModeText => IsCustomLocation ? "Custom" : "Default";

    /// <summary>
    /// One line describing where the server will go, for the Location card.
    /// </summary>
    /// <remarks>
    /// Replaces a pair of rows that read as two separate facts - a folder and a mode - when they are
    /// one. The path itself is the second line of the card, so this stays short.
    /// </remarks>
    public string LocationSummary =>
        IsCustomLocation ? "Folder you chose" : "Default ChunkPilot folder";

    [ObservableProperty]
    private CreationReviewSummary review = new();

    public ObservableCollection<string> BlockingMessages { get; } = [];

    public ObservableCollection<string> WarningMessages { get; } = [];

    public ObservableCollection<string> NameMessages { get; } = [];

    public ObservableCollection<string> LocationMessages { get; } = [];

    public bool HasBlockingMessages => BlockingMessages.Count > 0;

    public bool HasWarningMessages => WarningMessages.Count > 0;

    public bool HasNameMessages => NameMessages.Count > 0;

    public bool HasLocationMessages => LocationMessages.Count > 0;

    public bool HasSelectedVersion => SelectedVersion is not null;

    public string VersionSupportText => SelectedVersion is { } version
        ? VanillaSupportPolicy.Describe(version.Support)
        : "";

    public string VersionJavaText => SelectedVersion?.RequiredJavaMajor is { } major
        ? $"Needs Java {major} — {LiveVanillaReviewBuilder.DescribeJavaSource(SelectedVersion.JavaRequirementSource)}"
        : "ChunkPilot could not establish which Java this version needs";

    public string VersionArtifactText => SelectedVersion is { } version
        ? LiveVanillaReviewBuilder.DescribeArtifact(version)
        : "";

    public string ManagedJavaSummary =>
        LiveVanillaReviewBuilder.ManagedJavaBehaviour(SelectedVersion?.RequiredJavaMajor);

    public string DestinationSummary => Destination?.Summary ?? "Set once the server has a name";

    /// <summary>True once there is a real folder to show, rather than a sentence standing in for one.</summary>
    public bool HasResolvedDestination => Destination is not null;

    /// <summary>True only when a chooser was supplied, so the action is never offered inertly.</summary>
    public bool CanChooseLocation => locationChooser is not null;

    // ------------------------------------------------------------------ operation

    [ObservableProperty]
    private CreationStage operationStage = CreationStage.NotStarted;

    [ObservableProperty]
    private string operationDetail = "";

    [ObservableProperty]
    private double progressPercent;

    [ObservableProperty]
    private bool isIndeterminate = true;

    [ObservableProperty]
    private bool cancellationRequested;

    [ObservableProperty]
    private string outcomeMessage = "";

    [ObservableProperty]
    private string createdJavaSummary = "";

    [ObservableProperty]
    private string createdJavaDetails = "";

    /// <summary>Everything the operation is known to have concluded. Empty while it runs.</summary>
    public ObservableCollection<string> OutcomeWarnings { get; } = [];

    public bool IsOperationRunning => operationId is not null && !CreationStagePolicy.IsTerminal(OperationStage);

    public string OperationHeadline => CreationStagePolicy.Describe(EffectiveStage);

    /// <summary>
    /// What the user is told, which differs from what the Agent is doing only while cancelling.
    /// </summary>
    /// <remarks>
    /// Promotion, registration and the final checks cannot be abandoned half-way, so a cancellation
    /// that arrives during one of them is honoured at the next safe point rather than immediately.
    /// Saying "stopping" there would be a promise ChunkPilot cannot keep.
    /// </remarks>
    public CreationStage EffectiveStage
    {
        get
        {
            if (!CancellationRequested || CreationStagePolicy.IsTerminal(OperationStage))
                return OperationStage;
            return CreationStagePolicy.IsCriticalSection(OperationStage)
                ? CreationStage.WaitingForSafeCheckpoint
                : CreationStage.CancellingSafely;
        }
    }

    public bool ShowsCancellationNotice => CancellationRequested && !CreationStagePolicy.IsTerminal(OperationStage);

    public string CancellationNotice => CreationStagePolicy.IsCriticalSection(OperationStage)
        ? "ChunkPilot is part-way through putting the server in place. Stopping now would leave a folder "
          + "nobody owns, so it will finish this step and then stop at the next safe point."
        : "ChunkPilot is stopping. Nothing has been put in place, and the folder is untouched.";

    public bool IsSuccessful => CreationStagePolicy.IsSuccessful(OperationStage);

    public bool NeedsAttention => OperationStage == CreationStage.RecoveryRequired;

    public bool IsFailed => OperationStage
        is CreationStage.FailedNothingChanged or CreationStage.FailedRolledBack or CreationStage.Cancelled;

    public string CreatedServerName => createdServer?.Name ?? "";

    public string CreatedServerVersion => createdServer?.MinecraftVersion ?? "";

    public string CreatedServerPath => createdServer?.RootPath ?? "";

    public string CreatedServerPort => createdServer?.Port.ToString(CultureInfo.InvariantCulture) ??
                                       ServerPort.ToString(CultureInfo.InvariantCulture);

    public string NetworkingNextStep => NetworkingPreference switch
    {
        VanillaNetworkingPreference.FriendsOverInternet =>
            "Open the server, then choose Manage connectivity to review router and Windows Firewall setup. Nothing is open yet.",
        VanillaNetworkingPreference.ThisNetworkOnly =>
            "Open the server, then choose Manage connectivity to review local-network access. No firewall permission was added.",
        _ => "Open the server and choose Manage connectivity whenever you are ready. Networking remains unconfigured."
    };

    /// <summary>Operation identity, kept for a diagnostic copy rather than shown as a headline.</summary>
    public string OperationIdText => operationId?.ToString("D") ?? "";

    /// <summary>What a person needs in order to look into an attention-required outcome.</summary>
    public string DiagnosticDetails => string.Join(Environment.NewLine,
        $"ChunkPilot Create Server v2 — live Vanilla",
        $"Outcome: {CreationStagePolicy.Describe(OperationStage)}",
        $"Operation: {OperationIdText}",
        $"Server name: {ServerName.Trim()}",
        $"Minecraft version: {SelectedVersion?.VersionId ?? LiveVanillaReviewBuilder.NotEstablished}",
        $"Folder: {Destination?.CanonicalDestination ?? LiveVanillaReviewBuilder.NotEstablished}",
        $"Detail: {OutcomeMessage}");

    // ------------------------------------------------------------------ command availability

    public bool ShowsBack => CurrentStep is CreationWizardStep.Setup or CreationWizardStep.Review;

    public bool ShowsCancelWizard => CurrentStep is CreationWizardStep.Intent or CreationWizardStep.Setup
        or CreationWizardStep.Review;

    public bool ShowsNext => CurrentStep is CreationWizardStep.Intent or CreationWizardStep.Setup;

    public bool ShowsCreate => CurrentStep == CreationWizardStep.Review;

    public bool ShowsStop => CurrentStep == CreationWizardStep.Creating;

    public bool ShowsClose => CurrentStep == CreationWizardStep.Completion;

    public bool ShowsOpenServer => CurrentStep == CreationWizardStep.Completion && IsSuccessful &&
                                   navigator is not null && createdServer is not null;

    private bool CanGoNext() => ShowsNext && !HasBlockingMessages && StepIsComplete(CurrentStep);

    private bool CanGoBack() => ShowsBack;

    private bool CanCreate() =>
        CurrentStep == CreationWizardStep.Review && !submitted && EulaAccepted && PlanProblems().Count == 0;

    private bool CanStop() => IsOperationRunning && !CancellationRequested;

    private bool CanOpenServer() => ShowsOpenServer;

    private bool StepIsComplete(CreationWizardStep step) => step switch
    {
        CreationWizardStep.Intent => SelectedIntent is { IsLive: true },
        CreationWizardStep.Setup => SelectedVersion is { IsSelectable: true } &&
                                    !HasMaximumMemoryInputError && !HasServerPortInputError &&
                                    CreationNamePolicy.Validate(ServerName).All(issue =>
                                        issue.Severity != CreationIssueSeverity.Blocking) &&
                                    Destination is { IsAvailable: true },
        _ => true
    };

    // ------------------------------------------------------------------ commands

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        var target = Sequence[Math.Min(Sequence.Length - 1, CurrentStepNumber)];
        MoveTo(target);
        if (target == CreationWizardStep.Setup && CatalogState is LiveCatalogState.Idle)
            await LoadCatalogAsync(false).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        // Going back invalidates the acceptance, because what was accepted was this exact plan.
        EulaAccepted = false;
        MoveTo(Sequence[Math.Max(0, CurrentStepNumber - 2)]);
    }

    [RelayCommand]
    private async Task RefreshCatalogAsync() => await LoadCatalogAsync(true).ConfigureAwait(true);

    /// <summary>
    /// Chooses the folder new servers are created inside, leaving the folder name to ChunkPilot.
    /// </summary>
    [RelayCommand]
    private async Task ChooseLocationAsync()
    {
        var chosen = locationChooser?.Choose(
            "Choose where to create this server",
            IsCustomLocation ? InstanceRoot : Destination?.InstanceRoot ?? "");
        if (string.IsNullOrWhiteSpace(chosen))
            return;
        InstanceRoot = chosen.Trim();
        await ResolveDestinationAsync(ServerName).ConfigureAwait(true);
    }

    /// <summary>Returns to the managed root and its automatic naming.</summary>
    [RelayCommand]
    private async Task UseDefaultLocationAsync()
    {
        if (!IsCustomLocation)
            return;
        InstanceRoot = "";
        await ResolveDestinationAsync(ServerName).ConfigureAwait(true);
    }

    /// <summary>
    /// Opens the official EULA. Reading is not agreeing, and this changes no state at all.
    /// </summary>
    [RelayCommand]
    private void OpenEula() => links.Open(VanillaEulaAcceptance.OfficialSourceUrl);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        // Set before the first await. A second click cannot get past this, so a double click cannot
        // become two operations even though the submission itself is asynchronous.
        if (submitted)
            return;
        submitted = true;
        NotifyCommands();

        OperationStage = CreationStage.Submitting;
        OperationDetail = "";
        OutcomeMessage = "";
        OutcomeWarnings.Clear();
        IsIndeterminate = true;
        ProgressPercent = 0;
        MoveTo(CreationWizardStep.Creating);

        var plan = BuildPlan();
        try
        {
            operationId = await gateway.BeginAsync(plan, lifetime.Token).ConfigureAwait(true);
            OnPropertyChanged(nameof(OperationIdText));
            await WatchAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The window closed. The Agent owns the operation and carries on without us.
        }
        catch (Exception exception)
        {
            Conclude(CreationStage.FailedNothingChanged, exception.Message, []);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        // Idempotent at this end too, not only at the Agent's: a second click while the first request
        // is still in flight must not become a second request.
        if (operationId is not { } id || CancellationRequested)
            return;
        CancellationRequested = true;
        NotifyOperation();
        try
        {
            // Idempotent by contract: asking twice is the same as asking once.
            await gateway.CancelAsync(id, lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            OperationDetail = exception.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenServer))]
    private async Task OpenServerAsync()
    {
        if (navigator is null || createdServer is null)
            return;
        await navigator.OpenAsync(createdServer.Id).ConfigureAwait(true);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CopyDiagnostics() => CopyRequested?.Invoke(this, DiagnosticDetails);

    [RelayCommand]
    private void CopyValue(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            CopyRequested?.Invoke(this, value);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------ live work

    /// <summary>
    /// Reads the version list, mapping every outcome to a state the interface can explain.
    /// </summary>
    public async Task LoadCatalogAsync(bool forceRefresh)
    {
        CatalogState = LiveCatalogState.Loading;
        CatalogDetail = "";
        NotifyCatalog();
        try
        {
            var loaded = await gateway.GetCatalogAsync(IncludeSnapshots, forceRefresh, lifetime.Token)
                .ConfigureAwait(true);
            Apply(loaded);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            catalog = null;
            Versions.Clear();
            CatalogState = LiveCatalogState.RequestFailed;
            CatalogDetail = exception.Message;
            NotifyCatalog();
            Revalidate();
        }
    }

    private void Apply(VanillaVersionCatalog loaded)
    {
        catalog = loaded;
        var previous = SelectedVersion?.VersionId;

        Versions.Clear();
        foreach (var option in loaded.Options
                     .Where(option => IncludeSnapshots || option.Channel == VanillaReleaseChannel.Stable)
                     .Where(option => option.Channel != VanillaReleaseChannel.Historic))
            Versions.Add(option);

        CatalogState = loaded switch
        {
            { ProviderAvailable: false, Options.Count: 0 } => LiveCatalogState.NoUsableMetadata,
            { IsStale: true } => LiveCatalogState.StaleCache,
            { IsFromCache: true } => LiveCatalogState.Cached,
            _ => LiveCatalogState.Available
        };
        CatalogDetail = loaded.UnavailableDetail;

        // A refreshed list that no longer contains the chosen entry must not quietly select its
        // neighbour: the user picked a version, and silently running a different one is the exact
        // substitution this wizard exists to prevent.
        if (previous is not null)
        {
            var again = Versions.FirstOrDefault(option =>
                string.Equals(option.VersionId, previous, StringComparison.Ordinal));
            SelectedVersionDisappeared = again is null;
            SelectedVersion = again;
        }

        NotifyCatalog();
        Revalidate();
    }

    /// <summary>Polls the Agent until the operation reaches a state it will not leave.</summary>
    /// <remarks>
    /// Polling rather than a push channel because that is what the existing pipe protocol supports,
    /// and because a poll that misses a beat is harmless: the Agent's snapshot is the truth and it
    /// survives the window being closed entirely.
    /// </remarks>
    private async Task WatchAsync()
    {
        if (operationId is not { } id)
            return;
        while (!lifetime.IsCancellationRequested)
        {
            InstallOperationSnapshot snapshot;
            try
            {
                snapshot = await gateway.GetSnapshotAsync(id, lifetime.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                // Losing contact is not the same as the work stopping. Say so, keep the operation id,
                // and let a reopened window reattach to whatever the Agent is still doing.
                Conclude(CreationStage.RecoveryRequired,
                    "ChunkPilot lost contact with the background service while creating this server. "
                    + "The work may still be going on. " + exception.Message, []);
                return;
            }

            ApplySnapshot(snapshot);
            if (snapshot.IsTerminal)
            {
                await FinishAsync(snapshot).ConfigureAwait(true);
                return;
            }

            try
            {
                await Task.Delay(pollInterval, lifetime.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void ApplySnapshot(InstallOperationSnapshot snapshot)
    {
        OperationStage = snapshot.Progress.Stage == CreationStage.NotStarted
            ? CreationStagePolicy.ForPhase(snapshot.Progress.Phase)
            : snapshot.Progress.Stage;
        ProgressPercent = snapshot.Progress.OverallPercent;

        // Determinate only where a real total exists. Everything else is a sequence of short discrete
        // steps, and a bar creeping through them would be invented progress.
        IsIndeterminate = snapshot.Progress.TotalBytes is not > 0;
        OperationDetail = snapshot.Progress.TotalBytes is > 0
            ? string.Create(CultureInfo.CurrentCulture,
                $"{snapshot.Progress.BytesDownloaded / 1024d / 1024d:F1} MB of "
                + $"{snapshot.Progress.TotalBytes!.Value / 1024d / 1024d:F1} MB")
            : snapshot.Progress.CurrentStep;
        NotifyOperation();
    }

    private async Task FinishAsync(InstallOperationSnapshot snapshot)
    {
        createdServer = snapshot.Result?.Definition;
        if (createdServer is not null)
            await DescribeAssignedRuntimeAsync().ConfigureAwait(true);

        var stage = snapshot.Progress.Stage == CreationStage.NotStarted
            ? CreationStagePolicy.ForPhase(snapshot.Progress.Phase)
            : snapshot.Progress.Stage;
        var message = snapshot.Success
            ? CreationPhasePolicy.Describe(snapshot.Outcome)
            : snapshot.Error.Length > 0
                ? snapshot.Error
                : CreationPhasePolicy.Describe(snapshot.Outcome);
        Conclude(stage, message, snapshot.Warnings);
    }

    /// <summary>
    /// Names the runtime the Agent actually assigned, rather than the one it was asked for.
    /// </summary>
    /// <remarks>
    /// The created server's launch executable is the Java it was given, so matching it against the
    /// managed runtimes reports what is genuinely in place. If the match fails, the requirement is
    /// reported and the exact build is not claimed.
    /// </remarks>
    private async Task DescribeAssignedRuntimeAsync()
    {
        var requirement = SelectedVersion?.RequiredJavaMajor;
        CreatedJavaSummary = requirement is { } major
            ? $"Managed Java {major}"
            : "A managed Java runtime";
        CreatedJavaDetails = CreatedJavaSummary;
        if (createdServer is null || createdServer.Executable.Length == 0)
            return;
        try
        {
            var runtimes = await gateway.GetManagedRuntimesAsync(lifetime.Token).ConfigureAwait(true);
            var assigned = runtimes.FirstOrDefault(runtime =>
                runtime.JavaPath.Equals(createdServer.Executable, StringComparison.OrdinalIgnoreCase));
            if (assigned is not null)
                CreatedJavaDetails =
                    $"{assigned.Vendor} {assigned.Version} (Java {assigned.MajorVersion}, {assigned.Architecture}), "
                    + "kept privately by ChunkPilot";
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // The server exists either way. Leaving the requirement-level summary in place is honest;
            // inventing a build number because a lookup failed would not be.
        }
        OnPropertyChanged(nameof(CreatedJavaSummary));
        OnPropertyChanged(nameof(CreatedJavaDetails));
    }

    private void Conclude(CreationStage stage, string message, IReadOnlyList<string> warnings)
    {
        OperationStage = stage;
        OutcomeMessage = message;
        OutcomeWarnings.Clear();
        foreach (var warning in warnings)
            OutcomeWarnings.Add(warning);
        ProgressPercent = CreationStagePolicy.IsSuccessful(stage) ? 100 : ProgressPercent;
        IsIndeterminate = false;
        MoveTo(CreationWizardStep.Completion);
        NotifyOperation();
    }

    /// <summary>
    /// Reattaches to a Vanilla creation this Agent is already running, if there is one.
    /// </summary>
    /// <remarks>
    /// Closing the window never cancelled anything, so on reopening the honest thing is to show the
    /// work that is still going rather than an empty first step. Reporting only: this starts nothing.
    /// </remarks>
    public async Task<bool> TryReattachAsync()
    {
        try
        {
            var creations = await gateway.GetCreationsAsync(lifetime.Token).ConfigureAwait(true);
            var running = creations.FirstOrDefault(snapshot => !snapshot.IsTerminal);
            if (running is null)
                return false;
            submitted = true;
            operationId = running.OperationId;
            OnPropertyChanged(nameof(OperationIdText));
            MoveTo(CreationWizardStep.Creating);
            ApplySnapshot(running);
            _ = WatchAsync();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception) { return false; }
    }

    // ------------------------------------------------------------------ plan

    /// <summary>The plan exactly as it will be submitted. Built from state, never from a control.</summary>
    public VanillaCreationPlan BuildPlan() => new()
    {
        ServerName = ServerName.Trim(),
        Version = SelectedVersion ?? new VanillaVersionOption(),
        Eula = Eula,
        MaximumRamMb = MaximumMemoryMib,
        Port = ServerPort,
        NetworkingPreference = NetworkingPreference,
        // The location the review screen showed. The Agent re-derives the folder from it and the
        // destination policy checks it again immediately before anything is promoted.
        InstanceRoot = Destination?.InstanceRoot ?? InstanceRoot,
        MetadataRetrievedUtc = catalog?.RetrievedUtc,
        MetadataFromCache = catalog?.IsFromCache ?? false,
        AcknowledgedWarnings = Review.Warnings
    };

    /// <summary>Everything that must hold before the Agent is asked to do anything.</summary>
    public IReadOnlyList<string> PlanProblems()
    {
        var problems = new List<string>();
        if (SelectedIntent is not { IsLive: true })
            problems.Add("Only Vanilla can be created in this development build.");
        problems.AddRange(CreationNamePolicy.Validate(ServerName)
            .Where(issue => issue.Severity == CreationIssueSeverity.Blocking)
            .Select(issue => issue.Message));
        if (HasMaximumMemoryInputError)
            problems.Add(MaximumMemoryInputError);
        if (HasServerPortInputError)
            problems.Add(ServerPortInputError);
        if (SelectedVersion is null)
            problems.Add("Choose a Minecraft version.");
        else
            problems.AddRange(BuildPlan().Problems()
                .Where(problem => !problem.Contains("EULA", StringComparison.Ordinal)));
        if (Destination is { IsAvailable: false })
            problems.Add(Destination.Message);
        return problems;
    }

    // ------------------------------------------------------------------ state changes

    partial void OnCurrentStepChanged(CreationWizardStep value)
    {
        foreach (var name in new[]
                 {
                     nameof(CurrentStepNumber), nameof(StepPosition), nameof(StepTitle),
                     nameof(StepAutomationName), nameof(ShowsBack), nameof(ShowsCancelWizard),
                     nameof(ShowsNext), nameof(ShowsCreate), nameof(ShowsStop), nameof(ShowsClose),
                     nameof(ShowsOpenServer)
                 })
            OnPropertyChanged(name);
        var current = CurrentStepNumber;
        foreach (var item in Steps)
        {
            item.IsCurrent = item.Number == current;
            item.IsComplete = item.Number < current;
        }
        NotifyCommands();
    }

    partial void OnSelectedIntentChanged(LiveIntentOption? value)
    {
        if (value is { IsLive: false })
        {
            // A card that cannot be created is never left selected: selection would imply a choice
            // the user cannot act on, and Next would then be disabled for no visible reason.
            SelectedIntent = null;
            return;
        }
        Revalidate();
    }

    partial void OnIncludeSnapshotsChanged(bool value)
    {
        OnPropertyChanged(nameof(ChannelDescription));
        if (CatalogState is not LiveCatalogState.Idle)
            _ = LoadCatalogAsync(false);
    }

    partial void OnSelectedVersionChanged(VanillaVersionOption? value)
    {
        if (value is not null)
            SelectedVersionDisappeared = false;
        // Changing what will be built invalidates an acceptance given for something else.
        EulaAccepted = false;
        foreach (var name in new[]
                 {
                     nameof(HasSelectedVersion), nameof(VersionSupportText), nameof(VersionJavaText),
                     nameof(VersionArtifactText), nameof(ManagedJavaSummary)
                 })
            OnPropertyChanged(name);
        Revalidate();
    }

    partial void OnNetworkingPreferenceChanged(VanillaNetworkingPreference value)
    {
        EulaAccepted = false;
        Revalidate();
    }

    partial void OnServerNameChanged(string value)
    {
        Destination = null;
        OnPropertyChanged(nameof(DestinationSummary));
        OnPropertyChanged(nameof(HasResolvedDestination));
        EulaAccepted = false;
        Revalidate();
        if (CreationNamePolicy.Validate(value).All(issue => issue.Severity != CreationIssueSeverity.Blocking))
            _ = ResolveDestinationAsync(value);
    }

    partial void OnEulaAcceptedChanged(bool value)
    {
        // The moment is recorded when acceptance is given, not when the plan is built, so what is
        // stored is when the user agreed rather than when ChunkPilot got round to asking.
        eulaAcceptedAtUtc = value ? DateTimeOffset.UtcNow : null;
        OnPropertyChanged(nameof(EulaAcceptedDetail));
        Revalidate();
    }

    private async Task ResolveDestinationAsync(string name)
    {
        if (CreationNamePolicy.Validate(name).Any(issue => issue.Severity == CreationIssueSeverity.Blocking))
            return;
        try
        {
            var preview = await gateway.PreviewDestinationAsync(name.Trim(), InstanceRoot, lifetime.Token)
                .ConfigureAwait(true);
            // A stale answer for a name the user has already changed must not be shown.
            if (!string.Equals(preview.ServerName.Trim(), ServerName.Trim(), StringComparison.Ordinal))
                return;
            Destination = preview;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Destination = new VanillaDestinationPreview
            {
                ServerName = name,
                IsAvailable = false,
                Message = "ChunkPilot could not work out where this server would go. " + exception.Message
            };
        }
        OnPropertyChanged(nameof(DestinationSummary));
        OnPropertyChanged(nameof(HasResolvedDestination));
        Revalidate();
    }

    partial void OnInstanceRootChanged(string value)
    {
        foreach (var name in new[]
                 {
                     nameof(IsCustomLocation), nameof(LocationModeText), nameof(LocationSummary)
                 })
            OnPropertyChanged(name);
        // The plan changed, so an acceptance given for the previous one no longer applies.
        EulaAccepted = false;
    }

    private void MoveTo(CreationWizardStep step)
    {
        if (step == CurrentStep)
            return;
        CurrentStep = step;
        FocusRequested?.Invoke(this, step);
    }

    private void Revalidate()
    {
        var nameIssues = CreationNamePolicy.Validate(ServerName);
        Replace(NameMessages, nameIssues.Select(issue => issue.Message));
        Replace(LocationMessages, Destination is { IsAvailable: false } refused
            ? [refused.Message]
            : []);

        var blocking = new List<string>();
        if (CurrentStep != CreationWizardStep.Intent && SelectedIntent is not { IsLive: true })
            blocking.Add("Only Vanilla can be created in this development build.");
        if (SelectedVersion is { IsSelectable: false } chosen)
            blocking.Add($"Minecraft {chosen.VersionId} cannot be created: "
                         + VanillaSupportPolicy.Describe(chosen.Support) + ".");
        Replace(BlockingMessages, blocking);

        var warnings = new List<string>();
        if (SelectedVersionDisappeared)
            warnings.Add("The version you had chosen is no longer in Mojang's list, so ChunkPilot cleared it. "
                         + "Choose another version.");
        if (SelectedVersion is not null)
            warnings.AddRange(SelectedVersion.Warnings);
        if (CatalogState == LiveCatalogState.StaleCache)
            warnings.Add("This version list is the one ChunkPilot last saw. Mojang could not be reached to "
                         + "check whether anything changed.");
        Replace(WarningMessages, warnings);

        Review = LiveVanillaReviewBuilder.Build(ServerName, SelectedVersion, Destination, Eula, catalog,
            MaximumMemoryMib, ServerPort, NetworkingPreference,
            PlanProblems());

        foreach (var name in new[]
                 {
                     nameof(HasBlockingMessages), nameof(HasWarningMessages), nameof(HasNameMessages),
                     nameof(HasLocationMessages), nameof(HasMaximumMemoryInputError),
                     nameof(HasServerPortInputError),
                     nameof(DestinationSummary)
                 })
            OnPropertyChanged(name);
        NotifyCommands();
    }

    private void NotifyCatalog()
    {
        foreach (var name in new[]
                 {
                     nameof(IsCatalogLoading), nameof(HasVersions), nameof(ShowsCatalogProblem),
                     nameof(ShowsCatalogWarning), nameof(ShowsCacheNotice), nameof(CatalogStateHeadline),
                     nameof(CatalogFreshness)
                 })
            OnPropertyChanged(name);
        NotifyCommands();
    }

    private void NotifyOperation()
    {
        foreach (var name in new[]
                 {
                     nameof(IsOperationRunning), nameof(OperationHeadline), nameof(EffectiveStage),
                     nameof(ShowsCancellationNotice), nameof(CancellationNotice), nameof(IsSuccessful),
                     nameof(NeedsAttention), nameof(IsFailed), nameof(CreatedServerName),
                     nameof(CreatedServerVersion), nameof(CreatedServerPath), nameof(ShowsOpenServer),
                     nameof(CreatedServerPort), nameof(NetworkingNextStep),
                     nameof(DiagnosticDetails)
                 })
            OnPropertyChanged(name);
        NotifyCommands();
    }

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }

    private void NotifyCommands()
    {
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        CreateCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        OpenServerCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Stops watching. Deliberately does not cancel the operation.
    /// </summary>
    /// <remarks>
    /// Closing a window is not a decision to abandon a download. The Agent owns the operation, keeps
    /// running it, and a reopened wizard reattaches to it.
    /// </remarks>
    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
