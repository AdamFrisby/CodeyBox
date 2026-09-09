using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// HTTP <see cref="IJobTrackTestCaseClient"/>: POSTs a single
/// <see cref="JobTrackTestCaseImport"/> as camelCase JSON to JobTrack's import
/// endpoint. Idempotency is JobTrack's contract (it upserts on
/// <see cref="JobTrackTestCaseImport.ExternalSourceId"/>); this client re-sends
/// the same payload verbatim on each export.
///
/// <para>The bearer token is added per-request so the shared, factory-created
/// <see cref="HttpClient"/> is never mutated, and its value never appears on a
/// URL or in config. A non-success status throws so the exporter's retry policy
/// applies.</para>
/// </summary>
public sealed class HttpJobTrackTestCaseClient : IJobTrackTestCaseClient
{
    /// <summary>Named-client key registered with <c>IHttpClientFactory</c>.</summary>
    public const string HttpClientName = "jobtrack-export";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;

    public HttpJobTrackTestCaseClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    public async Task UpsertAsync(
        JobTrackExportEndpoint endpoint, JobTrackTestCaseImport import, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(import);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.ImportUri)
        {
            Content = JsonContent.Create(import, options: SerializerOptions),
        };
        if (!string.IsNullOrEmpty(endpoint.Token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
