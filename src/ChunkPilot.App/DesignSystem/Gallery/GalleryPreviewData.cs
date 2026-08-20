using System.Collections.ObjectModel;

namespace ChunkPilot.App.DesignSystem.Gallery;

/// <summary>A navigation destination shaped like the real rail items, for preview only.</summary>
public sealed record GalleryNavigationItem(string Label, string Description, AppIconKind Icon);

/// <summary>A server row for preview only. Every value is invented.</summary>
public sealed record GalleryServer(
    string ServerName,
    string Subtitle,
    string StateText,
    AppTone Tone,
    string Detail);

/// <summary>A table row for preview only.</summary>
public sealed record GalleryBackup(string Created, string Size, string Kind, string Verified);

/// <summary>
/// Synthetic data for the Design Gallery.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is invented and hard-coded. The gallery must never read a real server, a real
/// backup, ChunkPilot's database, AppData, ProgramData, the registry or the agent: a component
/// catalogue that touches live state can damage the thing it is documenting.
/// </para>
/// <para>
/// The names are obviously fictional so a screenshot of the gallery can never be mistaken for a
/// screenshot of somebody's actual setup.
/// </para>
/// </remarks>
public static class GalleryPreviewData
{
    public static ObservableCollection<GalleryNavigationItem> Destinations { get; } =
    [
        new("Dashboard", "Host resources and every managed server", AppIconKind.Home),
        new("Servers", "Switch between managed and imported servers", AppIconKind.Server),
        new("Backups", "Verified archives stored outside server folders", AppIconKind.Backup),
        new("Schedules", "Background work run by the local agent", AppIconKind.Calendar),
        new("Updates", "Staged updates, validation and rollback", AppIconKind.Download),
        new("Activity", "Audited lifecycle and data operations", AppIconKind.History),
        new("Settings", "Application preferences", AppIconKind.Settings)
    ];

    public static ObservableCollection<GalleryServer> Servers { get; } =
    [
        new("Sample Survival", "Paper 1.21.4", "Running", AppTone.Success, "CPU 12%"),
        new("Sample Modpack", "Fabric 1.20.1", "Starting", AppTone.Warning, "CPU 41%"),
        new("Sample Creative", "Vanilla 1.21.4", "Stopped", AppTone.Neutral, "Idle"),
        new("Sample Testing", "Purpur 1.20.6", "Crashed", AppTone.Danger, "Exit 1"),
        new("Sample Bedrock", "Bedrock 1.21.44", "Version unknown", AppTone.Neutral, "Not detected")
    ];

    public static ObservableCollection<GalleryBackup> Backups { get; } =
    [
        new("Today 3:12 PM", "1.4 GiB", "Full", "Verified"),
        new("Today 9:05 AM", "1.4 GiB", "Full", "Verified"),
        new("Yesterday 11:47 PM", "412 MiB", "World only", "Verified"),
        new("Yesterday 6:30 PM", "—", "Full", "Incomplete")
    ];

    public static ObservableCollection<string> Versions { get; } =
    [
        "1.21.4 (current)",
        "1.21.3",
        "1.20.6",
        "1.20.1"
    ];

    public static ObservableCollection<string> ConsoleLines { get; } =
    [
        "[12:04:11] [Server thread/INFO]: Starting minecraft server version 1.21.4",
        "[12:04:12] [Server thread/INFO]: Loading properties",
        "[12:04:12] [Server thread/WARN]: Ambiguity between arguments detected",
        "[12:04:18] [Server thread/INFO]: Preparing level \"world\"",
        "[12:04:24] [Server thread/INFO]: Done (11.903s)! For help, type \"help\"",
        "[12:07:02] [Server thread/INFO]: Saved the game"
    ];
}
