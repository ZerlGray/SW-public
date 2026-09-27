using Content.Shared.Damage.Components;
using Content.Shared.Imperial.Medieval.Magic.Mana;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.Shared.Imperial.Medieval.Skills;

/// <summary>Recalculates resource capacity without restoring spent resources.</summary>
public sealed class SkillManaSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly INetManager _net = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<SkillProfileChangedEvent>(OnChanged);
        SubscribeLocalEvent<ManaComponent, ManaInitializedEvent>(OnManaStartup);
    }

    private void OnChanged(ref SkillProfileChangedEvent args)
    {
        var uid = args.Uid;
        EnsureComp<SkillIntelligenceComponent>(uid);
        if (_net.IsClient || !TryComp<SkillsComponent>(uid, out var skills))
            return;
        if (TryComp<ManaComponent>(uid, out var mana))
            UpdateMana(uid, skills, mana);
    }

    private void OnManaStartup(EntityUid uid, ManaComponent mana, ref ManaInitializedEvent args)
    {
        if (!_net.IsServer || !TryComp<SkillsComponent>(uid, out var skills))
            return;
        // A replacement mana component starts with its own unmodified capacities.
        var state = EnsureComp<SkillIntelligenceComponent>(uid);
        state.ManaMaximumMultiplier = 1f;
        state.ManaRegenerationMultiplier = 1f;
        UpdateMana(uid, skills, mana);
    }

    private void UpdateMana(EntityUid uid, SkillsComponent skills, ManaComponent mana)
    {
        var state = EnsureComp<SkillIntelligenceComponent>(uid);
        var proto = _prototypes.Index<SkillPrototype>(SharedSkillsSystem.IntelligenceId);
        var level = SkillScaling.Level(skills, SharedSkillsSystem.IntelligenceId);
        var maximum = SkillScaling.Multiplier(level, proto.Modifiers["ManaPerLevel"]);
        var regeneration = SkillScaling.Multiplier(level, proto.Modifiers["ManaRegenPerLevel"]);
        mana.MaxMana = mana.MaxMana / state.ManaMaximumMultiplier * maximum;
        mana.Regen = mana.Regen / state.ManaRegenerationMultiplier * regeneration;
        state.ManaMaximumMultiplier = maximum;
        state.ManaRegenerationMultiplier = regeneration;
        mana.Mana = Math.Min(mana.Mana, mana.MaxMana);
        Dirty(uid, mana);
    }
}
