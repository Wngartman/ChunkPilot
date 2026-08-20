using System.Globalization;
using ChunkPilot.App;
using ChunkPilot.App.CreateServerLive;
using ChunkPilot.Core;

namespace ChunkPilot.UnitTests;

public sealed class EverydayUsabilityTests
{
    [Fact]
    public void Typed_and_preset_memory_use_the_same_normalization()
    {
        var typed = MemoryAllocationPolicy.ParseGigabytes("4.6", CultureInfo.InvariantCulture);
        var preset = MemoryAllocationPolicy.CommonPresets.Single(item => item.Gigabytes == 4);

        Assert.True(typed.IsValid);
        Assert.Equal(4_710, typed.Mebibytes);
        Assert.Equal(4_096, preset.Mebibytes);
        Assert.Equal(MemoryAllocationPolicy.NormalizeGigabytes(4m).Mebibytes, preset.Mebibytes);
    }

    [Theory]
    [InlineData(4096, "4")]
    [InlineData(4710, "4.6")]
    [InlineData(3000, "2.93")]
    public void Normalized_memory_formats_naturally_and_round_trips(int mebibytes, string expected)
    {
        var text = MemoryAllocationPolicy.FormatGigabytes(mebibytes, CultureInfo.InvariantCulture);
        Assert.Equal(expected, text);
        Assert.Equal(mebibytes,
            MemoryAllocationPolicy.ParseGigabytes(text, CultureInfo.InvariantCulture).Mebibytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e3")]
    [InlineData("4 GB")]
    [InlineData("99999999999999999999999999999")]
    public void Invalid_memory_text_is_rejected(string text) =>
        Assert.False(MemoryAllocationPolicy.ParseGigabytes(text, CultureInfo.InvariantCulture).IsValid);

    [Fact]
    public void Current_culture_decimal_is_supported_without_changing_persistence_units()
    {
        var culture = CultureInfo.GetCultureInfo("de-DE");
        var parsed = MemoryAllocationPolicy.ParseGigabytes("4,6", culture);
        Assert.Equal(4_710, parsed.Mebibytes);
        Assert.Equal("4,6", MemoryAllocationPolicy.FormatGigabytes(4_710, culture));
    }

    [Fact]
    public void Minimum_must_not_exceed_maximum()
    {
        Assert.Null(MemoryAllocationPolicy.ValidatePair(1_024, 4_710));
        Assert.NotNull(MemoryAllocationPolicy.ValidatePair(8_192, 4_710));
    }

    [Fact]
    public void Create_and_manage_produce_the_same_normalized_maximum()
    {
        using var create = new LiveVanillaWizardViewModel(new NoOpCreationGateway());
        create.MaximumMemoryText = "4.6";
        var manage = new MainViewModel(new NoOpAgentClient(), new NoOpDialogs());
        manage.MaximumMemoryText = "4.6";

        Assert.Equal(4_710, create.BuildPlan().MaximumRamMb);
        Assert.Equal(create.BuildPlan().MaximumRamMb, manage.MaximumRamMb);
    }

    [Fact]
    public void Typing_a_custom_memory_value_clears_a_stale_preset_selection()
    {
        using var create = new LiveVanillaWizardViewModel(new NoOpCreationGateway());
        var manage = new MainViewModel(new NoOpAgentClient(), new NoOpDialogs());
        var preset = MemoryAllocationPolicy.CommonPresets.Single(item => item.Gigabytes == 6);

        create.SelectedMemoryPreset = preset;
        manage.SelectedMaximumMemoryPreset = preset;
        create.MaximumMemoryText = "4.6";
        manage.MaximumMemoryText = "4.6";

        Assert.Null(create.SelectedMemoryPreset);
        Assert.Null(manage.SelectedMaximumMemoryPreset);
        Assert.Equal(4_710, create.MaximumMemoryMib);
        Assert.Equal(4_710, manage.MaximumRamMb);
    }

    [Fact]
    public void Create_review_reports_the_normalized_choice()
    {
        var version = SupportedVersion();
        var review = LiveVanillaReviewBuilder.Build("Test", version, null, new VanillaEulaAcceptance(),
            null, 4_710, []);
        var memory = review.Sections.SelectMany(section => section.Rows)
            .Single(row => row.Label == "Maximum memory");
        Assert.Equal("4.6 GB (4710 MiB)", memory.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Open_folder_is_unavailable_without_a_server_and_uses_the_exact_selected_root(bool managed)
    {
        var launcher = new RecordingFolderLauncher();
        var model = new MainViewModel(new NoOpAgentClient(), new NoOpDialogs(), folderLauncher: launcher);
        Assert.False(model.OpenServerFolderCommand.CanExecute(null));

        var snapshot = Snapshot(@"D:\Fixtures\Server [one] & friends", managed: managed);
        model.Servers.Add(snapshot);
        model.SelectedServer = snapshot;
        Assert.True(model.OpenServerFolderCommand.CanExecute(null));
        model.OpenServerFolderCommand.Execute(null);

        Assert.Equal(snapshot.Definition.RootPath, launcher.Paths.Single());
    }

    [Fact]
    public void Windows_folder_launcher_does_not_create_a_missing_directory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ChunkPilot-missing-" + Guid.NewGuid().ToString("N"));
        var result = new WindowsFolderLauncher().OpenExisting(path);
        Assert.False(result.Success);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void Folder_path_with_shell_characters_is_one_exact_argument_not_a_command()
    {
        const string path = @"D:\Fixtures\Server [one] & friends; still one path";
        var start = WindowsFolderLauncher.BuildStartInfo(path);
        Assert.Equal("explorer.exe", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal(path, Assert.Single(start.ArgumentList));
        Assert.Empty(start.Arguments);
    }

    [Fact]
    public async Task Rename_refreshes_authoritative_identity_without_touching_runtime_or_paths()
    {
        var original = Snapshot(@"D:\Fixtures\kept-root", ServerState.Running);
        var client = new MutableAgentClient(original);
        var dialogs = new RenameDialogs("  世界 Survival  ");
        var model = new MainViewModel(client, dialogs);
        model.Servers.Add(original);
        model.SelectedServer = original;

        await model.RenameServerCommand.ExecuteAsync(null);

        Assert.Equal("世界 Survival", model.SelectedServer!.Definition.Name);
        Assert.Equal(original.Definition.Id, model.SelectedServer.Definition.Id);
        Assert.Equal(original.Definition.RootPath, model.SelectedServer.Definition.RootPath);
        Assert.Equal(ServerState.Running, model.SelectedServer.State);
        Assert.Contains("RenameServer", client.Operations);
        Assert.DoesNotContain("Restart", client.Operations);
    }

    [Fact]
    public async Task Failed_rename_and_failed_memory_save_restore_authoritative_values()
    {
        var original = Snapshot(@"D:\Fixtures\kept-root");
        var client = new MutableAgentClient(original) { FailMutations = true };
        var model = new MainViewModel(client, new RenameDialogs("Changed"));
        model.Servers.Add(original);
        model.SelectedServer = original;

        await model.RenameServerCommand.ExecuteAsync(null);
        Assert.Equal("Original", model.SelectedServer!.Definition.Name);
        Assert.True(model.HasRenameServerError);

        model.MaximumMemoryText = "4.6";
        Assert.Equal(4_710, model.MaximumRamMb);
        await model.ApplyMemoryCommand.ExecuteAsync(null);
        Assert.Equal(4_096, model.MaximumRamMb);
        Assert.Equal("4", model.MaximumMemoryText);
        Assert.True(model.HasMemorySaveError);
    }

    [Fact]
    public void Running_server_memory_edit_reports_restart_required_before_saving()
    {
        var original = Snapshot(@"D:\Fixtures\kept-root", ServerState.Running);
        var model = new MainViewModel(new MutableAgentClient(original), new NoOpDialogs());
        model.Servers.Add(original);
        model.SelectedServer = original;
        model.MaximumMemoryText = "4.6";
        Assert.True(model.MemoryNeedsRestart);
        Assert.Contains("Restart is required", model.MemoryRestartNotice, StringComparison.Ordinal);
    }

    private static VanillaVersionOption SupportedVersion() => new()
    {
        VersionId = "1.21.11",
        HasServerDownload = true,
        ServerDownloadUrl = "https://example.invalid/server.jar",
        RequiredJavaMajor = 21,
        Support = VanillaVersionSupport.Supported
    };

    private static ServerSnapshot Snapshot(
        string root,
        ServerState state = ServerState.Stopped,
        bool managed = false) => new()
    {
        State = state,
        Definition = new ServerDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Original",
            RootPath = root,
            WorkingDirectory = root,
            MinimumRamMb = 1_024,
            MaximumRamMb = 4_096,
            IsManaged = managed
        }
    };

    private sealed class NoOpAgentClient : IAgentClient
    {
        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TResponse> SendAsync<TResponse>(string operation, object? payload = null,
            CancellationToken cancellationToken = default) => operation == "SetSetting"
                ? Task.FromResult((TResponse)(object)OperationResult.Ok("ok"))
                : Task.FromException<TResponse>(new InvalidOperationException("Fixture does not serve " + operation));
    }

    private class NoOpDialogs : IDialogService
    {
        public string? SelectFolder(string title, string? initialPath = null) => null;
        public string? SelectFile(string title, string filter) => null;
        public bool Confirm(string title, string message) => false;
        public void ShowError(string title, string message) { }
        public void ShowInformation(string title, string message) { }
        public virtual string? PromptServerDisplayName(string currentName) => null;
    }

    private sealed class RenameDialogs(string? result) : NoOpDialogs
    {
        public override string? PromptServerDisplayName(string currentName) => result;
    }

    private sealed class RecordingFolderLauncher : IFolderLauncher
    {
        public List<string> Paths { get; } = [];
        public FolderLaunchResult OpenExisting(string path)
        {
            Paths.Add(path);
            return FolderLaunchResult.Opened();
        }
    }

    private sealed class MutableAgentClient(ServerSnapshot snapshot) : IAgentClient
    {
        public ServerSnapshot Snapshot { get; private set; } = snapshot;
        public bool FailMutations { get; init; }
        public List<string> Operations { get; } = [];
        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<TResponse> SendAsync<TResponse>(string operation, object? payload = null,
            CancellationToken cancellationToken = default)
        {
            Operations.Add(operation);
            object response;
            switch (operation)
            {
                case "RenameServer":
                    if (FailMutations)
                        response = OperationResult.Fail("The display name was not saved. Try again.");
                    else
                    {
                        var request = Assert.IsType<RenameServerRequest>(payload);
                        Snapshot = Snapshot with { Definition = Snapshot.Definition with { Name = request.DisplayName } };
                        response = OperationResult.Ok("Display name changed.");
                    }
                    break;
                case "UpdateRam":
                    response = FailMutations
                        ? OperationResult.Fail("The memory allocation was not saved. Try again.")
                        : OperationResult.Ok("Memory saved.");
                    break;
                case "Dashboard":
                    response = new DashboardSnapshot { AgentConnected = true, Servers = [Snapshot] };
                    break;
                case "SetSetting":
                    response = OperationResult.Ok("ok");
                    break;
                default:
                    throw new InvalidOperationException("Fixture does not serve " + operation);
            }
            return Task.FromResult((TResponse)response);
        }
    }

    private sealed class NoOpCreationGateway : IVanillaCreationGateway
    {
        public Task<VanillaVersionCatalog> GetCatalogAsync(bool includeSnapshots, bool forceRefresh,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VanillaDestinationPreview> PreviewDestinationAsync(string serverName, string instanceRoot,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid> BeginAsync(VanillaCreationPlan plan, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<InstallOperationSnapshot> GetSnapshotAsync(Guid operationId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InstallOperationSnapshot>> GetCreationsAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelAsync(Guid operationId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedJavaRuntime>> GetManagedRuntimesAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
