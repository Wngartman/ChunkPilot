namespace ChunkPilot.App.DesignSystem;

/// <summary>
/// The complete semantic icon vocabulary of the ChunkPilot interface.
/// </summary>
/// <remarks>
/// <para>
/// Views never name an icon from the underlying icon package. They name intent, and
/// <see cref="AppIconMap"/> resolves that intent to exactly one glyph. This keeps a
/// concept such as "restart" visually identical everywhere and makes an icon swap a one-line change.
/// </para>
/// <para>
/// Adding a member is a design-system change: extend <see cref="AppIconMap"/>, show the icon in the
/// Design Gallery, and describe the intent in <c>docs/UI-DESIGN-SYSTEM.md</c>. Never add a member
/// whose meaning duplicates an existing one.
/// </para>
/// </remarks>
public enum AppIconKind
{
    // ---- Navigation and destinations ----
    Home,
    Server,
    Settings,
    Backup,
    History,
    Calendar,
    Terminal,
    Globe,
    World,
    People,
    Person,
    PersonAdd,
    Shield,
    ShieldChecked,
    Box,
    Mod,
    Plugin,
    Document,
    Note,
    Chart,
    Hardware,
    Storage,
    Network,
    Lab,
    Diagnostics,
    HeartPulse,

    // ---- Lifecycle actions ----
    Play,
    Stop,
    Pause,
    ArrowRestart,
    Save,
    Send,

    // ---- Content actions ----
    Add,
    Remove,
    Edit,
    Delete,
    Copy,
    Open,
    Folder,
    FolderOpen,
    Download,
    Upload,
    Export,
    Refresh,
    Restore,
    Undo,
    Search,
    Filter,
    Sort,
    Pin,
    Star,
    Link,
    Options,
    More,

    // ---- Security ----
    Lock,
    Key,
    Reveal,
    Conceal,

    // ---- Status and feedback ----
    /// <summary>A bare tick. Selection affordances: check boxes, menu checks, chosen options.</summary>
    Checkmark,

    /// <summary>A tick in a circle. Confirmed successful outcomes only.</summary>
    Success,
    Warning,
    Error,
    Info,
    Question,
    Alert,
    Dismiss,
    Unknown,
    Clock,
    Timer,

    // ---- Directional ----
    ChevronUp,
    ChevronDown,
    ChevronLeft,
    ChevronRight,
    ArrowLeft,
    ArrowRight,

    // ---- Shell chrome ----
    NavigationPanel,
    Sparkle
}
