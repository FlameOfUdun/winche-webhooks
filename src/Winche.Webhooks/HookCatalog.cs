using System.Text.Json.Nodes;

namespace Winche.Webhooks;

/// <summary>
/// Hooks that come from code rather than the console (e.g. Winche.Functions' triggers, one per deployed function).
/// They're matched and delivered like the console's — queue, retries, delivery log — but aren't edited there.
/// </summary>
public interface IHookProvider
{
    IReadOnlyList<HookSettings> Hooks { get; }
}

/// <summary>The change sources and actions this app has (each package registers its own).</summary>
public sealed class HookCatalog(IEnumerable<WincheChangeSource> sources, IEnumerable<IWincheAction> actions)
{
    public IReadOnlyList<WincheChangeSource> Sources { get; } = [.. sources];
    public IReadOnlyList<IWincheAction> Actions { get; } = [.. actions];
}
