using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Winche.Core;

namespace Winche.Webhooks;

/// <summary>The outcome of one attempt.</summary>
public sealed record HookAttempt(bool Ok, bool Retry, int? Status, string Result, int DurationMs);

/// <summary>Sends one delivery: POSTs a webhook (signed) or runs an action.</summary>
public sealed class HookRunner(IServiceProvider services, IHttpClientFactory http, HookCatalog catalog, ILogger<HookRunner> logger)
{
    public const string HttpClientName = "winche-webhooks";
    public static string SecretName(string hookId) => $"hooks.{hookId}.signingSecret";

    public async Task<HookAttempt> RunAsync(HookSettings hook, JsonObject envelope, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            return hook.Do.Kind == "webhook" ? await PostAsync(hook, envelope, watch, ct) : await ActAsync(hook, envelope, watch, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A function's trigger (fn.*) logs its own failure, in its words; a webhook says which one and why.
            if (hook.Id.StartsWith("fn.", StringComparison.Ordinal)) logger.LogDebug(ex, "Trigger {Hook} failed.", hook.Id);
            else logger.LogWarning(ex, "Webhook {Name} failed: {Reason}", string.IsNullOrWhiteSpace(hook.Name) ? hook.Id : hook.Name, ex.GetBaseException().Message);
            return new HookAttempt(false, true, null, ex.GetBaseException().Message, (int)watch.ElapsedMilliseconds);
        }
    }

    private async Task<HookAttempt> PostAsync(HookSettings hook, JsonObject envelope, Stopwatch watch, CancellationToken ct)
    {
        var secret = services.GetService<SecretStore>() is { Available: true } s ? await s.GetAsync(SecretName(hook.Id), ct) : null;
        var body = envelope.ToJsonString();
        using var request = new HttpRequestMessage(HttpMethod.Post, hook.Do.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Winche-Event", $"{envelope["source"]}.{envelope["type"]}");
        request.Headers.Add("Winche-Delivery", envelope["id"]!.GetValue<string>());
        if (secret is not null) request.Headers.Add(WebhookSignature.Header, WebhookSignature.Sign(secret, body, DateTimeOffset.UtcNow));
        HttpResponseMessage response;
        try { response = await http.CreateClient(HttpClientName).SendAsync(request, ct); }
        catch (HttpRequestException ex) { return new HookAttempt(false, true, null, $"Couldn't reach {request.RequestUri?.Host}: {ex.Message}", (int)watch.ElapsedMilliseconds); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new HookAttempt(false, true, null, "No answer within 15 seconds.", (int)watch.ElapsedMilliseconds); }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            var status = (int)response.StatusCode;
            var snippet = text.Length > 300 ? text[..300] : text;
            if (response.IsSuccessStatusCode) return new HookAttempt(true, false, status, snippet, (int)watch.ElapsedMilliseconds);
            // Worth trying again: the receiver's trouble or a rate limit. Other refusals won't change by themselves.
            var retry = status >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
            return new HookAttempt(false, retry, status, snippet, (int)watch.ElapsedMilliseconds);
        }
    }

    private async Task<HookAttempt> ActAsync(HookSettings hook, JsonObject envelope, Stopwatch watch, CancellationToken ct)
    {
        var action = catalog.Actions.FirstOrDefault(a => a.Name == hook.Do.Kind)
            ?? throw new InvalidOperationException($"This app has no action '{hook.Do.Kind}'.");
        var e = HookEvents.FromEnvelope(envelope);
        var options = hook.Do.Options.ToDictionary(kv => kv.Key, kv => Template.Fill(kv.Value, e.Variables));
        await using var scope = services.CreateAsyncScope();
        var result = await action.RunAsync(new WincheActionContext(e, options, scope.ServiceProvider), ct);
        return new HookAttempt(true, false, null, result, (int)watch.ElapsedMilliseconds);
    }
}

/// <summary>
/// Sends due deliveries on every server (each row claimed by one), retrying failures with growing gaps — 30 s, 2 min,
/// 10 min, 30 min, 1 h, 3 h, 6 h — then giving up (failed; retry from the console).
/// </summary>
internal sealed class HookDeliveryWorker(HookOutbox outbox, HookRunner runner, HookEvents events, TimeProvider time, WincheHooksOptions options,
    ILogger<HookDeliveryWorker> logger, IServiceProvider services) : BackgroundService
{
    private TimeSpan Tick => options.PollInterval;
    private TimeSpan[] Backoff => options.RetryDelays;
    private DateTimeOffset _pruned = DateTimeOffset.MinValue;

    public override async Task StartAsync(CancellationToken ct)
    {
        await outbox.InitializeAsync(ct);
        await DropActionHooksAsync(ct);
        await base.StartAsync(ct);
    }

    /// <summary>
    /// Hooks saved before with a ready-made action (delete documents, delete files, copy claims) are dropped, as a new
    /// version saying why: those actions are gone, and functions with a trigger do that now. Safe to repeat.
    /// </summary>
    private async Task DropActionHooksAsync(CancellationToken ct)
    {
        var area = services.GetService(typeof(Winche.Core.SettingsArea<HooksSettings>)) as Winche.Core.SettingsArea<HooksSettings>;
        if (area is null || services.GetService(typeof(Winche.Core.SettingsStore)) is not Winche.Core.SettingsStore store) return;
        var gone = area.Current.Hooks.Where(h => h.Do.Kind != HookActionSettings.Webhook).Select(h => h.Name).ToList();
        if (gone.Count == 0) return;
        try
        {
            await store.SaveAsync(HooksSettings.Area, System.Text.Json.JsonSerializer.SerializeToNode(area.Current.WithoutActions(), Winche.Core.SettingsArea.Json)!,
                "winche", $"Removed hooks with ready-made actions (write a function with their trigger instead): {string.Join(", ", gone)}", ct);
            logger.LogWarning("Removed {Count} hook(s) that ran a ready-made action: {Hooks}. Write a function with their trigger instead.", gone.Count, string.Join(", ", gone));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Couldn't remove the hooks with ready-made actions; they don't fire."); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = await outbox.ClaimAsync(20, time.GetUtcNow(), stoppingToken, options.ClaimLease);
                await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = stoppingToken }, (d, ct) => new ValueTask(SendAsync(d, ct)));
                if (time.GetUtcNow() - _pruned > TimeSpan.FromHours(1))
                {
                    await outbox.PruneAsync(stoppingToken);
                    _pruned = time.GetUtcNow();
                }
                if (batch.Count == 20) continue;   // more waiting
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Sending hook deliveries failed; trying again shortly."); }
            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SendAsync(HookDelivery d, CancellationToken ct)
    {
        var attempts = d.Attempts + 1;
        var now = time.GetUtcNow();
        if (events.All.FirstOrDefault(h => h.Id == d.HookId) is not { } hook)
        {
            // Just saved (or deployed) on another server, and this one hasn't caught up: back in the queue for a moment,
            // not an attempt. Only an old delivery's hook is really gone.
            if (now - d.CreatedAt < TimeSpan.FromMinutes(1))
                await outbox.RecordAsync(d.Id, HookOutbox.Pending, d.Attempts, now + TimeSpan.FromSeconds(2), null, d.LastStatus, d.LastResult, d.DurationMs ?? 0, ct);
            else await outbox.RecordAsync(d.Id, HookOutbox.Failed, attempts, null, now, null, "The hook was removed.", 0, ct);
            return;
        }
        if (!hook.Enabled)
        {
            await outbox.RecordAsync(d.Id, HookOutbox.Failed, attempts, null, now, null, "The hook is turned off.", 0, ct);
            return;
        }
        // A long run (a function) keeps its claim: renewed every third of the lease while it runs.
        using var renewing = new CancellationTokenSource();
        var renew = Task.Run(async () =>
        {
            var every = options.ClaimLease / 3;
            while (!renewing.IsCancellationRequested)
            {
                try { await Task.Delay(every, time, renewing.Token); } catch (OperationCanceledException) { return; }
                try { await outbox.RenewAsync(d.Id, time.GetUtcNow() + options.ClaimLease, CancellationToken.None); }
                catch (Exception ex) { logger.LogWarning(ex, "Couldn't keep the claim on delivery {Id}.", d.Id); }
            }
        }, CancellationToken.None);
        HookAttempt result;
        try { result = await runner.RunAsync(hook, d.Event, ct); }
        finally { renewing.Cancel(); await renew; }
        now = time.GetUtcNow();
        if (result.Ok) await outbox.RecordAsync(d.Id, HookOutbox.Delivered, attempts, null, now, result.Status, result.Result, result.DurationMs, ct);
        else if (result.Retry && attempts <= Backoff.Length)
            await outbox.RecordAsync(d.Id, HookOutbox.Pending, attempts, now + Backoff[attempts - 1], null, result.Status, result.Result, result.DurationMs, ct);
        else await outbox.RecordAsync(d.Id, HookOutbox.Failed, attempts, null, now, result.Status, result.Result, result.DurationMs, ct);
    }
}
