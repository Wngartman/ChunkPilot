using System.Collections.ObjectModel;
using System.Diagnostics;
using ChunkPilot.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChunkPilot.App;

public sealed partial class InstallServerViewModel : ObservableObject
{
    private readonly IAgentClient client;
    private readonly IDialogService dialogs;
    private Guid? operationId;

    public InstallServerViewModel(IAgentClient client, IDialogService dialogs)
    {
        this.client = client;
        this.dialogs = dialogs;
        instanceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ChunkPilot", "Servers");
        SelectedQuickStart = QuickStarts[0];
    }

    public IReadOnlyList<InstallSourceType> Sources { get; } =
    [
        InstallSourceType.Vanilla,
        InstallSourceType.Paper,
        InstallSourceType.Purpur,
        InstallSourceType.Fabric,
        InstallSourceType.Quilt,
        InstallSourceType.Forge,
        InstallSourceType.NeoForge,
        InstallSourceType.LocalZip,
        InstallSourceType.DirectUrl,
        InstallSourceType.ExistingPackageFolder
    ];

    public IReadOnlyList<QuickStartPreset> QuickStarts { get; } =
        Enum.GetValues<QuickStartKind>().Select(kind => QuickStartPresetFactory.Create(kind)).ToArray();

    public ObservableCollection<string> Versions { get; } = [];
    public ObservableCollection<CatalogItem> CatalogItems { get; } = [];
    public ObservableCollection<CatalogVersion> CatalogVersions { get; } = [];
    public IReadOnlyList<CatalogProvider> CatalogProviders { get; } =
        [CatalogProvider.Modrinth, CatalogProvider.CurseForge];

    private string expectedSha1 = "";
    private string expectedSha256 = "";
    private string expectedSha512 = "";

    [ObservableProperty]
    private string catalogSearch = "";

    [ObservableProperty]
    private CatalogProvider catalogProvider = CatalogProvider.Modrinth;

    [ObservableProperty]
    private CatalogItem? selectedCatalogItem;

    [ObservableProperty]
    private CatalogVersion? selectedCatalogVersion;

    [ObservableProperty]
    private QuickStartPreset selectedQuickStart =
        QuickStartPresetFactory.Create(QuickStartKind.VanillaWithFriends);

    [ObservableProperty]
    private string quickStartDetail =
        QuickStartPresetFactory.Create(QuickStartKind.VanillaWithFriends).PlainLanguageSummary;

    [ObservableProperty]
    private InstallSourceType sourceType = InstallSourceType.Vanilla;

    [ObservableProperty]
    private string source = "";

    [ObservableProperty]
    private string minecraftVersion = "";

    [ObservableProperty]
    private string build = "";

    [ObservableProperty]
    private string serverName = "My Minecraft Server";

    [ObservableProperty]
    private string instanceRoot;

    [ObservableProperty]
    private string javaPath = "java";

    [ObservableProperty]
    private bool useManagedJava = true;

    [ObservableProperty]
    private int minimumRamMb = 1_024;

    [ObservableProperty]
    private int maximumRamMb = 4_096;

    [ObservableProperty]
    private int port = 25_565;

    [ObservableProperty]
    private int maxPlayers = 20;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool eulaAccepted;

    [ObservableProperty]
    private bool allowHttp;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool isInstalling;

    [ObservableProperty]
    private double progressPercent;

    [ObservableProperty]
    private string currentStep = "Choose a source and load available versions.";

    [ObservableProperty]
    private string progressDetail = "";

    [ObservableProperty]
    private string stagingLogPath = "";

    [ObservableProperty]
    private InstallationResult? result;

    public bool NeedsCatalogVersion =>
        SourceType is InstallSourceType.Vanilla or InstallSourceType.Paper or InstallSourceType.Purpur or
            InstallSourceType.Fabric or InstallSourceType.Quilt or InstallSourceType.Forge or
            InstallSourceType.NeoForge;

    public bool NeedsSourcePath => !NeedsCatalogVersion;

    partial void OnSourceTypeChanged(InstallSourceType value)
    {
        Versions.Clear();
        MinecraftVersion = "";
        OnPropertyChanged(nameof(NeedsCatalogVersion));
        OnPropertyChanged(nameof(NeedsSourcePath));
    }

    partial void OnSelectedQuickStartChanged(QuickStartPreset value)
    {
        SourceType = value.SourceType is InstallSourceType.CustomPackage
            ? InstallSourceType.LocalZip
            : value.SourceType;
        UseManagedJava = value.ManagedJava;
        MaxPlayers = value.MaxPlayers;
        QuickStartDetail = value.PlainLanguageSummary + Environment.NewLine +
                           string.Join(Environment.NewLine, value.ReviewItems.Select(item => "• " + item));
    }

    partial void OnSelectedCatalogItemChanged(CatalogItem? value)
    {
        CatalogVersions.Clear();
        if (value is null)
            return;
        foreach (var version in value.Versions)
            CatalogVersions.Add(version);
        SelectedCatalogVersion = CatalogPolicy.SelectDefaultVersion(value, new CatalogQuery
        {
            MinecraftVersion = MinecraftVersion,
            Loader = SourceType is InstallSourceType.Fabric or InstallSourceType.Quilt or
                InstallSourceType.Forge or InstallSourceType.NeoForge ? SourceType.ToString() : "",
            MaximumChannel = ReleaseChannel.Stable
        }) ?? CatalogVersions.FirstOrDefault();
    }

    [RelayCommand]
    private async Task BrowseCatalogAsync()
    {
        try
        {
            CurrentStep = $"Searching the official {CatalogProvider} API…";
            var items = await client.SendAsync<CatalogItem[]>("BrowseCatalog", new CatalogQuery
            {
                Search = CatalogSearch.Trim(),
                Provider = CatalogProvider,
                MinecraftVersion = MinecraftVersion.Trim(),
                ServerPackRequired = true,
                ExcludeClientOnly = true,
                MaximumChannel = ReleaseChannel.Stable,
                Limit = 20
            }).ConfigureAwait(true);
            CatalogItems.Clear();
            foreach (var item in items)
                CatalogItems.Add(item);
            CurrentStep = items.Length == 0
                ? "No compatible server packages were returned. Client-only projects stay hidden."
                : $"Found {items.Length} server-capable result(s). Choose an exact version.";
        }
        catch (Exception exception)
        {
            CurrentStep = exception.Message;
            dialogs.ShowError("Catalog unavailable", exception.Message);
        }
    }

    [RelayCommand]
    private void UseCatalogSelection()
    {
        if (SelectedCatalogVersion is not { HasServerPackage: true } version ||
            string.IsNullOrWhiteSpace(version.DownloadUrl))
        {
            dialogs.ShowError("Server package required",
                "Choose an exact version with a provider-confirmed server package.");
            return;
        }
        SourceType = InstallSourceType.DirectUrl;
        Source = version.DownloadUrl;
        MinecraftVersion = version.MinecraftVersion;
        Build = version.VersionName;
        expectedSha1 = version.Sha1;
        expectedSha256 = version.Sha256;
        expectedSha512 = version.Sha512;
        CurrentStep = $"Selected {SelectedCatalogItem?.Name} {version.VersionName}; provider hash verification is required.";
    }

    [RelayCommand]
    private async Task LoadVersionsAsync()
    {
        if (!NeedsCatalogVersion)
            return;
        try
        {
            CurrentStep = "Loading official version metadata…";
            var versions = await client.SendAsync<string[]>("InstallVersions", new InstallVersionsRequest(SourceType))
                .ConfigureAwait(true);
            Versions.Clear();
            foreach (var version in versions)
                Versions.Add(version);
            MinecraftVersion = Versions.FirstOrDefault() ?? "";
            CurrentStep = Versions.Count > 0 ? "Choose a Minecraft version." : "No supported release versions were returned.";
        }
        catch (Exception exception)
        {
            dialogs.ShowError("Could not load versions", exception.Message);
            CurrentStep = exception.Message;
        }
    }

    [RelayCommand]
    private void BrowseSource()
    {
        if (SourceType == InstallSourceType.ExistingPackageFolder)
            Source = dialogs.SelectFolder("Select a local server package folder") ?? Source;
        else if (SourceType == InstallSourceType.LocalZip)
            Source = dialogs.SelectFile("Select a server ZIP or JAR", "Server packages (*.zip;*.jar)|*.zip;*.jar") ?? Source;
    }

    [RelayCommand]
    private void BrowseInstanceRoot() =>
        InstanceRoot = dialogs.SelectFolder("Choose the managed servers directory", InstanceRoot) ?? InstanceRoot;

    [RelayCommand]
    private void BrowseJava() =>
        JavaPath = dialogs.SelectFile("Select java.exe", "Java executable (java.exe)|java.exe") ?? JavaPath;

    [RelayCommand]
    private static void OpenEula() =>
        Process.Start(new ProcessStartInfo("https://www.minecraft.net/eula") { UseShellExecute = true });

    private bool CanInstall() => EulaAccepted && !IsInstalling;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        IsInstalling = true;
        Result = null;
        try
        {
            if (UseManagedJava && SourceType != InstallSourceType.ExistingPackageFolder)
            {
                var requiredMajor = JavaRuntimePolicy.RequiredMajorForMinecraft(MinecraftVersion);
                CurrentStep = $"Selecting a private managed Java {requiredMajor} runtime…";
                var runtimes = await client.SendAsync<ManagedJavaRuntime[]>("ManagedJavaRuntimes")
                    .ConfigureAwait(true);
                var selected = JavaRuntimePolicy.Select(runtimes, new JavaRuntimeRequirement
                {
                    MinimumMajor = requiredMajor,
                    Require64Bit = true,
                    Evidence = $"Minecraft {MinecraftVersion}"
                });
                if (selected is null)
                {
                    CurrentStep = $"Downloading and verifying Eclipse Temurin Java {requiredMajor}…";
                    selected = await client.SendAsync<ManagedJavaRuntime>(
                        "InstallManagedJava", new ManagedJavaInstallRequest(requiredMajor)).ConfigureAwait(true);
                }
                JavaPath = selected.JavaPath;
            }
            var request = new ServerInstallRequest
            {
                SourceType = SourceType,
                Source = Source.Trim(),
                MinecraftVersion = MinecraftVersion.Trim(),
                Build = Build.Trim(),
                ServerName = ServerName.Trim(),
                InstanceRoot = InstanceRoot.Trim(),
                JavaPath = JavaPath.Trim(),
                MinimumRamMb = MinimumRamMb,
                MaximumRamMb = MaximumRamMb,
                Port = Port,
                MaxPlayers = MaxPlayers,
                InitialProperties = PresetAppliesToSource()
                    ? new Dictionary<string, string>(
                        SelectedQuickStart.Properties,
                        StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                EnableDailyBackup = PresetAppliesToSource() &&
                                    SelectedQuickStart.DailyBackup,
                EulaAccepted = EulaAccepted,
                EulaAcceptedAt = DateTimeOffset.Now,
                AllowHttp = AllowHttp,
                ExpectedSha1 = expectedSha1,
                ExpectedSha256 = expectedSha256,
                ExpectedSha512 = expectedSha512
            };
            var started = await client.SendAsync<InstallOperationRequest>("BeginInstall", request).ConfigureAwait(true);
            operationId = started.OperationId;
            while (true)
            {
                await Task.Delay(250).ConfigureAwait(true);
                var snapshot = await client.SendAsync<InstallOperationSnapshot>(
                    "InstallProgress", new InstallOperationRequest(started.OperationId)).ConfigureAwait(true);
                ProgressPercent = snapshot.Progress.OverallPercent;
                CurrentStep = snapshot.Progress.CurrentStep;
                StagingLogPath = snapshot.Progress.StagingLogPath;
                ProgressDetail = snapshot.Progress.TotalBytes is > 0
                    ? $"{snapshot.Progress.BytesDownloaded:N0} / {snapshot.Progress.TotalBytes:N0} bytes · {snapshot.Progress.BytesPerSecond / 1024 / 1024:F1} MB/s"
                    : snapshot.Progress.Detail;
                if (!snapshot.IsTerminal)
                    continue;
                if (!snapshot.Success)
                    throw new InvalidOperationException(snapshot.Error);
                Result = snapshot.Result;
                break;
            }
        }
        catch (OperationCanceledException)
        {
            CurrentStep = "Installation cancelled.";
        }
        catch (Exception exception)
        {
            CurrentStep = "Installation failed.";
            dialogs.ShowError("Server installation failed",
                $"{exception.Message}\n\nStaging log:\n{StagingLogPath}");
        }
        finally
        {
            operationId = null;
            IsInstalling = false;
        }
    }

    private bool PresetAppliesToSource()
    {
        var presetSource = SelectedQuickStart.SourceType == InstallSourceType.CustomPackage
            ? InstallSourceType.LocalZip
            : SelectedQuickStart.SourceType;
        return presetSource == SourceType;
    }

    private bool CanCancel() => IsInstalling && operationId is not null;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync()
    {
        if (operationId is { } id)
            _ = await client.SendAsync<OperationResult>("CancelInstall", new InstallOperationRequest(id)).ConfigureAwait(true);
    }
}
