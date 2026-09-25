using System.Linq;
using Content.Server.Imperial.Medieval.Knowledge;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Examine;
using Content.Shared.Imperial.Medieval.Rituals;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Nutrition.Components;
using Content.Shared.Verbs;

namespace Content.Server.Imperial.Medieval.Rituals;

/// <summary>Players explicitly dedicate a real object, so ordinary ritual fees do not enable extra effects.</summary>
public sealed class MedievalOfferingPreparationSystem : EntitySystem
{
    [Dependency] private readonly MedievalKnowledgeSystem _knowledge = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private readonly MedievalZaygoSystem _zaygo = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<ItemComponent, GetVerbsEvent<AlternativeVerb>>(OnVerbs);
        SubscribeLocalEvent<RitualOfferingComponent, ExaminedEvent>(OnExamine);
    }

    private void OnExamine(EntityUid uid, RitualOfferingComponent component, ExaminedEvent args) =>
        args.PushMarkup(Loc.GetString("medieval-offering-dedicated", ("kind", Loc.GetString("medieval-offering-" + component.Kind))));

    /// <summary>Rechecked at reservation and completion: dedicating a flask does not preserve drained blood.</summary>
    public bool IsValidOffering(EntityUid uid, string kind) => Exists(uid) &&
        (kind == "mask" ? HasComp<ZaygoCapturedIdentityComponent>(uid) : Allowed(uid).Contains(kind));

    private void OnVerbs(EntityUid uid, ItemComponent component, GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess || HasComp<MedievalRitualOfferingReservedComponent>(uid)) return;
        _zaygo.AddPreparationVerbs(uid, component, args);
        if (HasComp<RitualOfferingComponent>(uid) && !HasComp<ZaygoWaxMaskComponent>(uid))
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("medieval-offering-clear"), Act = () =>
            {
                if (Exists(uid) && !HasComp<MedievalRitualOfferingReservedComponent>(uid) && _interaction.InRangeUnobstructed(args.User, uid))
                    RemComp<RitualOfferingComponent>(uid);
            }});
        var magnus = Enumerable.Range(2, 3).Any(t => _knowledge.HasKnowledge(args.User, "RitualMagnusCreation" + t));
        var zaygo = _knowledge.HasKnowledge(args.User, "RitualZaygoPlace4");
        foreach (var kind in Allowed(uid))
        {
            var forMagnus = MedievalMagnusSystem.GiftKinds.Contains(kind);
            if (forMagnus ? !magnus : !zaygo) continue;
            var selected = kind;
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("medieval-offering-prepare", ("kind", Loc.GetString("medieval-offering-" + kind))), Act = () =>
            {
                if (!Exists(uid) || HasComp<MedievalRitualOfferingReservedComponent>(uid) || !_interaction.InRangeUnobstructed(args.User, uid) || !Allowed(uid).Contains(selected)) return;
                if (forMagnus ? !Enumerable.Range(2, 3).Any(t => _knowledge.HasKnowledge(args.User, "RitualMagnusCreation" + t)) : !_knowledge.HasKnowledge(args.User, "RitualZaygoPlace4")) return;
                EnsureComp<RitualOfferingComponent>(uid).Kind = selected;
            }});
        }
    }

    private IEnumerable<string> Allowed(EntityUid uid)
    {
        var id = MetaData(uid).EntityPrototype?.ID ?? "";
        bool Is(string word) => id.Contains(word, StringComparison.OrdinalIgnoreCase);
        if (Is("Cloth") || Is("Gauze") || Is("Rope")) { yield return "recall"; yield return "vessels"; yield return "mist"; }
        if (Is("Hammer")) yield return "thunder";
        if (Is("Key")) yield return "phase";
        if (HasComp<FoodComponent>(uid)) { yield return "animation"; yield return "liquidLife"; yield return "beasts"; }
        if (Is("Stone") || Is("Rock")) { yield return "liquidWall"; yield return "structures"; }
        if (Is("Wood") && Is("Plank")) yield return "furniture";
        if (Is("Revent")) yield return "goods";
        if (_solutions.TryGetDrainableSolution(uid, out _, out var solution) &&
            solution.Contents.Where(entry => entry.Reagent.Data?.OfType<SapientBloodData>().Any() == true)
                .Sum(entry => entry.Quantity.Float()) >= 5)
            yield return "people";
    }
}
