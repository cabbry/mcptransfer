using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using MCPTransfer.Core.Ipfs;
using Xunit.Abstractions;

namespace MCPTransfer.Tests.Ipfs;

public class PinataIpfsClientTests
{
    private const string TestJwt = "test.jwt.token";

    private readonly ITestOutputHelper _output;

    public PinataIpfsClientTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task PinAsync_OnSuccess_ReturnsIpfsHashAndSetsAuthHeader()
    {
        var capturedAuthHeader = "";
        var capturedHttpMethod = "";
        var capturedRequestUri = "";

        var handler = new MockHttpMessageHandler((request, _) =>
        {
            capturedAuthHeader = request.Headers.Authorization?.ToString() ?? "";
            capturedHttpMethod = request.Method.Method;
            capturedRequestUri = request.RequestUri?.ToString() ?? "";
            return Respond(HttpStatusCode.OK, """{"IpfsHash":"QmTest123","PinSize":42}""");
        });

        using var http = new HttpClient(handler);
        using var client = new PinataIpfsClient(TestJwt, http);

        var cid = await client.PinAsync(new byte[] { 1, 2, 3 });

        Assert.Equal("QmTest123", cid);
        Assert.Equal("Bearer test.jwt.token", capturedAuthHeader);
        Assert.Equal("POST", capturedHttpMethod);
        Assert.EndsWith("/pinning/pinFileToIPFS", capturedRequestUri);
    }

    [Fact]
    public async Task PinAsync_OnUnauthorized_ThrowsUnauthorizedAccessException()
    {
        var handler = new MockHttpMessageHandler(
            (_, _) => Respond(HttpStatusCode.Unauthorized, """{"error":"invalid jwt"}"""));

        using var http = new HttpClient(handler);
        using var client = new PinataIpfsClient(TestJwt, http);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => client.PinAsync(new byte[] { 1 }));
    }

    [Fact]
    public async Task PinAsync_OnForbidden_ThrowsUnauthorizedAccessException()
    {
        var handler = new MockHttpMessageHandler(
            (_, _) => Respond(HttpStatusCode.Forbidden, "forbidden"));

        using var http = new HttpClient(handler);
        using var client = new PinataIpfsClient(TestJwt, http);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => client.PinAsync(new byte[] { 1 }));
    }

    [Fact]
    public async Task PinAsync_OnRateLimit_ThrowsHttpRequestException()
    {
        var handler = new MockHttpMessageHandler(
            (_, _) => Respond((HttpStatusCode)429, """{"error":"rate limited"}"""));

        using var http = new HttpClient(handler);
        using var client = new PinataIpfsClient(TestJwt, http);

        // 429 is a transient failure -> HttpRequestException, which is retryable by RetryPolicy.
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PinAsync(new byte[] { 1 }));
        Assert.Equal((HttpStatusCode)429, ex.StatusCode);
    }

    [Fact]
    public async Task PinAsync_OnServerError_ThrowsHttpRequestException()
    {
        var handler = new MockHttpMessageHandler(
            (_, _) => Respond(HttpStatusCode.InternalServerError, "boom"));

        using var http = new HttpClient(handler);
        using var client = new PinataIpfsClient(TestJwt, http);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PinAsync(new byte[] { 1 }));
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task PinAsync_OnInvalidJson_ThrowsInvalidOperationException()
    {
        var handler = new MockHttpMessageHandler(
            (_, _) => Respond(HttpStatusCode.OK, """{"NotIpfsHash":"oops"}"""));

        using var http = new HttpClient(handler);
        using var client = new PinataIpfsClient(TestJwt, http);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PinAsync(new byte[] { 1 }));
    }

    [Fact]
    public async Task FetchAsync_OnSuccess_ReturnsBytes()
    {
        var expected = new byte[] { 9, 8, 7, 6, 5 };
        var capturedUri = "";

        var handler = new MockHttpMessageHandler((request, _) =>
        {
            capturedUri = request.RequestUri?.ToString() ?? "";
            return Respond(HttpStatusCode.OK, expected);
        });

        using var http = new HttpClient(handler);
        using var client = new PinataIpfsClient(TestJwt, http);

        var bytes = await client.FetchAsync("QmTest");

        Assert.Equal(expected, bytes);
        Assert.EndsWith("/ipfs/QmTest", capturedUri);
    }

    [Fact]
    public async Task FetchAsync_OnNotFound_ThrowsKeyNotFoundException()
    {
        var handler = new MockHttpMessageHandler(
            (_, _) => Respond(HttpStatusCode.NotFound, "not pinned"));

        using var http = new HttpClient(handler);
        using var client = new PinataIpfsClient(TestJwt, http);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => client.FetchAsync("QmMissing"));
    }

    [Fact]
    public async Task FetchAsync_UsesCustomGatewayUrl()
    {
        var capturedUri = "";

        var handler = new MockHttpMessageHandler((request, _) =>
        {
            capturedUri = request.RequestUri?.ToString() ?? "";
            return Respond(HttpStatusCode.OK, new byte[] { 1 });
        });

        using var http = new HttpClient(handler);
        using var client = new PinataIpfsClient(
            TestJwt,
            http,
            gatewayUrl: "https://my-dedicated.mypinata.cloud/ipfs");

        await client.FetchAsync("QmTest");

        Assert.StartsWith("https://my-dedicated.mypinata.cloud/ipfs/", capturedUri);
    }

    [Fact]
    public void Constructor_RejectsEmptyJwt()
    {
        // ArgumentException.ThrowIfNullOrEmpty throws ArgumentException for empty
        // and ArgumentNullException (a subclass) for null -> ThrowsAny accepts both.
        Assert.ThrowsAny<ArgumentException>(() => new PinataIpfsClient(""));
        Assert.ThrowsAny<ArgumentException>(() => new PinataIpfsClient(null!));
    }

    [Fact]
    public void RetryPolicy_DefaultShouldRetry_TreatsUnauthorizedAsPermanent()
    {
        Assert.False(RetryPolicy.DefaultShouldRetry(new UnauthorizedAccessException("forbidden")));
        // And HttpRequestException for 5xx / 429 IS retryable by default.
        Assert.True(RetryPolicy.DefaultShouldRetry(new HttpRequestException("transient")));
    }

    // -------- Integration test against real Pinata (skipped without env var) --------

    [Fact]
    public async Task IntegrationTest_RealPinata_RoundTrip()
    {
        var jwt = Environment.GetEnvironmentVariable("PINATA_JWT");
        if (string.IsNullOrWhiteSpace(jwt))
        {
            _output.WriteLine("Skipping: PINATA_JWT environment variable not set.");
            return;
        }

        using var client = new PinataIpfsClient(jwt);
        var randomData = RandomNumberGenerator.GetBytes(512);

        var cid = await client.PinAsync(randomData);
        _output.WriteLine($"Pinned to CID: {cid}");
        Assert.False(string.IsNullOrEmpty(cid));

        // Public gateway can lag a few seconds behind pinning; retry-fetch a bit.
        byte[]? fetched = null;
        for (var attempt = 0; attempt < 10 && fetched is null; attempt++)
        {
            try
            {
                fetched = await client.FetchAsync(cid);
            }
            catch (KeyNotFoundException) when (attempt < 9)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        Assert.NotNull(fetched);
        Assert.Equal(randomData, fetched);
    }

    // -------- helpers --------

    private static HttpResponseMessage Respond(HttpStatusCode status, string body)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return response;
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new HttpResponseMessage(status) { Content = content };
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _responder;

        public MockHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request, cancellationToken));
        }
    }
}
