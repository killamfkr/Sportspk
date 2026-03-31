using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.StreamedPk.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamedPk;

/// <summary>
/// Best-effort conversion of an embed/page URL into a direct HLS/DASH/file URL for Jellyfin's player.
/// Many sports embeds load streams only from JavaScript; those cannot be resolved here.
/// </summary>
public sealed class EmbedPageStreamResolver
{
    private const int MaxHtmlScanBytes = 1_048_576;

    private static readonly Regex UrlExplicitManifestRegex = new(
        @"https?://[^\s""'<>]+\.(?:m3u8|mpd)(?:\?[^\s""'<>]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    /// <summary>Playlist URL where "m3u8" appears in the query or path fragment.</summary>
    private static readonly Regex UrlLooseM3u8Regex = new(
        @"https?://[^\s""'<>]+[?&][^\s""'<>]*m3u8[^\s""'<>]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex ProtocolRelativeRegex = new(
        @"//[^\s""'<>]+\.(?:m3u8|mpd)(?:\?[^\s""'<>]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EmbedPageStreamResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbedPageStreamResolver"/> class.
    /// </summary>
    public EmbedPageStreamResolver(IHttpClientFactory httpClientFactory, ILogger<EmbedPageStreamResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns a direct media URL when possible; otherwise null (caller may fall back to the original embed URL).
    /// </summary>
    public async Task<string?> TryResolveDirectStreamUrlAsync(string candidateUrl, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration ?? new StreamedPkPluginConfiguration();
        if (!cfg.TryResolveEmbedToDirectStream)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(candidateUrl))
        {
            return null;
        }

        if (LooksLikeDirectMediaUrl(candidateUrl))
        {
            return candidateUrl.Trim();
        }

        Uri uri;
        try
        {
            uri = new Uri(candidateUrl, UriKind.Absolute);
        }
        catch (UriFormatException)
        {
            return null;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var client = _httpClientFactory.CreateClient(StreamedPkHttpClients.EmbedResolver);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        try
        {
            using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently
                or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect)
            {
                var loc = response.Headers.Location;
                if (loc is not null)
                {
                    var absolute = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                    if (LooksLikeDirectMediaUrl(absolute.ToString()))
                    {
                        return absolute.ToString();
                    }
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            if (LooksLikeDirectMediaUrl(finalUri.ToString()))
            {
                return finalUri.ToString();
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (IsDirectStreamContentType(contentType))
            {
                return finalUri.ToString();
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[MaxHtmlScanBytes];
                var read = await stream.ReadAsync(buffer.AsMemory(0, MaxHtmlScanBytes), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                var span = buffer.AsSpan(0, read);
                if (span.StartsWith("#EXTM3U"u8))
                {
                    return finalUri.ToString();
                }

                var text = Encoding.UTF8.GetString(span);
                if (contentType is null or "text/html" or "application/xhtml+xml" or "application/javascript" or "text/javascript"
                    or "application/json" or "text/plain")
                {
                    var found = FindFirstStreamUrl(text, finalUri);
                    if (found is not null)
                    {
                        return found;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Embed resolver: failed to read body for {Url}", candidateUrl);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Embed resolver: request failed for {Url}", candidateUrl);
            return null;
        }
    }

    private static bool LooksLikeDirectMediaUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return false;
        }

        var path = u.AbsolutePath;
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var q = u.Query;
        return q.Contains("m3u8", StringComparison.OrdinalIgnoreCase)
               || q.Contains("format=m3u8", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDirectStreamContentType(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
        {
            return false;
        }

        return mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("dash", StringComparison.OrdinalIgnoreCase)
               || mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
               || mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindFirstStreamUrl(string htmlOrJs, Uri pageUri)
    {
        foreach (Match m in UrlExplicitManifestRegex.Matches(htmlOrJs))
        {
            if (m.Success && TryNormalizeUrl(m.Value, pageUri, out var abs))
            {
                return abs;
            }
        }

        foreach (Match m in UrlLooseM3u8Regex.Matches(htmlOrJs))
        {
            if (m.Success && TryNormalizeUrl(m.Value, pageUri, out var abs))
            {
                return abs;
            }
        }

        foreach (Match m in ProtocolRelativeRegex.Matches(htmlOrJs))
        {
            if (!m.Success)
            {
                continue;
            }

            var withScheme = "https:" + m.Value;
            if (TryNormalizeUrl(withScheme, pageUri, out var abs))
            {
                return abs;
            }
        }

        // Relative path like /path/playlist.m3u8?token=x
        var rel = Regex.Matches(
            htmlOrJs,
            @"(?<=[""'])(\/[^""'\s<>]+\.(?:m3u8|mpd)(?:\?[^""'\s<>]*)?)(?=[""'])",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));
        foreach (Match m in rel)
        {
            if (m.Success && TryNormalizeUrl(m.Value, pageUri, out var abs))
            {
                return abs;
            }
        }

        return null;
    }

    private static bool TryNormalizeUrl(string raw, Uri baseUri, out string absolute)
    {
        absolute = string.Empty;
        var trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absUri)
            && absUri.Scheme is ("http" or "https"))
        {
            absolute = absUri.ToString();
            return true;
        }

        if (Uri.TryCreate(baseUri, trimmed, out var relative))
        {
            absolute = relative.ToString();
            return true;
        }

        return false;
    }
}
