using System.Globalization;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.StreamedPk.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamedPk;

/// <summary>
/// Jellyfin channel: Streamed.pk, PPV.to, and CDN Live in a PlayTorrio-style layout.
/// </summary>
public sealed class StreamedPkChannel : IChannel, IRequiresMediaInfoCallback
{
    private const string ModeLive = "live";
    private const string ModeToday = "today";
    private const string ModeAll = "all";

    private const string PpvGroupAll = "_all";

    private const string DefaultPpvStreamsUrl = "https://old.ppv.to/api/streams";
    private const string DefaultCdnChannelsUrl = "https://api.cdn-live.tv/api/v1/channels/?user=cdnlivetv&plan=free";
    private const string DefaultCdnSportsUrl = "https://api.cdn-live.tv/api/v1/events/sports/?user=cdnlivetv&plan=free";

    /// <summary>
    /// Jellyfin combines channel item ExternalId, channel name, and this suffix before hashing to the library item Guid
    /// (see <c>ChannelManager.GetIdToHash</c>). Dynamic media sources must use the same Guid string as <c>MediaSourceInfo.Id</c>
    /// so the client’s <c>mediaSourceId</c> (from the item DTO) matches playback.
    /// </summary>
    private const string JellyfinChannelIdVersionSuffix = "16";

    private const string ChannelThumbResourceName = "Jellyfin.Plugin.StreamedPk.Assets.channel-thumb.png";

    private readonly StreamedPkClient _client;
    private readonly PlayTorrioFeedsClient _feeds;
    private readonly EmbedPageStreamResolver _embedResolver;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<StreamedPkChannel> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamedPkChannel"/> class.
    /// </summary>
    public StreamedPkChannel(
        StreamedPkClient client,
        PlayTorrioFeedsClient feeds,
        EmbedPageStreamResolver embedResolver,
        ILibraryManager libraryManager,
        ILogger<StreamedPkChannel> logger)
    {
        _client = client;
        _feeds = feeds;
        _embedResolver = embedResolver;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Live Matches";

    /// <inheritdoc />
    public string Description =>
        "Sports-style channel browsing: Streamed.pk schedules, PPV.to categories, and CDN Live — with optional embed-to-stream resolution.";

    /// <inheritdoc />
    public string DataVersion => "streamed-pk-channel-8-playback-readrate";

    /// <inheritdoc />
    public string HomePageUrl => "https://streamed.pk";

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            MediaTypes = [ChannelMediaType.Video],
            ContentTypes = [ChannelMediaContentType.Clip],
            MaxPageSize = 300,
            AutoRefreshLevels = 6
        };
    }

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => Plugin.Instance?.Configuration.EnableChannel ?? true;

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        if (TryParsePpvMediaId(id, out var ppvStreamId))
        {
            var ppvUrl = GetConfig().PpvStreamsUrl?.Trim();
            if (string.IsNullOrEmpty(ppvUrl))
            {
                ppvUrl = DefaultPpvStreamsUrl;
            }

            var rows = await _feeds.GetPpvStreamsAsync(ppvUrl, cancellationToken).ConfigureAwait(false);
            var row = rows.FirstOrDefault(r => r.Id == ppvStreamId);
            if (row is null || string.IsNullOrWhiteSpace(row.Iframe))
            {
                return [];
            }

            return await BuildMediaSourcesAsync(id, row.Iframe, "PPV.to", cancellationToken).ConfigureAwait(false);
        }

        if (TryParseCdnMediaId(id, out var cdnKey))
        {
            var cfg = GetConfig();
            var chUrl = string.IsNullOrWhiteSpace(cfg.CdnChannelsUrl) ? DefaultCdnChannelsUrl : cfg.CdnChannelsUrl.Trim();
            var spUrl = string.IsNullOrWhiteSpace(cfg.CdnSportsUrl) ? DefaultCdnSportsUrl : cfg.CdnSportsUrl.Trim();
            var playback = await _feeds.TryResolveCdnUrlByKeyAsync(cdnKey, chUrl, spUrl, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(playback))
            {
                return [];
            }

            return await BuildMediaSourcesAsync(id, playback, "CDN Live", cancellationToken).ConfigureAwait(false);
        }

        if (!TryParseMediaItemId(id, out var source, out var eventId, out var streamId))
        {
            return [];
        }

        var streams = await _client.GetStreamsAsync(source, eventId, cancellationToken).ConfigureAwait(false);
        var stream = streams.FirstOrDefault(s => string.Equals(s.Id, streamId, StringComparison.OrdinalIgnoreCase));
        if (stream is null || string.IsNullOrWhiteSpace(stream.EmbedUrl))
        {
            return [];
        }

        return await BuildMediaSourcesAsync(id, stream.EmbedUrl, "Streamed.pk", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.EnableChannel == false)
        {
            return new ChannelItemResult { Items = [], TotalRecordCount = 0 };
        }

        try
        {
            var folderId = query.FolderId ?? string.Empty;
            if (string.IsNullOrEmpty(folderId))
            {
                return GetProviderRoot(query);
            }

            if (string.Equals(folderId, "prov|streamed", StringComparison.Ordinal))
            {
                return Slice(GetStreamedModeFolders(), query);
            }

            if (string.Equals(folderId, "prov|ppv", StringComparison.Ordinal))
            {
                return await GetPpvCategoryFoldersAsync(query, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(folderId, "prov|cdn", StringComparison.Ordinal))
            {
                return GetCdnTopFolders(query);
            }

            if (TryParsePpvGroupFolder(folderId, out var ppvGroupKey))
            {
                return await GetPpvStreamMediaAsync(ppvGroupKey, query, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(folderId, "cdn|v|channels", StringComparison.Ordinal))
            {
                return await GetCdnChannelMediaAsync(query, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(folderId, "cdn|v|sports", StringComparison.Ordinal))
            {
                return await GetCdnSportTournamentFoldersAsync(query, cancellationToken).ConfigureAwait(false);
            }

            if (TryParseCdnTournamentFolder(folderId, out var tournamentKey))
            {
                return await GetCdnSportEventItemsAsync(tournamentKey, query, cancellationToken).ConfigureAwait(false);
            }

            if (TryParseCdnEventFolder(folderId, out var eventToken))
            {
                return await GetCdnEventChannelMediaAsync(eventToken, query, cancellationToken).ConfigureAwait(false);
            }

            if (TryParseModeFolder(folderId, out var mode))
            {
                return await GetSportFilterFoldersAsync(mode, cancellationToken, query).ConfigureAwait(false);
            }

            if (TryParseSportFilterFolder(folderId, out var sportKey, out var modeKey))
            {
                return await GetMatchSourceFoldersAsync(modeKey, sportKey, cancellationToken, query).ConfigureAwait(false);
            }

            if (TryParseStreamsFolder(folderId, out var src, out var evId))
            {
                return await GetStreamMediaAsync(src, evId, cancellationToken, query).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live Matches channel failed for folder {FolderId}", query.FolderId);
        }

        return new ChannelItemResult { Items = [], TotalRecordCount = 0 };
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        if (type != ImageType.Primary)
        {
            return Task.FromResult(new DynamicImageResponse { HasImage = false });
        }

        var asm = typeof(StreamedPkChannel).Assembly;
        var stream = asm.GetManifestResourceStream(ChannelThumbResourceName);
        if (stream is null)
        {
            _logger.LogWarning("Missing embedded channel image: {Resource}", ChannelThumbResourceName);
            return Task.FromResult(new DynamicImageResponse { HasImage = false });
        }

        return Task.FromResult(new DynamicImageResponse
        {
            HasImage = true,
            Stream = stream,
            Format = ImageFormat.Png
        });
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => [ImageType.Primary];

    private static StreamedPkPluginConfiguration GetConfig() =>
        Plugin.Instance?.Configuration ?? new StreamedPkPluginConfiguration();

    private string GetChannelVideoMediaSourceId(string channelExternalId)
    {
        var key = channelExternalId + Name + JellyfinChannelIdVersionSuffix;
        return _libraryManager.GetNewItemId(key, typeof(Video)).ToString("N", CultureInfo.InvariantCulture);
    }

    private const string BrowserLikeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    private async Task<IEnumerable<MediaSourceInfo>> BuildMediaSourcesAsync(
        string channelExternalId,
        string initialUrl,
        string friendlySourceName,
        CancellationToken cancellationToken)
    {
        var tryResolve = GetConfig().TryResolveEmbedToDirectStream;
        var originalUrl = initialUrl.Trim();
        var playbackUrl = originalUrl;
        if (tryResolve)
        {
            try
            {
                var resolved = await _embedResolver.TryResolveDirectStreamUrlAsync(initialUrl, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    playbackUrl = resolved.Trim();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Embed resolve failed, using original URL");
            }
        }

        var container = InferContainer(playbackUrl);
        var sourceLabel = UsesResolvedDirectUrl(playbackUrl, originalUrl) ? friendlySourceName + " (direct)" : friendlySourceName;

        // Remote HLS/DASH is usually remuxed/transcoded by the server; direct-play often fails on clients.
        // Progressive MP4 over HTTP can direct-play.
        var hlsOrDash = string.Equals(container, "hls", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(container, "mpd", StringComparison.OrdinalIgnoreCase)
                        || playbackUrl.Contains(".m3u", StringComparison.OrdinalIgnoreCase)
                        || playbackUrl.Contains(".mpd", StringComparison.OrdinalIgnoreCase);
        var mp4Like = string.Equals(container, "mp4", StringComparison.OrdinalIgnoreCase)
                      || playbackUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
        var supportsDirectPlay = !hlsOrDash && mp4Like;

        return
        [
            new MediaSourceInfo
            {
                Id = GetChannelVideoMediaSourceId(channelExternalId),
                Name = sourceLabel,
                Path = playbackUrl,
                Protocol = MediaProtocol.Http,
                IsRemote = true,
                IsInfiniteStream = true,
                // Jellyfin maps this to ffmpeg "-re" on the input. For HTTP/HLS that throttles reads to realtime,
                // which often stalls transcoding or causes clients to bail back to the previous screen.
                ReadAtNativeFramerate = false,
                VideoType = VideoType.VideoFile,
                SupportsDirectPlay = supportsDirectPlay,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                Container = container,
                AnalyzeDurationMs = 10_000,
                RequiredHttpHeaders = BuildLiveStreamHttpHeaders(originalUrl, playbackUrl),
                // Hints only (no probe yet). Helps StreamBuilder pick a transcode path; ffmpeg still inspects the real stream.
                MediaStreams =
                [
                    new MediaStream { Type = MediaStreamType.Video, Index = -1, IsInterlaced = false, Codec = "h264" },
                    new MediaStream { Type = MediaStreamType.Audio, Index = -1, Codec = "aac" }
                ]
            }
        ];
    }

    /// <summary>
    /// CDNs and embed players often reject ffmpeg unless Referer/User-Agent match a normal browser request.
    /// </summary>
    private static Dictionary<string, string> BuildLiveStreamHttpHeaders(string originalUrl, string playbackUrl)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = BrowserLikeUserAgent
        };

        var referer = originalUrl.Trim();
        if (!Uri.TryCreate(referer, UriKind.Absolute, out var refererUri) || refererUri.Scheme is not ("http" or "https"))
        {
            if (Uri.TryCreate(playbackUrl, UriKind.Absolute, out var playUri) && playUri.Scheme is ("http" or "https"))
            {
                referer = playUri.GetLeftPart(UriPartial.Authority) + "/";
            }
            else
            {
                return headers;
            }
        }

        headers["Referer"] = referer;
        if (Uri.TryCreate(referer, UriKind.Absolute, out var rUri) && rUri.Scheme is ("http" or "https"))
        {
            headers["Origin"] = rUri.GetLeftPart(UriPartial.Authority);
        }

        return headers;
    }

    private static bool UsesResolvedDirectUrl(string playbackUrl, string embedUrl) =>
        !string.Equals(playbackUrl.Trim(), embedUrl.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string InferContainer(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return string.Empty;
        }

        var path = u.AbsolutePath;
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return "hls";
        }

        if (path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
        {
            return "mpd";
        }

        if (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return "mp4";
        }

        return string.Empty;
    }

    private static bool TryParsePpvMediaId(string id, out int streamId)
    {
        streamId = 0;
        const string p = "ppv|m|";
        if (!id.StartsWith(p, StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(id[p.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out streamId);
    }

    private static bool TryParseCdnMediaId(string id, out string keyHex)
    {
        keyHex = string.Empty;
        const string p = "cdn|c|";
        if (!id.StartsWith(p, StringComparison.Ordinal))
        {
            return false;
        }

        var k = id[p.Length..];
        if (k.Length != 16)
        {
            return false;
        }

        foreach (var c in k)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        keyHex = k.ToLowerInvariant();
        return true;
    }

    private static bool TryParseMediaItemId(string id, out string source, out string eventId, out string streamId)
    {
        source = string.Empty;
        eventId = string.Empty;
        streamId = string.Empty;
        if (!id.StartsWith("media|", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = id.Split('|', 4, StringSplitOptions.None);
        if (parts.Length != 4)
        {
            return false;
        }

        source = parts[1];
        eventId = parts[2];
        streamId = parts[3];
        return !string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(eventId) && !string.IsNullOrWhiteSpace(streamId);
    }

    private ChannelItemResult GetProviderRoot(InternalChannelItemQuery query)
    {
        var cfg = GetConfig();
        var utc = DateTime.UtcNow;
        var items = new List<ChannelItemInfo>();

        if (cfg.EnableStreamedPk)
        {
            items.Add(new ChannelItemInfo
            {
                Id = "prov|streamed",
                Name = "Streamed.pk",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                Overview = "Live, today, and full schedules — filter by sport, then pick a source.",
                DateModified = utc
            });
        }

        if (cfg.EnablePpvTo)
        {
            items.Add(new ChannelItemInfo
            {
                Id = "prov|ppv",
                Name = "PPV.to",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                Overview = "Browse by category; playback uses each stream’s embed URL.",
                DateModified = utc
            });
        }

        if (cfg.EnableCdnLive)
        {
            items.Add(new ChannelItemInfo
            {
                Id = "prov|cdn",
                Name = "CDN Live",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                Overview = "Live TV channels and sports events from the configured CDN feed.",
                DateModified = utc
            });
        }

        return Slice(items, query);
    }

    private static IReadOnlyList<ChannelItemInfo> GetStreamedModeFolders()
    {
        var utc = DateTime.UtcNow;
        return
        [
            new ChannelItemInfo
            {
                Id = "mode|live",
                Name = "Live now",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                Overview = "Currently airing matches from the live feed.",
                DateModified = utc
            },
            new ChannelItemInfo
            {
                Id = "mode|today",
                Name = "Today's schedule",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                Overview = "Today’s full schedule.",
                DateModified = utc
            },
            new ChannelItemInfo
            {
                Id = "mode|all",
                Name = "All matches",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                Overview = "Full match list (may be large).",
                DateModified = utc
            }
        ];
    }

    private async Task<ChannelItemResult> GetPpvCategoryFoldersAsync(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var ppvUrl = GetConfig().PpvStreamsUrl?.Trim();
        if (string.IsNullOrEmpty(ppvUrl))
        {
            ppvUrl = DefaultPpvStreamsUrl;
        }

        var rows = await _feeds.GetPpvStreamsAsync(ppvUrl, cancellationToken).ConfigureAwait(false);
        var utc = DateTime.UtcNow;
        var items = new List<ChannelItemInfo>
        {
            new()
            {
                Id = "ppv|g|" + PpvGroupAll,
                Name = "All streams",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                Overview = "Every PPV.to stream with an iframe",
                DateModified = utc
            }
        };

        var categories = rows
            .Select(PpvCategoryLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var c in categories)
        {
            items.Add(new ChannelItemInfo
            {
                Id = "ppv|g|" + Base64UrlEncode(c),
                Name = c,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                DateModified = utc
            });
        }

        return Slice(items, query);
    }

    private static string PpvCategoryLabel(PpvStreamRow r) =>
        string.IsNullOrWhiteSpace(r.CategoryName) ? "Uncategorized" : r.CategoryName.Trim();

    private async Task<ChannelItemResult> GetPpvStreamMediaAsync(
        string groupKey,
        InternalChannelItemQuery query,
        CancellationToken cancellationToken)
    {
        var ppvUrl = GetConfig().PpvStreamsUrl?.Trim();
        if (string.IsNullOrEmpty(ppvUrl))
        {
            ppvUrl = DefaultPpvStreamsUrl;
        }

        var rows = await _feeds.GetPpvStreamsAsync(ppvUrl, cancellationToken).ConfigureAwait(false);
        List<PpvStreamRow> filtered;
        if (string.Equals(groupKey, PpvGroupAll, StringComparison.Ordinal))
        {
            filtered = rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        else
        {
            string decoded;
            try
            {
                decoded = Base64UrlDecode(groupKey);
            }
            catch (FormatException)
            {
                return new ChannelItemResult { Items = [], TotalRecordCount = 0 };
            }

            filtered = rows
                .Where(r => string.Equals(PpvCategoryLabel(r), decoded, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var utc = DateTime.UtcNow;
        var media = new List<ChannelItemInfo>();
        foreach (var r in filtered)
        {
            var label = string.IsNullOrWhiteSpace(r.Tag) ? r.Name : r.Name + " · " + r.Tag.Trim();
            media.Add(new ChannelItemInfo
            {
                Id = "ppv|m|" + r.Id,
                Name = label,
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Clip,
                IsLiveStream = true,
                DateModified = utc,
                ImageUrl = string.IsNullOrWhiteSpace(r.Poster) ? null : r.Poster.Trim(),
                MediaSources = []
            });
        }

        return Slice(media, query);
    }

    private static bool TryParsePpvGroupFolder(string folderId, out string groupKey)
    {
        groupKey = string.Empty;
        const string p = "ppv|g|";
        if (!folderId.StartsWith(p, StringComparison.Ordinal))
        {
            return false;
        }

        groupKey = folderId[p.Length..];
        return groupKey.Length > 0;
    }

    private static ChannelItemResult GetCdnTopFolders(InternalChannelItemQuery query)
    {
        var utc = DateTime.UtcNow;
        var items = new List<ChannelItemInfo>
        {
            new()
            {
                Id = "cdn|v|channels",
                Name = "Channels",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                Overview = "Online channels only",
                DateModified = utc
            },
            new()
            {
                Id = "cdn|v|sports",
                Name = "Sports",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                Overview = "Live sports by tournament",
                DateModified = utc
            }
        };

        return Slice(items, query);
    }

    private async Task<ChannelItemResult> GetCdnChannelMediaAsync(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var cfg = GetConfig();
        var chUrl = string.IsNullOrWhiteSpace(cfg.CdnChannelsUrl) ? DefaultCdnChannelsUrl : cfg.CdnChannelsUrl.Trim();
        var rows = await _feeds.GetCdnChannelsAsync(chUrl, cancellationToken).ConfigureAwait(false);
        var online = rows
            .Where(r => string.Equals(r.Status, "online", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var utc = DateTime.UtcNow;
        var media = new List<ChannelItemInfo>();
        foreach (var r in online)
        {
            media.Add(new ChannelItemInfo
            {
                Id = "cdn|c|" + PlayTorrioFeedsClient.StableUrlKey(r.Url),
                Name = r.Name + " · " + r.Code,
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Clip,
                IsLiveStream = true,
                DateModified = utc,
                ImageUrl = string.IsNullOrWhiteSpace(r.Image) ? null : r.Image.Trim(),
                MediaSources = []
            });
        }

        return Slice(media, query);
    }

    private async Task<ChannelItemResult> GetCdnSportTournamentFoldersAsync(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var cfg = GetConfig();
        var spUrl = string.IsNullOrWhiteSpace(cfg.CdnSportsUrl) ? DefaultCdnSportsUrl : cfg.CdnSportsUrl.Trim();
        var events = await _feeds.GetCdnSportEventsAsync(spUrl, cancellationToken).ConfigureAwait(false);
        var tournaments = events
            .Select(e => e.Tournament)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var utc = DateTime.UtcNow;
        var items = new List<ChannelItemInfo>
        {
            new()
            {
                Id = "cdn|t|" + PpvGroupAll,
                Name = "All events",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                Overview = "Every live sports row in the feed",
                DateModified = utc
            }
        };

        foreach (var t in tournaments)
        {
            items.Add(new ChannelItemInfo
            {
                Id = "cdn|t|" + Base64UrlEncode(t),
                Name = t,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                DateModified = utc
            });
        }

        return Slice(items, query);
    }

    private async Task<ChannelItemResult> GetCdnSportEventItemsAsync(
        string tournamentKey,
        InternalChannelItemQuery query,
        CancellationToken cancellationToken)
    {
        var cfg = GetConfig();
        var spUrl = string.IsNullOrWhiteSpace(cfg.CdnSportsUrl) ? DefaultCdnSportsUrl : cfg.CdnSportsUrl.Trim();
        var events = await _feeds.GetCdnSportEventsAsync(spUrl, cancellationToken).ConfigureAwait(false);
        List<CdnSportEventRow> filtered;
        if (string.Equals(tournamentKey, PpvGroupAll, StringComparison.Ordinal))
        {
            filtered = events.ToList();
        }
        else
        {
            string decoded;
            try
            {
                decoded = Base64UrlDecode(tournamentKey);
            }
            catch (FormatException)
            {
                return new ChannelItemResult { Items = [], TotalRecordCount = 0 };
            }

            filtered = events
                .Where(e => string.Equals(e.Tournament, decoded, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        filtered.Sort(static (a, b) => string.Compare(FormatCdnEventTitle(a), FormatCdnEventTitle(b), StringComparison.OrdinalIgnoreCase));

        var utc = DateTime.UtcNow;
        var items = new List<ChannelItemInfo>();
        foreach (var ev in filtered)
        {
            var title = FormatCdnEventTitle(ev);
            if (ev.Channels.Count == 1)
            {
                var ch = ev.Channels[0];
                items.Add(new ChannelItemInfo
                {
                    Id = "cdn|c|" + PlayTorrioFeedsClient.StableUrlKey(ch.Url),
                    Name = title + " · " + ch.ChannelName,
                    Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video,
                    ContentType = ChannelMediaContentType.Clip,
                    IsLiveStream = true,
                    DateModified = utc,
                    ImageUrl = PickCdnEventImage(ev, ch),
                    MediaSources = []
                });
            }
            else
            {
                items.Add(new ChannelItemInfo
                {
                    Id = MakeCdnEventFolderId(ev),
                    Name = title,
                    Type = ChannelItemType.Folder,
                    FolderType = ChannelFolderType.Container,
                    DateModified = utc,
                    ImageUrl = PickCdnEventImage(ev, null),
                    Overview = ev.Channels.Count + " feeds"
                });
            }
        }

        return Slice(items, query);
    }

    private async Task<ChannelItemResult> GetCdnEventChannelMediaAsync(
        string eventToken,
        InternalChannelItemQuery query,
        CancellationToken cancellationToken)
    {
        CdnSportEventRow? match;
        try
        {
            var decoded = Base64UrlDecode(eventToken);
            var sep = decoded.IndexOf('\u001f', StringComparison.Ordinal);
            if (sep <= 0 || sep >= decoded.Length - 1)
            {
                return new ChannelItemResult { Items = [], TotalRecordCount = 0 };
            }

            var gameId = decoded[..sep];
            var sport = decoded[(sep + 1)..];
            var cfg = GetConfig();
            var spUrl = string.IsNullOrWhiteSpace(cfg.CdnSportsUrl) ? DefaultCdnSportsUrl : cfg.CdnSportsUrl.Trim();
            var events = await _feeds.GetCdnSportEventsAsync(spUrl, cancellationToken).ConfigureAwait(false);
            match = events.FirstOrDefault(e =>
                string.Equals(e.GameId, gameId, StringComparison.Ordinal) &&
                string.Equals(e.SportName, sport, StringComparison.Ordinal));
        }
        catch (FormatException)
        {
            return new ChannelItemResult { Items = [], TotalRecordCount = 0 };
        }

        if (match is null)
        {
            return new ChannelItemResult { Items = [], TotalRecordCount = 0 };
        }

        var title = FormatCdnEventTitle(match);
        var utc = DateTime.UtcNow;
        var media = new List<ChannelItemInfo>();
        foreach (var ch in match.Channels)
        {
            media.Add(new ChannelItemInfo
            {
                Id = "cdn|c|" + PlayTorrioFeedsClient.StableUrlKey(ch.Url),
                Name = title + " · " + ch.ChannelName,
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Clip,
                IsLiveStream = true,
                DateModified = utc,
                ImageUrl = PickCdnEventImage(match, ch),
                MediaSources = []
            });
        }

        return Slice(media, query);
    }

    private static string MakeCdnEventFolderId(CdnSportEventRow ev) =>
        "cdn|ev|" + Base64UrlEncode(ev.GameId + "\u001f" + ev.SportName);

    private static bool TryParseCdnTournamentFolder(string folderId, out string tournamentKey)
    {
        tournamentKey = string.Empty;
        const string p = "cdn|t|";
        if (!folderId.StartsWith(p, StringComparison.Ordinal))
        {
            return false;
        }

        tournamentKey = folderId[p.Length..];
        return tournamentKey.Length > 0;
    }

    private static bool TryParseCdnEventFolder(string folderId, out string eventToken)
    {
        eventToken = string.Empty;
        const string p = "cdn|ev|";
        if (!folderId.StartsWith(p, StringComparison.Ordinal))
        {
            return false;
        }

        eventToken = folderId[p.Length..];
        return eventToken.Length > 0;
    }

    private static string FormatCdnEventTitle(CdnSportEventRow ev)
    {
        if (!string.IsNullOrWhiteSpace(ev.HomeTeam) && !string.IsNullOrWhiteSpace(ev.AwayTeam))
        {
            return ev.HomeTeam.Trim() + " vs " + ev.AwayTeam.Trim();
        }

        return string.IsNullOrWhiteSpace(ev.EventTitle) ? "Event " + ev.GameId : ev.EventTitle.Trim();
    }

    private static string? PickCdnEventImage(CdnSportEventRow ev, CdnSportChannelRow? channel)
    {
        var img = channel?.Image;
        if (!string.IsNullOrWhiteSpace(img))
        {
            return img.Trim();
        }

        foreach (var c in ev.Channels)
        {
            if (!string.IsNullOrWhiteSpace(c.Image))
            {
                return c.Image.Trim();
            }
        }

        return null;
    }

    private static string Base64UrlEncode(string text)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Base64UrlDecode(string b64)
    {
        var s = b64.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2:
                s += "==";
                break;
            case 3:
                s += "=";
                break;
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    private static bool TryParseModeFolder(string folderId, out string mode)
    {
        mode = string.Empty;
        if (!folderId.StartsWith("mode|", StringComparison.Ordinal))
        {
            return false;
        }

        mode = folderId["mode|".Length..];
        return mode is ModeLive or ModeToday or ModeAll;
    }

    private static bool TryParseSportFilterFolder(string folderId, out string sportKey, out string mode)
    {
        sportKey = string.Empty;
        mode = string.Empty;
        const string prefix = "sport|";
        if (!folderId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = folderId[prefix.Length..];
        var last = rest.LastIndexOf('|');
        if (last <= 0 || last >= rest.Length - 1)
        {
            return false;
        }

        sportKey = rest[..last];
        mode = rest[(last + 1)..];
        if (mode is not (ModeLive or ModeToday or ModeAll))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(sportKey);
    }

    private async Task<IReadOnlyList<ApiMatch>> LoadMatchesForModeAsync(string mode, CancellationToken cancellationToken) =>
        mode switch
        {
            ModeLive => await _client.GetLiveMatchesAsync(cancellationToken).ConfigureAwait(false),
            ModeToday => await _client.GetMatchesAllTodayAsync(cancellationToken).ConfigureAwait(false),
            ModeAll => await _client.GetMatchesAllListAsync(cancellationToken).ConfigureAwait(false),
            _ => Array.Empty<ApiMatch>()
        };

    private async Task<ChannelItemResult> GetSportFilterFoldersAsync(string mode, CancellationToken cancellationToken, InternalChannelItemQuery query)
    {
        var matches = await LoadMatchesForModeAsync(mode, cancellationToken).ConfigureAwait(false);
        var allSports = await _client.GetSportsAsync(cancellationToken).ConfigureAwait(false);
        var present = matches
            .Select(m => m.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filteredSports = allSports
            .Where(s => present.Contains(s.Id))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var utc = DateTime.UtcNow;
        var items = new List<ChannelItemInfo>
        {
            new()
            {
                Id = "sport|all|" + mode,
                Name = "All sports",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                Overview = "Every match in this feed",
                DateModified = utc
            }
        };

        foreach (var sport in filteredSports)
        {
            items.Add(new ChannelItemInfo
            {
                Id = "sport|" + sport.Id + "|" + mode,
                Name = sport.Name,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                DateModified = utc
            });
        }

        return Slice(items, query);
    }

    private async Task<ChannelItemResult> GetMatchSourceFoldersAsync(
        string mode,
        string sportKey,
        CancellationToken cancellationToken,
        InternalChannelItemQuery query)
    {
        var matches = (await LoadMatchesForModeAsync(mode, cancellationToken).ConfigureAwait(false)).ToList();
        if (sportKey != "all")
        {
            matches = matches
                .Where(m => string.Equals(m.Category, sportKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var rows = new List<(ApiMatch Match, ApiSourceRef Source)>();
        foreach (var match in matches)
        {
            if (match.Sources is null)
            {
                continue;
            }

            foreach (var src in match.Sources)
            {
                if (string.IsNullOrWhiteSpace(src.Source) || string.IsNullOrWhiteSpace(src.Id))
                {
                    continue;
                }

                rows.Add((match, src));
            }
        }

        rows.Sort(static (a, b) =>
        {
            var byMatch = CompareMatchesPlayTorrioStyle(a.Match, b.Match);
            if (byMatch != 0)
            {
                return byMatch;
            }

            return string.Compare(a.Source.Source, b.Source.Source, StringComparison.OrdinalIgnoreCase);
        });

        var baseUrl = _client.GetBaseUrl();
        var folders = new List<ChannelItemInfo>();

        foreach (var (match, src) in rows)
        {
            DateTime? premiere = null;
            try
            {
                premiere = DateTimeOffset.FromUnixTimeMilliseconds(match.Date).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
            }

            var imageUrl = BuildMatchPosterUrl(match, baseUrl);
            var matchTitle = FormatMatchDisplayName(match);
            var categoryOverview = FormatCategoryLabel(match.Category);
            var folderKey = MakeStreamsFolderId(src.Source, src.Id);
            var label = (match.Popular ? "★ " : string.Empty) + matchTitle + " · " + src.Source;
            folders.Add(new ChannelItemInfo
            {
                Id = folderKey,
                Name = label,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                DateModified = premiere ?? DateTime.UtcNow,
                PremiereDate = premiere,
                Overview = categoryOverview,
                ImageUrl = imageUrl
            });
        }

        return Slice(folders, query);
    }

    private static int CompareMatchesPlayTorrioStyle(ApiMatch a, ApiMatch b)
    {
        if (a.Popular != b.Popular)
        {
            return a.Popular ? -1 : 1;
        }

        return a.Date.CompareTo(b.Date);
    }

    private static string FormatMatchDisplayName(ApiMatch match)
    {
        var home = match.Teams?.Home?.Name?.Trim();
        var away = match.Teams?.Away?.Name?.Trim();
        if (!string.IsNullOrEmpty(home) && !string.IsNullOrEmpty(away))
        {
            return home + " vs " + away;
        }

        return string.IsNullOrWhiteSpace(match.Title) ? "Unknown match" : match.Title;
    }

    private static string? FormatCategoryLabel(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return null;
        }

        var spaced = category.Replace("-", " ", StringComparison.Ordinal);
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced);
    }

    private static string? BuildMatchPosterUrl(ApiMatch match, string baseUrl)
    {
        var hb = match.Teams?.Home?.Badge;
        var ab = match.Teams?.Away?.Badge;
        if (!string.IsNullOrEmpty(hb) && !string.IsNullOrEmpty(ab))
        {
            return baseUrl + "/api/images/poster/" + hb + "/" + ab + ".webp";
        }

        if (string.IsNullOrEmpty(match.Poster))
        {
            return null;
        }

        return match.Poster.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? match.Poster
            : baseUrl + match.Poster;
    }

    private async Task<ChannelItemResult> GetStreamMediaAsync(
        string source,
        string eventId,
        CancellationToken cancellationToken,
        InternalChannelItemQuery query)
    {
        var streams = await _client.GetStreamsAsync(source, eventId, cancellationToken).ConfigureAwait(false);
        var media = new List<ChannelItemInfo>();

        foreach (var stream in streams)
        {
            if (string.IsNullOrWhiteSpace(stream.EmbedUrl))
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(stream.Language) ? "Unknown" : stream.Language.Trim();
            if (stream.Hd)
            {
                label += " · HD";
            }

            label += " · #" + stream.StreamNo;

            media.Add(new ChannelItemInfo
            {
                Id = "media|" + source + "|" + eventId + "|" + stream.Id,
                Name = label,
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Clip,
                IsLiveStream = true,
                DateModified = DateTime.UtcNow,
                MediaSources = []
            });
        }

        return Slice(media, query);
    }

    private static ChannelItemResult Slice(IReadOnlyList<ChannelItemInfo> items, InternalChannelItemQuery query)
    {
        var start = query.StartIndex ?? 0;
        var total = items.Count;
        if (start < 0)
        {
            start = 0;
        }

        if (start >= total)
        {
            return new ChannelItemResult { Items = [], TotalRecordCount = total };
        }

        var limit = query.Limit ?? total;
        if (limit <= 0)
        {
            limit = total;
        }

        var slice = items.Skip(start).Take(limit).ToList();
        return new ChannelItemResult { Items = slice, TotalRecordCount = total };
    }

    internal static string MakeStreamsFolderId(string source, string eventId) =>
        "streams|" + source + "|" + Uri.EscapeDataString(eventId);

    internal static bool TryParseStreamsFolder(string folderId, out string source, out string eventId)
    {
        source = string.Empty;
        eventId = string.Empty;
        if (!folderId.StartsWith("streams|", StringComparison.Ordinal))
        {
            return false;
        }

        var rest = folderId["streams|".Length..];
        var sep = rest.IndexOf('|', StringComparison.Ordinal);
        if (sep <= 0 || sep >= rest.Length - 1)
        {
            return false;
        }

        source = rest[..sep];
        var encoded = rest[(sep + 1)..];
        try
        {
            eventId = Uri.UnescapeDataString(encoded);
        }
        catch (UriFormatException)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(eventId);
    }
}
