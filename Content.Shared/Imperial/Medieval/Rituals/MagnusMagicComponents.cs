using Content.Shared.Actions;
using Robust.Shared.GameStates;

namespace Content.Shared.Imperial.Medieval.Rituals;

public sealed partial class MagnusThunderActionEvent : WorldTargetActionEvent;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class MagnusPhasedComponent : Component
{
    [DataField, AutoNetworkedField] public bool Active;
    public TimeSpan Until;
    public bool OriginalCollision;
    public bool OriginalOcclusion;
}
