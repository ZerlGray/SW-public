namespace Content.Server.Imperial.Medieval.Trading;

/// <summary>Includes mutable item-specific properties in a market lot's identity.</summary>
public sealed class TradingItemIdentityEvent : EntityEventArgs
{
    public bool ForceUnique;
    public readonly List<string> Parts = new();
}
