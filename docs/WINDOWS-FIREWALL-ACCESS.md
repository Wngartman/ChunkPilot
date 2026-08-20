# Consent-first Windows Firewall access

Friend Connectivity v2 adds one optional Windows Firewall layer to **Direct internet**. Router mapping
and firewall permission are independent: selecting Direct internet, starting or stopping a server,
opening or closing ChunkPilot, and checking status are read-only with respect to the firewall.

## Durable permission, not hosting lifetime

The exact rule is durable server configuration. It is not owned by a WPF window, App session, Agent
process, or public-connectivity lease. Closing the UI—normally or by Task Manager, `taskkill`, or a
crash—does not remove the rule and never raises UAC after the user is gone. ChunkPilot has no elevated
window guardian, helper guard mode, dynamic WFP lifetime session, deferred privileged backend, or
permanent privileged service.

This is safe because a firewall permission is not a listener or public route. On actual UI-process
death the Agent revokes every public lease, stops router/tunnel renewal, safely stops the exact managed
server process and confirms its listener is gone. The durable rule may remain, but it serves no stopped
managed listener and is not evidence of LAN reachability, a router/tunnel route, or external
reachability. Starting that server later still does not manufacture a public lease; Direct internet
requires a new explicit enable action.

## User flow

1. Overview shows the currently observed Windows Firewall state.
2. **Allow through Windows Firewall** opens an inline explanation; it does not elevate or mutate.
3. Confirming asks Windows to run ChunkPilot's one-shot helper as administrator.
4. The non-elevated Agent re-reads policy and shows **Firewall rule ready** only when every intended
   property and ownership marker matches. Dismissing UAC is cancellation and changes nothing.

Windows-classified Public networks require a second explicit approval. ChunkPilot never changes the
network category. Managed policy, block-all inbound policy, a disabled firewall, a foreign block, and
unknown policy are shown instead of being overridden.

## Compatibility diagnostics

The Agent gathers independent evidence for the server target, trusted LAN path, Network List Manager
profile, firewall platform, effective policy, relevant rules, and mutation readiness. One typed reason
is selected by deterministic priority and the App maps that reason to concise copy and executable
actions. Presentation code never parses COM messages or target-detail strings to decide what happened.

Known Java, port, adapter, interface index, local address, gateway, and profile fields survive a failure
in another layer. A missing NLM profile therefore does not erase the exact Java and TCP port, and an
unverified Java runtime does not erase the trusted Ethernet/Wi-Fi path or Public/Private/Domain profile.
The compact Overview surface shows one diagnosis; **Firewall technical details** groups the retained
evidence by Server, Network, Firewall, and Operation. **Copy technical details** puts only that bounded,
local evidence on the clipboard after an explicit click. It includes no world data, tokens, public IP,
or unrelated adapter inventory.

The read-only **Check again** action replaces the current diagnostic snapshot. It refreshes target,
route/interface, NLM, policy, and rule evidence without UAC, router changes, network-category changes,
service control, or a firewall mutation.

Firewall platform activation and policy completeness are separate facts. After `HNetCfg.FwPolicy2`
is created, ChunkPilot independently reads the active profile bitmask, each profile's enabled state,
each profile's block-all-inbound state, local-policy modify state, and the firewall rules collection.
One later property failure is reported as **Windows Firewall policy couldn't be fully verified** and
does not erase a profile, enabled state, server target, or network path that Windows already returned.
The exact unavailable field remains in technical details.

The safety gate remains narrower than the diagnostic snapshot. Creating or updating a rule requires
the active profile, enabled and block-all values for that exact profile, local modify state, and a
complete rule enumeration. Removal and any claim that a rule is absent require complete rule
enumeration. A failure on an unrelated profile does not erase the selected profile's evidence, while a
missing field needed by the requested operation always fails closed.

`INetFwRules::_NewEnum` returns an object supporting native `IEnumVARIANT`; modern .NET exposes the
Automation result as a CLR `IEnumerator` wrapper. Both the non-elevated reader and elevated helper
consume that managed projection. Neither path manually re-declares `INetFwPolicy2` or `INetFwRules`.

Normal VPN and virtual-adapter presence is not an error. Firewall profile selection reuses the same
physical LAN filter and route evidence as router mapping, then requires one exact normalized adapter
GUID or InterfaceIndex match from NLM. VPN/tunnel, virtual-switch, disconnected, and link-local
interfaces cannot displace a proven Ethernet or Wi-Fi path. Genuine multiple-path or duplicate-profile
ambiguity fails closed; there is no manual chooser because the current server endpoint model cannot
validate an arbitrary manual adapter strongly enough.

Priority is deliberate: unreadable firewall platform and effective organization policy precede local
rule consent; mutation/ownership evidence precedes a new request; exact-target failures precede Public
approval; Public remains a known profile requiring separate consent. Thus managed policy plus Public is
reported as managed policy, while unknown Java plus Public is reported as unknown Java and still shows
the Public evidence in details.

ChunkPilot does not identify a third-party firewall by scanning processes, the registry, or a product
name list. Windows Security Center exposes aggregate provider health, while Windows Firewall's product
API describes third-party registrations; neither is treated here as authoritative proof that a named
product controls this server's inbound traffic. When Windows Firewall is disabled or unreadable, the UI
truthfully says another security product may control incoming connections without naming or blaming one.

Microsoft API references used for that boundary:

- [INetFwPolicy2::get_CurrentProfileTypes](https://learn.microsoft.com/windows/win32/api/netfw/nf-netfw-inetfwpolicy2-get_currentprofiletypes)
  returns a bitmask and may report multiple active profiles.
- [INetFwPolicy2::get_FirewallEnabled](https://learn.microsoft.com/windows/win32/api/netfw/nf-netfw-inetfwpolicy2-get_firewallenabled)
  and [INetFwPolicy2::get_BlockAllInboundTraffic](https://learn.microsoft.com/windows/win32/api/netfw/nf-netfw-inetfwpolicy2-get_blockallinboundtraffic)
  each take exactly one profile type per call.
- [INetFwPolicy2::get_LocalPolicyModifyState](https://learn.microsoft.com/windows/win32/api/netfw/nf-netfw-inetfwpolicy2-get_localpolicymodifystate)
  is the read-only evidence for whether a local rule can take effect.
- [INetFwRules](https://learn.microsoft.com/windows/win32/api/netfw/nn-netfw-inetfwrules)
  documents that `_NewEnum` returns an object supporting `IEnumVARIANT` for rule traversal.
- [WscGetSecurityProviderHealth](https://learn.microsoft.com/windows/win32/api/wscapi/nf-wscapi-wscgetsecurityproviderhealth)
  returns aggregate category health, not an authoritative active-product identity for this traffic.
- [INetFwProduct](https://learn.microsoft.com/windows/win32/api/netfw/nn-netfw-inetfwproduct)
  describes a third-party firewall product registration. ChunkPilot does not treat registration alone
  as proof that the product currently controls the server connection.

## Exact scope and ownership

An owned Java Edition rule is enabled, inbound, allow, TCP, one exact local port, one exact managed
Java executable, one authorized applicable profile, all remote clients, all interface types, no service,
and edge traversal disabled. ChunkPilot does not create UDP, any-program, any-port, or all-profile rules.

Ownership requires all of the following: a stable persisted server association, a unique stable rule
ID, the ChunkPilot rule group, the matching ChunkPilot description, and a matching live rule. A foreign
rule is never renamed, replaced, disabled, or removed. A same-name collision fails closed before the
firewall API's Add operation can replace it.

Foreign coverage is deliberately stricter than general Windows rule union semantics. A foreign allow
suppresses setup only when every known condition is provably equivalent to ChunkPilot's exact Java,
TCP port, single profile, unrestricted addresses and interfaces, no service, no AppContainer/package,
no user or machine authorization list, no local-user owner, no IPsec requirement, and no edge
traversal. Any-program, any-port, protocol-any, all-profile, or edge-enabled rules are classified as
broad and remain technical evidence only. ChunkPilot leaves them untouched and still offers its normal
exact-rule consent flow so ownership and removal remain deterministic.

The reader models `INetFwRule`'s application, service, protocol, ports, ICMP, addresses, profiles,
named interfaces, interface types, direction, action, enabled, and edge condition;
`INetFwRule2::EdgeTraversalOptions`; and `INetFwRule3`'s `LocalAppPackageId`, `LocalUserOwner`, local and
remote authorized-user lists, authorized remote-machine list, and `SecureFlags`. Per-property read
failures stay attached to the enumerated rule rather than being mistaken for an empty condition. An
unknown allow never suppresses setup. A clearly unrelated block is ignored, a proven applicable block
wins over every allow, and an unknown potentially applicable block prevents a false ready claim.

Microsoft API references for those semantics:

- [INetFwRule](https://learn.microsoft.com/windows/win32/api/netfw/nn-netfw-inetfwrule) documents the
  base rule match properties, including named interfaces and ICMP conditions.
- [INetFwRule2](https://learn.microsoft.com/windows/win32/api/netfw/nn-netfw-inetfwrule2) adds
  `EdgeTraversalOptions`.
- [INetFwRule3](https://learn.microsoft.com/windows/win32/api/netfw/nn-netfw-inetfwrule3) adds package,
  user, machine, and security conditions for AppContainer-aware rules.
- [INetFwRule3::get_LocalUserOwner](https://learn.microsoft.com/windows/win32/api/netfw/nf-netfw-inetfwrule3-get_localuserowner)
  states that, without local-user conditions, matching traffic must be destined to or originate from
  the owner SID.
- [NET_FW_AUTHENTICATE_TYPE](https://learn.microsoft.com/windows/win32/api/icftypes/ne-icftypes-net_fw_authenticate_type)
  defines the IPsec authentication/integrity/encryption requirements represented by `SecureFlags`.

The authoritative Java path comes from a live process identity owned by the managed server or its
persisted healthy ChunkPilot-managed runtime assignment. It never comes from PATH, a process name,
free text, or an arbitrary `java.exe`. Port, Java path, or profile changes make the existing rule stale.
An explicit update keeps the stable ID, changes only a proven-owned rule, and re-verifies it.

## Privilege boundary

The App and long-running Agent are ordinary user processes. The Agent builds and validates a fixed
create, update, or remove command. The App passes those arguments unchanged to a helper resolved only
from the application directory (or its deterministic sibling build directory during development).
Windows UAC elevates that helper for one operation, after which it exits.

The helper parser has no generic command, PowerShell, `netsh`, registry, service, filesystem, or child
process surface. It accepts only the rule identity, server identity, exact fully-qualified Java path,
valid TCP port, and exactly one Domain, Private, or Public profile needed for its firewall-domain
operation. It inspects a collision before Add and mutates/removes only proven ChunkPilot ownership.

Helper exit code is never sufficient evidence. The Agent correlates the operation ID, re-resolves the
current server/runtime/port/profile, reads firewall policy, and verifies the live postcondition.

## Persistence and lifecycle

Schema version 6 adds the forward-only `firewall_access` ownership record. Existing servers migrate as
having no ChunkPilot-owned rule. Configuration, stale target, last checked time, failures, and pending
removal evidence round-trip without changing server or world data.

A valid firewall rule persists across Stop, Start, Restart, App close, actual UI-process death, and
Agent restart; those events do not request UAC. Startup reconciliation is read-only and never repairs administrator changes. A
manually deleted, disabled, or edited rule becomes needs-attention/stale. A failed removal retains
ownership evidence. A dismissed UAC prompt creates no durable intent and remains only as the current
informational snapshot until a read-only check replaces it. Server deletion is refused until a proven-owned rule is verified
gone, preventing silent orphaning without risking the server folder or world.

## Truthfulness boundary

Firewall permission, a local listener, LAN reachability, a public router/tunnel route, and external
verification are separate truths. **Router and Windows Firewall are configured. External reachability
has not been verified.** A router mapping, a local socket, a local Minecraft status reply, and an exact
firewall rule do not prove that a friend on the internet can connect.

That line is corrected by evidence and by nothing else. A separate, deliberate **Check from outside**
can produce genuine external evidence for one exact endpoint at one moment, and only then does the
combined status stop saying reachability is unverified. The firewall layer neither performs nor gates
that check: a user holding an exact foreign allow rule, running a third-party product, or with Windows
Firewall switched off is still entitled to look outside, because a successful external connection is
stronger reality evidence than any local configuration inference. See
[External reachability probe](EXTERNAL-REACHABILITY-PROBE.md).
