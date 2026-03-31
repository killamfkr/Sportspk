using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamedPk;

/// <summary>
/// Fetches PPV.to and CDN Live feeds used by PlayTorrio-style navigation.
/// </summary>
public sealed class PlayTorrioFeedsClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PlayTorrioFeedsClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayTorrioFeedsClient"/> class.
    /// </summary>
    public PlayTorrioFeedsClient(IHttpClientFactory httpClientFactory, ILogger<PlayTorrioFeedsClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Stable short key for a URL (for Jellyfin folder / media ids).
    /// </summary>
    public static string StableUrlKey(string url)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>
    /// Loads flattened PPV streams (iframe required for playback).
    /// </summary>
    public async Task<IReadOnlyList<PpvStreamRow>> GetPpvStreamsAsync(string requestUrl, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(StreamedPkHttpClients.Api);
            using var resp = await client.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParsePpvStreams(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PPV.to: failed to load {Url}", requestUrl);
            return [];
        }
    }

    /// <summary>
    /// Loads CDN Live channel rows (caller filters <c>status == online</c>).
    /// </summary>
    public async Task<IReadOnlyList<CdnChannelRow>> GetCdnChannelsAsync(string requestUrl, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(StreamedPkHttpClients.Api);
            using var resp = await client.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseCdnChannels(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CDN Live: failed to load channels {Url}", requestUrl);
            return [];
        }
    }

    /// <summary>
    /// Finds a CDN playback URL by the stable key used in channel item ids.
    /// </summary>
    public async Task<string?> TryResolveCdnUrlByKeyAsync(
        string urlKeyHex,
        string channelsUrl,
        string sportsUrl,
        CancellationToken cancellationToken)
    {
        foreach (var row in await GetCdnChannelsAsync(channelsUrl, cancellationToken).ConfigureAwait(false))
        {
            if (!string.Equals(row.Status, "online", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(StableUrlKey(row.Url), urlKeyHex, StringComparison.OrdinalIgnoreCase))
            {
                return row.Url;
            }
        }

        foreach (var ev in await GetCdnSportEventsAsync(sportsUrl, cancellationToken).ConfigureAwait(false))
        {
            foreach (var ch in ev.Channels)
            {
                if (string.Equals(StableUrlKey(ch.Url), urlKeyHex, StringComparison.OrdinalIgnoreCase))
                {
                    return ch.Url;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Loads CDN Live sports events (nested under <c>cdn-live-tv</c>).
    /// </summary>
    public async Task<IReadOnlyList<CdnSportEventRow>> GetCdnSportEventsAsync(string requestUrl, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(StreamedPkHttpClients.Api);
            using var resp = await client.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseCdnSportEvents(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CDN Live: failed to load sports {Url}", requestUrl);
            return [];
        }
    }

    private static List<PpvStreamRow> ParsePpvStreams(string json)
    {
        var list = new List<PpvStreamRow>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("streams", out var groups) || groups.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var grp in groups.EnumerateArray())
        {
            if (!grp.TryGetProperty("streams", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var row in rows.EnumerateArray())
            {
                var id = ReadInt(row, "id");
                if (id is null)
                {
                    continue;
                }

                var iframe = ReadString(row, "iframe");
                if (string.IsNullOrWhiteSpace(iframe))
                {
                    continue;
                }

                list.Add(new PpvStreamRow(
                    Id: id.Value,
                    Name: ReadString(row, "name") ?? "Stream",
                    Poster: ReadString(row, "poster"),
                    Iframe: iframe.Trim(),
                    CategoryName: ReadString(row, "category_name"),
                    Tag: ReadString(row, "tag")));
            }
        }

        return list;
    }

    private static List<CdnChannelRow> ParseCdnChannels(string json)
    {
        var list = new List<CdnChannelRow>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("channels", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var el in arr.EnumerateArray())
        {
            var url = ReadString(el, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            list.Add(new CdnChannelRow(
                Name: ReadString(el, "name") ?? "Channel",
                Code: ReadString(el, "code") ?? string.Empty,
                Url: url.Trim(),
                Image: ReadString(el, "image"),
                Status: ReadString(el, "status") ?? string.Empty,
                Viewers: ReadInt(el, "viewers") ?? 0));
        }

        return list;
    }

    private static List<CdnSportEventRow> ParseCdnSportEvents(string json)
    {
        var list = new List<CdnSportEventRow>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("cdn-live-tv", out var cdnTv) || cdnTv.ValueKind != JsonValueKind.Object)
        {
            return list;
        }

        foreach (var sportProp in cdnTv.EnumerateObject())
        {
            var sportName = sportProp.Name;
            if (sportProp.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var ev in sportProp.Value.EnumerateArray())
            {
                var gameId = ReadGameId(ev);
                if (string.IsNullOrEmpty(gameId))
                {
                    continue;
                }

                var home = ReadTeamName(ev, "homeTeam");
                var away = ReadTeamName(ev, "awayTeam");
                var eventTitle = ReadString(ev, "event");
                var tournament = ReadString(ev, "tournament");
                var displayTournament = string.IsNullOrWhiteSpace(tournament) ? sportName : tournament!;

                var channels = new List<CdnSportChannelRow>();
                if (ev.TryGetProperty("channels", out var chArr) && chArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ch in chArr.EnumerateArray())
                    {
                        var u = ReadString(ch, "url");
                        if (string.IsNullOrWhiteSpace(u))
                        {
                            continue;
                        }

                        channels.Add(new CdnSportChannelRow(
                            ChannelName: ReadString(ch, "channel_name") ?? ReadString(ch, "name") ?? "Feed",
                            ChannelCode: ReadString(ch, "channel_code") ?? ReadString(ch, "code") ?? string.Empty,
                            Url: u.Trim(),
                            Image: ReadString(ch, "image")));
                    }
                }

                if (channels.Count == 0)
                {
                    continue;
                }

                list.Add(new CdnSportEventRow(
                    GameId: gameId,
                    SportName: sportName,
                    Tournament: displayTournament,
                    HomeTeam: home,
                    AwayTeam: away,
                    EventTitle: eventTitle,
                    Channels: channels));
            }
        }

        return list;
    }

    private static string? ReadGameId(JsonElement ev)
    {
        if (ev.TryGetProperty("gameID", out var p))
        {
            return p.ValueKind switch
            {
                JsonValueKind.String => p.GetString(),
                JsonValueKind.Number => p.GetRawText(),
                _ => null
            };
        }

        if (ev.TryGetProperty("gameId", out var p2))
        {
            return p2.ValueKind switch
            {
                JsonValueKind.String => p2.GetString(),
                JsonValueKind.Number => p2.GetRawText(),
                _ => null
            };
        }

        return null;
    }

    private static string? ReadTeamName(JsonElement ev, string prop)
    {
        if (!ev.TryGetProperty(prop, out var p))
        {
            return null;
        }

        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Object when p.TryGetProperty("name", out var n) => n.GetString(),
            _ => null
        };
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? ReadInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p))
        {
            return null;
        }

        return p.ValueKind switch
        {
            JsonValueKind.Number when p.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var j) => j,
            _ => null
        };
    }
}

/// <summary>
/// One playable PPV.to stream row.
/// </summary>
public sealed record PpvStreamRow(int Id, string Name, string? Poster, string Iframe, string? CategoryName, string? Tag);

/// <summary>
/// CDN Live channel list row.
/// </summary>
public sealed record CdnChannelRow(string Name, string Code, string Url, string? Image, string Status, int Viewers);

/// <summary>
/// CDN Live sports event with one or more channel feeds.
/// </summary>
public sealed record CdnSportEventRow(
    string GameId,
    string SportName,
    string Tournament,
    string? HomeTeam,
    string? AwayTeam,
    string? EventTitle,
    IReadOnlyList<CdnSportChannelRow> Channels);

/// <summary>
/// A feed line under a CDN sports event.
/// </summary>
public sealed record CdnSportChannelRow(string ChannelName, string ChannelCode, string Url, string? Image);
