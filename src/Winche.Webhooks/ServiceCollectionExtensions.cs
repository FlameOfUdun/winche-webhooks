using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Winche.Webhooks;

public sealed class WincheHooksOptions
{
    /// <summary>The project's database: the delivery queue lives there.</summary>
    public string ConnectionString { get; set; } = "";
    /// <summary>How long a webhook may take to answer (default 15 seconds).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a claimed delivery stays with the server that took it (default 5 minutes). It's renewed while the
    /// delivery runs, however long that is; if the server dies, another takes it once the claim runs out.
    /// </summary>
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often each server looks for deliveries that are due (default 1 second).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The waits before each retry of a failed delivery (default 30 s, 2 min, 10 min, 30 min, 1 h, 3 h, 6 h); after the
    /// last it's failed, to retry from the console.
    /// </summary>
    public TimeSpan[] RetryDelays { get; set; } =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(3), TimeSpan.FromHours(6)];
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds webhooks set up in the console: when documents, files or users change, call an address (signed, retried,
    /// logged). Anything else is a function with that trigger (Winche.Functions, whose triggers are delivered here too). The packages publish their changes to it — <c>o.UseHooks(h =&gt; h.PublishChanges())</c> in AddWincheDatabase
    /// and AddWincheStorage; Winche Auth on its own. Needs <c>AddWincheConfig(…)</c> for the hooks themselves and the
    /// webhooks' signing secrets.
    /// </summary>
    public static IServiceCollection AddWincheHooks(this IServiceCollection services, Action<WincheHooksOptions> configure)
    {
        var options = new WincheHooksOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString)) throw new InvalidOperationException("Winche.Webhooks: set ConnectionString.");
        services.AddSingleton(options);
        services.AddNpgsqlDataSource(options.ConnectionString, serviceKey: HookOutbox.DataSourceKey);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new HookCatalog(sp.GetServices<WincheChangeSource>(), sp.GetServices<IWincheAction>()));
        HookCatalog? catalog = null;
        IServiceProvider? root = null;
        Winche.Core.ServiceCollectionExtensions.AddWincheSettingsArea(services, HooksSettings.Area, "Hooks", new HooksSettings(),
            s => s.Problems(catalog ??= root?.GetRequiredService<HookCatalog>() ?? new HookCatalog([], [])));
        services.AddSingleton<HookOutbox>();
        services.AddSingleton(sp => { root = sp; return new HookEvents(sp.GetRequiredService<Winche.Core.SettingsArea<HooksSettings>>(), sp.GetRequiredService<HookOutbox>(), sp.GetServices<IHookProvider>()); });
        services.AddSingleton<IWincheChangeSink>(sp => sp.GetRequiredService<HookEvents>());   // what database, storage and auth publish to
        services.AddSingleton<HookRunner>();
        services.AddHttpClient(HookRunner.HttpClientName, c => c.Timeout = options.Timeout);
        // Deliveries start once hooks are set up (at once, unless the host sets products up on demand).
        Winche.Core.WincheReadyServiceExtensions.AddWincheProductService<HookDeliveryWorker>(services, Winche.Core.WincheProductNames.Hooks);
        services.AddSingleton<Winche.Core.IWincheProductSetup, HooksProductSetup>();
        services.AddHostedService(sp => new CatalogBinder(sp, v => root = v));
        return services;
    }

    /// <summary>Lets settings validation see the catalog (built from the app's services) once the app has started.</summary>
    private sealed class CatalogBinder(IServiceProvider sp, Action<IServiceProvider> bind) : Microsoft.Extensions.Hosting.IHostedService
    {
        public Task StartAsync(CancellationToken ct) { bind(sp); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
