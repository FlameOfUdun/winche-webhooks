namespace Winche.Webhooks;

/// <summary>The settings area <c>hooks</c>: what happens when documents, files or users change. Set up in the console.</summary>
public sealed record HooksSettings
{
    public const string Area = "hooks";

    public IReadOnlyList<HookSettings> Hooks { get; init; } = [];

    /// <summary>What's wrong, in sentences; <paramref name="catalog"/> knows the events and actions this app has.</summary>
    public IReadOnlyList<string> Problems(HookCatalog catalog)
    {
        var problems = new List<string>();
        var ids = new HashSet<string>();
        foreach (var h in Hooks)
        {
            var label = string.IsNullOrWhiteSpace(h.Name) ? $"Hook {h.Id}" : $"'{h.Name}'";
            if (string.IsNullOrWhiteSpace(h.Id) || h.Id.Length > 40 || h.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) problems.Add($"{label} needs an id (letters, digits, -).");
            else if (!ids.Add(h.Id)) problems.Add($"Two hooks have the id {h.Id}.");
            if (string.IsNullOrWhiteSpace(h.Name)) problems.Add($"{label} needs a name.");
            if (catalog.Sources.FirstOrDefault(s => s.Name == h.When.Source) is not { } source)
            {
                problems.Add($"{label}: this app has no '{h.When.Source}' events (add Winche.Webhooks.{char.ToUpperInvariant(h.When.Source.FirstOrDefault(' '))}{(h.When.Source.Length > 1 ? h.When.Source[1..] : "")}).");
                continue;
            }
            foreach (var e in h.When.Events.Where(e => !source.Events.Contains(e))) problems.Add($"{label}: '{e}' isn't a {source.Name} event ({string.Join(", ", source.Events)}).");
            PathPattern? pattern = null;
            if (!string.IsNullOrWhiteSpace(h.When.Path))
            {
                if (!source.HasPaths) problems.Add($"{label}: {source.Name} events have no path to match.");
                else if (!PathPattern.TryParse(h.When.Path, out pattern, out var error)) problems.Add($"{label}: {error}");
            }
            var vars = new HashSet<string>(source.Variables, StringComparer.Ordinal);
            if (pattern is not null) vars.UnionWith(pattern.Names);
            if (h.Do.Kind == HookActionSettings.Webhook)
            {
                if (!Uri.TryCreate(h.Do.Url, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http"))
                    problems.Add($"{label}: the address must be a full http(s) address.");
            }
            else
                problems.Add($"{label}: hooks call webhooks. To do something else when this happens, write a function with this trigger (Functions).");
        }
        return problems;
    }

    /// <summary>These hooks without those of before that ran a ready-made action (the actions are gone: functions do that now).</summary>
    public HooksSettings WithoutActions() => this with { Hooks = [.. Hooks.Where(h => h.Do.Kind == HookActionSettings.Webhook)] };
}

/// <summary>When <see cref="When"/> happens, do <see cref="Do"/>.</summary>
public sealed record HookSettings
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public HookTrigger When { get; init; } = new();
    public HookActionSettings Do { get; init; } = new();
}

public sealed record HookTrigger
{
    /// <summary>database, storage or auth.</summary>
    public string Source { get; init; } = "database";
    /// <summary>Which events (created, updated, deleted, …); all of the source's when empty.</summary>
    public IReadOnlyList<string> Events { get; init; } = [];
    /// <summary>Which paths, e.g. <c>patients/{patientId}</c> or <c>uploads/{rest=**}</c>; every path when empty.</summary>
    public string? Path { get; init; }
}

public sealed record HookActionSettings
{
    public const string Webhook = "webhook";

    /// <summary>
    /// "webhook" for the hooks saved in the console; hooks from code (<see cref="IHookProvider"/>, e.g. a function's
    /// trigger) may name an action instead (functions.run).
    /// </summary>
    public string Kind { get; init; } = Webhook;
    /// <summary>For webhooks: the address to POST to. Its signing secret is kept apart (hooks.{id}.signingSecret).</summary>
    public string? Url { get; init; }
    /// <summary>For actions: their options; <c>{name}</c> is filled from the path pattern and the event.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
}
