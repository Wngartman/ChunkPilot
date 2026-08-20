using System.Collections.ObjectModel;
using ChunkPilot.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChunkPilot.App.CreateServer;

/// <summary>
/// One entry in the wizard's step rail, carrying its own current/complete state.
/// </summary>
/// <remarks>
/// The state lives on the item rather than being derived in XAML so the rail's appearance and its
/// automation name come from the same value, and a screen reader hears "current" rather than being
/// told nothing because the cue was a highlight.
/// </remarks>
public sealed partial class CreationStepItem : ObservableObject
{
    public CreationStepItem(CreationWizardStep step, int number, string title)
    {
        Step = step;
        Number = number;
        Title = title;
    }

    public CreationWizardStep Step { get; }

    public int Number { get; }

    public string Title { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private bool isCurrent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private bool isComplete;

    public string AutomationName =>
        $"Step {Number}, {Title}, " + (IsCurrent ? "current step" : IsComplete ? "completed" : "not started");
}

/// <summary>One expert category described, but deliberately not yet implemented.</summary>
public sealed record AdvancedCategoryPreview(string Title, string Description);

/// <summary>
/// The Create Server v2 preview's wizard state: steps, selection, validation and review.
/// </summary>
/// <remarks>
/// <para>
/// PREVIEW ONLY. This view model has no agent client, no installer, no store and no provider. It
/// cannot start an installation because it holds nothing that could. Its entire data source is
/// <see cref="SyntheticPreviewCatalog"/>, which is invented and built into the binary.
/// </para>
/// <para>
/// Deliberately not one oversized view model: the user's answers live in
/// <see cref="CreationSelection"/>, what was resolved for them lives in
/// <see cref="ResolvedCreationContext"/>, the findings live in <see cref="CreationValidationResult"/>
/// and the review is rebuilt by <see cref="CreationReviewBuilder"/>. This type owns only the step
/// sequence and the commands that move through it.
/// </para>
/// </remarks>
public sealed partial class CreateServerPreviewViewModel : ObservableObject
{
    private static readonly CreationWizardStep[] Sequence =
    [
        CreationWizardStep.Intent,
        CreationWizardStep.Setup,
        CreationWizardStep.Review,
        CreationWizardStep.Completion
    ];

    /// <summary>Raised when the wizard wants focus moved to the first control of a step.</summary>
    public event EventHandler<CreationWizardStep>? FocusRequested;

    /// <summary>Raised when the preview should close. The window owns the actual close.</summary>
    public event EventHandler? CloseRequested;

    public CreateServerPreviewViewModel()
    {
        foreach (var project in SyntheticPreviewCatalog.ModpackProjects)
            Projects.Add(project);
        RefreshStepStates();
        Revalidate();
    }

    // ------------------------------------------------------------------ steps

    public IReadOnlyList<CreationStepItem> Steps { get; } =
    [
        new(CreationWizardStep.Intent, 1, "Choose server type"),
        new(CreationWizardStep.Setup, 2, "Basic setup"),
        new(CreationWizardStep.Review, 3, "Review"),
        new(CreationWizardStep.Completion, 4, "Preview complete")
    ];

    [ObservableProperty]
    private CreationWizardStep currentStep = CreationWizardStep.Intent;

    public int CurrentStepNumber => Array.IndexOf(Sequence, CurrentStep) + 1;

    public string StepPosition => $"Step {CurrentStepNumber} of {Sequence.Length}";

    public string StepTitle => Steps.First(step => step.Step == CurrentStep).Title;

    /// <summary>Step identity for assistive technology, which cannot see the rail's highlight.</summary>
    public string StepAutomationName => $"{StepPosition}. {StepTitle}.";

    // ------------------------------------------------------------------ selection

    [ObservableProperty]
    private CreationIntentCard? selectedIntent;

    [ObservableProperty]
    private string serverName = "";

    [ObservableProperty]
    private SyntheticPreviewOption? selectedOption;

    [ObservableProperty]
    private SyntheticPreviewProject? selectedProject;

    [ObservableProperty]
    private bool advancedAcknowledged;

    [ObservableProperty]
    private string projectSearch = "";

    public IReadOnlyList<CreationIntentCard> Intents { get; } = CreationIntentCatalog.Cards;

    public ObservableCollection<SyntheticPreviewOption> Options { get; } = [];

    public ObservableCollection<SyntheticPreviewProject> Projects { get; } = [];

    public ObservableCollection<SyntheticPreviewOption> ProjectVersions { get; } = [];

    /// <summary>The user's answers, assembled from the bound fields.</summary>
    public CreationSelection Selection => new()
    {
        Intent = SelectedIntent?.Intent,
        ServerName = ServerName,
        OptionId = SelectedOption?.Id ?? "",
        ProjectId = SelectedProject?.Id ?? "",
        AdvancedAcknowledged = AdvancedAcknowledged
    };

    // ------------------------------------------------------------------ resolved state

    [ObservableProperty]
    private ResolvedCreationContext context = new();

    [ObservableProperty]
    private CreationValidationResult validation = new();

    [ObservableProperty]
    private CreationReviewSummary review = new();

    public ObservableCollection<string> BlockingMessages { get; } = [];

    public ObservableCollection<string> WarningMessages { get; } = [];

    public ObservableCollection<string> NoticeMessages { get; } = [];

    public ObservableCollection<string> NameMessages { get; } = [];

    public bool HasBlockingMessages => BlockingMessages.Count > 0;

    public bool HasWarningMessages => WarningMessages.Count > 0;

    public bool HasNoticeMessages => NoticeMessages.Count > 0;

    public bool HasNameMessages => NameMessages.Count > 0;

    // ------------------------------------------------------------------ per-intent surface

    public bool HasSelectedIntent => SelectedIntent is not null;

    public bool ShowsOptionList =>
        SelectedIntent is { Intent: not CreationIntent.Modpack and not CreationIntent.Advanced };

    public bool ShowsProjectBrowser => SelectedIntent?.Intent == CreationIntent.Modpack;

    public bool ShowsProjectVersions => ShowsProjectBrowser && SelectedProject is not null;

    public bool ShowsAdvancedSummary => SelectedIntent?.Intent == CreationIntent.Advanced;

    public bool ShowsCrossplayExplanation => SelectedIntent?.Intent == CreationIntent.Crossplay;

    public bool ShowsPluginExplanation => SelectedIntent?.Intent == CreationIntent.Plugins;

    public bool ShowsModExplanation => SelectedIntent?.Intent == CreationIntent.Mods;

    public string OptionListLabel => SelectedIntent?.Intent switch
    {
        CreationIntent.Vanilla => "Minecraft version",
        CreationIntent.Plugins => "Plugin-capable server",
        CreationIntent.Mods => "Mod loader and Minecraft version",
        CreationIntent.Crossplay => "Server this is built on",
        _ => "Option"
    };

    public string SetupDescription => SelectedIntent?.Intent switch
    {
        CreationIntent.Vanilla => "Name your server and choose which version of Minecraft to run.",
        CreationIntent.Plugins => "Name your server and choose which plugin-capable server to run.",
        CreationIntent.Mods => "Name your server and choose a loader. The loader and the Minecraft version go together.",
        CreationIntent.Modpack => "Name your server, then find a modpack and choose one of its releases.",
        CreationIntent.Crossplay => "Name your server and choose the Java server the crossplay layer sits on.",
        CreationIntent.Advanced => "Name your server and read what expert setup will let you change.",
        _ => "Name your server and choose what to run."
    };

    public IReadOnlyList<AdvancedCategoryPreview> AdvancedCategories { get; } =
    [
        new("Server files", "Point ChunkPilot at a package, an archive or a folder you already have."),
        new("Java runtime", "Use a specific runtime instead of the one ChunkPilot would pick for you."),
        new("Launch settings", "Set memory limits, the port and the launch arguments yourself."),
        new("Starting configuration", "Seed server.properties values at creation time instead of afterwards.")
    ];

    public string AdvancedNotYetBuilt =>
        "These editors are not part of this preview. This step describes what expert setup will cover; the "
        + "controls themselves arrive in a later update.";

    // ------------------------------------------------------------------ command availability

    public bool ShowsBack => CurrentStep is CreationWizardStep.Setup or CreationWizardStep.Review;

    public bool ShowsCancel => CurrentStep != CreationWizardStep.Completion;

    public bool ShowsNext => CurrentStep is CreationWizardStep.Intent or CreationWizardStep.Setup;

    public bool ShowsFinish => CurrentStep == CreationWizardStep.Review;

    public bool ShowsClose => CurrentStep == CreationWizardStep.Completion;

    public bool IsCompleted => CurrentStep == CreationWizardStep.Completion;

    private bool CanGoNext() => ShowsNext && Validation.CanContinueFrom(CurrentStep);

    private bool CanGoBack() => ShowsBack;

    private bool CanFinishPreview() => ShowsFinish && Validation.CanFinish;

    private bool CanClosePreview() => ShowsClose;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() => MoveTo(Sequence[Math.Min(Sequence.Length - 1, CurrentStepNumber)]);

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => MoveTo(Sequence[Math.Max(0, CurrentStepNumber - 2)]);

    /// <summary>
    /// Completes the preview. Deliberately named for what it does: it changes a step and nothing else.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFinishPreview))]
    private void FinishPreview() => MoveTo(CreationWizardStep.Completion);

    [RelayCommand(CanExecute = nameof(CanClosePreview))]
    private void ClosePreview() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void MoveTo(CreationWizardStep step)
    {
        if (step == CurrentStep)
            return;
        CurrentStep = step;
        FocusRequested?.Invoke(this, step);
    }

    // ------------------------------------------------------------------ state changes

    partial void OnCurrentStepChanged(CreationWizardStep value)
    {
        OnPropertyChanged(nameof(CurrentStepNumber));
        OnPropertyChanged(nameof(StepPosition));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepAutomationName));
        OnPropertyChanged(nameof(ShowsBack));
        OnPropertyChanged(nameof(ShowsCancel));
        OnPropertyChanged(nameof(ShowsNext));
        OnPropertyChanged(nameof(ShowsFinish));
        OnPropertyChanged(nameof(ShowsClose));
        OnPropertyChanged(nameof(IsCompleted));
        RefreshStepStates();
        NotifyCommands();
    }

    private void RefreshStepStates()
    {
        var current = CurrentStepNumber;
        foreach (var item in Steps)
        {
            item.IsCurrent = item.Number == current;
            item.IsComplete = item.Number < current;
        }
    }

    partial void OnSelectedIntentChanged(CreationIntentCard? value)
    {
        // Changing intent drops only what the new intent invalidates. The server name is
        // intent-independent and survives, which is what the domain record's WithIntent expresses.
        SelectedOption = null;
        SelectedProject = null;
        AdvancedAcknowledged = false;
        ProjectSearch = "";

        Options.Clear();
        if (value is not null)
        {
            foreach (var option in SyntheticPreviewCatalog.OptionsFor(value.Intent))
                Options.Add(option);
        }

        // Advanced offers one conceptual choice, so the acknowledgement below is the real gate and a
        // single-item picker would be noise.
        if (value?.Intent == CreationIntent.Advanced)
            SelectedOption = Options.FirstOrDefault();

        OnPropertyChanged(nameof(HasSelectedIntent));
        OnPropertyChanged(nameof(ShowsOptionList));
        OnPropertyChanged(nameof(ShowsProjectBrowser));
        OnPropertyChanged(nameof(ShowsProjectVersions));
        OnPropertyChanged(nameof(ShowsAdvancedSummary));
        OnPropertyChanged(nameof(ShowsCrossplayExplanation));
        OnPropertyChanged(nameof(ShowsPluginExplanation));
        OnPropertyChanged(nameof(ShowsModExplanation));
        OnPropertyChanged(nameof(OptionListLabel));
        OnPropertyChanged(nameof(SetupDescription));
        Revalidate();
    }

    partial void OnSelectedProjectChanged(SyntheticPreviewProject? value)
    {
        SelectedOption = null;
        ProjectVersions.Clear();
        if (value is not null)
        {
            foreach (var version in value.Versions)
                ProjectVersions.Add(version);
        }
        OnPropertyChanged(nameof(ShowsProjectVersions));
        Revalidate();
    }

    partial void OnProjectSearchChanged(string value)
    {
        var term = value.Trim();
        var matches = SyntheticPreviewCatalog.ModpackProjects
            .Where(project => term.Length == 0 ||
                              project.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                              project.Summary.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Projects.Clear();
        foreach (var project in matches)
            Projects.Add(project);
        OnPropertyChanged(nameof(HasNoProjectMatches));
    }

    public bool HasNoProjectMatches => Projects.Count == 0;

    partial void OnSelectedOptionChanged(SyntheticPreviewOption? value) => Revalidate();

    partial void OnServerNameChanged(string value) => Revalidate();

    partial void OnAdvancedAcknowledgedChanged(bool value) => Revalidate();

    private void Revalidate()
    {
        var selection = Selection;
        var option = SelectedOption;
        Context = option?.ToContext() ?? new ResolvedCreationContext();
        Validation = CreationPreviewValidator.Validate(selection, option);
        Review = CreationReviewBuilder.Build(selection, Context, Validation);

        Replace(BlockingMessages, Validation.BlockingIssues
            .Where(issue => issue.Field != CreationNamePolicy.Fields.ServerName)
            .Select(issue => issue.Message));
        Replace(WarningMessages, Validation.Warnings.Select(issue => issue.Message));
        Replace(NoticeMessages, Validation.Notices
            .Where(issue => issue.Field != CreationNamePolicy.Fields.ServerName)
            .Select(issue => issue.Message));
        Replace(NameMessages, Validation
            .For(CreationNamePolicy.Fields.ServerName)
            .Select(issue => issue.Message));

        OnPropertyChanged(nameof(HasBlockingMessages));
        OnPropertyChanged(nameof(HasWarningMessages));
        OnPropertyChanged(nameof(HasNoticeMessages));
        OnPropertyChanged(nameof(HasNameMessages));
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
        FinishPreviewCommand.NotifyCanExecuteChanged();
        ClosePreviewCommand.NotifyCanExecuteChanged();
    }
}
