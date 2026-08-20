using System.Globalization;

namespace ChunkPilot.Core;

/// <summary>The only three things the privileged helper is able to do.</summary>
public enum FirewallHelperOperation
{
    Create,
    Update,
    Remove
}

/// <summary>
/// What the helper reports back. The Agent uses it to explain what happened; it never uses it as proof
/// that the firewall is configured, because that is decided by re-reading Windows afterwards.
/// </summary>
public enum FirewallHelperExitCode
{
    Applied = 0,
    UnexpectedFailure = 1,
    InvalidArguments = 10,
    /// <summary>A rule with this identifier exists that the helper could not prove is ChunkPilot's.</summary>
    OwnershipConflict = 11,
    /// <summary>Group Policy or an inbound-blocked profile means the change would not take effect.</summary>
    PolicyPrevented = 12,
    AccessDenied = 13,
    FirewallUnavailable = 14,
    /// <summary>A removal found nothing to remove. Idempotent, not a failure.</summary>
    NothingToRemove = 15
}

/// <summary>Whether the machine's firewall store can be changed at all right now.</summary>
public enum FirewallBackendStatus
{
    Available,
    Unavailable,
    GroupPolicyOverride,
    InboundBlocked
}

/// <summary>
/// One privileged firewall operation, fully specified.
/// </summary>
/// <remarks>
/// <para>
/// This is the entire vocabulary of the elevated helper. It cannot express a command line, a script, a
/// process to run, a registry path, a service, a file, a profile-wide setting, or a rule shape other
/// than one inbound allow rule for one program on one port. There is no field that could carry one, so
/// no caller — mistaken or malicious — can turn the helper into a general privileged tool by supplying
/// different input.
/// </para>
/// <para>
/// It travels as command-line arguments rather than as a file the helper reads after elevation. The
/// arguments are fixed at the moment Windows shows the prompt, so what the user approves is what runs;
/// a file could be rewritten in the gap between the prompt appearing and the helper reading it.
/// </para>
/// </remarks>
public sealed record FirewallHelperCommand
{
    public FirewallHelperOperation Operation { get; init; }

    /// <summary>
    /// Correlates this run with the request that started it. Not a secret and not treated as one: a
    /// command line is readable by the same user. Its job is to let the Agent discard a result that
    /// belongs to an operation it has already superseded.
    /// </summary>
    public Guid OperationId { get; init; }

    public Guid ServerId { get; init; }

    /// <summary>The rule to create, replace or remove.</summary>
    public Guid RuleId { get; init; }

    /// <summary>
    /// Legacy wire field retained for strict rejection of old two-rule update requests. Current
    /// updates keep one stable owned identity and replace that exact proven-owned rule in place.
    /// </summary>
    public Guid PreviousRuleId { get; init; }

    public string ProgramPath { get; init; } = "";
    public int Port { get; init; }
    public MappingTransport Transport { get; init; } = MappingTransport.Tcp;
    public FirewallProfile Profiles { get; init; } = FirewallProfile.None;

    public bool MutatesRuleShape =>
        Operation is FirewallHelperOperation.Create or FirewallHelperOperation.Update;

    public FirewallRulePlan ToPlan() => new()
    {
        ServerId = ServerId,
        RuleId = RuleId,
        ProgramPath = ProgramPath,
        Port = Port,
        Transport = Transport,
        Profiles = Profiles
    };
}

/// <summary>The outcome of parsing and validating a helper command line.</summary>
public sealed record FirewallHelperCommandParseResult
{
    public FirewallHelperCommand? Command { get; init; }
    public string Error { get; init; } = "";
    public bool Valid => Command is not null;

    public static FirewallHelperCommandParseResult Rejected(string error) => new() { Error = error };
}

/// <summary>
/// Builds and validates the helper's argument list.
/// </summary>
/// <remarks>
/// Validation is deliberately whole-input: every token must be a flag this contract knows, every flag
/// may appear once, every value must parse exactly, and flags that do not belong to the operation are
/// rejected rather than ignored. Anything unrecognised fails the whole command, so an argument the
/// helper does not understand can never be silently carried into a privileged operation.
/// </remarks>
public static class FirewallHelperCommandParser
{
    public const string OperationFlag = "--operation";
    public const string OperationIdFlag = "--operation-id";
    public const string ServerIdFlag = "--server-id";
    public const string RuleIdFlag = "--rule-id";
    public const string PreviousRuleIdFlag = "--previous-rule-id";
    public const string ProgramFlag = "--program";
    public const string PortFlag = "--port";
    public const string TransportFlag = "--transport";
    public const string ProfilesFlag = "--profiles";

    /// <summary>Runs the helper's own logic against an in-memory store. Nothing privileged happens.</summary>
    public const string SelfTestFlag = "--self-test";

    public static IReadOnlyList<string> ToArguments(FirewallHelperCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var arguments = new List<string>(16)
        {
            OperationFlag, OperationName(command.Operation),
            OperationIdFlag, command.OperationId.ToString("D", CultureInfo.InvariantCulture),
            ServerIdFlag, command.ServerId.ToString("D", CultureInfo.InvariantCulture),
            RuleIdFlag, command.RuleId.ToString("D", CultureInfo.InvariantCulture)
        };
        if (command.PreviousRuleId != Guid.Empty)
        {
            arguments.Add(PreviousRuleIdFlag);
            arguments.Add(command.PreviousRuleId.ToString("D", CultureInfo.InvariantCulture));
        }
        if (command.MutatesRuleShape)
        {
            arguments.Add(ProgramFlag);
            arguments.Add(command.ProgramPath);
            arguments.Add(PortFlag);
            arguments.Add(command.Port.ToString(CultureInfo.InvariantCulture));
            arguments.Add(TransportFlag);
            arguments.Add(TransportName(command.Transport));
            arguments.Add(ProfilesFlag);
            arguments.Add(ProfileNames(command.Profiles));
        }
        return arguments;
    }

    public static FirewallHelperCommandParseResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index++)
        {
            var flag = arguments[index];
            if (!IsKnownFlag(flag))
                return FirewallHelperCommandParseResult.Rejected($"Unrecognised argument '{flag}'.");
            if (index + 1 >= arguments.Count)
                return FirewallHelperCommandParseResult.Rejected($"'{flag}' has no value.");
            if (!values.TryAdd(flag, arguments[index + 1]))
                return FirewallHelperCommandParseResult.Rejected($"'{flag}' was given more than once.");
            index++;
        }

        if (!values.TryGetValue(OperationFlag, out var operationText))
            return FirewallHelperCommandParseResult.Rejected($"'{OperationFlag}' is required.");
        if (!TryParseOperation(operationText, out var operation))
            return FirewallHelperCommandParseResult.Rejected($"'{operationText}' is not a supported operation.");

        if (!TryGuid(values, OperationIdFlag, required: true, out var operationId, out var error) ||
            !TryGuid(values, ServerIdFlag, required: true, out var serverId, out error) ||
            !TryGuid(values, RuleIdFlag, required: true, out var ruleId, out error) ||
            !TryGuid(values, PreviousRuleIdFlag, required: false, out var previousRuleId, out error))
            return FirewallHelperCommandParseResult.Rejected(error);

        var mutatesShape = operation is FirewallHelperOperation.Create or FirewallHelperOperation.Update;
        if (!mutatesShape)
        {
            foreach (var flag in new[] { ProgramFlag, PortFlag, TransportFlag, ProfilesFlag })
            {
                if (values.ContainsKey(flag))
                    return FirewallHelperCommandParseResult.Rejected(
                        $"'{flag}' is not valid for {OperationName(operation)}.");
            }
            if (operation == FirewallHelperOperation.Remove && previousRuleId != Guid.Empty)
                return FirewallHelperCommandParseResult.Rejected(
                    $"'{PreviousRuleIdFlag}' is not valid for {OperationName(operation)}.");
            return new FirewallHelperCommandParseResult
            {
                Command = new FirewallHelperCommand
                {
                    Operation = operation,
                    OperationId = operationId,
                    ServerId = serverId,
                    RuleId = ruleId
                }
            };
        }

        if (operation == FirewallHelperOperation.Create && previousRuleId != Guid.Empty)
            return FirewallHelperCommandParseResult.Rejected(
                $"'{PreviousRuleIdFlag}' is not valid for {OperationName(operation)}.");
        if (operation == FirewallHelperOperation.Update && previousRuleId != Guid.Empty)
            return FirewallHelperCommandParseResult.Rejected(
                $"'{PreviousRuleIdFlag}' is no longer valid for update; updates retain one owned rule identity.");

        if (!values.TryGetValue(ProgramFlag, out var program))
            return FirewallHelperCommandParseResult.Rejected($"'{ProgramFlag}' is required.");
        if (!WindowsFirewallPolicy.IsTrustworthyJavaRuntime(program, out var runtimeReason))
            return FirewallHelperCommandParseResult.Rejected(runtimeReason);

        if (!values.TryGetValue(PortFlag, out var portText) ||
            !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
            return FirewallHelperCommandParseResult.Rejected($"'{PortFlag}' must be a port from 1 through 65535.");

        if (!values.TryGetValue(TransportFlag, out var transportText))
            return FirewallHelperCommandParseResult.Rejected($"'{TransportFlag}' is required.");
        if (!TryParseTransport(transportText, out var transport, out var transportError))
            return FirewallHelperCommandParseResult.Rejected(transportError);

        if (!values.TryGetValue(ProfilesFlag, out var profilesText))
            return FirewallHelperCommandParseResult.Rejected($"'{ProfilesFlag}' is required.");
        if (!TryParseProfiles(profilesText, out var profiles, out var profileError))
            return FirewallHelperCommandParseResult.Rejected(profileError);

        return new FirewallHelperCommandParseResult
        {
            Command = new FirewallHelperCommand
            {
                Operation = operation,
                OperationId = operationId,
                ServerId = serverId,
                RuleId = ruleId,
                PreviousRuleId = previousRuleId,
                ProgramPath = program.Trim(),
                Port = port,
                Transport = transport,
                Profiles = profiles
            }
        };
    }

    public static string OperationName(FirewallHelperOperation operation) => operation switch
    {
        FirewallHelperOperation.Create => "create",
        FirewallHelperOperation.Update => "update",
        _ => "remove"
    };

    private static bool IsKnownFlag(string flag) =>
        flag is OperationFlag or OperationIdFlag or ServerIdFlag or RuleIdFlag or PreviousRuleIdFlag
            or ProgramFlag or PortFlag or TransportFlag or ProfilesFlag;

    private static bool TryParseOperation(string text, out FirewallHelperOperation operation)
    {
        switch (text)
        {
            case "create":
                operation = FirewallHelperOperation.Create;
                return true;
            case "update":
                operation = FirewallHelperOperation.Update;
                return true;
            case "remove":
                operation = FirewallHelperOperation.Remove;
                return true;
            default:
                operation = FirewallHelperOperation.Remove;
                return false;
        }
    }

    /// <summary>
    /// Only TCP is accepted. Bedrock's UDP port is modelled end to end so it cannot be inherited by
    /// accident, and refused here so this milestone cannot open one.
    /// </summary>
    private static bool TryParseTransport(string text, out MappingTransport transport, out string error)
    {
        transport = MappingTransport.Tcp;
        if (text == "tcp")
        {
            error = "";
            return true;
        }
        error = text == "udp"
            ? "UDP firewall access is not supported in this version."
            : $"'{text}' is not a supported transport.";
        return false;
    }

    private static bool TryParseProfiles(string text, out FirewallProfile profiles, out string error)
    {
        profiles = FirewallProfile.None;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!seen.Add(part))
            {
                error = $"'{part}' was listed twice in {ProfilesFlag}.";
                return false;
            }
            switch (part)
            {
                case "domain":
                    profiles |= FirewallProfile.Domain;
                    break;
                case "private":
                    profiles |= FirewallProfile.Private;
                    break;
                case "public":
                    profiles |= FirewallProfile.Public;
                    break;
                default:
                    error = $"'{part}' is not a Windows firewall profile.";
                    return false;
            }
        }
        if (profiles == FirewallProfile.None)
        {
            error = $"'{ProfilesFlag}' must name at least one profile.";
            return false;
        }
        if (profiles != FirewallProfile.Domain && profiles != FirewallProfile.Private &&
            profiles != FirewallProfile.Public)
        {
            error = $"'{ProfilesFlag}' must name exactly one applicable profile.";
            return false;
        }
        error = "";
        return true;
    }

    private static bool TryGuid(
        IReadOnlyDictionary<string, string> values,
        string flag,
        bool required,
        out Guid value,
        out string error)
    {
        value = Guid.Empty;
        if (!values.TryGetValue(flag, out var text))
        {
            error = required ? $"'{flag}' is required." : "";
            return !required;
        }
        if (!Guid.TryParseExact(text, "D", out value) || value == Guid.Empty)
        {
            error = $"'{flag}' must be a GUID.";
            return false;
        }
        error = "";
        return true;
    }

    private static string TransportName(MappingTransport transport) =>
        transport == MappingTransport.Udp ? "udp" : "tcp";

    private static string ProfileNames(FirewallProfile profiles)
    {
        var names = new List<string>(3);
        if (profiles.HasFlag(FirewallProfile.Domain))
            names.Add("domain");
        if (profiles.HasFlag(FirewallProfile.Private))
            names.Add("private");
        if (profiles.HasFlag(FirewallProfile.Public))
            names.Add("public");
        return string.Join(",", names);
    }
}

/// <summary>
/// The whole privileged surface: read the rules, add one rule, remove one rule by name.
/// </summary>
/// <remarks>
/// Implemented for real by the helper against the Windows Firewall COM API, and by a fake in tests so
/// every branch of <see cref="FirewallHelperRunner"/> is proven without elevation and without touching
/// the machine's real policy store.
/// </remarks>
public interface IFirewallMutationBackend
{
    FirewallBackendStatus Status { get; }

    IReadOnlyList<FirewallRuleSnapshot> ReadRules();

    /// <summary>
    /// Adds a rule. Windows overwrites an existing rule with the same identifier, which is why every
    /// caller in <see cref="FirewallHelperRunner"/> establishes first that the identifier is free or
    /// provably ChunkPilot's.
    /// </summary>
    void AddRule(FirewallRulePlan plan);

    void RemoveRule(string ruleName);
}

/// <summary>What one privileged run did.</summary>
public sealed record FirewallHelperResult(FirewallHelperExitCode ExitCode, string Detail);

/// <summary>
/// The privileged operation itself, expressed once and shared by the helper executable and its tests.
/// </summary>
/// <remarks>
/// <para>
/// The ownership rule is enforced here as well as in the Agent, because this is the code that actually
/// holds administrator rights. It never overwrites and never deletes a rule that does not carry
/// ChunkPilot's own group and rule identifier, so a name collision with somebody else's rule stops the
/// operation instead of replacing their configuration.
/// </para>
/// <para>
/// An update retains the stable rule identifier. The helper first proves that the existing rule with
/// that identifier carries ChunkPilot ownership evidence, then uses the documented Add replacement
/// semantics to replace only that exact rule. A failed Add leaves the previous rule standing.
/// </para>
/// </remarks>
public static class FirewallHelperRunner
{
    public static FirewallHelperResult Run(FirewallHelperCommand command, IFirewallMutationBackend backend)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(backend);

        switch (backend.Status)
        {
            case FirewallBackendStatus.Unavailable:
                return new FirewallHelperResult(FirewallHelperExitCode.FirewallUnavailable,
                    "The Windows Firewall service did not answer.");
            case FirewallBackendStatus.GroupPolicyOverride:
                return new FirewallHelperResult(FirewallHelperExitCode.PolicyPrevented,
                    "Group Policy controls this firewall profile, so a local rule would not take effect.");
            case FirewallBackendStatus.InboundBlocked:
                return new FirewallHelperResult(FirewallHelperExitCode.PolicyPrevented,
                    "Windows is blocking all unsolicited inbound traffic, so an allow rule would not take effect.");
        }

        return command.Operation switch
        {
            FirewallHelperOperation.Remove => Remove(command, backend),
            _ => CreateOrUpdate(command, backend)
        };
    }

    private static FirewallHelperResult CreateOrUpdate(
        FirewallHelperCommand command, IFirewallMutationBackend backend)
    {
        var plan = command.ToPlan();
        var rules = backend.ReadRules();

        var collision = WindowsFirewallPolicy.FindByName(plan.RuleName, rules);
        if (collision is not null && !WindowsFirewallPolicy.CarriesOwnershipEvidence(collision, plan.RuleId))
            return new FirewallHelperResult(FirewallHelperExitCode.OwnershipConflict,
                $"A rule named '{plan.RuleName}' already exists and does not carry ChunkPilot's evidence. " +
                "It was left untouched.");

        if (command.Operation == FirewallHelperOperation.Update)
        {
            if (collision is null)
                return new FirewallHelperResult(FirewallHelperExitCode.OwnershipConflict,
                    $"The owned rule '{plan.RuleName}' is no longer present. Nothing was changed.");
            if (!WindowsFirewallPolicy.CarriesOwnershipEvidence(collision, command.RuleId))
                return new FirewallHelperResult(FirewallHelperExitCode.OwnershipConflict,
                    $"The rule named '{plan.RuleName}' this update would replace does not carry ChunkPilot's " +
                    "evidence. Nothing was changed.");
            backend.AddRule(plan);
            return new FirewallHelperResult(FirewallHelperExitCode.Applied,
                $"Updated the proven-owned rule '{plan.RuleName}'.");
        }

        backend.AddRule(plan);
        return new FirewallHelperResult(FirewallHelperExitCode.Applied, $"Added '{plan.RuleName}'.");
    }

    private static FirewallHelperResult Remove(
        FirewallHelperCommand command, IFirewallMutationBackend backend)
    {
        var ruleName = WindowsFirewallPolicy.RuleName(command.RuleId);
        var existing = WindowsFirewallPolicy.FindByName(ruleName, backend.ReadRules());
        if (existing is null)
            return new FirewallHelperResult(FirewallHelperExitCode.NothingToRemove,
                $"No rule named '{ruleName}' exists.");
        if (!WindowsFirewallPolicy.CarriesOwnershipEvidence(existing, command.RuleId))
            return new FirewallHelperResult(FirewallHelperExitCode.OwnershipConflict,
                $"The rule named '{ruleName}' does not carry ChunkPilot's evidence. It was left untouched.");
        backend.RemoveRule(ruleName);
        return new FirewallHelperResult(FirewallHelperExitCode.Applied, $"Withdrew '{ruleName}'.");
    }
}

/// <summary>
/// Finds the privileged helper in ChunkPilot's own installation, and nowhere else.
/// </summary>
/// <remarks>
/// Resolution starts from the running application's base directory, which is the trusted installation
/// folder in a packaged build. The development fallback is derived from that same directory rather than
/// searched for, so neither <c>PATH</c>, the working directory, nor any other writable search order can
/// decide which executable is elevated. It mirrors the way the Agent is already located.
/// </remarks>
public static class FirewallHelperLocator
{
    public const string HelperFileName = "ChunkPilot.FirewallHelper.exe";

    public static string? Resolve(string baseDirectory, Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentNullException.ThrowIfNull(fileExists);

        var packaged = Path.Combine(baseDirectory, HelperFileName);
        if (fileExists(packaged))
            return packaged;

        var configuration = baseDirectory.Contains(@"\Release\", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var sibling = Path.GetFullPath(Path.Combine(baseDirectory,
            "..", "..", "..", "..", "ChunkPilot.FirewallHelper", "bin", configuration,
            "net10.0-windows", HelperFileName));
        return fileExists(sibling) ? sibling : null;
    }
}
