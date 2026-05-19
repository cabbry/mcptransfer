using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MCPTransfer.Core.Ipfs;

/// <summary>
/// <see cref="IIpfsClient"/> backed by Pinata Cloud (https://pinata.cloud).
/// </summary>
/// <remarks>
/// <para>
/// Pin operations hit the authenticated API
/// (<c>POST https://api.pinata.cloud/pinning/pinFileToIPFS</c>) via JWT bearer.
/// Fetch operations go through an IPFS gateway (public
/// <c>gateway.pinata.cloud</c> by default, configurable for dedicated
/// gateways with their own token).
/// </para>
/// <para>
/// HTTP status codes are mapped to .NET exception types so that the
/// <see cref="RetryPolicy.DefaultShouldRetry"/> predicate makes the right
/// call out of the box:
/// </para>
/// <list type="bullet">
/// <item><description>200/201 → success.</description></item>
/// <item><description>401/403 → <see cref="UnauthorizedAccessException"/> (permanent, not retried).</description></item>
/// <item><description>404 → <see cref="KeyNotFoundException"/> (permanent for fetch).</description></item>
/// <item><description>Everything else → <see cref="HttpRequestException"/> (retried by default).</description></item>
/// </list>
/// <para>
/// For production use, wrap with <see cref="RetryingIpfsClient"/> to absorb
/// transient 429 / 5xx / network errors.
/// </para>
/// </remarks>
public sealed class PinataIpfsClient : IIpfsClient, IDisposable
{
    public const string DefaultApiBaseUrl = "https://api.pinata.cloud";
    public const string DefaultGatewayUrl = "https://gateway.pinata.cloud/ipfs";

    private readonly HttpClient _httpClient;
    private readonly string _jwt;
    private readonly string _apiBaseUrl;
    private readonly string _gatewayUrl;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Construct a client against the standard Pinata endpoints.
    /// </summary>
    /// <param name="jwt">A Pinata API JWT bearer token. Generate one at
    /// <c>https://app.pinata.cloud/developers/api-keys</c>. The JWT is
    /// stored only in this instance; do not commit it to source.</param>
    /// <param name="httpClient">Optional injected <see cref="HttpClient"/>
    /// (useful for DI and unit tests). When null, a private one is created
    /// with a 2-minute timeout and disposed with this instance.</param>
    /// <param name="gatewayUrl">Optional override for the IPFS gateway used
    /// by <see cref="FetchAsync"/>. Defaults to the public Pinata gateway.
    /// Trailing slashes are stripped.</param>
    /// <param name="apiBaseUrl">Optional override for the Pinata API base.</param>
    public PinataIpfsClient(
        string jwt,
        HttpClient? httpClient = null,
        string? gatewayUrl = null,
        string? apiBaseUrl = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(jwt);
        _jwt = jwt;
        _gatewayUrl = (gatewayUrl ?? DefaultGatewayUrl).TrimEnd('/');
        _apiBaseUrl = (apiBaseUrl ?? DefaultApiBaseUrl).TrimEnd('/');

        if (httpClient is null)
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
    }

    public async Task<string> PinAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        using var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(data.ToArray());
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        multipart.Add(fileContent, name: "file", fileName: "chunk.bin");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_apiBaseUrl}/pinning/pinFileToIPFS")
        {
            Content = multipart,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwt);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowIfErrorAsync(response, "pin", cancellationToken).ConfigureAwait(false);

        await using var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(bodyStream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("IpfsHash", out var hashElement))
        {
            throw new InvalidOperationException(
                "Pinata pin response did not include the expected 'IpfsHash' field.");
        }
        return hashElement.GetString()
            ?? throw new InvalidOperationException("Pinata 'IpfsHash' was null.");
    }

    public async Task<byte[]> FetchAsync(string cid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(cid);

        var url = $"{_gatewayUrl}/{Uri.EscapeDataString(cid)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowIfErrorAsync(response, "fetch", cancellationToken).ConfigureAwait(false);

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ThrowIfErrorAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message =
            $"Pinata {operation} failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. "
            + $"Body: {Truncate(body, 200)}";

        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => new KeyNotFoundException(message),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                => new UnauthorizedAccessException(message),
            _ => new HttpRequestException(message, inner: null, statusCode: response.StatusCode),
        };
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
