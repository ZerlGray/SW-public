using Content.Shared.Imperial.Medieval.Rituals;

namespace Content.Server.Imperial.Medieval.Rituals;

public sealed class MedievalRitualContext
{
    public required EntityUid Caster;
    public required EntityUid Center;
    public required EntityUid Target;
    public required MedievalRitualPrototype Ritual;
    public required IReadOnlyList<EntityUid> Participants;
    public required IReadOnlyList<EntityUid> Offerings;
    public IReadOnlyList<EntityUid>? StationaryTargets;
    /// <summary>Real carriers used by the effect but not sacrificed, e.g. blank document sheets.</summary>
    public readonly HashSet<EntityUid> ReservedObjects = new();
    /// <summary>Effect-specific validated plans, discarded on cancellation. No writes to world in preflight.</summary>
    public readonly Dictionary<string, object> Data = new();
}

/// <summary>Raised at the beginning and again immediately before consuming reserved offerings.</summary>
public sealed class MedievalRitualValidateEvent(MedievalRitualContext context) : EntityEventArgs
{
    public MedievalRitualContext Context = context;
    public string? Error;
    public bool Handled;
}

/// <summary>Raised once, after final validation. All consumed entities still exist until this returns.</summary>
public sealed class MedievalRitualExecuteEvent(MedievalRitualContext context) : EntityEventArgs
{
    public MedievalRitualContext Context = context;
}

[RegisterComponent]
public sealed partial class MedievalRitualAttemptComponent : Component
{
    public MedievalRitualContext Context = default!;
    public Dictionary<EntityUid, int> Reserved = new();
    public List<(EntityUid Holder, EntityUid Solution, string Reagent, Content.Shared.FixedPoint.FixedPoint2 Amount)> Liquids = new();
    public Dictionary<EntityUid, Robust.Shared.Map.MapCoordinates> Positions = new();
    public bool Invalid;
    public Content.Shared.DoAfter.DoAfterId? DoAfter;
}

[RegisterComponent]
public sealed partial class MedievalRitualOfferingReservedComponent : Component
{
    public EntityUid Center;
}
