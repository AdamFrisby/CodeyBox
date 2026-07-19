using System.Diagnostics;
using CodeyBox.Git;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="HostGitIdentityReader"/>. Each test writes a synthetic
/// .gitconfig under a temporary $HOME directory so we never touch the operator's
/// real global git config.
/// </summary>
[Collection("Host git identity")]
public sealed class HostGitIdentityReaderTests : IDisposable
{
    private readonly string _home;
    public HostGitIdentityReaderTests() => _home = Directory.CreateTempSubdirectory("codeybox-git-home-").FullName;
    public void Dispose() { CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_home); }

    private void WriteGitConfig(string content)
        => File.WriteAllText(Path.Combine(_home, ".gitconfig"), content);

    [Fact]
    public async Task Read_WithNameAndEmail_ReturnsBoth()
    {
        WriteGitConfig("[user]\n\tname = Alice Operator\n\temail = alice@example.com\n");

        var result = HostGitIdentityReader.Read(homeDir: _home);

        Assert.NotNull(result);
        Assert.Equal("Alice Operator", result.Name);
        Assert.Equal("alice@example.com", result.Email);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Read_EmptyGitConfig_ReturnsNull()
    {
        WriteGitConfig("");

        var result = HostGitIdentityReader.Read(homeDir: _home);

        Assert.Null(result);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Read_MissingEmailOnly_ReturnsNull()
    {
        WriteGitConfig("[user]\n\tname = Bob\n");

        var result = HostGitIdentityReader.Read(homeDir: _home);

        Assert.Null(result);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Read_MissingNameOnly_ReturnsNull()
    {
        WriteGitConfig("[user]\n\temail = bob@example.com\n");

        var result = HostGitIdentityReader.Read(homeDir: _home);

        Assert.Null(result);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Read_MissingGitconfigFile_ReturnsNull()
    {
        // No .gitconfig written — home dir is empty.
        var result = HostGitIdentityReader.Read(homeDir: _home);

        Assert.Null(result);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Read_UnicodeNameAndEmail_RoundTrips()
    {
        const string name = "Ångström Ünïcödé 日本語";
        const string email = "unicode@example.org";
        WriteGitConfig($"[user]\n\tname = {name}\n\temail = {email}\n");

        var result = HostGitIdentityReader.Read(homeDir: _home);

        Assert.NotNull(result);
        Assert.Equal(name, result.Name);
        Assert.Equal(email, result.Email);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Read_NameWithSpaces_ReturnsTrimmed()
    {
        WriteGitConfig("[user]\n\tname =   Padded Name   \n\temail = padded@example.com\n");

        var result = HostGitIdentityReader.Read(homeDir: _home);

        Assert.NotNull(result);
        Assert.Equal("Padded Name", result.Name);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Read_ReturnsNullNotThrows_WhenGitConfigHasNoUserSection()
    {
        WriteGitConfig("[core]\n\tautocrlf = false\n");

        var result = HostGitIdentityReader.Read(homeDir: _home);

        Assert.Null(result);
        await Task.CompletedTask;
    }
}
