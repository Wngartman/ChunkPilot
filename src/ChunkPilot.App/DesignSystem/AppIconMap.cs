using FluentGlyph = FluentIcons.Common.Icon;

namespace ChunkPilot.App.DesignSystem;

/// <summary>
/// The single place where ChunkPilot's semantic icon vocabulary meets the icon package.
/// </summary>
/// <remarks>
/// Nothing else in the application may reference <c>FluentIcons</c> types. Enforced by
/// <c>DesignSystemContractTests</c>.
/// </remarks>
public static class AppIconMap
{
    /// <summary>Resolves a semantic icon to its FluentIcons glyph.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when a new <see cref="AppIconKind"/> member has no mapping. Failing loudly is
    /// deliberate: a silent fallback glyph is how icon drift starts.
    /// </exception>
    public static FluentGlyph Resolve(AppIconKind kind) => kind switch
    {
        AppIconKind.Home => FluentGlyph.Home,
        AppIconKind.Server => FluentGlyph.Server,
        AppIconKind.Settings => FluentGlyph.Settings,
        AppIconKind.Backup => FluentGlyph.Archive,
        AppIconKind.History => FluentGlyph.History,
        AppIconKind.Calendar => FluentGlyph.Calendar,
        // WindowConsole renders blank at the small navigation size in the current FluentIcons
        // font. Code is the legible command-console mark at 16 DIP.
        AppIconKind.Terminal => FluentGlyph.Code,
        AppIconKind.Globe => FluentGlyph.Globe,
        AppIconKind.World => FluentGlyph.Earth,
        AppIconKind.People => FluentGlyph.People,
        AppIconKind.Person => FluentGlyph.Person,
        AppIconKind.PersonAdd => FluentGlyph.PersonAdd,
        AppIconKind.Shield => FluentGlyph.Shield,
        AppIconKind.ShieldChecked => FluentGlyph.ShieldCheckmark,
        AppIconKind.Box => FluentGlyph.Box,
        AppIconKind.Mod => FluentGlyph.Cube,
        AppIconKind.Plugin => FluentGlyph.PuzzleCube,
        AppIconKind.Document => FluentGlyph.Document,
        AppIconKind.Note => FluentGlyph.Note,
        AppIconKind.Chart => FluentGlyph.DataTrending,
        AppIconKind.Hardware => FluentGlyph.DeveloperBoard,
        AppIconKind.Storage => FluentGlyph.Storage,
        AppIconKind.Network => FluentGlyph.Router,
        AppIconKind.Lab => FluentGlyph.Beaker,
        AppIconKind.Diagnostics => FluentGlyph.Bug,
        AppIconKind.HeartPulse => FluentGlyph.HeartPulse,

        AppIconKind.Play => FluentGlyph.Play,
        AppIconKind.Stop => FluentGlyph.Stop,
        AppIconKind.Pause => FluentGlyph.Pause,
        AppIconKind.ArrowRestart => FluentGlyph.ArrowClockwise,
        AppIconKind.Save => FluentGlyph.Save,
        AppIconKind.Send => FluentGlyph.Send,

        AppIconKind.Add => FluentGlyph.Add,
        AppIconKind.Remove => FluentGlyph.Subtract,
        AppIconKind.Edit => FluentGlyph.Edit,
        AppIconKind.Delete => FluentGlyph.Delete,
        AppIconKind.Copy => FluentGlyph.Copy,
        AppIconKind.Open => FluentGlyph.Open,
        AppIconKind.Folder => FluentGlyph.Folder,
        AppIconKind.FolderOpen => FluentGlyph.FolderOpen,
        AppIconKind.Download => FluentGlyph.ArrowDownload,
        AppIconKind.Upload => FluentGlyph.ArrowUpload,
        AppIconKind.Export => FluentGlyph.ArrowExport,
        AppIconKind.Refresh => FluentGlyph.ArrowSync,
        AppIconKind.Restore => FluentGlyph.ArrowCounterclockwise,
        AppIconKind.Undo => FluentGlyph.ArrowUndo,
        AppIconKind.Search => FluentGlyph.Search,
        AppIconKind.Filter => FluentGlyph.Filter,
        AppIconKind.Sort => FluentGlyph.ArrowSort,
        AppIconKind.Pin => FluentGlyph.Pin,
        AppIconKind.Star => FluentGlyph.Star,
        AppIconKind.Link => FluentGlyph.Link,
        AppIconKind.Options => FluentGlyph.Options,
        AppIconKind.More => FluentGlyph.MoreHorizontal,

        AppIconKind.Lock => FluentGlyph.LockClosed,
        AppIconKind.Key => FluentGlyph.Key,
        AppIconKind.Reveal => FluentGlyph.Eye,
        AppIconKind.Conceal => FluentGlyph.EyeOff,

        AppIconKind.Checkmark => FluentGlyph.Checkmark,
        AppIconKind.Success => FluentGlyph.CheckmarkCircle,
        AppIconKind.Warning => FluentGlyph.Warning,
        AppIconKind.Error => FluentGlyph.ErrorCircle,
        AppIconKind.Info => FluentGlyph.Info,
        AppIconKind.Question => FluentGlyph.Question,
        AppIconKind.Alert => FluentGlyph.Alert,
        AppIconKind.Dismiss => FluentGlyph.Dismiss,
        AppIconKind.Unknown => FluentGlyph.QuestionCircle,
        AppIconKind.Clock => FluentGlyph.Clock,
        AppIconKind.Timer => FluentGlyph.Timer,

        AppIconKind.ChevronUp => FluentGlyph.ChevronUp,
        AppIconKind.ChevronDown => FluentGlyph.ChevronDown,
        AppIconKind.ChevronLeft => FluentGlyph.ChevronLeft,
        AppIconKind.ChevronRight => FluentGlyph.ChevronRight,
        AppIconKind.ArrowLeft => FluentGlyph.ArrowLeft,
        AppIconKind.ArrowRight => FluentGlyph.ArrowRight,

        AppIconKind.NavigationPanel => FluentGlyph.PanelLeft,
        AppIconKind.Sparkle => FluentGlyph.Sparkle,

        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "Every AppIconKind must be mapped in AppIconMap.Resolve.")
    };
}
