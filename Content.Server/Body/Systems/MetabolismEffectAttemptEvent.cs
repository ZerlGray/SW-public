using Content.Shared.EntityEffects;
using Content.Shared.FixedPoint;

namespace Content.Server.Body.Systems;

[ByRefEvent]
public record struct MetabolismEffectAttemptEvent(string Reagent, string Group,
    EntityEffect Effect, FixedPoint2 Amount, bool Cancelled = false);
