using System.Collections.ObjectModel;
using System.Globalization;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChunkPilot.App;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
// The daily workspace: the file editor, operation notices, memory in GB, and game rules.
//
// Everything here is presentation. The Agent still owns the filesystem, the server process and every
// command; this file decides what a beginner is shown about the answers.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>What the file pane is showing. One state at a time, all of them explicit.</summary>
public enum FileEditorState
{
    /// <summary>Nothing picked yet.</summary>
    NoSelection,

    /// <summary>A folder is selected. Opening it navigates rather than editing anything.</summary>
    Folder,

    /// <summary>Reading the file.</summary>
    Loading,

    /// <summary>Editable text, loaded.</summary>
    Text,

    /// <summary>Editable text, loaded, and the file has no content.</summary>
    Empty,

    /// <summary>Not a text format. Facts and a safe action, never decoded bytes.</summary>
    Binary,

    /// <summary>Editable format, but larger than the editor will load.</summary>
    TooLarge,

    /// <summary>The read failed. The reason is shown as the Agent reported it.</summary>
    Error
}

/// <summary>Tone of a shell notice. Mirrors the design system's alert tones.</summary>
public enum OperationNoticeTone
{
    Success,
    Warning,
    Danger
}

public sealed partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRenameServerError))]
    private string renameServerError = "";

    public bool HasRenameServerError => RenameServerError.Length > 0;

    private bool CanRenameServer() => !IsBusy && SelectedServer is not null;

    [RelayCommand(CanExecute = nameof(CanRenameServer))]
    private async Task RenameServerAsync()
    {
        if (SelectedServer is null)
            return;
        var serverId = SelectedServer.Definition.Id;
        var priorName = SelectedServer.Definition.Name;
        var requested = dialogs.PromptServerDisplayName(priorName);
        if (requested is null)
            return;

        RenameServerError = "";
        var blocking = CreationNamePolicy.Validate(requested)
            .FirstOrDefault(issue => issue.Severity == CreationIssueSeverity.Blocking);
        if (blocking is not null)
        {
            RenameServerError = blocking.Message;
            return;
        }

        await RunBusyAsync("Saving display name…", async () =>
        {
            var result = await client.SendAsync<OperationResult>("RenameServer",
                new RenameServerRequest(serverId, requested.Trim())).ConfigureAwait(true);
            if (!result.Success)
            {
                RenameServerError = result.Message;
                return;
            }
            await RefreshAsync().ConfigureAwait(true);
            SelectedServer = Servers.FirstOrDefault(server => server.Definition.Id == serverId);
            StatusMessage = result.Message;
        }, "Display name could not be changed").ConfigureAwait(true);
    }

    // ────────────────────────────────────────────────────────────── operation notices

    /// <summary>
    /// The last operation outcome worth interrupting for, shown as a themed banner in the shell.
    /// </summary>
    /// <remarks>
    /// This replaces the Windows message box a failed operation used to raise. A backup that could not
    /// read a locked file is not a modal event: the user has not lost anything, the server is still
    /// running, and a dialog they must dismiss before they can even read the rest of the page is worse
    /// than a banner that stays until they have. Successes are reported through
    /// <see cref="StatusMessage"/>, which the shell shows quietly.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationNotice))]
    private string operationNoticeTitle = "";

    [ObservableProperty]
    private string operationNoticeDetail = "";

    [ObservableProperty]
    private OperationNoticeTone operationNoticeTone = OperationNoticeTone.Danger;

    public bool HasOperationNotice => OperationNoticeTitle.Length > 0;

    public AppTone OperationNoticeAppTone => OperationNoticeTone switch
    {
        OperationNoticeTone.Success => AppTone.Success,
        OperationNoticeTone.Warning => AppTone.Warning,
        _ => AppTone.Danger
    };

    [RelayCommand]
    private void DismissOperationNotice()
    {
        OperationNoticeTitle = "";
        OperationNoticeDetail = "";
    }

    /// <summary>
    /// Raises a notice naming the server the failure belongs to.
    /// </summary>
    /// <remarks>
    /// The server name is part of the title because ChunkPilot manages several: "Backup failed" alone
    /// leaves the user guessing which one. The detail is the message the Agent produced, unedited -
    /// paraphrasing it is how a diagnostic stops being usable.
    /// </remarks>
    public void ShowOperationFailure(string action, string detail, string? serverName = null)
    {
        var name = serverName ?? SelectedServer?.Definition.Name;
        OperationNoticeTone = OperationNoticeTone.Danger;
        OperationNoticeTitle = string.IsNullOrWhiteSpace(name) ? action : $"{action} · {name}";
        OperationNoticeDetail = detail;
    }

    // ────────────────────────────────────────────────────────────── file browser and editor

    [ObservableProperty]
    private FileSystemEntry? selectedFileEntry;

    [ObservableProperty]
    private string editorPath = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveFileCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditorDirty))]
    private string editorContent = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveFileCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditorDirty))]
    private TextFileContent? loadedTextFile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveFileCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditorEditable))]
    [NotifyPropertyChangedFor(nameof(ShowsEditorEmptyNotice))]
    private FileEditorState editorState = FileEditorState.NoSelection;

    /// <summary>Name, size and modified time of the selection. Facts, not a decoded preview.</summary>
    [ObservableProperty]
    private string editorFacts = "";

    /// <summary>Why the file could not be read or edited, in the Agent's own words.</summary>
    [ObservableProperty]
    private string editorMessage = "";

    /// <summary>Quiet confirmation that a save landed. Cleared by the next edit.</summary>
    [ObservableProperty]
    private string editorSavedNotice = "";

    /// <summary>
    /// Why the last save did not go through. Separate from <see cref="EditorMessage"/> on purpose: that
    /// one explains the state of the selection, this one explains a failed action, and they can be true
    /// at the same time.
    /// </summary>
    [ObservableProperty]
    private string editorSaveError = "";

    /// <summary>True while the editor holds a change that has not been written.</summary>
    public bool IsEditorDirty =>
        LoadedTextFile is not null &&
        !string.Equals(LoadedTextFile.Content, EditorContent, StringComparison.Ordinal);

    /// <summary>True when the text box may be typed in at all.</summary>
    public bool IsEditorEditable => EditorState is FileEditorState.Text or FileEditorState.Empty;

    public bool ShowsEditorEmptyNotice => EditorState == FileEditorState.Empty;

    /// <summary>The folder being browsed, as a person would say it.</summary>
    public string CurrentFolderText => CurrentFolder.Length == 0 ? "Server folder" : CurrentFolder;

    /// <summary>
    /// Loads whatever was selected, deciding the state from the entry before asking the Agent.
    /// </summary>
    /// <remarks>
    /// Selecting a file loads it. That sounds obvious, and it was the whole defect: nothing was wired
    /// to the selection, so clicking a file left the editor blank and the page looked broken.
    /// </remarks>
    partial void OnSelectedFileEntryChanged(FileSystemEntry? value)
    {
        if (suppressFileSelection)
            return;
        _ = LoadSelectedFileAsync(value);
    }

    private bool suppressFileSelection;

    private async Task LoadSelectedFileAsync(FileSystemEntry? entry)
    {
        EditorSavedNotice = "";
        EditorSaveError = "";
        if (SelectedServer is null || entry is null)
        {
            SetEditorState(FileEditorState.NoSelection, "", "", "");
            return;
        }

        var kind = ServerFilePolicy.Classify(entry.Name, entry.IsDirectory, entry.SizeBytes);
        EditorPath = entry.RelativePath;
        var facts = DescribeEntry(entry);
        switch (kind)
        {
            case ServerFileKind.Folder:
                SetEditorState(FileEditorState.Folder, entry.Name, facts,
                    "Open this folder to see what is inside it.");
                return;
            case ServerFileKind.Binary:
                // A JAR, a region file or an image. Decoding it as text would show mojibake and invite
                // an edit that corrupts it, so the pane shows what it is and offers the folder instead.
                SetEditorState(FileEditorState.Binary, entry.Name, facts,
                    "This is not a text file, so ChunkPilot does not open it for editing.");
                return;
            case ServerFileKind.TooLarge:
                SetEditorState(FileEditorState.TooLarge, entry.Name, facts,
                    $"This file is larger than {ServerFilePolicy.MaximumEditableBytes / (1024 * 1024)} MB, " +
                    "so it is not loaded into the editor. Open the folder to work with it another way.");
                return;
        }

        SetEditorState(FileEditorState.Loading, entry.Name, facts, "");
        var serverId = SelectedServer.Definition.Id;
        try
        {
            var content = await client.SendAsync<TextFileContent>("ReadFile",
                new FilesRequest(serverId, entry.RelativePath)).ConfigureAwait(true);
            // The user may have clicked something else while this was in flight; a late answer must not
            // replace what they are looking at now.
            if (SelectedFileEntry is null || SelectedFileEntry.RelativePath != entry.RelativePath)
                return;
            LoadedTextFile = content;
            EditorContent = content.Content;
            SetEditorState(
                content.Content.Length == 0 ? FileEditorState.Empty : FileEditorState.Text,
                entry.Name, facts,
                content.Content.Length == 0 ? "This file is empty." : "");
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (SelectedFileEntry is null || SelectedFileEntry.RelativePath != entry.RelativePath)
                return;
            SetEditorState(FileEditorState.Error, entry.Name, facts, exception.Message);
        }
    }

    private void SetEditorState(FileEditorState state, string title, string facts, string message)
    {
        if (state is not (FileEditorState.Text or FileEditorState.Empty))
        {
            LoadedTextFile = null;
            EditorContent = "";
        }
        EditorTitle = title;
        EditorFacts = facts;
        EditorMessage = message;
        EditorState = state;
    }

    [ObservableProperty]
    private string editorTitle = "";

    private static string DescribeEntry(FileSystemEntry entry) =>
        entry.IsDirectory
            ? $"Folder · modified {entry.ModifiedAt.ToLocalTime():M/d h:mm tt}"
            : string.Create(CultureInfo.CurrentCulture,
                $"{FormatBytes(entry.SizeBytes)} · modified {entry.ModifiedAt.ToLocalTime():M/d h:mm tt}");

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)Math.Max(0, bytes);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{size:F0} {units[unit]}"
            : $"{size:F1} {units[unit]}";
    }

    /// <summary>Opens the selected folder, or reloads the selected file from disk.</summary>
    [RelayCommand]
    private async Task OpenSelectedFileAsync()
    {
        if (SelectedServer is null || SelectedFileEntry is null)
            return;
        if (SelectedFileEntry.IsDirectory)
        {
            await LoadFilesCoreAsync(SelectedFileEntry.RelativePath).ConfigureAwait(true);
            return;
        }
        await LoadSelectedFileAsync(SelectedFileEntry).ConfigureAwait(true);
    }

    /// <summary>Shows the current folder in Windows Explorer, for a file the editor will not open.</summary>
    [RelayCommand]
    private void OpenEditorFolder()
    {
        if (SelectedServer is null)
            return;
        var folder = Path.Combine(SelectedServer.Definition.RootPath, CurrentFolder);
        if (!Directory.Exists(folder))
            folder = SelectedServer.Definition.RootPath;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "explorer.exe", CommandLineQuoter.QuoteWindowsArgument(folder)) { UseShellExecute = true });
    }

    /// <summary>
    /// Writes the edited file through the Agent's atomic path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enabled only when the text genuinely differs from what was loaded, so the button cannot invite a
    /// write that changes nothing. Encoding, byte-order mark, line endings, comments and ordering are
    /// carried on the loaded <see cref="TextFileContent"/> and handed straight back, so a save preserves
    /// the file's shape rather than rewriting it in ChunkPilot's own style.
    /// </para>
    /// <para>
    /// A failed save keeps the user's text exactly as they left it: nothing is reloaded and the editor is
    /// not cleared, because the edit is the only copy of their work.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanSaveFile))]
    private async Task SaveFileAsync()
    {
        if (SelectedServer is null || LoadedTextFile is null)
            return;
        var serverId = SelectedServer.Definition.Id;
        var pending = LoadedTextFile with { Content = EditorContent };
        EditorSavedNotice = "";
        EditorSaveError = "";
        try
        {
            var result = await client.SendAsync<OperationResult>("WriteFile",
                new WriteFileRequest(serverId, pending)).ConfigureAwait(true);
            if (!result.Success)
            {
                EditorSaveError = result.Message;
                return;
            }
            // Re-read so the next save carries the new hash; the editor keeps showing what was written.
            var reloaded = await client.SendAsync<TextFileContent>("ReadFile",
                new FilesRequest(serverId, pending.RelativePath)).ConfigureAwait(true);
            LoadedTextFile = reloaded;
            EditorContent = reloaded.Content;
            EditorSavedNotice = $"Saved {DateTimeOffset.Now:h:mm tt}";
            StatusMessage = $"{pending.RelativePath} saved.";
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            // The edit stays exactly as typed. It is the only copy of the user's work.
            EditorSaveError = exception.Message;
        }
    }

    private bool CanSaveFile() => IsEditorEditable && IsEditorDirty;

    internal Task<TextFileContent> ReadWebUiFileAsync(Guid serverId, string relativePath,
        CancellationToken cancellationToken = default)
    {
        EnsureWebUiServer(serverId);
        return client.SendAsync<TextFileContent>("ReadFile",
            new FilesRequest(serverId, relativePath), cancellationToken);
    }

    internal async Task LoadWebUiFolderAsync(Guid serverId, string relativePath)
    {
        EnsureWebUiServer(serverId);
        await LoadFilesCoreAsync(relativePath).ConfigureAwait(true);
    }

    internal Task<OperationResult> WriteWebUiFileAsync(Guid serverId, TextFileContent content,
        CancellationToken cancellationToken = default)
    {
        EnsureWebUiServer(serverId);
        return client.SendAsync<OperationResult>("WriteFile",
            new WriteFileRequest(serverId, content), cancellationToken);
    }

    private void EnsureWebUiServer(Guid serverId)
    {
        if (SelectedServer?.Definition.Id != serverId || Servers.All(server => server.Definition.Id != serverId))
            throw new InvalidOperationException("The requested server is not the active server.");
    }

    // ────────────────────────────────────────────────────────────── memory in GB

    /// <summary>
    /// Memory shown in GB, stored in MiB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The launch profile, the JVM arguments and the database all keep MiB, because that is what
    /// <c>-Xms</c> and <c>-Xmx</c> take and converting them would be risk with no benefit. Only the
    /// presentation changes: nobody thinks about their server in mebibytes.
    /// </para>
    /// <para>
    /// The setter leaves the stored value untouched when the choice already displays as the same number,
    /// so opening the page on a server configured at an unusual 3000 MiB and closing it again cannot
    /// quietly re-round its allocation.
    /// </para>
    /// </remarks>
    public double MinimumMemoryGb
    {
        get => ToGigabytes(MinimumRamMb);
        set
        {
            var megabytes = MemoryAllocationPolicy.NormalizeGigabytes((decimal)value).Mebibytes ?? MinimumRamMb;
            if (ToGigabytes(MinimumRamMb) == ToGigabytes(megabytes))
                return;
            MinimumRamMb = megabytes;
            if (MaximumRamMb < MinimumRamMb)
                MaximumRamMb = MinimumRamMb;
        }
    }

    public double MaximumMemoryGb
    {
        get => ToGigabytes(MaximumRamMb);
        set
        {
            var megabytes = MemoryAllocationPolicy.NormalizeGigabytes((decimal)value).Mebibytes ?? MaximumRamMb;
            if (ToGigabytes(MaximumRamMb) == ToGigabytes(megabytes))
                return;
            MaximumRamMb = megabytes;
            if (MinimumRamMb > MaximumRamMb)
                MinimumRamMb = MaximumRamMb;
        }
    }

    /// <summary>The exact figures the JVM will receive. Shown as detail, never as the control.</summary>
    public string MemoryDetailText =>
        string.Create(CultureInfo.CurrentCulture,
            $"Java receives -Xms{MinimumRamMb}M -Xmx{MaximumRamMb}M.");

    /// <summary>
    /// The memory values offered, in GB.
    /// </summary>
    /// <remarks>
    /// A short list of sensible sizes rather than a free field: half-gigabyte steps low down where they
    /// matter, whole gigabytes after that. Capped by what this computer actually has, so the list cannot
    /// suggest an allocation the machine cannot honour, and always includes the value already
    /// configured so an existing server's setting is never silently absent from its own control.
    /// </remarks>
    public IReadOnlyList<double> MemoryChoices
    {
        get
        {
            double[] candidates =
            [
                0.5, 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10, 12, 16, 20, 24, 32, 48, 64
            ];
            var hostGb = Dashboard.Host.TotalMemoryBytes > 0
                ? Dashboard.Host.TotalMemoryBytes / (double)(1024 * 1024 * 1024)
                : 64d;
            var cap = Math.Max(1d, Math.Min(64d, Math.Floor(hostGb)));
            var values = candidates.Where(value => value <= cap).ToList();
            foreach (var configured in new[] { MinimumMemoryGb, MaximumMemoryGb })
            {
                if (configured > 0 && !values.Contains(configured))
                    values.Add(configured);
            }
            values.Sort();
            return values;
        }
    }

    private static double ToGigabytes(int mebibytes) => (double)(mebibytes / 1024m);

    public IReadOnlyList<MemoryPreset> MemoryPresets => MemoryAllocationPolicy.CommonPresets;

    private string minimumMemoryText = "1";
    public string MinimumMemoryText
    {
        get => minimumMemoryText;
        set
        {
            if (!SetProperty(ref minimumMemoryText, value))
                return;
            ReparseMemoryInputs();
        }
    }

    private string maximumMemoryText = "4";
    public string MaximumMemoryText
    {
        get => maximumMemoryText;
        set
        {
            if (!SetProperty(ref maximumMemoryText, value))
                return;
            ReparseMemoryInputs();
        }
    }

    private MemoryPreset? selectedMaximumMemoryPreset;
    public MemoryPreset? SelectedMaximumMemoryPreset
    {
        get => selectedMaximumMemoryPreset;
        set
        {
            if (!SetProperty(ref selectedMaximumMemoryPreset, value) || value is null)
                return;
            MaximumMemoryText = MemoryAllocationPolicy.FormatGigabytes(value.Mebibytes, CultureInfo.CurrentCulture);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMemoryInputError))]
    [NotifyPropertyChangedFor(nameof(MemoryInputErrorText))]
    private string minimumMemoryInputError = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMemoryInputError))]
    [NotifyPropertyChangedFor(nameof(MemoryInputErrorText))]
    private string maximumMemoryInputError = "";

    public bool HasMemoryInputError => MinimumMemoryInputError.Length > 0 || MaximumMemoryInputError.Length > 0;
    public string MemoryInputErrorText => MinimumMemoryInputError.Length > 0
        ? MinimumMemoryInputError
        : MaximumMemoryInputError;

    private void ReparseMemoryInputs()
    {
        var minimum = MemoryAllocationPolicy.ParseGigabytes(MinimumMemoryText, CultureInfo.CurrentCulture);
        var maximum = MemoryAllocationPolicy.ParseGigabytes(MaximumMemoryText, CultureInfo.CurrentCulture);
        SynchronizeMaximumMemoryPreset(maximum.Mebibytes);
        MinimumMemoryInputError = minimum.Error;
        MaximumMemoryInputError = maximum.Error;
        if (minimum.Mebibytes is { } minimumMib)
            MinimumRamMb = minimumMib;
        if (maximum.Mebibytes is { } maximumMib)
            MaximumRamMb = maximumMib;
        if (minimum.IsValid && maximum.IsValid)
            MaximumMemoryInputError = MemoryAllocationPolicy.ValidatePair(MinimumRamMb, MaximumRamMb) ?? "";
        NotifyMemoryState();
    }

    private void SynchronizeMaximumMemoryPreset(int? maximumMib)
    {
        var matchingPreset = maximumMib is { } value
            ? MemoryPresets.FirstOrDefault(preset => preset.Mebibytes == value)
            : null;
        if (Equals(selectedMaximumMemoryPreset, matchingPreset))
            return;
        selectedMaximumMemoryPreset = matchingPreset;
        OnPropertyChanged(nameof(SelectedMaximumMemoryPreset));
    }

    private int loadedMinimumRamMb = 1_024;
    private int loadedMaximumRamMb = 4_096;
    private bool memoryRestartPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMemorySaveError))]
    private string memorySaveError = "";

    [ObservableProperty]
    private string memorySavedNotice = "";

    public bool HasMemorySaveError => MemorySaveError.Length > 0;

    public bool HasMemoryChanges => SelectedServer is not null &&
        (MinimumRamMb != loadedMinimumRamMb || MaximumRamMb != loadedMaximumRamMb);

    public bool MemoryNeedsRestart => memoryRestartPending ||
        (HasMemoryChanges && SelectedServer?.State == ServerState.Running);

    public string MemoryRestartNotice => memoryRestartPending
        ? "Memory allocation saved. Restart the server to use it."
        : HasMemoryChanges && SelectedServer?.State == ServerState.Running
            ? "Restart is required after saving this memory allocation."
            : "";

    public bool CanRestartForMemory => SelectedServer?.State == ServerState.Running;
    public bool ShowsMemoryRestartNow => memoryRestartPending && CanRestartForMemory;

    private void MarkMemoryLoaded(int minimum, int maximum)
    {
        loadedMinimumRamMb = minimum;
        loadedMaximumRamMb = maximum;
        minimumMemoryText = MemoryAllocationPolicy.FormatGigabytes(minimum, CultureInfo.CurrentCulture);
        maximumMemoryText = MemoryAllocationPolicy.FormatGigabytes(maximum, CultureInfo.CurrentCulture);
        SynchronizeMaximumMemoryPreset(maximum);
        MinimumMemoryInputError = "";
        MaximumMemoryInputError = "";
        OnPropertyChanged(nameof(MinimumMemoryText));
        OnPropertyChanged(nameof(MaximumMemoryText));
        NotifyMemoryState();
    }

    private void RestoreLoadedMemory()
    {
        MinimumRamMb = loadedMinimumRamMb;
        MaximumRamMb = loadedMaximumRamMb;
        MarkMemoryLoaded(loadedMinimumRamMb, loadedMaximumRamMb);
    }

    private void NotifyMemoryState()
    {
        OnPropertyChanged(nameof(HasMemoryChanges));
        OnPropertyChanged(nameof(MemoryNeedsRestart));
        OnPropertyChanged(nameof(MemoryRestartNotice));
        OnPropertyChanged(nameof(CanRestartForMemory));
        OnPropertyChanged(nameof(ShowsMemoryRestartNow));
        ApplyMemoryCommand.NotifyCanExecuteChanged();
        ApplyMemoryAndRestartCommand.NotifyCanExecuteChanged();
        RestartForMemoryCommand.NotifyCanExecuteChanged();
    }

    partial void OnMinimumRamMbChanged(int value) => NotifyMemoryState();
    partial void OnMaximumRamMbChanged(int value) => NotifyMemoryState();

    private bool CanApplyMemory() => !IsBusy && !HasMemoryInputError && HasMemoryChanges &&
        SelectedServer?.State is ServerState.Stopped or ServerState.Crashed or ServerState.Running;

    private bool CanApplyMemoryAndRestart() => CanApplyMemory() && CanRestartForMemory;

    [RelayCommand(CanExecute = nameof(CanApplyMemory))]
    private async Task ApplyMemoryAsync() => await ApplyMemoryCoreAsync(restart: false).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanApplyMemoryAndRestart))]
    private async Task ApplyMemoryAndRestartAsync() => await ApplyMemoryCoreAsync(restart: true).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanRestartForMemory))]
    private async Task RestartForMemoryAsync()
    {
        if (SelectedServer is null)
            return;
        memoryRestartPending = false;
        NotifyMemoryState();
        await RestartServerAsync(SelectedServer).ConfigureAwait(true);
    }

    private async Task ApplyMemoryCoreAsync(bool restart)
    {
        if (SelectedServer is null)
            return;
        var serverId = SelectedServer.Definition.Id;
        var wasRunning = SelectedServer.State == ServerState.Running;
        MemorySaveError = "";
        MemorySavedNotice = "";
        await RunBusyAsync("Saving memory allocation…", async () =>
        {
            OperationResult result;
            try
            {
                result = await client.SendAsync<OperationResult>("UpdateRam",
                    new RamUpdateRequest(serverId, MinimumRamMb, MaximumRamMb)).ConfigureAwait(true);
            }
            catch
            {
                RestoreLoadedMemory();
                throw;
            }
            if (!result.Success)
            {
                MemorySaveError = result.Message;
                RestoreLoadedMemory();
                return;
            }
            if (SelectedServer?.Definition.Id != serverId)
                return;
            MarkMemoryLoaded(MinimumRamMb, MaximumRamMb);
            MemorySavedNotice = "Memory allocation saved.";
            memoryRestartPending = wasRunning && !restart;
            StatusMessage = result.Message;
            if (restart && wasRunning)
            {
                ApplyOptimisticServerState(serverId, ServerState.Restarting);
                try
                {
                    var restartResult = await client.SendAsync<OperationResult>("Restart",
                        ConnectivityRequest(serverId, PublicConnectivityOperation.RestartServer))
                        .ConfigureAwait(true);
                    StatusMessage = restartResult.Message;
                }
                finally
                {
                    await RefreshAsync().ConfigureAwait(true);
                }
            }
            else
            {
                await RefreshAsync().ConfigureAwait(true);
            }
            NotifyMemoryState();
        }, "Memory allocation could not be saved").ConfigureAwait(true);
    }

    // ────────────────────────────────────────────────────────────── game rules

    /// <summary>The rules for the selected server, each one a real control bound to a real value.</summary>
    public ObservableCollection<GameruleRow> Gamerules { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsGamerules))]
    [NotifyPropertyChangedFor(nameof(ShowsGameruleUnavailable))]
    private bool gamerulesAvailable;

    [ObservableProperty]
    private string gameruleUnavailableReason = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCustomGameruleCommand))]
    private string customGameruleName = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCustomGameruleCommand))]
    private string customGameruleValue = "";

    [ObservableProperty]
    private string customGameruleResult = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsGamerules))]
    [NotifyPropertyChangedFor(nameof(ShowsGameruleUnavailable))]
    private bool isLoadingGamerules;

    public bool ShowsGamerules => GamerulesAvailable && !IsLoadingGamerules && Gamerules.Count > 0;

    public bool ShowsGameruleUnavailable => !GamerulesAvailable && !IsLoadingGamerules;

    [RelayCommand]
    private async Task RefreshGamerulesAsync() => await LoadGamerulesAsync().ConfigureAwait(true);

    /// <summary>
    /// Reads game rules from the Agent, which reads them from the running server.
    /// </summary>
    /// <remarks>
    /// A stopped server produces an explanation rather than controls. Nothing here falls back to
    /// Vanilla's documented defaults: a switch showing a default the world may not be using is worse
    /// than a page saying it does not know yet.
    /// </remarks>
    private async Task LoadGamerulesAsync()
    {
        if (SelectedServer is null)
            return;
        var serverId = SelectedServer.Definition.Id;
        IsLoadingGamerules = true;
        try
        {
            var response = await client.SendAsync<GameruleStateResponse>(
                "ReadGamerules", new GameruleReadRequest(serverId)).ConfigureAwait(true);
            if (SelectedServer?.Definition.Id != serverId)
                return;
            GamerulesAvailable = response.CanChange && response.Rules.Count > 0;
            GameruleUnavailableReason = response.UnavailableReason.Length > 0
                ? response.UnavailableReason
                : "Game rules are not available for this server.";
            ApplyGameruleStates(response.Rules);
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException)
        {
            GamerulesAvailable = false;
            GameruleUnavailableReason = exception.Message;
        }
        finally
        {
            IsLoadingGamerules = false;
            OnPropertyChanged(nameof(ShowsGamerules));
            OnPropertyChanged(nameof(ShowsGameruleUnavailable));
        }
    }

    private void ApplyGameruleStates(IReadOnlyList<GameruleState> states)
    {
        // Rows are reused rather than rebuilt so a control keeps its focus and its pending flag across
        // a refresh triggered by something the user did elsewhere.
        foreach (var state in states)
        {
            var existing = Gamerules.FirstOrDefault(row =>
                string.Equals(row.Name, state.Name, StringComparison.Ordinal));
            if (existing is null)
                Gamerules.Add(new GameruleRow(state, ApplyGameruleAsync));
            else
                existing.Adopt(state);
        }
        foreach (var stale in Gamerules
                     .Where(row => states.All(state => state.Name != row.Name))
                     .ToArray())
            Gamerules.Remove(stale);
        OnPropertyChanged(nameof(ShowsGamerules));
    }

    /// <summary>
    /// Sends one rule change and re-reads the authoritative value.
    /// </summary>
    /// <remarks>
    /// The row is left pending until the Agent answers, and reverts to the last known value if the
    /// change was refused. What the control shows afterwards is what the server reported, not what was
    /// asked for.
    /// </remarks>
    private async Task<bool> ApplyGameruleAsync(string name, string value)
    {
        if (SelectedServer is null)
            return false;
        var serverId = SelectedServer.Definition.Id;
        if (GamerulePolicy.Validate(name, value) is { } invalid)
        {
            StatusMessage = invalid;
            return false;
        }
        try
        {
            var result = await client.SendAsync<OperationResult>("ApplyGamerules",
                new GameruleApplyRequest(serverId,
                    new Dictionary<string, string>(StringComparer.Ordinal) { [name] = value }))
                .ConfigureAwait(true);
            StatusMessage = result.Message;
            if (!result.Success)
                return false;
            await LoadGamerulesAsync().ConfigureAwait(true);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException)
        {
            StatusMessage = exception.Message;
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyCustomGamerule))]
    private async Task ApplyCustomGameruleAsync()
    {
        if (SelectedServer is null)
            return;
        var name = CustomGameruleName.Trim();
        var value = CustomGameruleValue.Trim();
        if (GamerulePolicy.ValidateCustom(name, value) is { } invalid)
        {
            CustomGameruleResult = invalid;
            return;
        }
        try
        {
            var result = await client.SendAsync<OperationResult>("ApplyCustomGamerule",
                new GameruleApplyRequest(SelectedServer.Definition.Id,
                    new Dictionary<string, string>(StringComparer.Ordinal) { [name] = value }))
                .ConfigureAwait(true);
            CustomGameruleResult = result.Message;
            StatusMessage = result.Message;
            if (result.Success && GamerulePolicy.Find(name) is not null)
                await LoadGamerulesAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            CustomGameruleResult = exception.Message;
        }
    }

    private bool CanApplyCustomGamerule() =>
        SelectedServer?.State == ServerState.Running &&
        CustomGameruleName.Trim().Length > 0 && CustomGameruleValue.Trim().Length > 0;
}

/// <summary>
/// One game rule as a control: its value, whether a change is in flight, and how to change it.
/// </summary>
/// <remarks>
/// The switch and the number box bind to <see cref="BooleanValue"/> and <see cref="IntegerValue"/>
/// two-way. Adopting an authoritative value sets the backing field directly, so refreshing the page
/// cannot look like a user gesture and send the command back to the server.
/// </remarks>
public sealed partial class GameruleRow : ObservableObject
{
    private readonly Func<string, string, Task<bool>> apply;
    private bool suppressCommands;

    internal GameruleRow(GameruleState state, Func<string, string, Task<bool>> apply)
    {
        this.apply = apply;
        Name = state.Name;
        Adopt(state);
    }

    /// <summary>The exact gamerule key, always available as the control's detail.</summary>
    public string Name { get; }

    [ObservableProperty]
    private string label = "";

    [ObservableProperty]
    private string description = "";

    [ObservableProperty]
    private GameruleValueKind kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    private bool isPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    [NotifyPropertyChangedFor(nameof(ProvenanceText))]
    private GameruleProvenance provenance;

    [ObservableProperty]
    private int minimum;

    [ObservableProperty]
    private int maximum;

    public bool IsBoolean => Kind == GameruleValueKind.Boolean;

    public bool IsInteger => Kind == GameruleValueKind.WholeNumber;

    /// <summary>A control is only live for a value the server actually reported.</summary>
    public bool IsEnabled => !IsPending && Provenance == GameruleProvenance.ReportedByServer;

    /// <summary>Where the shown value came from. Never blank, never implied.</summary>
    public string ProvenanceText => Provenance switch
    {
        GameruleProvenance.ReportedByServer => "Reported by the server",
        GameruleProvenance.QueuedForNextStart => "Queued for the next start",
        _ => "Not read from the server"
    };

    public bool BooleanValue
    {
        get => booleanValue;
        set
        {
            if (booleanValue == value)
                return;
            var previous = booleanValue;
            SetProperty(ref booleanValue, value);
            if (suppressCommands)
                return;
            _ = SendAsync(value ? "true" : "false", () =>
            {
                SetProperty(ref booleanValue, previous, nameof(BooleanValue));
            });
        }
    }

    public int IntegerValue
    {
        get => integerValue;
        set
        {
            var clamped = Maximum > Minimum ? Math.Clamp(value, Minimum, Maximum) : value;
            if (integerValue == clamped)
                return;
            var previous = integerValue;
            SetProperty(ref integerValue, clamped);
            if (suppressCommands)
                return;
            _ = SendAsync(clamped.ToString(CultureInfo.InvariantCulture), () =>
            {
                SetProperty(ref integerValue, previous, nameof(IntegerValue));
            });
        }
    }

    private bool booleanValue;
    private int integerValue;

    /// <summary>Takes an authoritative state without treating it as a user change.</summary>
    internal void Adopt(GameruleState state)
    {
        suppressCommands = true;
        try
        {
            Label = state.Label;
            Description = state.Description;
            Kind = state.Kind;
            Minimum = state.Minimum;
            Maximum = state.Maximum;
            Provenance = state.Provenance;
            SetProperty(ref booleanValue, state.BooleanValue, nameof(BooleanValue));
            SetProperty(ref integerValue, state.IntegerValue, nameof(IntegerValue));
            IsPending = false;
            OnPropertyChanged(nameof(IsBoolean));
            OnPropertyChanged(nameof(IsInteger));
        }
        finally
        {
            suppressCommands = false;
        }
    }

    private async Task SendAsync(string value, Action revert)
    {
        IsPending = true;
        try
        {
            if (await apply(Name, value).ConfigureAwait(true))
                return;
            suppressCommands = true;
            try
            {
                revert();
            }
            finally
            {
                suppressCommands = false;
            }
        }
        finally
        {
            IsPending = false;
        }
    }
}
