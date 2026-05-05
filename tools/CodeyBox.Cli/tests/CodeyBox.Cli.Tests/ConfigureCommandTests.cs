using System.Text.Json;
using CodeyBox.Cli.Tests.Helpers;

namespace CodeyBox.Cli.Tests;

[Collection("cli-sequential")]
public sealed class ConfigureCommandTests
{
    [Fact]
    public async Task Configure_WritesConfigFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);
        var prevIn = Console.In;
        Console.SetIn(new StringReader("http://localhost:9999\nsecretkey123\n"));

        using var output = new TestOutput();
        try
        {
            var code = await CliApp.InvokeAsync(["configure"]);

            Assert.Equal(0, code);

            var configPath = Path.Combine(tempDir, "config.json");
            Assert.True(File.Exists(configPath), $"Config file not found at {configPath}");

            var json = await File.ReadAllTextAsync(configPath);
            var config = JsonSerializer.Deserialize(json, CliJsonContext.Default.CliConfig);
            Assert.Equal("http://localhost:9999", config!.ApiBaseUrl);
            Assert.Equal("secretkey123", config.ApiKey);
        }
        finally
        {
            Console.SetIn(prevIn);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", null);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Configure_SubsequentCommands_ReadSavedConfig()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", tempDir);

        string? capturedKey = null;
        Func<ResolvedConfig, CodeyBoxClient> factory = config =>
        {
            capturedKey = config.ApiKey;
            return new CodeyBoxClient(
                new HttpClient(new FakeHttpMessageHandler(_ => SampleData.WorkItemListResponse([])))
                { BaseAddress = new Uri(config.ApiBaseUrl) });
        };

        var prevIn = Console.In;
        Console.SetIn(new StringReader("http://localhost:7777\nmysavedkey\n"));
        using var output = new TestOutput();
        try
        {
            await CliApp.InvokeAsync(["configure"]);

            output.Out.GetStringBuilder().Clear();
            await CliApp.InvokeAsync(["queue", "ls"], factory);

            Assert.Equal("mysavedkey", capturedKey);
        }
        finally
        {
            Console.SetIn(prevIn);
            Environment.SetEnvironmentVariable("CODEYBOX_CLI_CONFIG_DIR", null);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
