using Winche.Core;

namespace Winche.Webhooks;

/// <summary>Hooks' setup for hosts that set products up on demand: the delivery log (every product with hooks needs it).</summary>
internal sealed class HooksProductSetup(HookOutbox outbox) : IWincheProductSetup
{
    public string Product => WincheProductNames.Hooks;

    public IReadOnlyList<string> Steps { get; } = ["Create the hook delivery log"];

    public Task RunStepAsync(int step, CancellationToken ct) =>
        step == 0 ? outbox.InitializeAsync(ct) : throw new ArgumentOutOfRangeException(nameof(step));
}
