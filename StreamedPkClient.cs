using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamedPk;

/// <summary>
/// HTTP client for https://streamed.pk REST API.
/// </summary>
public sealed class StreamedPkClient
{

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StreamedPkClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamedPkClient"/> class.
    /// </summary>
    public StreamedPkClient(IHttpClientFactory httpClientFactory, ILogger<StreamedPkClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets configured API base URL with trailing slash removed.
    /// </summary>
    public string GetBaseUrl()
    {
        var raw = Plugin.Instance?.Configuration.ApiBaseUrl?.Trim() ?? "https://streamed.pk";
        return raw.TrimEnd('/');
    }

    /// <summary>
    /// Fetches sports categories.
    /// </summary>
    public async Task<IReadOnlyList<ApiSport>> GetSportsAsync(CancellationToken cancellationToken)
    {
        var data = await GetJsonAsync<ApiSport[]>("/api/sports", cancellationToken).ConfigureAwait(false);
        return data ?? Array.Empty<ApiSport>();
    }

    /// <summary>
    /// Fetches live matches (PlayTorrio "Live" mode).
    /// </summary>
    public async Task<IReadOnlyList<ApiMatch>> GetLiveMatchesAsync(CancellationToken cancellationToken)
    {
        var data = await GetJsonAsync<ApiMatch[]>("/api/matches/live", cancellationToken).ConfigureAwait(false);
        return data ?? Array.Empty<ApiMatch>();
    }

    /// <summary>
    /// Fetches matches scheduled for today (PlayTorrio "Today" mode).
    /// </summary>
    public async Task<IReadOnlyList<ApiMatch>> GetMatchesAllTodayAsync(CancellationToken cancellationToken)
    {
        var data = await GetJsonAsync<ApiMatch[]>("/api/matches/all-today", cancellationToken).ConfigureAwait(false);
        return data ?? Array.Empty<ApiMatch>();
    }

    /// <summary>
    /// Fetches all listed matches (PlayTorrio "All" mode).
    /// </summary>
    public async Task<IReadOnlyList<ApiMatch>> GetMatchesAllListAsync(CancellationToken cancellationToken)
    {
        var data = await GetJsonAsync<ApiMatch[]>("/api/matches/all", cancellationToken).ConfigureAwait(false);
        return data ?? Array.Empty<ApiMatch>();
    }

    /// <summary>
    /// Fetches matches for a sport id (e.g. football).
    /// </summary>
    public async Task<IReadOnlyList<ApiMatch>> GetMatchesBySportAsync(string sportId, CancellationToken cancellationToken)
    {
        var path = "/api/matches/" + Uri.EscapeDataString(sportId);
        var data = await GetJsonAsync<ApiMatch[]>(path, cancellationToken).ConfigureAwait(false);
        return data ?? Array.Empty<ApiMatch>();
    }

    /// <summary>
    /// Fetches stream rows for a source and event id.
    /// </summary>
    public async Task<IReadOnlyList<ApiStream>> GetStreamsAsync(string source, string eventId, CancellationToken cancellationToken)
    {
        var path = "/api/stream/" + Uri.EscapeDataString(source) + "/" + Uri.EscapeDataString(eventId);
        var data = await GetJsonAsync<ApiStream[]>(path, cancellationToken).ConfigureAwait(false);
        return data ?? Array.Empty<ApiStream>();
    }

    private async Task<T?> GetJsonAsync<T>(string relativePath, CancellationToken cancellationToken)
    {
        var baseUrl = GetBaseUrl();
        var client = _httpClientFactory.CreateClient(StreamedPkHttpClients.Api);
        var uri = baseUrl + relativePath;
        try
        {
            using var response = await client.GetAsync(new Uri(uri), cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Streamed.pk request failed: {Uri}", uri);
            return default;
        }
    }
}
