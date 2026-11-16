using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Velocity.Data.Database;
using Velocity.Data.Repositories;
using Velocity.DownloadEngine;
using Velocity.Core.Interfaces;

namespace Velocity.Services;

public static class ApplicationBootstrapper
{
    public static ServiceProvider BuildServiceProvider(string databasePath)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton(new DownloadRepository(databasePath));
        services.AddSingleton(new SettingsRepository(databasePath));
        services.AddSingleton<SettingsService>();
        services.AddHttpClient<IDownloadEngine, HttpDownloadEngine>(client =>
        {
            // Large files may legitimately take hours. The engine cancels a transfer when the server goes
            // silent for 45 seconds, so there is no fixed ceiling on the whole request.
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Velocity/1.0 (Windows download manager)");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            // Turning a VPN on, off or onto another server closes every routed connection at once. A short
            // pooled lifetime means the next request — a retry, a resume, a new segment — opens a socket to
            // the new route instead of waiting for a dead one to time out, so a switch never stops a download
            // or a browser hand-off.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        });
        services.AddSingleton<DownloadManager>();
        services.AddSingleton<SchedulerService>();
        return services.BuildServiceProvider();
    }
}
