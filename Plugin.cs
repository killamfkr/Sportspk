using Jellyfin.Plugin.StreamedPk.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.StreamedPk;

/// <summary>
/// Streamed.pk sports channel plugin.
/// </summary>
public class Plugin : BasePlugin<StreamedPkPluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the running plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Live Matches";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a1f3c8d2-6e4b-4a9f-9c7e-2b8d5e1f3a0c");

    /// <inheritdoc />
    public override string Description =>
        "Sports channels: Streamed.pk schedules, PPV.to categories, and CDN Live — with server-side stream handling and configurable feeds.";

    /// <inheritdoc />
    public override string ConfigurationFileName => "Jellyfin.Plugin.StreamedPk.xml";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html"
        };
    }
}
