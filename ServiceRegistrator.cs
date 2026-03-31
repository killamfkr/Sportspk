using System.Net;
using System.Net.Http;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.StreamedPk;

/// <summary>
/// Registers HTTP client and channel services.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(StreamedPkHttpClients.Api, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin.Plugin.StreamedPk/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        serviceCollection.AddHttpClient(StreamedPkHttpClients.EmbedResolver)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All
            })
            .ConfigureHttpClient(client =>
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
                client.Timeout = TimeSpan.FromSeconds(25);
            });

        serviceCollection.AddSingleton<StreamedPkClient>();
        serviceCollection.AddSingleton<PlayTorrioFeedsClient>();
        serviceCollection.AddSingleton<EmbedPageStreamResolver>();
        serviceCollection.AddSingleton<IChannel, StreamedPkChannel>();
    }
}
