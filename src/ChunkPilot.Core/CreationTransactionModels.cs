namespace ChunkPilot.Core;

/// <summary>
/// The phases of a managed server-creation transaction.
/// </summary>
/// <remarks>
/// <para>
/// A phase is only entered once the durable evidence for it exists, so the phase recorded in the
/// journal is always a statement about what has already happened rather than what is about to. That
/// is what makes recovery after a crash a decision rather than a guess.
/// </para>
/// <para>
/// <see cref="CreationPhasePolicy"/> owns the rules for each phase: what may follow it, whether
/// cancellation is safe there, and whether a server may appear in the library.
/// </para>
/// </remarks>
public enum CreationPhase
{
    /// <summary>The request exists. Nothing has been inspected or written.</summary>
    Requested,

    /// <summary>The destination is being evaluated against the destination policy.</summary>
    ValidatingDestination,

    /// <summary>An operation-owned staging directory is being created.</summary>
    PreparingStaging,

    /// <summary>Files are being downloaded, extracted or copied into staging.</summary>
    MaterializingCandidate,

    /// <summary>The staged candidate is being checked before anything is promoted.</summary>
    VerifyingCandidate,

    /// <summary>The candidate is verified and carries its ownership marker. Last safe stop.</summary>
    ReadyToActivate,

    /// <summary>Promotion has begun. The outcome is not yet known.</summary>
    Activating,

    /// <summary>Promotion completed. The destination now holds the candidate.</summary>
    Activated,

    /// <summary>Persistence of the server record has begun. The outcome is not yet known.</summary>
    Registering,

    /// <summary>The server record was written.</summary>
    Registered,

    /// <summary>The written record and the activated directory are being checked against each other.</summary>
    VerifyingRegistration,

    /// <summary>Every invariant held. The server exists and is truthfully registered.</summary>
    Completed,

    /// <summary>Cancellation was requested and the operation is unwinding to a safe checkpoint.</summary>
    Cancelling,

    /// <summary>Owned changes are being reversed after a failure or cancellation.</summary>
    RollingBack,

    /// <summary>Owned changes were reversed. The destination is as it was before the operation.</summary>
    RolledBack,

    /// <summary>Creation succeeded but operation-owned temporary state could not be removed yet.</summary>
    CleanupPending,

    /// <summary>Automatic reconciliation is not provably safe. A person must look.</summary>
    RecoveryRequired,

    /// <summary>The operation failed at a point where nothing was activated.</summary>
    Failed
}

/// <summary>
/// The truthful conclusion of a creation attempt, as it stands right now.
/// </summary>
/// <remarks>
/// Every value answers the only question that matters after an interruption: what is actually true
/// on disk and in the database? None of them is a hopeful summary, and none may be reported without
/// the corresponding evidence.
/// </remarks>
public enum CreationOutcome
{
    /// <summary>Still running. No conclusion yet.</summary>
    InProgress,

    /// <summary>Nothing was activated. Operation-owned staging can be removed safely.</summary>
    NothingActivated,

    /// <summary>Nothing was activated and the staged candidate is still usable.</summary>
    StagingResumable,

    /// <summary>The destination holds the candidate but the server record is not complete.</summary>
    ActivatedRegistrationIncomplete,

    /// <summary>The server record exists but the final checks have not passed.</summary>
    RegisteredVerificationIncomplete,

    /// <summary>Creation completed and every invariant held.</summary>
    Completed,

    /// <summary>Creation completed, but temporary files could not be removed. The server is fine.</summary>
    CompletedWithCleanupWarning,

    /// <summary>Owned changes were reversed. The destination is as it was.</summary>
    RolledBack,

    /// <summary>A person must decide. ChunkPilot will not mutate anything further on its own.</summary>
    RecoveryRequired,

    /// <summary>Cleanup failed, and what is live is nevertheless known and correct.</summary>
    CleanupFailedServerKnown,

    /// <summary>Evidence disagrees with itself. Automatic mutation has stopped.</summary>
    Inconsistent
}

/// <summary>How a verified candidate was promoted into its destination.</summary>
public enum CreationActivationMode
{
    /// <summary>Not yet decided.</summary>
    Undecided,

    /// <summary>A single same-volume directory rename. Atomic as far as the filesystem is concerned.</summary>
    DirectoryMove,

    /// <summary>
    /// A copy across volumes followed by a verified switch. Not filesystem-atomic; the journal and
    /// the ownership marker provide the recovery guarantee instead.
    /// </summary>
    StagedCopy
}

/// <summary>What a recovery pass decided to do with one interrupted operation.</summary>
public enum CreationRecoveryDisposition
{
    /// <summary>No recovery has run against this entry yet.</summary>
    None,

    /// <summary>Nothing had been activated; operation-owned staging was removed.</summary>
    DiscardedStaging,

    /// <summary>Activation was completed and registration was finished from the journal.</summary>
    CompletedRegistration,

    /// <summary>The final checks were run and passed.</summary>
    CompletedVerification,

    /// <summary>Owned changes were reversed.</summary>
    RolledBack,

    /// <summary>Only temporary cleanup remained, and it was retried.</summary>
    RetriedCleanup,

    /// <summary>Nothing was changed because the evidence does not permit a safe decision.</summary>
    AttentionRequired
}

/// <summary>
/// Rules that every phase obeys. Kept beside the enum so a new phase cannot be added without
/// answering the same questions the existing ones answer.
/// </summary>
public static class CreationPhasePolicy
{
    /// <summary>
    /// Phases where a cancellation request can be honoured immediately.
    /// </summary>
    /// <remarks>
    /// Everything up to and including <see cref="CreationPhase.ReadyToActivate"/> has changed nothing
    /// outside operation-owned staging, so stopping there is free.
    /// </remarks>
    public static bool CanCancelSafely(CreationPhase phase) => phase
        is CreationPhase.Requested
        or CreationPhase.ValidatingDestination
        or CreationPhase.PreparingStaging
        or CreationPhase.MaterializingCandidate
        or CreationPhase.VerifyingCandidate
        or CreationPhase.ReadyToActivate;

    /// <summary>
    /// Phases that must not be interrupted part-way.
    /// </summary>
    /// <remarks>
    /// Promotion and persistence are the two places where stopping halfway produces exactly the
    /// ambiguous half-created server this transaction exists to prevent. A cancellation arriving
    /// here is recorded and acted on at the next safe checkpoint.
    /// </remarks>
    public static bool IsCriticalSection(CreationPhase phase) => phase
        is CreationPhase.Activating
        or CreationPhase.Registering
        or CreationPhase.VerifyingRegistration;

    /// <summary>True when the operation has reached a state it will not leave on its own.</summary>
    public static bool IsTerminal(CreationPhase phase) => phase
        is CreationPhase.Completed
        or CreationPhase.RolledBack
        or CreationPhase.RecoveryRequired
        or CreationPhase.Failed;

    /// <summary>
    /// True when a server created by this operation may legitimately appear in the library.
    /// </summary>
    /// <remarks>
    /// Only after registration has been written <em>and</em> verified. <see cref="CreationPhase.CleanupPending"/>
    /// qualifies because it is reached only after verification passed; the outstanding work is a
    /// temporary file, not the server.
    /// </remarks>
    public static bool MayAppearAsServer(CreationPhase phase) => phase
        is CreationPhase.Completed or CreationPhase.CleanupPending;

    /// <summary>The phases that may legally follow <paramref name="phase"/>.</summary>
    public static IReadOnlyList<CreationPhase> AllowedNext(CreationPhase phase) => phase switch
    {
        CreationPhase.Requested => [CreationPhase.ValidatingDestination, CreationPhase.Cancelling, CreationPhase.Failed],
        CreationPhase.ValidatingDestination => [CreationPhase.PreparingStaging, CreationPhase.Cancelling, CreationPhase.Failed],
        CreationPhase.PreparingStaging => [CreationPhase.MaterializingCandidate, CreationPhase.Cancelling, CreationPhase.Failed],
        CreationPhase.MaterializingCandidate => [CreationPhase.VerifyingCandidate, CreationPhase.Cancelling, CreationPhase.Failed],
        CreationPhase.VerifyingCandidate => [CreationPhase.ReadyToActivate, CreationPhase.Cancelling, CreationPhase.Failed],
        CreationPhase.ReadyToActivate => [CreationPhase.Activating, CreationPhase.Cancelling, CreationPhase.Failed],
        CreationPhase.Activating => [CreationPhase.Activated, CreationPhase.RollingBack, CreationPhase.RecoveryRequired, CreationPhase.Failed],
        CreationPhase.Activated => [CreationPhase.Registering, CreationPhase.RollingBack, CreationPhase.RecoveryRequired],
        CreationPhase.Registering => [CreationPhase.Registered, CreationPhase.RollingBack, CreationPhase.RecoveryRequired],
        CreationPhase.Registered => [CreationPhase.VerifyingRegistration, CreationPhase.RecoveryRequired],
        CreationPhase.VerifyingRegistration => [CreationPhase.Completed, CreationPhase.CleanupPending, CreationPhase.RecoveryRequired],
        CreationPhase.Completed => [CreationPhase.CleanupPending],
        CreationPhase.CleanupPending => [CreationPhase.Completed, CreationPhase.CleanupPending],
        CreationPhase.Cancelling => [CreationPhase.RollingBack, CreationPhase.RolledBack, CreationPhase.Failed],
        CreationPhase.RollingBack => [CreationPhase.RolledBack, CreationPhase.RecoveryRequired],
        _ => []
    };

    /// <summary>True when the transition is one the state machine permits.</summary>
    public static bool CanTransition(CreationPhase from, CreationPhase to) =>
        from == to || AllowedNext(from).Contains(to);

    /// <summary>
    /// What the phase means, in words a user reads rather than an enum name.
    /// </summary>
    public static string Describe(CreationPhase phase) => phase switch
    {
        CreationPhase.Requested => "Getting ready",
        CreationPhase.ValidatingDestination => "Checking the folder",
        CreationPhase.PreparingStaging => "Preparing a separate working folder",
        CreationPhase.MaterializingCandidate => "Collecting the server files",
        CreationPhase.VerifyingCandidate => "Checking the files before anything is moved",
        CreationPhase.ReadyToActivate => "Ready to put the server in place",
        CreationPhase.Activating => "Putting the server in place",
        CreationPhase.Activated => "Server files are in place",
        CreationPhase.Registering => "Adding the server to ChunkPilot",
        CreationPhase.Registered => "Added to ChunkPilot",
        CreationPhase.VerifyingRegistration => "Final checks",
        CreationPhase.Completed => "Created",
        CreationPhase.Cancelling => "Stopping safely",
        CreationPhase.RollingBack => "Undoing the changes",
        CreationPhase.RolledBack => "Undone; the folder is as it was",
        CreationPhase.CleanupPending => "Created; tidying up temporary files",
        CreationPhase.RecoveryRequired => "Needs your attention",
        CreationPhase.Failed => "Failed; nothing was put in place",
        _ => "Unknown"
    };

    /// <summary>What the outcome means, in the same register.</summary>
    public static string Describe(CreationOutcome outcome) => outcome switch
    {
        CreationOutcome.InProgress => "Still working.",
        CreationOutcome.NothingActivated => "Nothing was put in place. The folder is untouched.",
        CreationOutcome.StagingResumable => "Nothing was put in place. The prepared files are still available.",
        CreationOutcome.ActivatedRegistrationIncomplete =>
            "The files are in place but the server was not fully added to ChunkPilot.",
        CreationOutcome.RegisteredVerificationIncomplete =>
            "The server was added but the final checks did not pass.",
        CreationOutcome.Completed => "The server was created.",
        CreationOutcome.CompletedWithCleanupWarning =>
            "The server was created. Some temporary files could not be removed yet.",
        CreationOutcome.RolledBack => "The changes were undone. The folder is as it was.",
        CreationOutcome.RecoveryRequired =>
            "ChunkPilot stopped rather than guess. Nothing further was changed.",
        CreationOutcome.CleanupFailedServerKnown =>
            "Temporary files could not be removed. What is on disk and in ChunkPilot is known and correct.",
        CreationOutcome.Inconsistent =>
            "The evidence disagrees with itself, so ChunkPilot stopped changing anything.",
        _ => "Unknown."
    };

    /// <summary>True when the outcome means creation genuinely succeeded.</summary>
    public static bool IsSuccessful(CreationOutcome outcome) => outcome
        is CreationOutcome.Completed or CreationOutcome.CompletedWithCleanupWarning;

    /// <summary>True when a person must look before anything else happens.</summary>
    public static bool NeedsUserAttention(CreationOutcome outcome) => outcome
        is CreationOutcome.RecoveryRequired
        or CreationOutcome.Inconsistent
        or CreationOutcome.ActivatedRegistrationIncomplete
        or CreationOutcome.RegisteredVerificationIncomplete;
}

/// <summary>
/// What a creation operation is doing right now, in the vocabulary a beginner reads.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="CreationPhase"/>. The phase is the transaction's own state
/// machine and exists to make recovery a decision rather than a guess; the stage is what the user is
/// told. Keeping them apart means the progress wording can be exact — "Downloading the server",
/// "Preparing Java" — without the interface ever showing an internal enum name or an interface change
/// forcing a state-machine change.
/// </para>
/// <para>
/// The stage is reported by whoever knows: the coordinator names the runtime steps it performs, the
/// installer names the download and its verification, and the transaction derives the rest from its
/// phase through <see cref="CreationStagePolicy.ForPhase"/>.
/// </para>
/// </remarks>
public enum CreationStage
{
    /// <summary>Nothing has been submitted. The zero value, so an unset stage never claims work.</summary>
    NotStarted,

    /// <summary>The App has sent the plan and is waiting for the operation to be accepted.</summary>
    Submitting,

    /// <summary>Accepted, and the first checks are running.</summary>
    Preparing,

    /// <summary>The destination folder is being checked.</summary>
    CheckingFolder,

    /// <summary>A compatible Java runtime is being found or obtained.</summary>
    PreparingJava,

    /// <summary>An obtained Java runtime is being checked against its published checksum.</summary>
    VerifyingJava,

    /// <summary>The server download is in progress. The only stage with real byte progress.</summary>
    DownloadingServer,

    /// <summary>The downloaded server is being checked against the provider's published hash.</summary>
    VerifyingServerDownload,

    /// <summary>Configuration and the accepted EULA file are being written into the staged candidate.</summary>
    PreparingServerFiles,

    /// <summary>The staged candidate is being checked before anything is moved.</summary>
    FinalSafetyCheck,

    /// <summary>The verified candidate is being put in place. Not interruptible.</summary>
    Activating,

    /// <summary>The server is being added to ChunkPilot. Not interruptible.</summary>
    Registering,

    /// <summary>What was written is being read back and checked. Not interruptible.</summary>
    FinalVerification,

    /// <summary>Temporary files are being removed.</summary>
    CleaningUp,

    /// <summary>Cancelling was asked for and can be honoured now.</summary>
    CancellingSafely,

    /// <summary>Cancelling was asked for during a step that must finish first.</summary>
    WaitingForSafeCheckpoint,

    /// <summary>Owned changes are being reversed.</summary>
    RollingBack,

    /// <summary>An interrupted operation is being reconciled.</summary>
    Recovering,

    /// <summary>Created, checked and registered.</summary>
    Completed,

    /// <summary>Created and registered; some temporary files could not be removed.</summary>
    CompletedWithCleanupWarning,

    /// <summary>Stopped before anything was put in place.</summary>
    Cancelled,

    /// <summary>Stopped at a point where nothing was put in place.</summary>
    FailedNothingChanged,

    /// <summary>Failed after a change was made, and that change was reversed.</summary>
    FailedRolledBack,

    /// <summary>ChunkPilot stopped rather than guess. A person must look.</summary>
    RecoveryRequired
}

/// <summary>
/// The one place a stage's wording and its interruptibility are decided.
/// </summary>
/// <remarks>
/// Wording lives here rather than in XAML so the progress line, the completion headline and a test
/// assertion all read the same sentence, and so no stage can be added without being given one.
/// </remarks>
public static class CreationStagePolicy
{
    /// <summary>The stage a transaction phase corresponds to.</summary>
    public static CreationStage ForPhase(CreationPhase phase) => phase switch
    {
        CreationPhase.Requested => CreationStage.Preparing,
        CreationPhase.ValidatingDestination => CreationStage.CheckingFolder,
        CreationPhase.PreparingStaging => CreationStage.Preparing,
        CreationPhase.MaterializingCandidate => CreationStage.PreparingServerFiles,
        CreationPhase.VerifyingCandidate => CreationStage.FinalSafetyCheck,
        CreationPhase.ReadyToActivate or CreationPhase.Activating => CreationStage.Activating,
        CreationPhase.Activated or CreationPhase.Registering or CreationPhase.Registered =>
            CreationStage.Registering,
        CreationPhase.VerifyingRegistration => CreationStage.FinalVerification,
        CreationPhase.Completed => CreationStage.Completed,
        CreationPhase.CleanupPending => CreationStage.CleaningUp,
        CreationPhase.Cancelling => CreationStage.CancellingSafely,
        CreationPhase.RollingBack => CreationStage.RollingBack,
        CreationPhase.RolledBack => CreationStage.FailedRolledBack,
        CreationPhase.RecoveryRequired => CreationStage.RecoveryRequired,
        CreationPhase.Failed => CreationStage.FailedNothingChanged,
        _ => CreationStage.NotStarted
    };

    /// <summary>What the stage means, addressed to the person watching it.</summary>
    public static string Describe(CreationStage stage) => stage switch
    {
        CreationStage.NotStarted => "Not started",
        CreationStage.Submitting => "Starting",
        CreationStage.Preparing => "Getting ready",
        CreationStage.CheckingFolder => "Checking the folder",
        CreationStage.PreparingJava => "Preparing Java",
        CreationStage.VerifyingJava => "Verifying Java",
        CreationStage.DownloadingServer => "Downloading the server",
        CreationStage.VerifyingServerDownload => "Verifying the download",
        CreationStage.PreparingServerFiles => "Preparing the server files",
        CreationStage.FinalSafetyCheck => "Running a final safety check",
        CreationStage.Activating => "Putting the server in place",
        CreationStage.Registering => "Adding the server to ChunkPilot",
        CreationStage.FinalVerification => "Final checks",
        CreationStage.CleaningUp => "Tidying up temporary files",
        CreationStage.CancellingSafely => "Stopping safely",
        CreationStage.WaitingForSafeCheckpoint => "Waiting for a safe point to stop",
        CreationStage.RollingBack => "Undoing the changes",
        CreationStage.Recovering => "Sorting out an interrupted attempt",
        CreationStage.Completed => "Created",
        CreationStage.CompletedWithCleanupWarning => "Created; some temporary files remain",
        CreationStage.Cancelled => "Stopped; nothing was put in place",
        CreationStage.FailedNothingChanged => "Stopped; the folder was not changed",
        CreationStage.FailedRolledBack => "Stopped; the changes were undone",
        CreationStage.RecoveryRequired => "Stopped to protect your files",
        _ => "Working"
    };

    /// <summary>True when a cancellation request can be acted on the moment it arrives.</summary>
    public static bool AllowsImmediateCancellation(CreationStage stage) => stage
        is CreationStage.Submitting
        or CreationStage.Preparing
        or CreationStage.CheckingFolder
        or CreationStage.PreparingJava
        or CreationStage.VerifyingJava
        or CreationStage.DownloadingServer
        or CreationStage.VerifyingServerDownload
        or CreationStage.PreparingServerFiles
        or CreationStage.FinalSafetyCheck;

    /// <summary>True when stopping half-way would produce exactly the ambiguity the transaction prevents.</summary>
    public static bool IsCriticalSection(CreationStage stage) => stage
        is CreationStage.Activating or CreationStage.Registering or CreationStage.FinalVerification;

    /// <summary>True when the operation has reached a state it will not leave on its own.</summary>
    public static bool IsTerminal(CreationStage stage) => stage
        is CreationStage.Completed
        or CreationStage.CompletedWithCleanupWarning
        or CreationStage.Cancelled
        or CreationStage.FailedNothingChanged
        or CreationStage.FailedRolledBack
        or CreationStage.RecoveryRequired;

    /// <summary>True when the outcome is one the user can open a server from.</summary>
    public static bool IsSuccessful(CreationStage stage) => stage
        is CreationStage.Completed or CreationStage.CompletedWithCleanupWarning;

    /// <summary>The only coherent user-visible stage for a successful creation outcome.</summary>
    public static CreationStage ForSuccessfulOutcome(CreationOutcome outcome) => outcome switch
    {
        CreationOutcome.Completed => CreationStage.Completed,
        CreationOutcome.CompletedWithCleanupWarning => CreationStage.CompletedWithCleanupWarning,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome,
            "A non-successful outcome cannot be represented as successful creation progress.")
    };
}

/// <summary>Why a destination was accepted or refused.</summary>
public enum CreationDestinationVerdict
{
    /// <summary>The path does not exist and may be created.</summary>
    Available,

    /// <summary>The path exists, is a directory, and is provably empty.</summary>
    AvailableEmpty,

    /// <summary>The path exists and contains files that ChunkPilot did not put there.</summary>
    BlockedNotEmpty,

    /// <summary>A file, not a directory, already occupies the path.</summary>
    BlockedFileExists,

    /// <summary>A managed server is already registered at this exact path.</summary>
    BlockedManagedServer,

    /// <summary>An imported server is registered at this path. Imported folders are never taken over.</summary>
    BlockedImportedServer,

    /// <summary>The path is inside a server ChunkPilot already knows about.</summary>
    BlockedInsideKnownServer,

    /// <summary>The path contains a server ChunkPilot already knows about.</summary>
    BlockedContainsKnownServer,

    /// <summary>Another creation operation is already using this path.</summary>
    BlockedActiveOperation,

    /// <summary>The path is a junction, symlink or other reparse point.</summary>
    BlockedReparsePoint,

    /// <summary>The path overlaps its own staging directory, or is otherwise not a safe target.</summary>
    BlockedUnsafePath
}

/// <summary>The destination policy's decision, with the wording the user sees.</summary>
/// <param name="Verdict">Why the destination was accepted or refused.</param>
/// <param name="CanonicalPath">The destination after canonicalisation.</param>
/// <param name="Message">Plain language: what is true, what was changed, and what to do next.</param>
/// <param name="DestinationExisted">True when the directory already existed before the operation.</param>
public sealed record CreationDestinationDecision(
    CreationDestinationVerdict Verdict,
    string CanonicalPath,
    string Message,
    bool DestinationExisted)
{
    /// <summary>True when creation may proceed against this destination.</summary>
    public bool IsAllowed => Verdict is CreationDestinationVerdict.Available
        or CreationDestinationVerdict.AvailableEmpty;
}

/// <summary>
/// The durable record of one creation transaction.
/// </summary>
/// <remarks>
/// <para>
/// Written before the side effect it describes becomes possible, and updated once that side effect
/// is known to have completed. The boolean evidence flags are deliberately separate from
/// <see cref="Phase"/>: the phase says where the operation believed it was, the flags say what is
/// known to have actually happened, and recovery trusts the flags.
/// </para>
/// <para>
/// Holds no secrets, no provider credentials and no payload. Everything here is either an
/// identifier, a canonical path, a timestamp or a small enumeration.
/// </para>
/// </remarks>
public sealed record CreationJournalEntry
{
    /// <summary>The shape this record was written with. A newer shape is never guessed at.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid OperationId { get; init; }

    /// <summary>The identity the server will have. Fixed before activation so recovery can match it.</summary>
    public Guid ServerId { get; init; }

    /// <summary>What is being created, for example the install source type.</summary>
    public string CreationKind { get; init; } = "";

    public string ServerName { get; init; } = "";
    public string CanonicalDestination { get; init; } = "";
    public string CanonicalStaging { get; init; } = "";
    public string InstanceRoot { get; init; } = "";
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public CreationPhase Phase { get; init; } = CreationPhase.Requested;

    /// <summary>The last phase whose durable side effect is known to have completed.</summary>
    public CreationPhase LastCompletedCheckpoint { get; init; } = CreationPhase.Requested;

    /// <summary>True when the destination directory already existed and was accepted as empty.</summary>
    public bool DestinationExistedBefore { get; init; }

    public bool ActivationBegan { get; init; }
    public bool ActivationCompleted { get; init; }
    public CreationActivationMode ActivationMode { get; init; } = CreationActivationMode.Undecided;
    public bool RegistrationBegan { get; init; }
    public bool RegistrationCompleted { get; init; }
    public bool VerificationPassed { get; init; }
    public bool CancellationRequested { get; init; }
    public CreationOutcome Outcome { get; init; } = CreationOutcome.InProgress;
    public string RollbackState { get; init; } = "";
    public string CleanupState { get; init; } = "";

    /// <summary>Redacted, structured description of the last failure. Never a raw payload.</summary>
    public string LastError { get; init; } = "";

    /// <summary>How many times startup recovery has already tried this entry.</summary>
    public int RecoveryAttempts { get; init; }

    public CreationRecoveryDisposition RecoveryDisposition { get; init; } = CreationRecoveryDisposition.None;

    /// <summary>File name of the ownership marker written into the candidate before activation.</summary>
    public string OwnershipMarkerFile { get; init; } = "";

    /// <summary>
    /// When the user deliberately accepted the Minecraft EULA for this creation.
    /// </summary>
    /// <remarks>
    /// Default when no acceptance was required or recorded. Recovery will not finish registering a
    /// Minecraft server on the strength of a folder alone: the acceptance has to be durable, and the
    /// journal is where it survives a crash. The legal text itself is never stored.
    /// </remarks>
    public DateTimeOffset EulaAcceptedUtc { get; init; }

    /// <summary>The official EULA location that was shown when acceptance was given.</summary>
    public string EulaSourceUrl { get; init; } = "";

    /// <summary>
    /// The server exactly as it will be registered, recorded once the candidate is verified.
    /// </summary>
    /// <remarks>
    /// Without it, an operation interrupted between activation and registration could only ever be
    /// handed to a person: the folder would be provably ours and there would still be nothing to
    /// write. It is the same record the servers table holds — identity, paths, launch profile and
    /// limits — and contains no secret, so keeping a copy costs nothing and turns a dead end into a
    /// resumable checkpoint.
    /// </remarks>
    public ServerDefinition? PlannedDefinition { get; init; }
}

/// <summary>
/// A journal row as it was read back, including rows this build cannot interpret.
/// </summary>
/// <param name="OperationId">The row's key, which is readable even when the payload is not.</param>
/// <param name="SchemaVersion">The version recorded on the row.</param>
/// <param name="Entry">The parsed entry, or null when the row cannot be interpreted.</param>
/// <param name="UnreadableReason">Why the row could not be interpreted. Empty when it could.</param>
public sealed record CreationJournalRecord(
    Guid OperationId,
    int SchemaVersion,
    CreationJournalEntry? Entry,
    string UnreadableReason)
{
    /// <summary>True when this build can safely act on the row.</summary>
    public bool IsReadable => Entry is not null && UnreadableReason.Length == 0;
}

/// <summary>The result of checking an activated, registered server against its journal.</summary>
/// <param name="Passed">True only when every invariant held.</param>
/// <param name="Failures">The invariants that did not hold, in the order they were checked.</param>
public sealed record CreationVerificationResult(bool Passed, IReadOnlyList<string> Failures)
{
    public static CreationVerificationResult Success { get; } = new(true, []);
}

/// <summary>What one recovery pass did with one interrupted operation.</summary>
/// <param name="OperationId">The operation the entry belongs to.</param>
/// <param name="Disposition">What was decided.</param>
/// <param name="Outcome">The truthful conclusion after the pass.</param>
/// <param name="Detail">Plain-language explanation, suitable for activity history.</param>
public sealed record CreationRecoveryReport(
    Guid OperationId,
    CreationRecoveryDisposition Disposition,
    CreationOutcome Outcome,
    string Detail);
