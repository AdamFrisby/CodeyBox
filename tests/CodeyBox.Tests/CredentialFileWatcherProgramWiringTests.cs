using CodeyBox.Api;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class CredentialFileWatcherProgramWiringTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("codeybox-program-watchers-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void ProgramWiresCredentialSourcesWithConfiguredWatchFlag(string configuredValue, bool expectedWatching)
    {
        var paths = CredentialWatcherPaths.Create(_tempDir);
        using var env = new EnvironmentVariablesScope(
            (CredentialFileWatcherSettings.EnvironmentVariable, configuredValue),
            ("CODEYBOX_CLAUDE_OAUTH_FILE", paths.Claude),
            ("CODEYBOX_CODEX_OAUTH_FILE", paths.Codex),
            ("CODEYBOX_GEMINI_OAUTH_FILE", paths.GeminiOAuth),
            ("CODEYBOX_GEMINI_SETTINGS_FILE", paths.GeminiSettings));
        using var factory = new CredentialWatcherProgramFactory(_tempDir);

        var sources = new CredentialFileSource[]
        {
            factory.Services.GetRequiredService<ClaudeCredentialFileSource>(),
            factory.Services.GetRequiredService<CodexCredentialFileSource>(),
            factory.Services.GetRequiredService<GeminiOAuthCredentialFileSource>(),
            factory.Services.GetRequiredService<GeminiSettingsCredentialFileSource>(),
        };

        Assert.Equal(
            new[] { paths.Claude, paths.Codex, paths.GeminiOAuth, paths.GeminiSettings },
            sources.Select(source => source.FilePath));
        Assert.All(sources, source => Assert.Equal(expectedWatching, source.IsWatching));
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void ProgramWiresCredentialSourcesWithConfigurationKeyWhenEnvironmentUnset(
        string configuredValue,
        bool expectedWatching)
    {
        var paths = CredentialWatcherPaths.Create(_tempDir);
        using var env = new EnvironmentVariablesScope(
            (CredentialFileWatcherSettings.EnvironmentVariable, null),
            ("CodeyBox__CredentialFileWatchers", null),
            ("CODEYBOX_CLAUDE_OAUTH_FILE", paths.Claude),
            ("CODEYBOX_CODEX_OAUTH_FILE", paths.Codex),
            ("CODEYBOX_GEMINI_OAUTH_FILE", paths.GeminiOAuth),
            ("CODEYBOX_GEMINI_SETTINGS_FILE", paths.GeminiSettings));
        using var factory = new CredentialWatcherProgramFactory(_tempDir, configuredValue);

        var sources = new CredentialFileSource[]
        {
            factory.Services.GetRequiredService<ClaudeCredentialFileSource>(),
            factory.Services.GetRequiredService<CodexCredentialFileSource>(),
            factory.Services.GetRequiredService<GeminiOAuthCredentialFileSource>(),
            factory.Services.GetRequiredService<GeminiSettingsCredentialFileSource>(),
        };

        Assert.All(sources, source => Assert.Equal(expectedWatching, source.IsWatching));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, "false", false)]
    [InlineData(null, "0", false)]
    [InlineData(null, "no", false)]
    [InlineData(null, "off", false)]
    [InlineData(null, "true", true)]
    [InlineData(null, "1", true)]
    [InlineData(null, "yes", true)]
    [InlineData(null, "on", true)]
    [InlineData("true", "false", true)]
    [InlineData("false", "true", false)]
    [InlineData("", "false", true)]
    public void CredentialFileWatcherSetting_ParsesEnvironmentBeforeConfiguration(
        string? environmentValue,
        string? configuredValue,
        bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CredentialFileWatcherSettings.ConfigurationKey] = configuredValue,
            })
            .Build();

        Assert.Equal(expected, CredentialFileWatcherSettings.IsEnabled(configuration, environmentValue));
    }

    [Theory]
    [InlineData("flase", null)]
    [InlineData(null, "tru")]
    public void CredentialFileWatcherSetting_RejectsUnknownValues(
        string? environmentValue,
        string? configuredValue)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CredentialFileWatcherSettings.ConfigurationKey] = configuredValue,
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => CredentialFileWatcherSettings.IsEnabled(configuration, environmentValue));
        Assert.Contains(CredentialFileWatcherSettings.EnvironmentVariable, ex.Message);
    }

    private sealed class CredentialWatcherProgramFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        private readonly string? _credentialFileWatchers;

        public CredentialWatcherProgramFactory(string root, string? credentialFileWatchers = null)
        {
            _root = root;
            _credentialFileWatchers = credentialFileWatchers;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    [CredentialFileWatcherSettings.ConfigurationKey] = _credentialFileWatchers,
                    ["CodeyBox:StateDatabasePath"] = Path.Combine(_root, "state.db"),
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(_root, "git"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(_root, "logs", "api-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(_root, "logs", "audit-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(_root, "agent-streams"),
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        }
    }

    private sealed record CredentialWatcherPaths(
        string Claude,
        string Codex,
        string GeminiOAuth,
        string GeminiSettings)
    {
        public static CredentialWatcherPaths Create(string root)
            => new(
                WriteJson(root, "claude/.credentials.json", """{"claudeAiOauth":{"accessToken":"claude"}}"""),
                WriteJson(root, "codex/auth.json", """{"tokens":{"access_token":"codex"}}"""),
                WriteJson(root, "gemini/oauth_creds.json", """{"access_token":"gemini"}"""),
                WriteJson(root, "gemini/settings.json", """{"security":{"auth":{"selectedType":"oauth-personal"}}}"""));

        private static string WriteJson(string root, string relativePath, string json)
        {
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            return path;
        }
    }

    private sealed class EnvironmentVariablesScope : IDisposable
    {
        private readonly List<(string Name, string? Previous)> _previous = [];

        public EnvironmentVariablesScope(params (string Name, string? Value)[] variables)
        {
            foreach (var variable in variables)
            {
                _previous.Add((variable.Name, Environment.GetEnvironmentVariable(variable.Name)));
                Environment.SetEnvironmentVariable(variable.Name, variable.Value);
            }
        }

        public void Dispose()
        {
            for (var i = _previous.Count - 1; i >= 0; i--)
                Environment.SetEnvironmentVariable(_previous[i].Name, _previous[i].Previous);
        }
    }
}
