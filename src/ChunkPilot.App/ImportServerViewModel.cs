using System.Collections.ObjectModel;
using ChunkPilot.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChunkPilot.App;

public sealed partial class ImportServerViewModel : ObservableObject
{
    public ImportServerViewModel(ServerDetectionResult detection)
    {
        Detection = detection;
        Candidates = new ObservableCollection<LaunchCandidate>(detection.Candidates);
        SelectedCandidate = Candidates.FirstOrDefault();
        name = detection.SuggestedName;
        executable = SelectedCandidate?.Executable ?? "";
        arguments = SelectedCandidate?.Arguments ?? "";
        workingDirectory = SelectedCandidate?.WorkingDirectory ?? detection.RootPath;
        readinessPattern = @"Done \(.+?\)!|For help, type";
        shutdownTimeoutSeconds = detection.Ecosystem is ServerEcosystem.Forge or ServerEcosystem.NeoForge ? 120 : 60;
    }

    public ServerDetectionResult Detection { get; }
    public ObservableCollection<LaunchCandidate> Candidates { get; }

    [ObservableProperty]
    private LaunchCandidate? selectedCandidate;

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private string executable;

    [ObservableProperty]
    private string arguments;

    [ObservableProperty]
    private string workingDirectory;

    [ObservableProperty]
    private string readinessPattern;

    [ObservableProperty]
    private int shutdownTimeoutSeconds;

    [ObservableProperty]
    private bool runInBackground = true;

    partial void OnSelectedCandidateChanged(LaunchCandidate? value)
    {
        if (value is null)
            return;
        Executable = value.Executable;
        Arguments = ServerLaunchPolicy.EnsureNoGui(value.Arguments, Detection.Ecosystem, RunInBackground);
        WorkingDirectory = value.WorkingDirectory;
    }

    public ServerDefinition BuildDefinition() => new()
    {
        Name = Name.Trim(),
        RootPath = Detection.RootPath,
        Executable = Executable.Trim(),
        Arguments = ServerLaunchPolicy.EnsureNoGui(Arguments, Detection.Ecosystem, RunInBackground),
        WorkingDirectory = WorkingDirectory.Trim(),
        ReadinessPattern = ReadinessPattern.Trim(),
        ShutdownTimeoutSeconds = Math.Clamp(ShutdownTimeoutSeconds, 5, 900),
        Ecosystem = Detection.Ecosystem,
        MinecraftVersion = Detection.MinecraftVersion,
        LoaderVersion = Detection.LoaderVersion,
        Port = Detection.Port,
        RunInBackground = RunInBackground
    };
}
