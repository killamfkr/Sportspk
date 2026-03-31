using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.StreamedPk.Configuration;

/// <summary>
/// Plugin settings for the Streamed.pk channel.
/// </summary>
public class StreamedPkPluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the channel is registered.
    /// </summary>
    public bool EnableChannel { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the Streamed.pk provider folder is shown.
    /// </summary>
    public bool EnableStreamedPk { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the PPV.to provider folder is shown.
    /// </summary>
    public bool EnablePpvTo { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the CDN Live provider folder is shown.
    /// </summary>
    public bool EnableCdnLive { get; set; } = true;

    /// <summary>
    /// Gets or sets the API root (no trailing slash), e.g. https://streamed.pk.
    /// </summary>
    public string ApiBaseUrl { get; set; } = "https://streamed.pk";

    /// <summary>
    /// Gets or sets the PPV.to streams JSON URL.
    /// </summary>
    public string PpvStreamsUrl { get; set; } = "https://old.ppv.to/api/streams";

    /// <summary>
    /// Gets or sets the CDN Live channels list URL.
    /// </summary>
    public string CdnChannelsUrl { get; set; } = "https://api.cdn-live.tv/api/v1/channels/?user=cdnlivetv&plan=free";

    /// <summary>
    /// Gets or sets the CDN Live sports events URL.
    /// </summary>
    public string CdnSportsUrl { get; set; } = "https://api.cdn-live.tv/api/v1/events/sports/?user=cdnlivetv&plan=free";

    /// <summary>
    /// Gets or sets a value indicating whether to try resolving embed URLs to direct HLS/DASH links before playback.
    /// </summary>
    public bool TryResolveEmbedToDirectStream { get; set; } = true;
}
