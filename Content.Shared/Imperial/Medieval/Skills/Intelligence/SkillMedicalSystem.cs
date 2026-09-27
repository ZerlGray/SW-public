using Content.Shared.DoAfter;
using Content.Shared.Imperial.Medieval.Medical;
using Content.Shared.Medical.Healing;
using Robust.Shared.Prototypes;

namespace Content.Shared.Imperial.Medieval.Skills;

public sealed class SkillMedicalSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly SkillWorkbenchSystem _workbenches = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<SkillIntelligenceComponent, BeforeDoAfterStartEvent>(OnDoAfter);
        SubscribeLocalEvent<SkillsComponent, MedicalTreatmentAttemptEvent>(OnMedicalTreatment);
        SubscribeLocalEvent<SkillsComponent, GetHealingSpeedModifiersEvent>(OnHealingSpeed);
        SubscribeLocalEvent<SkillsComponent, GetMedicalHealingMultiplierEvent>(OnHealingPower);
    }

    private float Modifier(SkillsComponent skills, string key) =>
        SkillScaling.Multiplier(SkillScaling.Level(skills, SharedSkillsSystem.IntelligenceId),
            _prototypes.Index<SkillPrototype>(SharedSkillsSystem.IntelligenceId).Modifiers[key]);

    private void OnDoAfter(EntityUid uid, SkillIntelligenceComponent state, ref BeforeDoAfterStartEvent args)
    {
        if (args.Args.Target is { } target && !_workbenches.CanUse(uid, target))
            args.Cancelled = true;
    }

    private void OnMedicalTreatment(EntityUid uid, SkillsComponent skills, ref MedicalTreatmentAttemptEvent args)
    {
        if (SkillScaling.Level(skills, SharedSkillsSystem.IntelligenceId) >= SkillScaling.Basic)
            return;
        args.Cancelled = true;
        args.Reason = "skills-require-intelligence-4";
    }

    private void OnHealingSpeed(EntityUid uid, SkillsComponent skills, ref GetHealingSpeedModifiersEvent args) =>
        args.Modifier /= Modifier(skills, "HealingSpeedPerLevel");

    private void OnHealingPower(EntityUid uid, SkillsComponent skills, ref GetMedicalHealingMultiplierEvent args) =>
        args.Multiplier *= Modifier(skills, "MedicalPowerPerLevel");
}
