using System.Linq;
using Content.Server._CP14.Workbench;
using Content.Server.Botany.Components;
using Content.Server.Imperial.Medieval.Knowledge;
using Content.Server.SpikeTrap.Components;
using Content.Shared.Actions;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Cuffs;
using Content.Shared.Cuffs.Components;
using Content.Shared.Damage;
using Content.Shared.Dice;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.GameTicking;
using Content.Shared.Imperial.Medieval.BookAbilities;
using Content.Shared.Imperial.Medieval.Knowledge;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Mobs.Components;
using Content.Shared.Nutrition.Components;
using Content.Shared.Paper;
using Content.Shared.Popups;
using Content.Shared.Storage.Components;
using Content.Shared.Verbs;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.Imperial.Medieval.BookAbilities;

/// <summary>World interactions learned from books. Delayed work keeps server-owned input identities.</summary>
public sealed partial class MedievalBookAbilitySystem : EntitySystem
{
    [Dependency] private readonly MedievalKnowledgeSystem _knowledge = default!;
    [Dependency] private readonly MedievalCompanionSystem _companions = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private readonly SharedCuffableSystem _cuffs = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly PaperSystem _paper = default!;
    [Dependency] private readonly MetaDataSystem _meta = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private sealed record PendingWork(EntityUid User, EntityUid Target, string Knowledge, Func<bool> Validate, Action Complete);
    private readonly Dictionary<string, PendingWork> _pending = new();
    private readonly Dictionary<EntityUid, EntityUid> _loosenedCuffs = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<MetaDataComponent, GetVerbsEvent<AlternativeVerb>>(OnVerbs);
        SubscribeLocalEvent<LearnedKnowledgeComponent, BookAbilityDoAfterEvent>(OnWork);
        SubscribeLocalEvent<LearnedKnowledgeComponent, BookEscapeBondsActionEvent>(OnEscapeBonds);
        SubscribeLocalEvent<LearnedKnowledgeComponent, BookSurveyActionEvent>(OnSurvey);
        InitializeCombat();
        InitializeSpeechAndMagic();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ =>
        {
            _pending.Clear();
            _loosenedCuffs.Clear();
            _voices.Clear();
            _signatures.Clear();
            _ripostes.Clear();
            _guardBreak.Clear();
            _piercingShot.Clear();
        });
    }

    private bool Knows(EntityUid uid, string knowledge) => _knowledge.HasKnowledge(uid, knowledge);

    private void OnVerbs(EntityUid uid, MetaDataComponent meta, GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract) return;
        var user = args.User;
        if (TryComp<BookPackedObjectComponent>(uid, out var packed))
        {
            Add(args, "book-ability-unpack", () => Start(user, uid, packed.Ability, 5,
                () => HasComp<BookPackedObjectComponent>(uid), () => Unpack(user, uid)));
            return;
        }
        if (TryComp<DiceComponent>(uid, out var dice) && Knows(user, "BookDiceCheat") && _hands.IsHolding(user, uid, out _))
        {
            for (var i = 1; i <= dice.Sides; i++)
            {
                var side = i;
                args.Verbs.Add(new AlternativeVerb
                {
                    Text = Loc.GetString("book-ability-loaded-die", ("value", (side - dice.Offset) * dice.Multiplier)),
                    Act = () =>
                    {
                        if (!_hands.IsHolding(user, uid, out _) || !Knows(user, "BookDiceCheat")) return;
                        var loaded = EnsureComp<BookLoadedDiceComponent>(uid);
                        loaded.Side = side;
                        loaded.User = user;
                        Dirty(uid, loaded);
                    }
                });
            }
        }
        if (TryComp<SpikeTrapComponent>(uid, out var trap) && Knows(user, "BookTrapDisarm"))
            Add(args, trap.Enabled ? "book-ability-disarm" : "book-ability-pack-trap", () => Start(user, uid, "BookTrapDisarm", 8,
                () => HasComp<SpikeTrapComponent>(uid), () =>
                {
                    if (trap.Enabled)
                    {
                        trap.Enabled = false;
                        if (trap.ActiveTrapEntity is { } active) QueueDel(active);
                        trap.ActiveTrapEntity = null;
                    }
                    else Pack(user, uid, "BookTrapDisarm");
                }));
        if (HasComp<EntityStorageComponent>(uid) && !Transform(uid).Anchored && Knows(user, "BookPorter"))
            Add(args, "book-ability-pack-crate", () => Start(user, uid, "BookPorter", 6,
                () => !Transform(uid).Anchored && !ContainsPerson(uid), () => Pack(user, uid, "BookPorter")));
        if (CanPackWorkbench(uid) && Knows(user, "BookPackWorkbench"))
            Add(args, "book-ability-pack-workbench", () => Start(user, uid, "BookPackWorkbench", 20,
                () => CanPackWorkbench(uid) && !ContainsPerson(uid), () => Pack(user, uid, "BookPackWorkbench")));
        if (TryComp<PlantHolderComponent>(uid, out var plant) && plant.Seed != null && Knows(user, "BookTransplant"))
            Add(args, "book-ability-transplant", () => Start(user, uid, "BookTransplant", 15,
                () => plant.Seed != null, () => Pack(user, uid, "BookTransplant")));
        if (uid == user && TryComp<CuffableComponent>(uid, out var cuffs) && cuffs.CuffedHandCount > 0 && Knows(user, "BookEscapeBonds"))
            Add(args, "book-ability-loosen-bonds", () => Loosen(user));
        var food = _hands.GetActiveItem(user);
        if (_companions.CanTame(uid, user) && Knows(user, "BookTaming") && food is { } held && IsFood(held))
            Add(args, "book-ability-tame", () => StartTaming(user, uid, held));
        if (Knows(user, "BookExtractReagent")) AddExtractionVerbs(uid, args);
        AddSpeechAndMagicVerbs(uid, args);
    }

    private void Add(GetVerbsEvent<AlternativeVerb> args, string text, Action action) =>
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString(text), Act = action });

    private bool Start(EntityUid user, EntityUid target, string knowledge, float seconds, Func<bool> validate, Action complete)
    {
        if (!Knows(user, knowledge) || !validate() || !_interaction.InRangeUnobstructed(user, target)) return false;
        var token = Guid.NewGuid().ToString();
        _pending[token] = new(user, target, knowledge, validate, complete);
        if (_doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, seconds,
                new BookAbilityDoAfterEvent { Ability = knowledge, Option = token }, user, target)
            {
                BreakOnMove = true,
                BreakOnDamage = true,
                NeedHand = false,
                Hidden = knowledge == "BookEscapeBonds",
                RequireCanInteract = knowledge != "BookEscapeBonds"
            })) return true;
        _pending.Remove(token);
        return false;
    }

    private void OnWork(EntityUid uid, LearnedKnowledgeComponent comp, BookAbilityDoAfterEvent args)
    {
        if (args.Handled || !_pending.Remove(args.Option, out var pending)) return;
        args.Handled = true;
        if (args.Cancelled || pending.User != uid || !Exists(pending.Target) ||
            !Knows(uid, pending.Knowledge) || !pending.Validate() ||
            !_interaction.InRangeUnobstructed(uid, pending.Target)) return;
        pending.Complete();
    }

    private bool ContainsPerson(EntityUid uid)
    {
        if (HasComp<MobStateComponent>(uid)) return true;
        if (!TryComp<ContainerManagerComponent>(uid, out var manager)) return false;
        return manager.Containers.Values.Any(c => c.ContainedEntities.Any(ContainsPerson));
    }

    private bool CanPackWorkbench(EntityUid uid)
    {
        if (!HasComp<CP14WorkbenchComponent>(uid)) return false;
        // Only the portable medieval workstation family, never special event/guild machines.
        return MetaData(uid).EntityPrototype?.ID is "MedievalWoodWorkerPlace" or "MedievalClothWorkingPlace" or
            "MedievalBlacksmithArmor" or "MedievalLeatherWorkingPlace" or "MedievalKeybench";
    }

    private void Pack(EntityUid user, EntityUid target, string ability)
    {
        // Another worker may have packed this target while our own work was still running.
        if (ContainsPerson(target) || _containers.TryGetContainingContainer(target, out _)) return;
        // Packing cannot turn an in-progress craft into a remote, movable source of output.
        foreach (var doAfter in EntityQuery<DoAfterComponent>())
            foreach (var work in doAfter.DoAfters.Values.ToArray())
                if (!work.Completed && !work.Cancelled && (work.Args.Target == target || work.Args.EventTarget == target))
                    _doAfter.Cancel(work.Id);
        if (TryComp<SpikeTrapComponent>(target, out var trap))
        {
            if (trap.ActiveTrapEntity is { } active) QueueDel(active);
            if (trap.DeactiveTrapEntity is { } inactive) QueueDel(inactive);
            trap.ActiveTrapEntity = null;
            trap.DeactiveTrapEntity = null;
        }
        var bundle = Spawn("MedievalBookPackedObject", Transform(user).Coordinates);
        var comp = EnsureComp<BookPackedObjectComponent>(bundle);
        comp.WasAnchored = Transform(target).Anchored;
        comp.Ability = ability;
        _meta.SetEntityName(bundle, Loc.GetString("book-ability-packed-name", ("name", Name(target))));
        if (comp.WasAnchored) _transform.Unanchor(target);
        var container = _containers.EnsureContainer<ContainerSlot>(bundle, "packed-object");
        if (!_containers.Insert(target, container))
        {
            if (comp.WasAnchored) _transform.AnchorEntity(target, Transform(target));
            QueueDel(bundle);
            return;
        }
        _hands.TryPickupAnyHand(user, bundle);
    }

    private void Unpack(EntityUid user, EntityUid bundle)
    {
        if (!_containers.TryGetContainer(bundle, "packed-object", out var container) || container.ContainedEntities.Count != 1) return;
        var target = container.ContainedEntities[0];
        var wasAnchored = Comp<BookPackedObjectComponent>(bundle).WasAnchored;
        if (!_containers.Remove(target, container)) return;
        _transform.SetCoordinates(target, Transform(user).Coordinates);
        _transform.AttachToGridOrMap(target);
        if (wasAnchored) _transform.AnchorEntity(target, Transform(target));
        if (TryComp<SpikeTrapComponent>(target, out var trap))
        {
            trap.Enabled = true;
            trap.Ready = false;
            trap.Cooldown = 2f;
        }
        QueueDel(bundle);
    }

    private bool IsFood(EntityUid uid) => HasComp<EdibleComponent>(uid) || HasComp<FoodComponent>(uid);

    private void StartTaming(EntityUid user, EntityUid beast, EntityUid food)
    {
        if (!_hands.IsHolding(user, food, out _) || !IsFood(food) || !_companions.CanTame(beast, user) ||
            _companions.OwnedCount(user) >= 1 || !TryComp<DamageableComponent>(beast, out var damage) || damage.TotalDamage < 20)
        {
            _popup.PopupEntity(Loc.GetString("book-ability-tame-requirements"), user, user);
            return;
        }
        if (!Start(user, beast, "BookTaming", 30, () => _companions.CanTame(beast, user),
                () => _companions.Tame(beast, user))) return;
        // Feeding is a real preparation cost. It buys the quiet interval needed to train a dangerous beast.
        QueueDel(food);
        _companions.Pacify(beast, TimeSpan.FromSeconds(35));
    }

    private void Loosen(EntityUid user)
    {
        if (!TryComp<CuffableComponent>(user, out var cuff) || cuff.CuffedHandCount == 0) return;
        var restraint = cuff.LastAddedCuffs;
        Start(user, user, "BookEscapeBonds", 10,
            () => cuff.Container.ContainedEntities.Contains(restraint), () =>
            {
                _loosenedCuffs[user] = restraint;
                _popup.PopupEntity(Loc.GetString("book-ability-bonds-ready"), user, user);
            });
    }

    private void OnEscapeBonds(EntityUid uid, LearnedKnowledgeComponent comp, BookEscapeBondsActionEvent args)
    {
        if (args.Handled || !Knows(uid, "BookEscapeBonds")) return;
        if (_loosenedCuffs.Remove(uid, out var restraint) && TryComp<CuffableComponent>(uid, out var cuff) &&
            cuff.Container.ContainedEntities.Contains(restraint))
        {
            _cuffs.Uncuff(uid, uid, restraint, cuff);
            args.Handled = true;
        }
        else Loosen(uid);
    }

    private void AddExtractionVerbs(EntityUid source, GetVerbsEvent<AlternativeVerb> args)
    {
        var user = args.User;
        if (_hands.GetActiveItem(user) is not { } receiver || receiver == source ||
            !HasComp<RefillableSolutionComponent>(receiver) || !HasComp<DrainableSolutionComponent>(source) ||
            !IsOpenContainer(source) || !IsOpenContainer(receiver)) return;
        if (_solutions.TryGetDrainableSolution(source, out var solution, out var mixture))
        {
            foreach (var reagent in mixture.Contents.ToArray())
            {
                var reagentId = reagent.Reagent;
                var label = _prototypes.Index<ReagentPrototype>(reagentId.Prototype).LocalizedName;
                args.Verbs.Add(new AlternativeVerb
                {
                    Text = Loc.GetString("book-ability-extract", ("reagent", label)),
                    Act = () => Start(user, source, "BookExtractReagent", 10,
                        () => _hands.IsHolding(user, receiver, out _) && !Deleted(receiver),
                        () => TryExtract(user, source, receiver, reagentId))
                });
            }
        }
    }

    public bool TryExtract(EntityUid user, EntityUid source, EntityUid receiver, ReagentId reagent)
    {
        if (!Knows(user, "BookExtractReagent") || source == receiver || !_hands.IsHolding(user, receiver, out _) ||
            !IsOpenContainer(source) || !IsOpenContainer(receiver) ||
            !_interaction.InRangeUnobstructed(user, source) ||
            !_solutions.TryGetDrainableSolution(source, out var sourceSol, out var solution) ||
            !_solutions.TryGetRefillableSolution(receiver, out var destination, out var destinationMixture) ||
            destinationMixture.Volume != 0) return false;
        var amount = solution.GetReagentQuantity(reagent);
        if (amount <= 0 || destinationMixture.AvailableVolume < amount) return false;
        var separated = new Solution { Temperature = solution.Temperature };
        separated.AddReagent(reagent, amount);
        _solutions.RemoveReagent(sourceSol.Value, reagent, amount);
        if (!_solutions.TryAddSolution(destination.Value, separated))
        {
            _solutions.TryAddSolution(sourceSol.Value, separated);
            return false;
        }
        return true;
    }

    private bool IsOpenContainer(EntityUid uid) => !TryComp<OpenableComponent>(uid, out var openable) || openable.Opened;

    private void OnSurvey(EntityUid uid, LearnedKnowledgeComponent comp, BookSurveyActionEvent args)
    {
        if (args.Handled || !Knows(uid, "BookCartography") || _hands.GetActiveItem(uid) is not { } sheet ||
            !TryComp<PaperComponent>(sheet, out var paper) || paper.EditingDisabled || !HasPen(uid)) return;
        args.Handled = Start(uid, sheet, "BookCartography", 8,
            () => _hands.IsHolding(uid, sheet, out _) && !paper.EditingDisabled && HasPen(uid),
            () => Survey(uid, sheet, paper));
    }
}
