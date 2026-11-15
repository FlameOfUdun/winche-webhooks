using System.Text.Json.Nodes;
using Winche.Core;

namespace Winche.Webhooks;

/// <summary>
/// Where adapters report events: each enabled hook that matches gets a delivery in the outbox, carrying the event and
/// the variables (the event's plus the hook's path captures).
/// </summary>
public sealed class HookEvents(SettingsArea<HooksSettings> area, HookOutbox outbox, IEnumerable<IHookProvider>? providers = null)
    : IWincheChangeSink
{
    /// <summary>Changes published by database, storage and auth: queued for every matching hook.</summary>
    public Task OnChangesAsync(IReadOnlyList<WincheChange> changes, CancellationToken ct) => RaiseAsync(changes, ct);

    private readonly IReadOnlyList<IHookProvider> _providers = [.. providers ?? []];

    /// <summary>Every hook in force: the console's and those from code.</summary>
    /// <summary>The console's webhooks (a hook of before with a ready-made action never fires) and the code's hooks.</summary>
    public IEnumerable<HookSettings> All => area.Current.Hooks.Where(h => h.Do.Kind == HookActionSettings.Webhook).Concat(_providers.SelectMany(p => p.Hooks));

    public async Task RaiseAsync(WincheChange e, CancellationToken ct = default) => await RaiseAsync([e], ct);

    public async Task RaiseAsync(IReadOnlyList<WincheChange> events, CancellationToken ct = default)
    {
        var hooks = All.Where(h => h.Enabled).ToList();
        if (hooks.Count == 0) return;
        var items = new List<(HookSettings, JsonObject, WincheChange)>();
        foreach (var e in events)
            foreach (var h in hooks)
                if (Match(h, e) is { } vars) items.Add((h, Envelope(h, e, vars), e));
        await outbox.AddAsync(items, ct);
    }

    /// <summary>The variables when <paramref name="hook"/> wants <paramref name="e"/>; null when it doesn't.</summary>
    public static IReadOnlyDictionary<string, string>? Match(HookSettings hook, WincheChange e)
    {
        if (hook.When.Source != e.Source) return null;
        if (hook.When.Events.Count > 0 && !hook.When.Events.Contains(e.Type)) return null;
        var vars = new Dictionary<string, string>(e.Variables, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(hook.When.Path))
        {
            if (e.Path is null || !PathPattern.TryParse(hook.When.Path, out var pattern, out _) || pattern!.Match(e.Path) is not { } captures) return null;
            foreach (var (k, v) in captures) vars[k] = v;
        }
        return vars;
    }

    /// <summary>The body a webhook receives (and an action reads back).</summary>
    internal static JsonObject Envelope(HookSettings hook, WincheChange e, IReadOnlyDictionary<string, string> vars) => new()
    {
        ["id"] = e.Id,
        ["hook"] = new JsonObject { ["id"] = hook.Id, ["name"] = hook.Name },
        ["source"] = e.Source,
        ["type"] = e.Type,
        ["path"] = e.Path,
        ["at"] = e.At.ToString("O"),
        ["vars"] = new JsonObject(vars.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
        ["data"] = e.Data?.DeepClone(),
        ["before"] = e.Before?.DeepClone(),
    };

    internal static WincheChange FromEnvelope(JsonObject o) => new(
        o["source"]!.GetValue<string>(), o["type"]!.GetValue<string>(), o["path"]?.GetValue<string>(), DateTimeOffset.Parse(o["at"]!.GetValue<string>()),
        o["data"] as JsonObject, o["before"] as JsonObject,
        (o["vars"] as JsonObject ?? []).ToDictionary(kv => kv.Key, kv => kv.Value?.GetValue<string>() ?? "", StringComparer.Ordinal))
    { Id = o["id"]!.GetValue<string>() };
}
