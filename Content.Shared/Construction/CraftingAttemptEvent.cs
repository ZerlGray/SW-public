namespace Content.Shared.Construction;

/// <summary>Raised on the user before queuing or completing work at a crafting station.</summary>
[ByRefEvent]
public record struct CraftingAttemptEvent(EntityUid Station, bool Cancelled = false);
