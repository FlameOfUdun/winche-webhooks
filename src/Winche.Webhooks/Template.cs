namespace Winche.Webhooks;

/// <summary><c>{name}</c> placeholders in action options.</summary>
public static class Template
{
    private static readonly System.Text.RegularExpressions.Regex Placeholder = new(@"\{([A-Za-z0-9_]+)\}");

    public static IReadOnlyList<string> Missing(string template, IReadOnlySet<string> names) =>
        [.. Placeholder.Matches(template).Select(m => m.Groups[1].Value).Where(n => !names.Contains(n)).Distinct()];

    /// <summary>Fills the placeholders; throws when one has no value.</summary>
    public static string Fill(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder.Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v
            : throw new InvalidOperationException($"There's no value for {{{m.Groups[1].Value}}} in this event."));
}
