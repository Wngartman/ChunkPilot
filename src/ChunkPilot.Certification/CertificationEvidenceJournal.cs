using System.Text.Json;

namespace ChunkPilot.Certification;

internal sealed record CurseForgeRuntimeCertificationPendingEvidence
{
    public const string ExpectedDocumentType =
        "ChunkPilot.CurseForgeRuntimeCertificationPendingEvidence";

    public string DocumentType { get; init; } = ExpectedDocumentType;
    public int SchemaVersion { get; init; } = 1;
    public string State { get; init; } = "PendingBeforeRecoverableRunCleanup";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required CurseForgeRuntimeCertificationReport Report { get; init; }
}

internal interface ICertificationEvidenceJournal
{
    string PendingPath { get; }
    Task WritePendingAsync(
        CurseForgeRuntimeCertificationReport report,
        CancellationToken cancellationToken);
    Task FinalizeAsync(
        CurseForgeRuntimeCertificationReport report,
        CancellationToken cancellationToken);
}

/// <summary>
/// Persists a same-directory, crash-surviving report snapshot before recoverable cleanup. The final
/// immutable report is atomically promoted only after the controller has completed cleanup evidence.
/// A failed finalization deliberately leaves the pending journal in place.
/// </summary>
internal sealed class CertificationEvidenceJournal : ICertificationEvidenceJournal
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string reportPath;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool pendingOwnedByThisInstance;

    public CertificationEvidenceJournal(string reportPath, string pendingPath)
    {
        this.reportPath = Path.GetFullPath(reportPath);
        PendingPath = Path.GetFullPath(pendingPath);
        var reportDirectory = Path.GetDirectoryName(this.reportPath);
        var pendingDirectory = Path.GetDirectoryName(PendingPath);
        if (string.IsNullOrWhiteSpace(reportDirectory) ||
            !reportDirectory.Equals(pendingDirectory, StringComparison.OrdinalIgnoreCase) ||
            this.reportPath.Equals(PendingPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "The final report and pending evidence must be distinct siblings.");
        RejectUnsafeExistingPath(this.reportPath, allowRegularFile: false);
        RejectUnsafeExistingPath(PendingPath, allowRegularFile: false);
    }

    public string PendingPath { get; }

    public async Task WritePendingAsync(
        CurseForgeRuntimeCertificationReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RejectUnsafeExistingPath(reportPath, allowRegularFile: false);
            RejectUnsafeExistingPath(PendingPath, allowRegularFile: pendingOwnedByThisInstance);
            await WriteDurableAtomicAsync(
                PendingPath,
                new CurseForgeRuntimeCertificationPendingEvidence { Report = report },
                overwrite: pendingOwnedByThisInstance,
                cancellationToken).ConfigureAwait(false);
            pendingOwnedByThisInstance = true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task FinalizeAsync(
        CurseForgeRuntimeCertificationReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.CompletedAtUtc is null ||
            report.Result is not ("PASSED" or "FAILED" or "CANCELLED"))
            throw new InvalidOperationException(
                "Only a completed terminal certification report can replace pending evidence.");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!pendingOwnedByThisInstance || !File.Exists(PendingPath))
                throw new InvalidOperationException(
                    "The controller cannot finalize without its durable pending evidence journal.");
            RejectUnsafeExistingPath(PendingPath, allowRegularFile: true);
            RejectUnsafeExistingPath(reportPath, allowRegularFile: false);
            await WriteDurableAtomicAsync(
                reportPath, report, overwrite: false, cancellationToken).ConfigureAwait(false);
            File.Delete(PendingPath);
            pendingOwnedByThisInstance = false;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task WriteDurableAtomicAsync<T>(
        string target,
        T value,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                "Certification evidence cannot be written through a reparse-point directory.");
        var temporary = Path.Combine(
            directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void RejectUnsafeExistingPath(string path, bool allowRegularFile)
    {
        if (Directory.Exists(path))
            throw new InvalidOperationException(
                "A certification evidence path unexpectedly names a directory.");
        if (!File.Exists(path))
            return;
        if (!allowRegularFile ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                "A certification evidence path is already occupied or unsafe.");
    }
}
