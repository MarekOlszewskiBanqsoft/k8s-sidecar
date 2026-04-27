using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace K8sSidecar;

/// <summary>
/// HTTP request helper with retry support.
/// Matches the Python sidecar's request() function behavior.
/// </summary>
public sealed class HttpRequester
{
    private readonly ILogger _logger = Logging.CreateLogger("http_requester");
    private readonly HttpClient _httpClient;
    private readonly SidecarConfig _config;
    private readonly ResiliencePipeline<HttpResponseMessage> _retryPipeline;

    public HttpRequester(SidecarConfig config)
    {
        _config = config;

        var handler = new HttpClientHandler();
        if (config.ReqSkipTlsVerify)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(config.ReqTimeout)
        };

        _retryPipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = config.ReqRetryTotal,
                Delay = TimeSpan.FromSeconds(config.ReqRetryBackoffFactor),
                BackoffType = DelayBackoffType.Exponential,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .HandleResult(r => !config.Enable5xx && (int)r.StatusCode >= 500)
            })
            .Build();
    }

    /// <summary>
    /// Send an HTTP request (GET or POST). Returns the response, or null on failure.
    /// </summary>
    public HttpResponseMessage? SendRequest(string? url, string? method, bool enable5xx = false, object? payload = null)
    {
        if (string.IsNullOrEmpty(url))
        {
            _logger.LogWarning("No url provided. Doing nothing.");
            return null;
        }

        try
        {
            return _retryPipeline.Execute(() =>
            {
                var request = new HttpRequestMessage();
                request.RequestUri = new Uri(url);

                // Basic auth
                var (username, password) = FetchBasicAuthCredentials();
                if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
                {
                    // Use ASCII encoding for basic auth credentials (supports ascii, latin1, utf-8 style)
                    var credBytes = Encoding.UTF8.GetBytes($"{username}:{password}");
                    var credentials = Convert.ToBase64String(credBytes);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                }

                if (method?.Equals("POST", StringComparison.OrdinalIgnoreCase) == true)
                {
                    request.Method = HttpMethod.Post;
                    if (payload != null)
                    {
                        var json = payload is string s ? s : JsonSerializer.Serialize(payload);
                        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    }
                    var response = _httpClient.Send(request);
                    var responseText = ReadResponseContent(response);
                    _logger.LogInformation("{Payload} sent to {Url}. Response: {StatusCode} {Reason} {Body}",
                        payload, url, (int)response.StatusCode, response.ReasonPhrase, responseText);
                    return response;
                }
                else if (method == null || method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    request.Method = HttpMethod.Get;
                    var response = _httpClient.Send(request);
                    var responseText = ReadResponseContent(response);
                    _logger.LogInformation("Request sent to {Url}. Response: {StatusCode} {Reason} {Body}",
                        url, (int)response.StatusCode, response.ReasonPhrase, responseText);
                    return response;
                }
                else
                {
                    _logger.LogWarning("Invalid REQ_METHOD: '{Method}', please use 'GET' or 'POST'. Doing nothing.", method);
                    return new HttpResponseMessage(HttpStatusCode.OK); // dummy
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Max retries exceeded for URL {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Read text content of an HTTP response.
    /// </summary>
    public string ReadResponseText(HttpResponseMessage? response)
    {
        if (response == null) return string.Empty;
        return ReadResponseContent(response);
    }

    /// <summary>
    /// Read byte content of an HTTP response.
    /// </summary>
    public byte[] ReadResponseBytes(HttpResponseMessage? response)
    {
        if (response == null) return Array.Empty<byte>();
        using var stream = response.Content.ReadAsStream();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static string ReadResponseContent(HttpResponseMessage response)
    {
        using var stream = response.Content.ReadAsStream();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private (string? username, string? password) FetchBasicAuthCredentials()
    {
        var username = _config.ReqUsername;
        var password = _config.ReqPassword;

        if (!string.IsNullOrEmpty(_config.ReqUsernameFile))
        {
            var fromFile = FileHelpers.ReadFileContent(_config.ReqUsernameFile);
            if (fromFile != null) username = fromFile;
        }

        if (!string.IsNullOrEmpty(_config.ReqPasswordFile))
        {
            var fromFile = FileHelpers.ReadFileContent(_config.ReqPasswordFile);
            if (fromFile != null) password = fromFile;
        }

        return (username, password);
    }
}
