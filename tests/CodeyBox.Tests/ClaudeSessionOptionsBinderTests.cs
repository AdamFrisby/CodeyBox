using System.Collections.Concurrent;
using CodeyBox.Agents.Claude;
using CodeyBox.Api;
using Microsoft.Extensions.Options;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the configuration -> <see cref="ClaudeSessionWorkerOptions"/> binding
/// path used by <c>Program.cs</c>: case-insensitive transport parse with
/// invalid-value fallback to <see cref="ClaudeSessionTransport.Print"/>,
/// override-dictionary clear+repopulate semantics, and the
/// <see cref="IOptionsMonitor{T}.OnChange"/> wiring that mutates the live
/// singleton so the worker observes the new transport on the next dispatch.
/// </summary>
public sealed class ClaudeSessionOptionsBinderTests
{
    [Theory]
    [InlineData("acp", ClaudeSessionTransport.Acp)]
    [InlineData("ACP", ClaudeSessionTransport.Acp)]
    [InlineData("AcP", ClaudeSessionTransport.Acp)]
    [InlineData("print", ClaudeSessionTransport.Print)]
    [InlineData("PRINT", ClaudeSessionTransport.Print)]
    public void ParseTransport_KnownValue_ReturnsExpected(string input, ClaudeSessionTransport expected)
        => Assert.Equal(expected, ClaudeSessionOptionsBinder.ParseTransport(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("not-a-transport")]
    [InlineData("garbage")]
    public void ParseTransport_InvalidOrWhitespace_FallsBackToPrint(string? input)
        => Assert.Equal(ClaudeSessionTransport.Print, ClaudeSessionOptionsBinder.ParseTransport(input));

    [Fact]
    public void Apply_InvalidTransport_FlipsLiveSingletonToPrint_Default()
    {
        var live = new ClaudeSessionWorkerOptions { Transport = ClaudeSessionTransport.Acp };
        ClaudeSessionOptionsBinder.Apply(live, new ClaudeSessionOptions { Transport = "xyzzy" });
        Assert.Equal(ClaudeSessionTransport.Print, live.Transport);
    }

    [Fact]
    public void Apply_EnabledAndEmitTurnMetrics_RoundTripIntoLiveSingleton()
    {
        var live = new ClaudeSessionWorkerOptions();
        ClaudeSessionOptionsBinder.Apply(live, new ClaudeSessionOptions
        {
            Enabled = true,
            EmitTurnMetrics = false,
            Transport = "acp",
        });

        Assert.True(live.Enabled);
        Assert.False(live.EmitTurnMetrics);
        Assert.Equal(ClaudeSessionTransport.Acp, live.Transport);
    }

    [Fact]
    public void Apply_OverrideDictionariesRepopulate_OnEveryReload_PriorEntriesPurged()
    {
        var live = new ClaudeSessionWorkerOptions();
        live.TransportOverridesByAgentClassMember["stale-member"] = ClaudeSessionTransport.Acp;
        live.TransportOverridesByProject["stale-proj"] = ClaudeSessionTransport.Acp;

        ClaudeSessionOptionsBinder.Apply(live, new ClaudeSessionOptions
        {
            TransportOverridesByAgentClassMember = new Dictionary<string, string>
            {
                ["fast-member"] = "acp",
            },
            TransportOverridesByProject = new Dictionary<string, string>
            {
                ["proj-A"] = "acp",
            },
        });

        Assert.False(live.TransportOverridesByAgentClassMember.ContainsKey("stale-member"));
        Assert.Equal(ClaudeSessionTransport.Acp,
            live.TransportOverridesByAgentClassMember["fast-member"]);
        Assert.False(live.TransportOverridesByProject.ContainsKey("stale-proj"));
        Assert.Equal(ClaudeSessionTransport.Acp,
            live.TransportOverridesByProject["proj-A"]);
    }

    [Fact]
    public void Apply_OverrideMaps_BlankKeysAndInvalidValuesAreScrubbed()
    {
        var live = new ClaudeSessionWorkerOptions();
        ClaudeSessionOptionsBinder.Apply(live, new ClaudeSessionOptions
        {
            TransportOverridesByAgentClassMember = new Dictionary<string, string>
            {
                ["valid"] = "acp",
                ["   "] = "acp",        // blank key dropped
                ["other"] = "garbage",   // invalid value → Print fallback
            },
        });

        Assert.Equal(2, live.TransportOverridesByAgentClassMember.Count);
        Assert.Equal(ClaudeSessionTransport.Acp,
            live.TransportOverridesByAgentClassMember["valid"]);
        Assert.Equal(ClaudeSessionTransport.Print,
            live.TransportOverridesByAgentClassMember["other"]);
    }

    [Fact]
    public void ReplaceOverrides_NullSource_ClearsTarget()
    {
        var target = new ConcurrentDictionary<string, ClaudeSessionTransport>();
        target["was-here"] = ClaudeSessionTransport.Acp;
        ClaudeSessionOptionsBinder.ReplaceOverrides(target, source: null);
        Assert.Empty(target);
    }

    [Fact]
    public void OnChangeWiring_FiringMonitor_MutatesLiveSingleton_OnNextSnapshot()
    {
        // Replays the exact factory body in Program.cs: a captured live
        // ClaudeSessionWorkerOptions singleton that the IOptionsMonitor.OnChange
        // callback mutates in place so the worker sees the new transport on the
        // next dispatch without restart.
        var initial = new CodeyBoxOptions
        {
            ClaudeSession = new ClaudeSessionOptions
            {
                Transport = "print",
                TransportOverridesByAgentClassMember = new Dictionary<string, string>
                {
                    ["member-a"] = "acp",
                },
            },
        };
        var monitor = new FiringOptionsMonitor<CodeyBoxOptions>(initial);

        var live = new ClaudeSessionWorkerOptions();
        ClaudeSessionOptionsBinder.Apply(live, monitor.CurrentValue.ClaudeSession);
        monitor.OnChange((opts, _) => ClaudeSessionOptionsBinder.Apply(live, opts.ClaudeSession));

        // Initial snapshot reflects the constructor-time bind.
        Assert.Equal(ClaudeSessionTransport.Print, live.Transport);
        Assert.Equal(ClaudeSessionTransport.Acp,
            live.TransportOverridesByAgentClassMember["member-a"]);

        // Fire a reload: flip transport to acp, swap the member map, add a project map.
        monitor.Fire(new CodeyBoxOptions
        {
            ClaudeSession = new ClaudeSessionOptions
            {
                Transport = "acp",
                TransportOverridesByAgentClassMember = new Dictionary<string, string>
                {
                    ["member-b"] = "print",
                },
                TransportOverridesByProject = new Dictionary<string, string>
                {
                    ["proj-1"] = "acp",
                },
            },
        });

        // Same live instance was mutated in place — not replaced.
        Assert.Equal(ClaudeSessionTransport.Acp, live.Transport);
        Assert.False(live.TransportOverridesByAgentClassMember.ContainsKey("member-a"));
        Assert.Equal(ClaudeSessionTransport.Print,
            live.TransportOverridesByAgentClassMember["member-b"]);
        Assert.Equal(ClaudeSessionTransport.Acp,
            live.TransportOverridesByProject["proj-1"]);

        // Second reload: invalid transport string → Print fallback, override maps cleared.
        monitor.Fire(new CodeyBoxOptions
        {
            ClaudeSession = new ClaudeSessionOptions
            {
                Transport = "definitely-not-a-transport",
            },
        });

        Assert.Equal(ClaudeSessionTransport.Print, live.Transport);
        Assert.Empty(live.TransportOverridesByAgentClassMember);
        Assert.Empty(live.TransportOverridesByProject);
    }

    private sealed class FiringOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private T _value;
        private readonly List<Action<T, string?>> _listeners = new();
        private readonly object _gate = new();

        public FiringOptionsMonitor(T initial) => _value = initial;

        public T CurrentValue => _value;
        public T Get(string? name) => _value;

        public IDisposable OnChange(Action<T, string?> listener)
        {
            lock (_gate) _listeners.Add(listener);
            return new Subscription(() => { lock (_gate) _listeners.Remove(listener); });
        }

        public void Fire(T next)
        {
            _value = next;
            Action<T, string?>[] snapshot;
            lock (_gate) snapshot = _listeners.ToArray();
            foreach (var l in snapshot) l(next, null);
        }

        private sealed class Subscription : IDisposable
        {
            private readonly Action _onDispose;
            public Subscription(Action onDispose) => _onDispose = onDispose;
            public void Dispose() => _onDispose();
        }
    }
}
