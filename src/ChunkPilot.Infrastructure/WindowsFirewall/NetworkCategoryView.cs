using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Reads how Windows classifies each connected network through the documented Network List Manager
/// interfaces. This view is read-only and never changes a machine-wide network category.
/// </summary>
public sealed class NetworkCategoryView : INetworkCategoryView
{
    private static readonly Guid NetworkListManagerClsid = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    [SupportedOSPlatform("windows")]
    public IReadOnlyList<NetworkCategoryBinding> Enumerate() => Read().Bindings;

    [SupportedOSPlatform("windows")]
    public NetworkCategorySnapshot Read()
    {
        if (!OperatingSystem.IsWindows())
            return NetworkCategorySnapshot.Unavailable(NetworkListStatus.UnsupportedPlatform,
                "Network List Manager is available only on Windows.");

        object? managerObject = null;
        IEnumNetworkConnections? connections = null;
        try
        {
            var type = Type.GetTypeFromCLSID(NetworkListManagerClsid);
            if (type is null)
                return NetworkCategorySnapshot.Unavailable(NetworkListStatus.ApiUnavailable,
                    "Windows did not expose the Network List Manager component.");
            managerObject = Activator.CreateInstance(type);
            if (managerObject is not INetworkListManager manager)
                return NetworkCategorySnapshot.Unavailable(NetworkListStatus.ApiUnavailable,
                    "Windows did not create a usable Network List Manager instance.");

            connections = manager.GetNetworkConnections();
            var results = new List<NetworkCategoryBinding>(8);
            var buffer = new INetworkConnection[1];
            while (connections.Next(1, buffer, out var fetched) == 0 &&
                   fetched == 1 && buffer[0] is { } connection)
            {
                buffer[0] = null!;
                INetwork? network = null;
                try
                {
                    var adapterId = connection.GetAdapterId();
                    network = connection.GetNetwork();
                    var categoryValue = network.GetCategory();
                    results.Add(new NetworkCategoryBinding
                    {
                        AdapterId = Normalize(adapterId),
                        InterfaceIndex = InterfaceIndex(adapterId),
                        NetworkName = network.GetName() ?? "",
                        Category = Enum.IsDefined((WindowsNetworkCategory)categoryValue)
                            ? (WindowsNetworkCategory)categoryValue
                            : WindowsNetworkCategory.Unknown,
                        Connected = connection.GetIsConnected()
                    });
                }
                catch (COMException)
                {
                    // A connection can disappear while Windows is enumerating it. That connection is
                    // not evidence for a profile, but the remaining connections are still useful.
                }
                finally
                {
                    Release(network);
                    Release(connection);
                }
            }
            return new NetworkCategorySnapshot
            {
                Status = NetworkListStatus.Available,
                Bindings = results,
                Detail = results.Count == 0
                    ? "Network List Manager returned no connected profile bindings."
                    : $"Network List Manager returned {results.Count} profile binding(s)."
            };
        }
        catch (UnauthorizedAccessException exception)
        {
            return NetworkCategorySnapshot.Unavailable(NetworkListStatus.AccessDenied,
                $"Network List Manager access was denied (0x{exception.HResult:X8}).");
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or
                                              PlatformNotSupportedException)
        {
            return NetworkCategorySnapshot.Unavailable(NetworkListStatus.ReadFailed,
                $"Network List Manager could not be read (0x{exception.HResult:X8}).");
        }
        finally
        {
            Release(connections);
            Release(managerObject);
        }
    }

    /// <summary>Normalises an adapter GUID to the form NetworkInterface.Id uses.</summary>
    internal static string Normalize(object? adapterId)
    {
        var text = adapterId switch
        {
            Guid guid => guid.ToString("B"),
            string value => value,
            null => "",
            _ => adapterId.ToString() ?? ""
        };
        return Guid.TryParse(text, out var parsed)
            ? parsed.ToString("B").ToUpperInvariant()
            : text.Trim().ToUpperInvariant();
    }

    private static int InterfaceIndex(Guid adapterId)
    {
        if (ConvertInterfaceGuidToLuid(ref adapterId, out var luid) != 0 ||
            ConvertInterfaceLuidToIndex(ref luid, out var index) != 0 || index > int.MaxValue)
            return 0;
        return (int)index;
    }

    private static void Release(object? comObject)
    {
        if (OperatingSystem.IsWindows() && comObject is not null && Marshal.IsComObject(comObject))
            _ = Marshal.ReleaseComObject(comObject);
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint ConvertInterfaceGuidToLuid(ref Guid interfaceGuid, out ulong interfaceLuid);

    [DllImport("iphlpapi.dll")]
    private static extern uint ConvertInterfaceLuidToIndex(ref ulong interfaceLuid, out uint interfaceIndex);

    [ComImport]
    [Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetworks(int flags);

        [return: MarshalAs(UnmanagedType.Interface)]
        INetwork GetNetwork(Guid networkId);

        [return: MarshalAs(UnmanagedType.Interface)]
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport]
    [Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [DispId(-4)]
        object NewEnum
        {
            [return: MarshalAs(UnmanagedType.IUnknown)]
            get;
        }

        [PreserveSig]
        int Next(
            uint count,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0), Out] INetworkConnection[] connections,
            out uint fetched);

        void Skip(uint count);
        void Reset();
        void Clone([MarshalAs(UnmanagedType.Interface)] out IEnumNetworkConnections enumerator);
    }

    [ComImport]
    [Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        INetwork GetNetwork();

        [return: MarshalAs(UnmanagedType.VariantBool)]
        bool GetIsConnectedToInternet();

        [return: MarshalAs(UnmanagedType.VariantBool)]
        bool GetIsConnected();

        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
        int GetDomainType();
    }

    [ComImport]
    [Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        string GetName();

        void SetName([MarshalAs(UnmanagedType.BStr)] string name);

        [return: MarshalAs(UnmanagedType.BStr)]
        string GetDescription();

        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
        int GetDomainType();

        [return: MarshalAs(UnmanagedType.Interface)]
        IEnumNetworkConnections GetNetworkConnections();

        void GetTimeCreatedAndConnected(
            out uint createdLow, out uint createdHigh, out uint connectedLow, out uint connectedHigh);

        [return: MarshalAs(UnmanagedType.VariantBool)]
        bool GetIsConnectedToInternet();

        [return: MarshalAs(UnmanagedType.VariantBool)]
        bool GetIsConnected();

        int GetConnectivity();
        int GetCategory();
        void SetCategory(int category);
    }
}
