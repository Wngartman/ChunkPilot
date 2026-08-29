using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace ChunkPilot.Certification;

internal sealed record CertificationRunRecycleEvidence
{
    public bool Attempted { get; init; }
    public bool ExactOwnedRunProven { get; init; }
    public long BytesMovedToRecycleBin { get; init; }
    public int FilesMovedToRecycleBin { get; init; }
    public int DirectoriesMovedToRecycleBin { get; init; }
    public bool SourceRunRootAbsent { get; init; }
    public bool Success { get; init; }
    public string Outcome { get; init; } = "Not attempted";
}

internal interface ICertificationRecycleBin
{
    bool MoveDirectory(string path);
}

/// <summary>
/// Uses the Windows shell's recoverable-delete contract with every interactive surface disabled.
/// An inability to use the Recycle Bin is a certification failure; this never falls back to a
/// permanent recursive delete.
/// </summary>
internal sealed class SilentWindowsCertificationRecycleBin : ICertificationRecycleBin
{
    private const uint FileOperationSilent = 0x0004;
    private const uint FileOperationNoConfirmation = 0x0010;
    private const uint FileOperationNoErrorUi = 0x0400;
    private const uint FileOperationNoConfirmMakeDirectory = 0x0200;
    private const uint FileOperationRecycleOnDelete = 0x00080000;
    private const uint FileOperationEarlyFailure = 0x00100000;
    private const uint FileOperationNoMinimizeBox = 0x01000000;
    private const uint FileOperationAddUndoRecord = 0x20000000;
    private static readonly Guid FileOperationClassId =
        new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly Guid ShellItemInterfaceId =
        new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    public bool MoveDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Windows Recycle Bin cleanup is available only on Windows.");
        Exception? failure = null;
        var recycled = false;
        var thread = new Thread(() =>
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException(
                        "Windows Recycle Bin cleanup is available only on Windows.");
                recycled = MoveDirectoryOnStaThread(path);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "ChunkPilot certification Recycle Bin cleanup"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(5)))
            throw new TimeoutException(
                "The no-dialog Windows Recycle Bin operation exceeded its bounded deadline.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return recycled;
    }

    [SupportedOSPlatform("windows")]
    private static bool MoveDirectoryOnStaThread(string path)
    {
        IShellItem? shellItem = null;
        IFileOperation? operation = null;
        var progressSink = new DeleteProgressSink();
        try
        {
            var itemId = ShellItemInterfaceId;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(
                Path.GetFullPath(path), IntPtr.Zero, ref itemId, out shellItem));
            var operationType = Type.GetTypeFromCLSID(FileOperationClassId, throwOnError: true)
                                ?? throw new PlatformNotSupportedException(
                                    "The Windows recoverable file-operation service is unavailable.");
            operation = (IFileOperation)(Activator.CreateInstance(operationType)
                         ?? throw new InvalidOperationException(
                             "The Windows recoverable file-operation service did not start."));
            operation.SetOperationFlags(
                FileOperationSilent | FileOperationNoConfirmation | FileOperationNoErrorUi |
                FileOperationNoConfirmMakeDirectory | FileOperationRecycleOnDelete |
                FileOperationEarlyFailure | FileOperationNoMinimizeBox | FileOperationAddUndoRecord);
            operation.DeleteItem(shellItem, progressSink);
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            if (aborted)
                throw new OperationCanceledException(
                    "The Windows shell did not complete the recoverable cleanup operation.");
            return progressSink.ConfirmedRecoverableDelete;
        }
        finally
        {
            GC.KeepAlive(progressSink);
            if (operation is not null && Marshal.IsComObject(operation))
                _ = Marshal.FinalReleaseComObject(operation);
            if (shellItem is not null && Marshal.IsComObject(shellItem))
                _ = Marshal.FinalReleaseComObject(shellItem);
        }
    }

    [ComImport]
    [Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr progressSink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint operationFlags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr progressDialog);
        void SetProperties(IntPtr propertyChangeArray);
        void SetOwnerWindow(IntPtr ownerWindow);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(
            IShellItem item,
            [MarshalAs(UnmanagedType.LPWStr)] string newName,
            IntPtr progressSink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void MoveItem(
            IShellItem item,
            IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string newName,
            IntPtr progressSink);
        void MoveItems(IntPtr items, IShellItem destinationFolder);
        void CopyItem(
            IShellItem item,
            IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string copyName,
            IntPtr progressSink);
        void CopyItems(IntPtr items, IShellItem destinationFolder);
        void DeleteItem(
            IShellItem item,
            [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink progressSink);
        void DeleteItems(IntPtr items);
        void NewItem(
            IShellItem destinationFolder,
            uint fileAttributes,
            [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string templateName,
            IntPtr progressSink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComVisible(true)]
    [Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig]
        int StartOperations();

        [PreserveSig]
        int FinishOperations(int result);

        [PreserveSig]
        int PreRenameItem(
            uint flags,
            IShellItem item,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName);

        [PreserveSig]
        int PostRenameItem(
            uint flags,
            IShellItem item,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName,
            int renameResult,
            IShellItem? newlyCreated);

        [PreserveSig]
        int PreMoveItem(
            uint flags,
            IShellItem item,
            IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName);

        [PreserveSig]
        int PostMoveItem(
            uint flags,
            IShellItem item,
            IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName,
            int moveResult,
            IShellItem? newlyCreated);

        [PreserveSig]
        int PreCopyItem(
            uint flags,
            IShellItem item,
            IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName);

        [PreserveSig]
        int PostCopyItem(
            uint flags,
            IShellItem item,
            IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName,
            int copyResult,
            IShellItem? newlyCreated);

        [PreserveSig]
        int PreDeleteItem(uint flags, IShellItem item);

        [PreserveSig]
        int PostDeleteItem(
            uint flags,
            IShellItem item,
            int deleteResult,
            IShellItem? newlyCreated);

        [PreserveSig]
        int PreNewItem(
            uint flags,
            IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName);

        [PreserveSig]
        int PostNewItem(
            uint flags,
            IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName,
            [MarshalAs(UnmanagedType.LPWStr)] string? templateName,
            uint fileAttributes,
            int newItemResult,
            IShellItem? newItem);

        [PreserveSig]
        int UpdateProgress(uint workTotal, uint workSoFar);

        [PreserveSig]
        int ResetTimer();

        [PreserveSig]
        int PauseTimer();

        [PreserveSig]
        int ResumeTimer();
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class DeleteProgressSink : IFileOperationProgressSink
    {
        private int postDeleteCount;
        private bool deleteSucceeded;
        private bool recycleItemCreated;

        public bool ConfirmedRecoverableDelete =>
            postDeleteCount == 1 && deleteSucceeded && recycleItemCreated;

        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IShellItem item, string? newName) => 0;
        public int PostRenameItem(
            uint flags,
            IShellItem item,
            string? newName,
            int renameResult,
            IShellItem? newlyCreated) => 0;
        public int PreMoveItem(
            uint flags,
            IShellItem item,
            IShellItem destinationFolder,
            string? newName) => 0;
        public int PostMoveItem(
            uint flags,
            IShellItem item,
            IShellItem destinationFolder,
            string? newName,
            int moveResult,
            IShellItem? newlyCreated) => 0;
        public int PreCopyItem(
            uint flags,
            IShellItem item,
            IShellItem destinationFolder,
            string? newName) => 0;
        public int PostCopyItem(
            uint flags,
            IShellItem item,
            IShellItem destinationFolder,
            string? newName,
            int copyResult,
            IShellItem? newlyCreated) => 0;
        public int PreDeleteItem(uint flags, IShellItem item) => 0;

        public int PostDeleteItem(
            uint flags,
            IShellItem item,
            int deleteResult,
            IShellItem? newlyCreated)
        {
            postDeleteCount++;
            deleteSucceeded = deleteResult >= 0;
            recycleItemCreated = newlyCreated is not null;
            return 0;
        }

        public int PreNewItem(
            uint flags,
            IShellItem destinationFolder,
            string? newName) => 0;
        public int PostNewItem(
            uint flags,
            IShellItem destinationFolder,
            string? newName,
            string? templateName,
            uint fileAttributes,
            int newItemResult,
            IShellItem? newItem) => 0;
        public int UpdateProgress(uint workTotal, uint workSoFar) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(
            IntPtr bindContext,
            ref Guid handlerId,
            ref Guid interfaceId,
            out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint displayNameType, out IntPtr name);
        void GetAttributes(uint attributeMask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        out IShellItem shellItem);
}

internal static class OwnedCertificationRunCleanup
{
    private const int MaximumEntries = 500_000;

    public static Task<CertificationRunRecycleEvidence> MoveToRecycleBinAsync(
        CurseForgeRuntimeCertificationOptions options,
        ICertificationRecycleBin recycleBin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(recycleBin);
        var runtime = Path.GetFullPath(options.RuntimeRoot);
        var data = Path.GetFullPath(options.DataRoot);
        var servers = Path.GetFullPath(options.ManagedServersRoot);
        var temporary = Path.GetFullPath(options.TemporaryRoot);
        var runRoot = Path.GetDirectoryName(data)
                      ?? throw new InvalidOperationException(
                          "The task-owned certification run root is unavailable.");
        var serversParent = Path.GetDirectoryName(servers);
        var temporaryParent = Path.GetDirectoryName(temporary);
        if (!runRoot.Equals(serversParent, StringComparison.OrdinalIgnoreCase) ||
            !runRoot.Equals(temporaryParent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(data).Equals("data", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(servers).Equals("servers", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(temporary).Equals("temp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The recoverable cleanup target is not the exact fresh data/server/temp run root.");

        _ = OwnedCertificationRuntime.RequireOwnedDescendant(
            runtime, runRoot, "recoverable fresh run cleanup root");
        if (!Directory.Exists(data) || !Directory.Exists(servers) || !Directory.Exists(temporary))
            throw new InvalidOperationException(
                "Every exact task-owned data, server, and temporary tree must exist before recoverable cleanup.");
        if ((File.GetAttributes(temporary) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                "The exact task-owned temporary tree is redirected and cannot be recycled safely.");
        var topLevel = Directory.EnumerateFileSystemEntries(runRoot).ToArray();
        if (topLevel.Length != 3 ||
            !topLevel.Any(path => path.Equals(data, StringComparison.OrdinalIgnoreCase)) ||
            !topLevel.Any(path => path.Equals(servers, StringComparison.OrdinalIgnoreCase)) ||
            !topLevel.Any(path => path.Equals(temporary, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "The fresh run root contains an unexpected entry and was retained for review.");

        var inventory = BoundedNoFollowFileTree.Inventory(
            runRoot, MaximumEntries, cancellationToken);
        var bytes = inventory.Where(entry => !entry.IsDirectory)
            .Aggregate(0L, (total, entry) => checked(total + entry.SizeBytes));
        var files = inventory.Count(entry => !entry.IsDirectory);
        var directories = inventory.Count(entry => entry.IsDirectory) + 1;

        cancellationToken.ThrowIfCancellationRequested();
        bool recycled;
        try
        {
            recycled = recycleBin.MoveDirectory(runRoot);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Task.FromResult(new CertificationRunRecycleEvidence
            {
                Attempted = true,
                ExactOwnedRunProven = true,
                BytesMovedToRecycleBin = 0,
                FilesMovedToRecycleBin = 0,
                DirectoriesMovedToRecycleBin = 0,
                SourceRunRootAbsent = !Directory.Exists(runRoot),
                Success = false,
                Outcome = "Windows Recycle Bin move failed; no permanent-delete fallback was used."
            });
        }

        var absent = !Directory.Exists(runRoot) && !File.Exists(runRoot);
        var success = recycled && absent;
        return Task.FromResult(new CertificationRunRecycleEvidence
        {
            Attempted = true,
            ExactOwnedRunProven = true,
            BytesMovedToRecycleBin = success ? bytes : 0,
            FilesMovedToRecycleBin = success ? files : 0,
            DirectoriesMovedToRecycleBin = success ? directories : 0,
            SourceRunRootAbsent = absent,
            Success = success,
            Outcome = success
                ? "Exact fresh data/server run moved to the Windows Recycle Bin."
                : !recycled && absent
                    ? "Windows removed the exact fresh run root without returning a recoverable Recycle Bin item; cleanup was not certified and no permanent-delete fallback was used."
                    : !recycled
                        ? "Windows did not confirm a recoverable Recycle Bin item; cleanup was not certified and no permanent-delete fallback was used."
                        : "Windows confirmed a recoverable Recycle Bin item but the exact fresh run root remained; cleanup was not certified."
        });
    }
}
