using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;

namespace CodeyBox.Tests;

/// <summary>
/// Startup config validation tests for the Gemini-QualityScore-ReasoningMode
/// constraint: any Gemini member with QualityScore >= 90 must have
/// ReasoningMode="high". Verified by resolving AgentClassRouter from the DI
/// container — the factory lambda runs validation on first resolve.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class GeminiReasoningValidationTests
{
    [Fact]
    public void Gemini_QualityScore95_WithHighReasoning_Accepted()
    {
        using var factory = new ValidationTestFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "gemini",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "95",
            ["CodeyBox:AgentClasses:0:Members:0:ReasoningMode"] = "high",
        });

        // Should not throw: Gemini-95 with ReasoningMode="high" is valid.
        var router = factory.Services.GetRequiredService<AgentClassRouter>();
        Assert.NotNull(router);
    }

    [Fact]
    public void Gemini_QualityScore95_WithoutReasoningMode_Rejected()
    {
        using var factory = new ValidationTestFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "gemini",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "95",
            // ReasoningMode intentionally absent
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => factory.Services.GetRequiredService<AgentClassRouter>());

        Assert.Contains("ReasoningMode=\"high\"", ex.Message);
        Assert.Contains("QualityScore=95", ex.Message);
    }

    [Fact]
    public void Gemini_QualityScore90_WithoutReasoningMode_Rejected()
    {
        using var factory = new ValidationTestFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "gemini",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "90",
        });

        Assert.Throws<InvalidOperationException>(
            () => factory.Services.GetRequiredService<AgentClassRouter>());
    }

    [Fact]
    public void Gemini_QualityScore89_WithoutReasoningMode_Accepted()
    {
        // Score < 90 is below the frontier-adjacent threshold: ReasoningMode not required.
        using var factory = new ValidationTestFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "gemini",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "89",
        });

        var router = factory.Services.GetRequiredService<AgentClassRouter>();
        Assert.NotNull(router);
    }

    [Fact]
    public void AnyMember_MissingQualityScore_Rejected()
    {
        // QualityScore is required — no silent default.
        using var factory = new ValidationTestFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            // QualityScore absent
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => factory.Services.GetRequiredService<AgentClassRouter>());

        Assert.Contains("missing QualityScore", ex.Message);
    }

    [Fact]
    public void AnyMember_QualityScoreAbove200_Rejected()
    {
        using var factory = new ValidationTestFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "201",
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => factory.Services.GetRequiredService<AgentClassRouter>());

        Assert.Contains("outside the valid range", ex.Message);
    }

    [Fact]
    public void AnyMember_QualityScoreNegative_Rejected()
    {
        using var factory = new ValidationTestFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:AgentClasses:0:Id"] = "frontier",
            ["CodeyBox:AgentClasses:0:Members:0:Agent"] = "claude",
            ["CodeyBox:AgentClasses:0:Members:0:Billing"] = "Subscription",
            ["CodeyBox:AgentClasses:0:Members:0:QualityScore"] = "-1",
        });

        Assert.Throws<InvalidOperationException>(
            () => factory.Services.GetRequiredService<AgentClassRouter>());
    }

    [Fact]
    public void TodModifier_AbsoluteValueAbove5_Rejected()
    {
        using var factory = new ValidationTestFactory(new Dictionary<string, string?>
        {
            ["CodeyBox:AgentScoreModifiers:ByTimeOfDay:0:Agent"] = "claude",
            ["CodeyBox:AgentScoreModifiers:ByTimeOfDay:0:Modifier"] = "-6",
            ["CodeyBox:AgentScoreModifiers:ByTimeOfDay:0:Windows:0:Days:0"] = "Mon",
            ["CodeyBox:AgentScoreModifiers:ByTimeOfDay:0:Windows:0:StartUtc"] = "14:00",
            ["CodeyBox:AgentScoreModifiers:ByTimeOfDay:0:Windows:0:EndUtc"] = "22:00",
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => factory.Services.GetRequiredService<AgentClassRouter>());

        Assert.Contains("absolute value must be ≤ 5", ex.Message);
    }
}

/// <summary>
/// Lightweight WebApplicationFactory that starts the app with injected
/// in-memory config. All hosted services are removed so only the DI
/// container resolution (and startup validation within factory lambdas)
/// is exercised.
/// </summary>
internal sealed class ValidationTestFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _extraConfig;

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"codeybox-val-{Guid.NewGuid():N}.db");

    public ValidationTestFactory(Dictionary<string, string?> extraConfig)
    {
        _extraConfig = extraConfig;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var tmp = Path.GetTempPath();
            var baseConfig = new Dictionary<string, string?>
            {
                ["CodeyBox:DangerouslyDisableAuth"] = "true",
                ["CodeyBox:StateDatabasePath"] = _dbPath,
                ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
            };
            foreach (var kvp in _extraConfig) baseConfig[kvp.Key] = kvp.Value;
            cfg.AddInMemoryCollection(baseConfig);
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { File.Delete(_dbPath); } catch { /* best-effort */ }
        }
        base.Dispose(disposing);
    }
}
