using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Reads Windows Firewall policy through the documented Windows Firewall with Advanced Security COM
/// API (<c>INetFwPolicy2</c>, <c>INetFwRules</c>, <c>INetFwRule</c> in FirewallAPI.dll).
/// </summary>
/// <remarks>
/// Reading needs no elevation. Every policy field is captured independently so one optional COM read
/// cannot erase facts Windows already returned, while callers can still require the complete evidence
/// needed for a safe mutation.
/// </remarks>
[SuppressMessage("Globalization", "CA1304:Specify CultureInfo",
    Justification = "COM automation member names are invariant Windows API identifiers.")]
public sealed class NetFwPolicyReader : IWindowsFirewallPolicyReader
{
    private const string PolicyProgId = "HNetCfg.FwPolicy2";

    private static readonly int[] ProfileValues =
    [
        (int)FirewallProfile.Domain, (int)FirewallProfile.Private, (int)FirewallProfile.Public
    ];

    private readonly Func<INetFwPolicyAccessor?> accessorFactory;

    [SupportedOSPlatform("windows")]
    public NetFwPolicyReader() : this(CreateAccessor) { }

    internal NetFwPolicyReader(Func<INetFwPolicyAccessor?> accessorFactory) =>
        this.accessorFactory = accessorFactory ?? throw new ArgumentNullException(nameof(accessorFactory));

    [SupportedOSPlatform("windows")]
    public FirewallPolicySnapshot Read()
    {
        if (!OperatingSystem.IsWindows())
            return FirewallPolicySnapshot.Unavailable("Windows Firewall is only available on Windows.",
                FirewallPlatformStatus.UnsupportedPlatform);

        INetFwPolicyAccessor? policy;
        try
        {
            policy = accessorFactory();
        }
        catch (Exception exception) when (IsPolicyReadException(exception))
        {
            var actual = Actual(exception);
            var status = actual is UnauthorizedAccessException
                ? FirewallPlatformStatus.AccessDenied
                : FirewallPlatformStatus.ReadFailed;
            return FirewallPolicySnapshot.Unavailable(
                $"The firewall policy component {PolicyProgId} could not be created " +
                $"(0x{actual.HResult:X8}): {actual.Message}", status);
        }

        if (policy is null)
            return FirewallPolicySnapshot.Unavailable(
                $"The firewall policy component {PolicyProgId} is not registered on this computer.",
                FirewallPlatformStatus.ApiUnavailable);

        using (policy)
        {
            var current = FirewallProfile.None;
            var enabled = FirewallProfile.None;
            var blockedInbound = FirewallProfile.None;
            var enabledUnavailable = FirewallProfile.None;
            var blockedUnavailable = FirewallProfile.None;
            var modifyState = FirewallPolicyModifyState.Unknown;
            IReadOnlyList<FirewallRuleSnapshot> rules = [];
            var unavailable = FirewallPolicyUnavailableFields.None;
            var details = new List<string>(8);

            if (TryRead(() => policy.CurrentProfileTypes, "CurrentProfileTypes", details, out var currentValue))
                current = ToProfiles(currentValue);
            else
                unavailable |= FirewallPolicyUnavailableFields.CurrentProfiles;

            foreach (var value in ProfileValues)
            {
                var profile = (FirewallProfile)value;
                if (TryRead(() => policy.FirewallEnabled(value), $"FirewallEnabled({profile})", details,
                        out var isEnabled))
                {
                    if (isEnabled)
                        enabled |= profile;
                }
                else
                {
                    unavailable |= FirewallPolicyUnavailableFields.FirewallEnabled;
                    enabledUnavailable |= profile;
                }

                if (TryRead(() => policy.BlockAllInboundTraffic(value),
                        $"BlockAllInboundTraffic({profile})", details, out var blockAllInbound))
                {
                    if (blockAllInbound)
                        blockedInbound |= profile;
                }
                else
                {
                    unavailable |= FirewallPolicyUnavailableFields.BlockAllInboundTraffic;
                    blockedUnavailable |= profile;
                }
            }

            if (TryRead(() => policy.LocalPolicyModifyState, "LocalPolicyModifyState", details,
                    out var modifyValue))
            {
                var candidate = (FirewallPolicyModifyState)modifyValue;
                modifyState = Enum.IsDefined(candidate) ? candidate : FirewallPolicyModifyState.Unknown;
            }
            else
            {
                unavailable |= FirewallPolicyUnavailableFields.LocalPolicyModifyState;
            }

            try
            {
                var ruleRead = policy.ReadRules();
                rules = ruleRead.Rules;
                if (ruleRead.Complete)
                    details.Add($"Rules: read {rules.Count}.");
                else
                {
                    unavailable |= FirewallPolicyUnavailableFields.Rules;
                    details.Add(ruleRead.Detail);
                }
            }
            catch (Exception exception) when (IsPolicyReadException(exception))
            {
                unavailable |= FirewallPolicyUnavailableFields.Rules;
                details.Add(FailureDetail("Rules", exception));
            }

            if (unavailable == FirewallPolicyUnavailableFields.None)
                details.Insert(0, "Windows Firewall policy read completed.");
            else
                details.Insert(0, $"Windows Firewall policy was partially read; unavailable: {unavailable}.");

            return new FirewallPolicySnapshot
            {
                Available = true,
                PlatformStatus = FirewallPlatformStatus.Available,
                CurrentProfiles = current,
                EnabledProfiles = enabled,
                BlockAllInboundProfiles = blockedInbound,
                ModifyState = modifyState,
                Rules = rules,
                UnavailableFields = unavailable,
                FirewallEnabledUnavailableProfiles = enabledUnavailable,
                BlockAllInboundUnavailableProfiles = blockedUnavailable,
                Detail = string.Join(" ", details)
            };
        }
    }

    private static bool TryRead<T>(Func<T> read, string operation, ICollection<string> details, out T value)
    {
        try
        {
            value = read();
            return true;
        }
        catch (Exception exception) when (IsPolicyReadException(exception))
        {
            value = default!;
            details.Add(FailureDetail(operation, exception));
            return false;
        }
    }

    private static string FailureDetail(string operation, Exception exception)
    {
        var actual = Actual(exception);
        return $"{operation} failed (0x{actual.HResult:X8}): {actual.Message}";
    }

    private static Exception Actual(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null })
            exception = exception.InnerException;
        return exception;
    }

    private static bool IsPolicyReadException(Exception exception) => Actual(exception) is
        COMException or MissingMemberException or InvalidCastException or UnauthorizedAccessException;

    [SupportedOSPlatform("windows")]
    private static INetFwPolicyAccessor? CreateAccessor()
    {
        var type = Type.GetTypeFromProgID(PolicyProgId);
        if (type is null)
            return null;
        var policy = Activator.CreateInstance(type);
        return policy is null ? null : new ReflectionNetFwPolicyAccessor(policy);
    }

    private static FirewallProfile ToProfiles(int value) =>
        (FirewallProfile)(value & (int)(FirewallProfile.Domain | FirewallProfile.Private | FirewallProfile.Public));
}

internal interface INetFwPolicyAccessor : IDisposable
{
    int CurrentProfileTypes { get; }
    bool FirewallEnabled(int profile);
    bool BlockAllInboundTraffic(int profile);
    int LocalPolicyModifyState { get; }
    NetFwRuleReadResult ReadRules();
}

internal sealed record NetFwRuleReadResult(
    IReadOnlyList<FirewallRuleSnapshot> Rules,
    bool Complete,
    string Detail = "");

internal sealed class ReflectionNetFwPolicyAccessor(object policy) : INetFwPolicyAccessor
{
    private const int AllProfilesValue = 0x7fffffff;
    private object? policy = policy;

    public int CurrentProfileTypes => GetInt(Policy, "CurrentProfileTypes");
    public bool FirewallEnabled(int profile) => GetBool(Policy, "FirewallEnabled", profile);
    public bool BlockAllInboundTraffic(int profile) => GetBool(Policy, "BlockAllInboundTraffic", profile);
    public int LocalPolicyModifyState => GetInt(Policy, "LocalPolicyModifyState");

    private object Policy => policy ?? throw new ObjectDisposedException(nameof(ReflectionNetFwPolicyAccessor));

    public NetFwRuleReadResult ReadRules()
    {
        object? collection = null;
        try
        {
            collection = Policy.GetType().InvokeMember(
                "Rules", BindingFlags.GetProperty, binder: null, target: Policy, args: null,
                culture: CultureInfo.InvariantCulture);
            if (collection is null)
                return new NetFwRuleReadResult([], false, "Windows Firewall reported no rule collection.");

            var results = new List<FirewallRuleSnapshot>(512);
            var complete = true;
            string itemFailure = "";
            var enumeration = Enumerate(collection);
            try
            {
                foreach (var item in enumeration.Items)
                {
                    try
                    {
                        var snapshot = ReadRule(item);
                        results.Add(snapshot);
                        if (snapshot.UnavailableFields.HasFlag(FirewallRuleUnavailableFields.Name))
                        {
                            complete = false;
                            itemFailure = "A Windows Firewall rule name could not be read; collision checks are incomplete.";
                        }
                    }
                    catch (Exception exception) when (exception is COMException or MissingMemberException or
                                                          InvalidCastException or TargetInvocationException)
                    {
                        complete = false;
                        if (itemFailure.Length == 0)
                        {
                            var actual = exception is TargetInvocationException { InnerException: not null }
                                ? exception.InnerException
                                : exception;
                            itemFailure = $"A Windows Firewall rule could not be read " +
                                          $"(0x{actual.HResult:X8}): {actual.Message}";
                        }
                    }
                    finally
                    {
                        Release(item);
                    }
                }
            }
            catch (Exception exception) when (exception is COMException or MissingMemberException or
                                                  InvalidCastException or TargetInvocationException)
            {
                return new NetFwRuleReadResult(results, false,
                    $"Windows Firewall rules could not be enumerated: {exception.Message}");
            }

            if (!enumeration.Complete)
                complete = false;
            var detail = enumeration.Detail.Length > 0
                ? enumeration.Detail
                : itemFailure.Length > 0
                    ? itemFailure
                : complete
                    ? $"Read {results.Count} Windows Firewall rules."
                    : "At least one Windows Firewall rule changed while it was being read; no mutation is safe yet.";
            return new NetFwRuleReadResult(results, complete, detail);
        }
        finally
        {
            Release(collection);
        }
    }

    /// <summary>
    /// COM interop projects <c>INetFwRules::_NewEnum</c>'s native <c>IEnumVARIANT</c> as a CLR
    /// <see cref="IEnumerator"/> wrapper on modern .NET. Requiring the raw COM interface rejects that
    /// valid projection even though enumeration succeeded.
    /// </summary>
    internal static NetFwEnumeration Enumerate(object collection)
    {
        try
        {
            var value = collection.GetType().InvokeMember(
                "_NewEnum", BindingFlags.GetProperty | BindingFlags.InvokeMethod,
                binder: null, target: collection, args: null, culture: CultureInfo.InvariantCulture);
            return value is IEnumerator enumerator
                ? new NetFwEnumeration(Walk(enumerator), true, "")
                : new NetFwEnumeration([], false,
                    "Windows Firewall did not return a managed rule enumerator.");
        }
        catch (Exception exception) when (exception is COMException or MissingMemberException or
                                              TargetInvocationException)
        {
            return new NetFwEnumeration([], false,
                $"Windows Firewall rules could not be enumerated: {exception.Message}");
        }
    }

    private static IEnumerable<object> Walk(IEnumerator enumerator)
    {
        try
        {
            while (enumerator.MoveNext())
            {
                if (enumerator.Current is { } value)
                    yield return value;
            }
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    internal static FirewallRuleSnapshot ReadRule(object item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var unavailable = FirewallRuleUnavailableFields.None;

        T Read<T>(string member, FirewallRuleUnavailableFields field, Func<object?, T> convert, T fallback)
        {
            try
            {
                var raw = item.GetType().InvokeMember(member, BindingFlags.GetProperty, binder: null,
                    target: item, args: null, culture: CultureInfo.InvariantCulture);
                return convert(raw);
            }
            catch (Exception exception) when (IsRulePropertyReadException(exception))
            {
                unavailable |= field;
                return fallback;
            }
        }

        var rawProfiles = Read("Profiles", FirewallRuleUnavailableFields.Profiles, Int, 0);
        return new FirewallRuleSnapshot
        {
            Name = Read("Name", FirewallRuleUnavailableFields.Name, Text, ""),
            Description = Read("Description", FirewallRuleUnavailableFields.Description, Text, ""),
            Grouping = Read("Grouping", FirewallRuleUnavailableFields.Grouping, Text, ""),
            Enabled = Read("Enabled", FirewallRuleUnavailableFields.Enabled, Bool, false),
            Direction = Read("Direction", FirewallRuleUnavailableFields.Direction,
                value => (FirewallRuleDirection)Int(value), FirewallRuleDirection.Unknown),
            Action = Read("Action", FirewallRuleUnavailableFields.Action,
                value => Int(value) == 1 ? FirewallRuleAction.Allow : FirewallRuleAction.Block,
                FirewallRuleAction.Block),
            Protocol = Read("Protocol", FirewallRuleUnavailableFields.Protocol, Int,
                WindowsFirewallPolicy.ProtocolAny),
            LocalPorts = Read("LocalPorts", FirewallRuleUnavailableFields.LocalPorts, Text, ""),
            RemotePorts = Read("RemotePorts", FirewallRuleUnavailableFields.RemotePorts, Text, ""),
            IcmpTypesAndCodes = Read("IcmpTypesAndCodes", FirewallRuleUnavailableFields.IcmpTypesAndCodes,
                Text, ""),
            LocalAddresses = Read("LocalAddresses", FirewallRuleUnavailableFields.LocalAddresses, Text, ""),
            RemoteAddresses = Read("RemoteAddresses", FirewallRuleUnavailableFields.RemoteAddresses, Text, ""),
            ApplicationName = Read("ApplicationName", FirewallRuleUnavailableFields.ApplicationName, Text, ""),
            ServiceName = Read("ServiceName", FirewallRuleUnavailableFields.ServiceName, Text, ""),
            Profiles = ToProfiles(rawProfiles),
            AppliesToAllProfiles = (rawProfiles & AllProfilesValue) == AllProfilesValue,
            Interfaces = Read("Interfaces", FirewallRuleUnavailableFields.Interfaces, Strings, []),
            InterfaceTypes = Read("InterfaceTypes", FirewallRuleUnavailableFields.InterfaceTypes, Text, ""),
            EdgeTraversal = Read("EdgeTraversal", FirewallRuleUnavailableFields.EdgeTraversal, Bool, false),
            EdgeTraversalOptions = Read("EdgeTraversalOptions",
                FirewallRuleUnavailableFields.EdgeTraversalOptions, Int, 0),
            LocalAppPackageId = Read("LocalAppPackageId",
                FirewallRuleUnavailableFields.LocalAppPackageId, Text, ""),
            LocalUserOwner = Read("LocalUserOwner", FirewallRuleUnavailableFields.LocalUserOwner, Text, ""),
            LocalUserAuthorizedList = Read("LocalUserAuthorizedList",
                FirewallRuleUnavailableFields.LocalUserAuthorizedList, Text, ""),
            RemoteUserAuthorizedList = Read("RemoteUserAuthorizedList",
                FirewallRuleUnavailableFields.RemoteUserAuthorizedList, Text, ""),
            RemoteMachineAuthorizedList = Read("RemoteMachineAuthorizedList",
                FirewallRuleUnavailableFields.RemoteMachineAuthorizedList, Text, ""),
            SecureFlags = Read("SecureFlags", FirewallRuleUnavailableFields.SecureFlags, Int, 0),
            UnavailableFields = unavailable
        };
    }

    private static bool IsRulePropertyReadException(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null })
            exception = exception.InnerException;
        return exception is COMException or MissingMemberException or InvalidCastException or ArgumentException;
    }

    private static int Int(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);
    private static bool Bool(object? value) => Convert.ToBoolean(value, CultureInfo.InvariantCulture);

    private static IReadOnlyList<string> Strings(object? value)
    {
        if (value is null)
            return [];
        if (value is string text)
            return text.Length == 0 ? [] : [text];
        if (value is not IEnumerable sequence)
            throw new InvalidCastException("The firewall interface condition is not an enumerable value.");
        var items = new List<string>();
        foreach (var item in sequence)
        {
            if (item is string candidate && candidate.Length > 0)
                items.Add(candidate);
        }
        return items;
    }

    private static int GetInt(object target, string member) =>
        Convert.ToInt32(target.GetType().InvokeMember(
            member, BindingFlags.GetProperty, binder: null, target: target, args: null,
            culture: CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static bool GetBool(object target, string member, int profile) =>
        Convert.ToBoolean(target.GetType().InvokeMember(
            member, BindingFlags.GetProperty, binder: null, target: target, args: [profile],
            culture: CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string Text(object? value) => value as string ?? "";

    private static FirewallProfile ToProfiles(int value) =>
        (FirewallProfile)(value & (int)(FirewallProfile.Domain | FirewallProfile.Private | FirewallProfile.Public));

    private static void Release(object? comObject)
    {
        if (OperatingSystem.IsWindows() && comObject is not null && Marshal.IsComObject(comObject))
            _ = Marshal.ReleaseComObject(comObject);
    }

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref policy, null);
        Release(current);
    }
}

internal sealed record NetFwEnumeration(
    IEnumerable<object> Items,
    bool Complete,
    string Detail);
