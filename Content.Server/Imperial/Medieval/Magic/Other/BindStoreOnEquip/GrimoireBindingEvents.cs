namespace Content.Server.Imperial.Medieval.Magic.BindStoreOnEquip;

/// <summary>
/// Raised on the prospective owner before binding, and broadcast so rules can also reject owners
/// that lack their components. Subscribers may cancel; the grimoire system owns the binding itself.
/// </summary>
public sealed class GrimoireBindAttemptEvent(EntityUid owner, EntityUid grimoire, bool startingGrimoire)
    : CancellableEntityEventArgs
{
    public readonly EntityUid Owner = owner;
    public readonly EntityUid Grimoire = grimoire;
    public readonly bool StartingGrimoire = startingGrimoire;
}

/// <summary>Raised on the owner after a grimoire is bound, restored or upgraded.</summary>
public sealed class GrimoireBoundEvent(EntityUid grimoire) : EntityEventArgs
{
    public readonly EntityUid Grimoire = grimoire;
}
