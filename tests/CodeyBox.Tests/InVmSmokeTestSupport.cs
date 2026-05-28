using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>Sandbox provider that runs a scripted exec handler and counts clones.</summary>
internal sealed class ScriptedSandboxProvider : ISandboxProvider
{
    private readonly Func<SandboxExec, SandboxExecResult> _onExec;
    public int CreateCount { get; private set; }
    public Func<Exception>? ThrowOnCreate { get; set; }

    public ScriptedSandboxProvider(Func<SandboxExec, SandboxExecResult> onExec) => _onExec = onExec;

    public string Name => "scripted";

    public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        CreateCount++;
        if (ThrowOnCreate is not null) throw ThrowOnCreate();
        return Task.FromResult<ISandbox>(new ScriptedSandbox(_onExec));
    }

    public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

    public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;

    private sealed class ScriptedSandbox : ISandbox
    {
        private readonly Func<SandboxExec, SandboxExecResult> _onExec;
        public ScriptedSandbox(Func<SandboxExec, SandboxExecResult> onExec) => _onExec = onExec;
        public string Id => "scripted-sandbox";
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default) =>
            Task.FromResult(_onExec(exec));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Baseline resolver returning a fixed ref; can be made to throw.</summary>
internal sealed class StubBaselineResolver : IBaselineImageResolver
{
    public string? Ref { get; set; }
    public bool ThrowOnResolve { get; set; }

    public StubBaselineResolver(string? r) => Ref = r;

    public string? ResolveBaselineRef(string? profileName, SandboxProfileFlavor flavor)
    {
        if (ThrowOnResolve) throw new InvalidOperationException("baseline resolve failed");
        return Ref;
    }

    public Task<IReadOnlyList<BaselineImageInfo>> ListBaselineImagesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BaselineImageInfo>>([]);

    public Task DisposeBaselineImageAsync(string name, CancellationToken ct) => Task.CompletedTask;
}
