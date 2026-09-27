using Content.Shared.UserInterface;
using Robust.Shared.GameStates;

namespace Content.Shared.Imperial.Medieval.Skills;

[RegisterComponent, NetworkedComponent]
public sealed partial class SkillWorkbenchComponent : Component;

public sealed class SkillWorkbenchSystem : EntitySystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<SkillWorkbenchComponent, ActivatableUIOpenAttemptEvent>(OnOpen);
        SubscribeLocalEvent<SkillsComponent, Content.Shared.Construction.CraftingAttemptEvent>(OnCraft);
    }

    public bool CanUse(EntityUid user, EntityUid workstation) => !HasComp<SkillWorkbenchComponent>(workstation)
        || !TryComp<SkillsComponent>(user, out var skills) || SkillScaling.Level(skills, SharedSkillsSystem.IntelligenceId) >= SkillScaling.Trained;

    private void OnCraft(EntityUid uid, SkillsComponent skills, ref Content.Shared.Construction.CraftingAttemptEvent args) =>
        args.Cancelled |= !CanUse(uid, args.Station);

    private void OnOpen(EntityUid uid, SkillWorkbenchComponent workbench, ActivatableUIOpenAttemptEvent args)
    {
        if (!CanUse(args.User, uid))
            args.Cancel();
    }

}
