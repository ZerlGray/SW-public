using Content.Shared.Actions;
using Content.Shared.DoAfter;
using Robust.Shared.Serialization;
using Robust.Shared.GameStates;

namespace Content.Shared.Imperial.Medieval.Rituals;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class MedievalRitualCenterComponent : Component
{
    [DataField, AutoNetworkedField] public new EntityUid? Owner;
    [DataField, AutoNetworkedField] public EntityUid? Target;
    [DataField] public EntityUid? LinkedCenter;
    [DataField] public HashSet<EntityUid> Participants = new();
    [DataField, AutoNetworkedField] public float PreviewRadius = 3;
    [DataField, AutoNetworkedField] public bool PreviewTheft;
}

public sealed partial class MedievalDrawRitualActionEvent : WorldTargetActionEvent;

public sealed partial class MedievalUseBlessingActionEvent : EntityTargetActionEvent
{
    [DataField] public string Kind = string.Empty;
}

public sealed partial class MedievalActivateBlessingActionEvent : InstantActionEvent
{
    [DataField] public string Kind = string.Empty;
}

[Serializable, NetSerializable]
public sealed partial class MedievalPrayerDoAfterEvent : SimpleDoAfterEvent;

[Serializable, NetSerializable]
public sealed partial class MedievalBlessingDoAfterEvent : SimpleDoAfterEvent
{
    [DataField] public string Kind = string.Empty;
}

/// <summary>Only exempts exhaustion; stun, sleep, restraints and actual damage remain independent.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class MedievalValtorEnduranceComponent : Component
{
    [DataField, AutoNetworkedField] public TimeSpan Until;
}

[RegisterComponent]
public sealed partial class RitualOfferingComponent : Component
{
    [DataField] public string Kind = string.Empty;
}

/// <summary>Shared hook used before the ordinary stamina gate.</summary>
[ByRefEvent]
public record struct MedievalExhaustionCheckEvent(bool IgnoreExhaustion);
