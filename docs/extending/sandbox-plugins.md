# Sandbox provider plugins

A plugin can contribute a sandbox backend by implementing `ISandboxProvider`.
The provider's `Name` becomes its provider kind: name it from a
`SandboxClass` member's `ProviderKind` and placement selects it like any
built-in backend. Each kind is constructed once and shared across every member
that names it, independent of registration order.

```csharp
using CodeyBox.Core;
using CodeyBox.PluginSdk;

[CodeyBoxPlugin(
    id: "acme.vm-backend",
    displayName: "Acme VM backend",
    minHostApiVersion: "1.3")]
public sealed class AcmeSandboxProvider : ISandboxProvider
{
    // The provider kind. Normalised (trimmed, lowercase) and matched by exact
    // ordinal equality — never substring.
    public string Name => "acme-vm";

    public IReadOnlyList<string> DeclaredCapabilities => ["teardown"];

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        // Build and start the sandbox; disposal must tear it down regardless
        // of state, per the ISandboxProvider contract.
        throw new NotImplementedException();
    }

    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

    public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
}
```

```json
{
  "CodeyBox": {
    "SandboxClasses": [
      {
        "Id": "default",
        "Members": [
          { "MemberId": "acme", "ProviderKind": "acme-vm", "Capacity": 8, "PreferenceScore": 100 }
        ]
      }
    ]
  }
}
```

## Trust model

A sandbox backend is the one plugin that most benefits from misdescribing its
own containment — claiming isolation it does not provide. The host therefore
treats a plugin backend the same way it treats agent runners (`IAgentRunner`
stays blocked to plugins entirely): the plugin supplies the mechanism, the
host owns every security claim about it.

### What a plugin sandbox provider can claim

- **Its kind and what it implements.** `Name` picks the kind; `DeclaredCapabilities`
  declares implemented operations (`baseline-bake`, `suspend-resume`, `teardown`,
  `disk-guard`). Placement refuses work needing an operation the provider does
  not implement, even when the member config claims it — unchanged from built-ins.
- **Its isolation level for workload-trust routing.** `IsolationLevel` flows through
  the same `ValidateWorkloadTrust` gate as built-ins: untrusted workloads in
  production still require dedicated-kernel isolation, and the gate reads the
  live provider instance, so opening the kind registry opens no path around it.

### What it cannot claim

- **Enforced network-egress isolation.** Every plugin-contributed kind is
  classified `NotEnforced` by the host's `HostPlatformSupport.GetEgressEnforcement`
  switch, which names exactly the reviewed in-tree kinds. No plugin return value
  and no configuration can promote a plugin kind to `EnforcedOnOrchestratorHost`
  or `EnforcedOnRemoteExecutorHost` — promotion is an in-tree change subject to
  review, never a plugin capability. Claiming otherwise in marketing or comments
  does not change the classification; only the host switch does.
- **Platform support.** Whether the kind runs on an OS is decided by the host
  from its own registry, not asserted by the plugin.

### Where a `NotEnforced` provider may and may not be used

- **May:** sandboxes with no named network profile (the default denied/loopback
  posture), where no enforced-egress guarantee is required.
- **May not:** any acquisition naming a network profile. The profile's allowlist
  only exists as host-side nftables rules the provider never attaches to, so
  placement excludes `NotEnforced` members before the decider runs and fails the
  acquisition as unplaceable (`enforced-egress`) naming the refused kinds — it is
  never quietly used where enforcement was required. The same exclusion applies
  to the built-in `bubblewrap` and `process` runners.

At startup the host logs the egress classification of every constructed kind, so
an operator can see which providers actually contain network egress and which do
not; `NotEnforced` providers are logged as a warning stating the restriction.

## Name rules (fail closed)

- Blank names, names over 128 characters, and names with control characters are
  refused at startup.
- A kind colliding with a built-in (`incus`, `multipass`, …) is refused — a plugin
  must not shadow a reviewed backend. Rename the plugin's `Name`.
- Two plugins claiming one kind are both refused; a kind belongs to exactly one
  plugin.
- A member naming a genuinely unknown kind fails closed naming every registered
  kind — built-ins and plugin-contributed — and never silently falls back to
  another provider.

## What is unchanged

- `IAgentRunner` remains blocked to plugins; this extension point changes one
  surface only.
- Capability declaration (bake, suspend, teardown, disk guard) applies unchanged.
- The early static configuration gate only knows built-in kinds; the authoritative
  platform-support and workload-trust checks run at provider construction with the
  live plugin instance.
