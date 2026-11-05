using System.Net.Http.Headers;
using System.Text;
using ReviewGate.Contracts;

namespace ReviewGate.Transport.Http;

// The only network edge of the product. Endpoints are fixed in code: nothing in a plan, profile or
// response can redirect a request or its key elsewhere. Keys are read per request and only placed in
// the outgoing header; they never enter a result, log or exception message.
public sealed class HttpTransport : IModelTransport, IDisposable
{
    public const string GoogleApiRevision = "2026-05-20";
    public const int MaxResponseBytes = 8 * 1024 * 1024;
    // Fixed endpoints come only from the provider catalog; nothing in a plan or response can change them.
    public static readonly IReadOnlyDictionary<string, Uri> Endpoints = ProviderCatalog.All.ToDictionary(p => p.Id, p => new Uri(p.Endpoint));
    readonly ICredentialStore credentials;
    readonly HttpClient client;
    public HttpTransport(ICredentialStore credentials, HttpMessageHandler? handler = null)
    {
        this.credentials = credentials;
        client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All }, handler is null)
        { Timeout = Timeout.InfiniteTimeSpan };
    }
    public bool HasCredential(string provider) => Endpoints.ContainsKey(provider) && credentials.Read(provider) is { Length: > 0 };
    public async Task<TransportResult> SendAsync(ReviewerProfile reviewer, string body, CancellationToken cancellation)
    {
        var endpoint = Endpoints.TryGetValue(reviewer.HostingProvider, out var uri) ? uri : throw new ReviewError(14, "unsupported_capability");
        string key = credentials.Read(reviewer.HostingProvider) is { Length: > 0 } stored ? stored : throw new ReviewError(11, "credential_missing");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(body, new UTF8Encoding(false), "application/json") };
        if (reviewer.HostingProvider == "google") { request.Headers.Add("x-goog-api-key", key); request.Headers.Add("Api-Revision", GoogleApiRevision); }
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(reviewer.TimeoutSeconds));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            int status = (int)response.StatusCode;
            if (status is >= 300 and < 400) return TransportResult.Unknown("unexpected_redirect");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes) return TransportResult.Unknown("response_too_large");
                buffer.Write(chunk, 0, read);
            }
            try { return new("received", status, Data.Utf8.GetString(buffer.ToArray()), null); }
            catch (DecoderFallbackException) { return new("received", status, "{}", "invalid_utf8_response"); }
        }
        // A timeout or broken connection after the request may have been sent leaves the outcome and the
        // charge unknown; the caller records it as unknown and never re-sends automatically.
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return TransportResult.Unknown("timeout"); }
        catch (HttpRequestException) { return TransportResult.Unknown("network_error"); }
        catch (IOException) { return TransportResult.Unknown("network_error"); }
    }
    public void Dispose() => client.Dispose();
}
