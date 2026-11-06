namespace OpsFlow.Application.Outbox;

/// <summary>
/// Asks the outbox worker to dispatch now instead of waiting for its next scheduled run. Call it only AFTER the transaction that staged the
/// events has committed; the schedule remains the fallback, so a lost signal only delays delivery.
/// </summary>
public interface IOutboxSignal
{
    void Notify();
}

/// <summary>Used when no worker runs in this process (tests, tooling, disabled background server).</summary>
internal sealed class NoOutboxSignal : IOutboxSignal
{
    public void Notify() { }
}
