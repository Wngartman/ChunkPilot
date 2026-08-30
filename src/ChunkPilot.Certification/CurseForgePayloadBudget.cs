using System.Security.Cryptography;
using System.Text.Json;

namespace ChunkPilot.Certification;

internal sealed record CurseForgePayloadLedger
{
    public const string ExpectedDocumentType = "ChunkPilot.CurseForgePayloadLedger";
    public const int CurrentSchemaVersion = 2;
    public string DocumentType { get; init; } = ExpectedDocumentType;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public long BudgetBytes { get; init; } = CurseForgePayloadBudget.MaximumBytes;
    public IReadOnlyList<CurseForgePayloadLedgerEntry> Entries { get; init; } = [];
}

internal sealed record CurseForgePayloadLedgerEntry
{
    public Guid OperationId { get; init; }
    public string Kind { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string FileId { get; init; } = "";
    public long ExpectedBytes { get; init; }
    public long DownloadedBytes { get; init; }
    public string ProviderIdentityFingerprint { get; init; } = "";
    public string LocalSha256 { get; init; } = "";
    public string State { get; init; } = "Reserved";
    public bool TransferCompleted { get; init; }
    public DateTimeOffset ReservedAtUtc { get; init; }
    public DateTimeOffset? OutcomeRecordedAtUtc { get; init; }
}

internal sealed record CurseForgePayloadBudgetSnapshot(
    long LimitBytes,
    long GuardedCumulativeBytes,
    long CompletedCumulativeBytes,
    int UnknownOrInterruptedReservationCount,
    long UnknownOrInterruptedReservedBytes,
    long ObservedIncompleteBytes);

internal sealed record CurseForgePayloadReservationRequest(
    Guid OperationId,
    string Kind,
    string ProjectId,
    string FileId,
    long ExpectedBytes,
    string ProviderSha1);

/// <summary>
/// Durable conservative accounting for live CurseForge payloads. A full expected size is reserved
/// atomically before the provider download begins. Interrupted reservations continue to count toward
/// the ceiling, so a crash can overcount but can never silently permit more than the hard limit.
/// </summary>
internal sealed class CurseForgePayloadBudget(string ledgerPath)
{
    public const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    internal const long MaximumLedgerBytes = 4L * 1024 * 1024;
    internal const int MaximumLedgerEntries = 10_000;
    private readonly string path = Path.GetFullPath(ledgerPath);
    private readonly SemaphoreSlim gate = new(1, 1);
    private string LockPath => path + ".lock";

    public async Task<CurseForgePayloadBudgetSnapshot> ReserveAsync(
        Guid operationId,
        string kind,
        string projectId,
        string fileId,
        long expectedBytes,
        string providerSha1,
        CancellationToken cancellationToken)
        => await ReserveBatchAsync(
            [new CurseForgePayloadReservationRequest(
                operationId, kind, projectId, fileId, expectedBytes, providerSha1)],
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Validates and persists a related set of payload reservations with one durable ledger write.
    /// Either every reservation is present after this method succeeds, or none of this batch is
    /// added. This prevents a generated-pack plan from consuming a partial series of reservations
    /// when a later item or the ledger write fails.
    /// </summary>
    public async Task<CurseForgePayloadBudgetSnapshot> ReserveBatchAsync(
        IReadOnlyList<CurseForgePayloadReservationRequest> reservations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservations);
        if (reservations.Count == 0)
            throw new ArgumentException("At least one payload reservation is required.", nameof(reservations));
        var requestedIdentities = new HashSet<string>(StringComparer.Ordinal);
        long requestedBytes = 0;
        foreach (var reservation in reservations)
        {
            ArgumentNullException.ThrowIfNull(reservation);
            ValidateIdentity(
                reservation.OperationId, reservation.Kind, reservation.ProjectId,
                reservation.FileId, reservation.ExpectedBytes, reservation.ProviderSha1);
            if (!requestedIdentities.Add($"{reservation.OperationId:N}:{reservation.Kind}"))
                throw new ArgumentException(
                    "A payload reservation batch contains a duplicate operation identity.",
                    nameof(reservations));
            requestedBytes = checked(requestedBytes + reservation.ExpectedBytes);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = AcquireProcessLock();
            var ledger = await ReadAsync(cancellationToken).ConfigureAwait(false);
            foreach (var reservation in reservations)
            {
                var existing = ledger.Entries.FirstOrDefault(entry =>
                    entry.OperationId == reservation.OperationId &&
                    entry.Kind.Equals(reservation.Kind, StringComparison.Ordinal));
                if (existing is null)
                    continue;
                if (!Matches(
                        existing, reservation.ProjectId, reservation.FileId,
                        reservation.ExpectedBytes, reservation.ProviderSha1))
                    throw new InvalidDataException(
                        "The payload-budget operation identity was already reserved for different provider evidence.");
                throw new InvalidOperationException(
                    "The exact payload transfer reservation was already issued; replay is refused and must use a fresh operation identity.");
            }
            if (reservations.Count > MaximumLedgerEntries - ledger.Entries.Count)
                throw new InvalidDataException(
                    $"The CurseForge payload ledger reached its {MaximumLedgerEntries}-entry safety limit.");

            var guarded = GuardedBytes(ledger);
            if (requestedBytes > MaximumBytes - guarded)
                throw new InvalidOperationException(
                    $"The next CurseForge payload batch would exceed the hard {MaximumBytes} byte campaign limit before download.");
            var reservedAtUtc = DateTimeOffset.UtcNow;
            ledger = ledger with
            {
                Entries = ledger.Entries.Concat(
                    reservations.Select(reservation => new CurseForgePayloadLedgerEntry
                    {
                        OperationId = reservation.OperationId,
                        Kind = reservation.Kind,
                        ProjectId = reservation.ProjectId,
                        FileId = reservation.FileId,
                        ExpectedBytes = reservation.ExpectedBytes,
                        ProviderIdentityFingerprint = ProviderFingerprint(reservation.ProviderSha1),
                        ReservedAtUtc = reservedAtUtc
                    })).ToArray()
            };
            await WriteAsync(ledger, cancellationToken).ConfigureAwait(false);
            return Snapshot(ledger);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CurseForgePayloadBudgetSnapshot> CompleteAsync(
        Guid operationId,
        string kind,
        long downloadedBytes,
        string localSha256,
        string state,
        bool transferCompleted,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(downloadedBytes);
        if (localSha256.Length > 0 &&
            (localSha256.Length != 64 || localSha256.Any(character => !Uri.IsHexDigit(character))))
            throw new ArgumentException("A local payload SHA-256 must be a 64-character hexadecimal digest.");
        if (string.IsNullOrWhiteSpace(state) || state.Length > 48)
            throw new ArgumentException("A bounded payload state is required.");

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = AcquireProcessLock();
            var ledger = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var existing = ledger.Entries.SingleOrDefault(entry =>
                               entry.OperationId == operationId && entry.Kind.Equals(kind, StringComparison.Ordinal))
                           ?? throw new InvalidOperationException(
                               "The payload outcome cannot be recorded because no matching reservation exists.");
            if (existing.OutcomeRecordedAtUtc is not null)
                throw new InvalidOperationException(
                    "The exact payload reservation already has a terminal observed outcome.");
            if (downloadedBytes > existing.ExpectedBytes)
                throw new InvalidDataException("Observed payload bytes exceeded the pre-download reservation.");
            if (transferCompleted &&
                (downloadedBytes != existing.ExpectedBytes || localSha256.Length != 64))
                throw new InvalidDataException(
                    "A completed payload requires its full reserved byte count and local SHA-256.");
            if (!transferCompleted && localSha256.Length > 0)
                throw new InvalidDataException(
                    "An unknown or interrupted payload cannot claim a completed local SHA-256.");
            var updated = existing with
            {
                DownloadedBytes = Math.Max(existing.DownloadedBytes, downloadedBytes),
                LocalSha256 = localSha256.Length > 0
                    ? localSha256.ToLowerInvariant()
                    : existing.LocalSha256,
                State = state,
                TransferCompleted = transferCompleted,
                OutcomeRecordedAtUtc = DateTimeOffset.UtcNow
            };
            ledger = ledger with
            {
                Entries = ledger.Entries.Select(entry => ReferenceEquals(entry, existing) ? updated : entry)
                    .ToArray()
            };
            await WriteAsync(ledger, cancellationToken).ConfigureAwait(false);
            return Snapshot(ledger);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CurseForgePayloadBudgetSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = AcquireProcessLock();
            return Snapshot(await ReadAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CurseForgePayloadLedger> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return new CurseForgePayloadLedger();
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            info.Length is < 1 or > MaximumLedgerBytes)
            throw new InvalidDataException(
                $"The CurseForge payload ledger must be a regular file no larger than {MaximumLedgerBytes} bytes.");
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 1 or > MaximumLedgerBytes)
            throw new InvalidDataException(
                $"The CurseForge payload ledger must be no larger than {MaximumLedgerBytes} bytes.");
        var serialized = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(serialized, cancellationToken).ConfigureAwait(false);
        if (stream.Length > MaximumLedgerBytes)
            throw new InvalidDataException(
                $"The CurseForge payload ledger exceeded {MaximumLedgerBytes} bytes while being read.");
        CurseForgePayloadLedger ledger;
        try
        {
            ValidateSerializedEntryCount(serialized);
            ledger = JsonSerializer.Deserialize<CurseForgePayloadLedger>(
                         serialized, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                     ?? throw new InvalidDataException(
                         "The CurseForge payload ledger is empty or malformed.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The CurseForge payload ledger is malformed.", exception);
        }
        ValidateLedger(ledger);
        return ledger;
    }

    internal static void ValidateSerializedEntryCount(ReadOnlySpan<byte> serialized)
    {
        var reader = new Utf8JsonReader(
            serialized,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        var entriesFound = false;
        var waitingForEntriesValue = false;
        var inEntries = false;
        var entriesDepth = -1;
        var count = 0;
        while (reader.Read())
        {
            if (!inEntries && reader.TokenType == JsonTokenType.PropertyName &&
                reader.CurrentDepth == 1 && reader.ValueTextEquals("entries"u8))
            {
                if (entriesFound)
                    throw new InvalidDataException(
                        "The CurseForge payload ledger contains duplicate entry collections.");
                entriesFound = true;
                waitingForEntriesValue = true;
                continue;
            }
            if (waitingForEntriesValue)
            {
                if (reader.TokenType != JsonTokenType.StartArray)
                    throw new InvalidDataException(
                        "The CurseForge payload ledger entry collection is not an array.");
                waitingForEntriesValue = false;
                inEntries = true;
                entriesDepth = reader.CurrentDepth;
                continue;
            }
            if (!inEntries)
                continue;
            if (reader.TokenType == JsonTokenType.EndArray &&
                reader.CurrentDepth == entriesDepth)
            {
                inEntries = false;
                continue;
            }
            if (reader.CurrentDepth == entriesDepth + 1 &&
                reader.TokenType is (JsonTokenType.StartObject or JsonTokenType.StartArray or
                    JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or
                    JsonTokenType.False or JsonTokenType.Null) &&
                ++count > MaximumLedgerEntries)
                throw new InvalidDataException(
                    $"The CurseForge payload ledger exceeds its {MaximumLedgerEntries}-entry safety limit.");
        }
        if (!entriesFound || waitingForEntriesValue || inEntries)
            throw new InvalidDataException(
                "The CurseForge payload ledger does not contain one complete bounded entry collection.");
    }

    private async Task WriteAsync(CurseForgePayloadLedger ledger, CancellationToken cancellationToken)
    {
        ValidateLedger(ledger);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
            File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                "The CurseForge payload ledger cannot be written through a reparse point.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream, ledger,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true },
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (new FileInfo(temporary).Length > MaximumLedgerBytes)
                throw new InvalidDataException(
                    $"The CurseForge payload ledger exceeded its {MaximumLedgerBytes}-byte safety limit.");
            if (File.Exists(path) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "The CurseForge payload ledger target became a reparse point.");
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void ValidateLedger(CurseForgePayloadLedger ledger)
    {
        if (!string.Equals(
                ledger.DocumentType,
                CurseForgePayloadLedger.ExpectedDocumentType,
                StringComparison.Ordinal) ||
            ledger.SchemaVersion != CurseForgePayloadLedger.CurrentSchemaVersion ||
            ledger.BudgetBytes != MaximumBytes ||
            ledger.Entries is null ||
            ledger.Entries.Count > MaximumLedgerEntries)
            throw new InvalidDataException("The CurseForge payload ledger schema or hard limit is unsupported.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in ledger.Entries)
        {
            if (entry is null ||
                string.IsNullOrWhiteSpace(entry.ProviderIdentityFingerprint) ||
                entry.ProviderIdentityFingerprint.Length != 64 ||
                entry.ProviderIdentityFingerprint.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException("The CurseForge payload ledger lacks its one-way provider identity fingerprint.");
            ValidateStoredIdentity(entry);
            if (entry.ReservedAtUtc == default ||
                entry.DownloadedBytes < 0 || entry.DownloadedBytes > entry.ExpectedBytes ||
                entry.LocalSha256 is null || string.IsNullOrWhiteSpace(entry.State) ||
                entry.State.Length > 48 ||
                entry.LocalSha256.Length > 0 &&
                (entry.LocalSha256.Length != 64 || entry.LocalSha256.Any(character => !Uri.IsHexDigit(character))) ||
                entry.TransferCompleted &&
                (entry.DownloadedBytes != entry.ExpectedBytes || entry.LocalSha256.Length != 64 ||
                 entry.OutcomeRecordedAtUtc is null) ||
                !entry.TransferCompleted && entry.LocalSha256.Length > 0 ||
                entry.OutcomeRecordedAtUtc is null &&
                (!entry.State.Equals("Reserved", StringComparison.Ordinal) ||
                 entry.DownloadedBytes != 0) ||
                entry.OutcomeRecordedAtUtc is not null &&
                !entry.TransferCompleted &&
                entry.State.Equals("Reserved", StringComparison.Ordinal) ||
                !identities.Add($"{entry.OperationId:N}:{entry.Kind}"))
                throw new InvalidDataException("The CurseForge payload ledger contains contradictory evidence.");
        }
        _ = GuardedBytes(ledger);
    }

    private static void ValidateStoredIdentity(CurseForgePayloadLedgerEntry entry)
    {
        if (entry.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(entry.Kind) ||
            entry.Kind.Length > 48 || string.IsNullOrWhiteSpace(entry.ProjectId) ||
            entry.ProjectId.Length > 80 || string.IsNullOrWhiteSpace(entry.FileId) ||
            entry.FileId.Length > 80 || !PositiveNumeric(entry.ProjectId) ||
            !PositiveNumeric(entry.FileId) || entry.ExpectedBytes <= 0 || entry.ExpectedBytes > MaximumBytes)
            throw new InvalidDataException(
                "The CurseForge payload ledger contains an incomplete bounded provider identity.");
    }

    private static void ValidateIdentity(
        Guid operationId,
        string kind,
        string projectId,
        string fileId,
        long expectedBytes,
        string providerSha1)
    {
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(kind) || kind.Length > 48 ||
            string.IsNullOrWhiteSpace(projectId) || projectId.Length > 80 ||
            string.IsNullOrWhiteSpace(fileId) || fileId.Length > 80 ||
            !PositiveNumeric(projectId) || !PositiveNumeric(fileId) ||
            expectedBytes <= 0 || expectedBytes > MaximumBytes || providerSha1.Length != 40 ||
            providerSha1.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Complete bounded provider identity is required before reserving payload bytes.");
    }

    private static bool Matches(
        CurseForgePayloadLedgerEntry entry,
        string projectId,
        string fileId,
        long expectedBytes,
        string providerSha1) =>
        entry.ProjectId.Equals(projectId, StringComparison.Ordinal) &&
        entry.FileId.Equals(fileId, StringComparison.Ordinal) &&
        entry.ExpectedBytes == expectedBytes &&
        entry.ProviderIdentityFingerprint.Equals(
            ProviderFingerprint(providerSha1), StringComparison.OrdinalIgnoreCase);

    private static bool PositiveNumeric(string value) =>
        long.TryParse(value, out var parsed) && parsed > 0;

    private FileStream AcquireProcessLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (Directory.Exists(LockPath) ||
            File.Exists(LockPath) &&
            (File.GetAttributes(LockPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                "The exact CurseForge payload-ledger lock path is unsafe.");
        try
        {
            return new FileStream(
                LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                FileOptions.WriteThrough);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Another controller owns the exact CurseForge payload ledger; concurrent certification is refused.",
                exception);
        }
    }

    private static string ProviderFingerprint(string providerSha1) =>
        Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("curseforge-sha1:" + providerSha1.ToLowerInvariant())))
            .ToLowerInvariant();

    private static long GuardedBytes(CurseForgePayloadLedger ledger)
    {
        long result = 0;
        foreach (var entry in ledger.Entries)
            result = checked(result + entry.ExpectedBytes);
        if (result > MaximumBytes)
            throw new InvalidDataException("The CurseForge payload ledger already exceeds its hard limit.");
        return result;
    }

    private static CurseForgePayloadBudgetSnapshot Snapshot(CurseForgePayloadLedger ledger) =>
        new(
            MaximumBytes,
            GuardedBytes(ledger),
            ledger.Entries.Where(entry => entry.TransferCompleted)
                .Aggregate(0L, (total, entry) => checked(total + entry.DownloadedBytes)),
            ledger.Entries.Count(entry => !entry.TransferCompleted),
            ledger.Entries.Where(entry => !entry.TransferCompleted)
                .Aggregate(0L, (total, entry) => checked(total + entry.ExpectedBytes)),
            ledger.Entries.Where(entry => !entry.TransferCompleted)
                .Aggregate(0L, (total, entry) => checked(total + entry.DownloadedBytes)));
}
