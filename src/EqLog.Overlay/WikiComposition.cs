using System.Net.Http;
using EqLog.Application.Wiki;
using EqLog.Core;
using EqLog.Infrastructure.Persistence;
using EqLog.Infrastructure.Wiki;

namespace EqLog.Overlay;

internal static class WikiComposition
{
    private static readonly WikiCacheRepository Caches = new(SettingsComposition.Paths.Folder,
        mobRepository: TrackingComposition.MobRepository);
    public static Task<WikiStartup> LoadAsync()
    {
        return new WikiStartupService(Caches).LoadAsync();
    }
    public static WikiService CreateService(GameSession session)
    {
        Caches.ProtectMobWrites(session.MobsWritable);
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EqLogOverlay/1.0");
        return session.CreateWikiService(new WikiTransport(http, ownsHttp: true), Caches);
    }
}
