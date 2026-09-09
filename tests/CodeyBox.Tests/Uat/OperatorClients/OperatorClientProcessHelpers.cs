using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeyBox.Tests.Uat.OperatorClients;

internal static class OperatorClientPaths
{
    internal static string RepoRoot { get; } = FindRepoRoot();

    internal static string CliProject =>
        Path.Combine(RepoRoot, "tools", "CodeyBox.Cli", "CodeyBox.Cli.csproj");

    /// <summary>
    /// Pre-built CLI dll produced by the CodeyBox.Tests project's build-only
    /// reference to CodeyBox.Cli. Executing this directly with `dotnet path/to/dll`
    /// avoids the per-invocation `dotnet run` rebuild that can exceed the test
    /// timeout under cold sandbox caches.
    /// </summary>
    internal static string CliDll =>
        Path.Combine(RepoRoot, "tools", "CodeyBox.Cli", "bin",
            BuildConfiguration, "net10.0", "CodeyBox.Cli.dll");

#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodeyBox.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}

internal static partial class OperatorClientProcess
{
    // Shared per-test-process to avoid paying dotnet's cold-start cost (NuGet
    // restore, MSBuild SDK resolution) on every CLI invocation; only the
    // codeybox-side cliConfig directory needs per-call isolation.
    private static readonly Lazy<string> SharedDotnetCliHome = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "codeybox-dotnet-home-shared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    });

    internal static Task<ProcessResult> RunCodeyBoxCliAsync(
        IReadOnlyList<string> args,
        IDictionary<string, string?>? environment = null,
        string? stdin = null)
    {
        // Execute the already-built dll directly. The CLI is pulled in as a
        // build-only ProjectReference in CodeyBox.Tests.csproj, so the dll is
        // guaranteed to exist whenever the test assembly is built.
        var dotnetArgs = new List<string> { OperatorClientPaths.CliDll };
        dotnetArgs.AddRange(args);
        return RunDotnetAsync(dotnetArgs, environment, stdin);
    }

    private static async Task<ProcessResult> RunDotnetAsync(
        IReadOnlyList<string> args,
        IDictionary<string, string?>? environment,
        string? stdin)
    {
        var home = SharedDotnetCliHome.Value;
        var cliConfig = Path.Combine(Path.GetTempPath(), "codeybox-cli-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cliConfig);

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = OperatorClientPaths.RepoRoot,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            startInfo.Environment["DOTNET_CLI_HOME"] = home;
            startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            startInfo.Environment["DOTNET_NOLOGO"] = "1";
            startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
            startInfo.Environment["CODEYBOX_CLI_CONFIG_DIR"] = cliConfig;
            startInfo.Environment["CODEYBOX_CLI_API_KEY"] = "";
            startInfo.Environment["CODEYBOX_CLI_API_URL"] = "";

            if (environment is not null)
            {
                foreach (var (key, value) in environment)
                    startInfo.Environment[key] = value ?? "";
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start dotnet process.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            if (stdin is not null)
                await process.StandardInput.WriteAsync(stdin.AsMemory(), timeout.Token);
            process.StandardInput.Close();

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Process already exited.
                }

                throw new TimeoutException($"dotnet {string.Join(' ', args)} did not exit within the test timeout.");
            }

            return new ProcessResult(
                process.ExitCode,
                await stdoutTask,
                await stderrTask);
        }
        finally
        {
            TryDeleteDirectory(cliConfig);
        }
    }

    internal static string ReadCliVersionFromSource()
    {
        var path = Path.Combine(OperatorClientPaths.RepoRoot, "tools", "CodeyBox.Cli", "CliApp.cs");
        var source = File.ReadAllText(path);
        var match = CliVersionRegex().Match(source);
        if (!match.Success)
            throw new InvalidOperationException("Could not find CliApp.CliVersion in source.");
        return match.Groups["version"].Value;
    }

    [GeneratedRegex("CliVersion\\s*=\\s*\"(?<version>[^\"]+)\"")]
    private static partial Regex CliVersionRegex();

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

internal sealed class FakeOperatorApiServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<Func<FakeOperatorApiRequest, FakeOperatorApiResponse>> _responders = new();
    private readonly List<Task> _connectionTasks = [];
    private readonly Task _acceptLoop;

    internal FakeOperatorApiServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"http://127.0.0.1:{port}";
        _acceptLoop = AcceptLoopAsync();
    }

    internal string BaseUrl { get; }
    internal ConcurrentQueue<FakeOperatorApiRequest> Requests { get; } = new();

    internal void EnqueueResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _responders.Enqueue(_ => new FakeOperatorApiResponse((int)statusCode, json, "application/json"));
    }

    internal void EnqueueResponse(Func<FakeOperatorApiRequest, FakeOperatorApiResponse> responder)
    {
        _responders.Enqueue(responder);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                var task = HandleClientAsync(client);
                lock (_connectionTasks)
                    _connectionTasks.Add(task);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var request = await ReadRequestAsync(stream, _cts.Token);
        Requests.Enqueue(request);

        var response = _responders.TryDequeue(out var responder)
            ? responder(request)
            : new FakeOperatorApiResponse(500, """{"error":"no fake response queued"}""", "application/json");

        var body = Encoding.UTF8.GetBytes(response.Body);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {response.StatusCode} {ReasonPhrase(response.StatusCode)}\r\n" +
            $"Content-Type: {response.ContentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n" +
            "\r\n");
        await stream.WriteAsync(header, _cts.Token);
        await stream.WriteAsync(body, _cts.Token);
    }

    private static async Task<FakeOperatorApiRequest> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var headerBytes = new List<byte>();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
                throw new IOException("Client closed the connection before sending headers.");
            headerBytes.Add(buffer[0]);
            var count = headerBytes.Count;
            if (count >= 4 &&
                headerBytes[count - 4] == '\r' &&
                headerBytes[count - 3] == '\n' &&
                headerBytes[count - 2] == '\r' &&
                headerBytes[count - 1] == '\n')
                break;
        }

        var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', 3);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
                headers[line[..colon]] = line[(colon + 1)..].Trim();
        }

        byte[] bodyBytes;
        if (headers.TryGetValue("Transfer-Encoding", out var transferEncoding) &&
            transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            bodyBytes = await ReadChunkedBodyAsync(stream, ct);
        }
        else
        {
            var contentLength = headers.TryGetValue("Content-Length", out var lengthText) &&
                int.TryParse(lengthText, out var length)
                ? length
                : 0;
            bodyBytes = new byte[contentLength];
            var offset = 0;
            while (offset < contentLength)
            {
                var read = await stream.ReadAsync(bodyBytes.AsMemory(offset, contentLength - offset), ct);
                if (read == 0)
                    throw new IOException("Client closed the connection before sending the full request body.");
                offset += read;
            }
        }

        return new FakeOperatorApiRequest(
            requestLine[0],
            requestLine[1],
            headers,
            Encoding.UTF8.GetString(bodyBytes));
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(NetworkStream stream, CancellationToken ct)
    {
        using var body = new MemoryStream();
        while (true)
        {
            var sizeLine = await ReadAsciiLineAsync(stream, ct);
            var semicolon = sizeLine.IndexOf(';', StringComparison.Ordinal);
            var sizeText = semicolon >= 0 ? sizeLine[..semicolon] : sizeLine;
            var size = Convert.ToInt32(sizeText.Trim(), 16);
            if (size == 0)
            {
                while (!string.IsNullOrEmpty(await ReadAsciiLineAsync(stream, ct)))
                {
                }

                break;
            }

            var chunk = new byte[size];
            var offset = 0;
            while (offset < size)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(offset, size - offset), ct);
                if (read == 0)
                    throw new IOException("Client closed the connection before sending the full chunk.");
                offset += read;
            }

            body.Write(chunk);
            var crlf = new byte[2];
            var crlfOffset = 0;
            while (crlfOffset < crlf.Length)
            {
                var read = await stream.ReadAsync(crlf.AsMemory(crlfOffset, crlf.Length - crlfOffset), ct);
                if (read == 0)
                    throw new IOException("Client closed the connection before sending chunk terminator.");
                crlfOffset += read;
            }
        }

        return body.ToArray();
    }

    private static async Task<string> ReadAsciiLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
                throw new IOException("Client closed the connection before sending a complete line.");
            if (buffer[0] == '\n')
                break;
            bytes.Add(buffer[0]);
        }

        if (bytes.Count > 0 && bytes[^1] == '\r')
            bytes.RemoveAt(bytes.Count - 1);
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK",
        201 => "Created",
        202 => "Accepted",
        204 => "No Content",
        401 => "Unauthorized",
        404 => "Not Found",
        500 => "Internal Server Error",
        _ => "Status",
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (ObjectDisposedException)
        {
        }

        Task[] tasks;
        lock (_connectionTasks)
            tasks = [.. _connectionTasks];
        await Task.WhenAll(tasks);
        _cts.Dispose();
    }
}

internal sealed record FakeOperatorApiRequest(
    string Method,
    string Target,
    IReadOnlyDictionary<string, string> Headers,
    string Body);

internal sealed record FakeOperatorApiResponse(int StatusCode, string Body, string ContentType);
