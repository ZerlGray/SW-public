using System.Linq;
using System.Text;
using Content.Server.Chat.Managers;
using Content.Server.Examine;
using Content.Shared.Actions;
using Content.Shared.Actions.Components;
using Content.Shared.Chat;
using Content.Shared.Examine;
using Content.Shared.Humanoid;
using Content.Shared.Imperial.Medieval.BookAbilities;
using Content.Shared.Imperial.Medieval.Knowledge;
using Content.Shared.Imperial.Medieval.Language;
using Content.Shared.Imperial.Medieval.Magic;
using Content.Shared.Imperial.Medieval.Magic.Mana;
using Content.Shared.Interaction.Events;
using Content.Shared.Paper;
using Content.Shared.Verbs;
using Content.Shared.Tag;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server.Imperial.Medieval.BookAbilities;

[RegisterComponent]
public sealed partial class BookScrollActionComponent : Component
{
    [DataField] public EntityUid Scroll;
    public EntityUid? ReservedBy;
}

[RegisterComponent]
public sealed partial class BookMagicTraceComponent : Component
{
    [DataField] public string Spell = string.Empty;
    [DataField] public string Race = string.Empty;
    [DataField] public string Signature = string.Empty;
    [DataField] public int Tier;
    [DataField] public TimeSpan CastAt;
}

public sealed partial class MedievalBookAbilitySystem
{
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly ExamineSystem _examine = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly ManaSystem _mana = default!;
    [Dependency] private readonly TagSystem _tags = default!;
    private readonly Dictionary<EntityUid, (EntityUid Source, TimeSpan Until)> _voices = new();
    private readonly Dictionary<EntityUid, string> _signatures = new();

    private void InitializeSpeechAndMagic()
    {
        SubscribeLocalEvent<MetaDataComponent, MedievalAfterCastSpellEvent>(OnSpellCast);
        SubscribeLocalEvent<BookMagicTraceComponent, ExaminedEvent>(OnTraceExamined);
        SubscribeLocalEvent<BookSpellScrollComponent, GetItemActionsEvent>(OnScrollActions);
        SubscribeLocalEvent<BookScrollActionComponent, MedievalBeforeCastSpellEvent>(OnScrollBeforeCast);
        SubscribeLocalEvent<BookScrollActionComponent, MedievalFailCastSpellEvent>(OnScrollFailed);
        SubscribeLocalEvent<LearnedKnowledgeComponent, BookReadTracesActionEvent>(OnReadTracesAction);
        SubscribeLocalEvent<LearnedKnowledgeComponent, BookVentriloquismActionEvent>(OnProjectVoiceAction);
    }

    private void OnReadTracesAction(EntityUid uid, LearnedKnowledgeComponent comp, BookReadTracesActionEvent args)
    {
        if (args.Handled || !Knows(uid, "BookMagicTraces")) return;
        ReadTraces(uid);
        args.Handled = true;
    }

    private void OnProjectVoiceAction(EntityUid uid, LearnedKnowledgeComponent comp, BookVentriloquismActionEvent args)
    {
        if (args.Handled || !Knows(uid, "BookVentriloquism") || !_examine.InRangeUnOccluded(uid, args.Target, 8f)) return;
        _voices[uid] = (args.Target, _timing.CurTime + TimeSpan.FromMinutes(2));
        args.Handled = true;
        _popup.PopupEntity(Loc.GetString("book-ability-voice-ready"), uid, uid);
    }

    public bool CanLipRead(EntityUid listener, EntityUid source, string language)
    {
        return Knows(listener, "BookLipReading") && HasComp<HumanoidAppearanceComponent>(source) &&
            TryComp<LanguageSpeakerComponent>(listener, out var languages) && languages.Languages.ContainsKey(language) &&
            _examine.InRangeUnOccluded(source, listener, 8f);
    }

    public EntityUid VoiceSource(EntityUid speaker)
    {
        if (!_voices.TryGetValue(speaker, out var voice)) return speaker;
        if (voice.Until < _timing.CurTime || !Exists(voice.Source) || !Knows(speaker, "BookVentriloquism") ||
            !_examine.InRangeUnOccluded(speaker, voice.Source, 8f))
        {
            _voices.Remove(speaker);
            return speaker;
        }
        return voice.Source;
    }

    private void AddSpeechAndMagicVerbs(EntityUid uid, GetVerbsEvent<AlternativeVerb> args)
    {
        var user = args.User;
        if (Knows(user, "BookVentriloquism"))
            Add(args, "book-ability-project-voice", () =>
            {
                if (!_examine.InRangeUnOccluded(user, uid, 8f)) return;
                _voices[user] = (uid, _timing.CurTime + TimeSpan.FromMinutes(2));
                _popup.PopupEntity(Loc.GetString("book-ability-voice-ready"), user, user);
            });
        if (uid == user && Knows(user, "BookMagicTraces"))
            Add(args, "book-ability-read-traces", () => ReadTraces(user));
        if (!Knows(user, "BookSpellScribing") || !HasPen(user) || !TryComp<PaperComponent>(uid, out var paper) ||
            paper.EditingDisabled || !string.IsNullOrWhiteSpace(paper.Content) || HasComp<BookSpellScrollComponent>(uid) ||
            HasComp<LearnableBookComponent>(uid)) return;
        foreach (var action in _actions.GetActions(user))
        {
            if (MetaData(action).EntityPrototype?.ID is not { } id ||
                !TryComp<WorldTargetActionComponent>(action, out var target) ||
                target.Event is not MedievalProjectileSpellEvent || HasComp<BookScrollActionComponent>(action)) continue;
            var spell = action.Owner;
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString("book-ability-scribe", ("spell", Name(spell))),
                Act = () => Start(user, uid, "BookSpellScribing", 60,
                    () => HasPen(user) && !paper.EditingDisabled && string.IsNullOrWhiteSpace(paper.Content) && !HasComp<BookSpellScrollComponent>(uid) &&
                        _actions.GetActions(user).Any(a => a.Owner == spell) && HasScribingMana(user, spell),
                    () => Scribe(user, uid, spell, id))
            });
        }
    }

    private bool HasPen(EntityUid user) => _hands.EnumerateHeld(user).Any(item => _tags.HasTag(item, "Write"));

    private bool HasScribingMana(EntityUid user, EntityUid spell)
    {
        var cost = TryComp<ManaDrainSpellComponent>(spell, out var drain) ? drain.ManaDrain : 0;
        return TryComp<ManaComponent>(user, out var mana) && mana.Mana - mana.CastedSpells.Values.Sum() >= cost;
    }

    private void Scribe(EntityUid user, EntityUid sheet, EntityUid originalAction, string prototype)
    {
        if (!HasScribingMana(user, originalAction)) return;
        var cost = TryComp<ManaDrainSpellComponent>(originalAction, out var drain) ? drain.ManaDrain : 0;
        var scroll = EnsureComp<BookSpellScrollComponent>(sheet);
        scroll.Spell = prototype;
        if (!_actions.AddAction(user, ref scroll.Action, prototype, sheet) || scroll.Action is not { } action)
        {
            RemComp<BookSpellScrollComponent>(sheet);
            return;
        }
        if (TryComp<ManaComponent>(user, out var mana)) _mana.TryChangeMana(user, mana.Mana - cost, mana);
        RemComp<ManaDrainSpellComponent>(action);
        EnsureComp<BookScrollActionComponent>(action).Scroll = sheet;
        _paper.SetContent(sheet, Loc.GetString("book-ability-scroll-content", ("spell", Name(originalAction))));
        _meta.SetEntityName(sheet, Loc.GetString("book-ability-scroll-name", ("spell", Name(originalAction))));
        Comp<PaperComponent>(sheet).EditingDisabled = true;
        Dirty(sheet, Comp<PaperComponent>(sheet));
    }

    private void OnScrollActions(EntityUid uid, BookSpellScrollComponent comp, GetItemActionsEvent args)
    {
        if (comp.Spent || comp.Action == null) return;
        args.AddAction(ref comp.Action, comp.Spell);
    }

    private void OnScrollBeforeCast(EntityUid uid, BookScrollActionComponent comp, ref MedievalBeforeCastSpellEvent args)
    {
        if (args.Cancelled)
        {
            args.HasResourceReservation |= args.IsContinuation && comp.ReservedBy == args.Performer;
            return;
        }
        if (!TryComp<BookSpellScrollComponent>(comp.Scroll, out var scroll) || scroll.Spent ||
            !_hands.IsHolding(args.Performer, comp.Scroll, out _) ||
            (args.IsContinuation ? comp.ReservedBy != args.Performer : comp.ReservedBy != null))
        {
            args.Cancelled = true;
            return;
        }
        comp.ReservedBy = args.Performer;
        args.HasResourceReservation = true;
    }

    private void OnScrollFailed(EntityUid uid, BookScrollActionComponent comp, MedievalFailCastSpellEvent args)
    {
        if (comp.ReservedBy == args.Performer) comp.ReservedBy = null;
    }

    private void OnSpellCast(EntityUid uid, MetaDataComponent comp, MedievalAfterCastSpellEvent args)
    {
        if (!Exists(args.Performer)) return;
        if (!_signatures.TryGetValue(args.Performer, out var signature))
        {
            signature = Guid.NewGuid().ToString("N")[..8];
            _signatures[args.Performer] = signature;
        }
        var trace = Spawn("MedievalBookMagicTrace", Transform(args.Performer).Coordinates);
        var info = EnsureComp<BookMagicTraceComponent>(trace);
        info.Spell = Name(args.Action);
        var spellId = MetaData(args.Action).EntityPrototype?.ID ?? string.Empty;
        info.Tier = spellId.Contains("Senior") ? 3 : spellId.Contains("Middle") ? 2 : 1;
        info.Signature = signature;
        info.Race = TryComp<HumanoidAppearanceComponent>(args.Performer, out var appearance) ? appearance.Species : "Unknown";
        info.CastAt = _timing.CurTime;
        if (TryComp<BookScrollActionComponent>(uid, out var linked) &&
            TryComp<BookSpellScrollComponent>(linked.Scroll, out var scroll) && !scroll.Spent)
        {
            scroll.Spent = true;
            _actions.RemoveAction(args.Performer, uid);
            QueueDel(linked.Scroll);
        }
    }

    private string TraceText(BookMagicTraceComponent comp) => Loc.GetString("book-ability-trace", ("spell", comp.Spell),
        ("race", comp.Race), ("tier", comp.Tier), ("signature", comp.Signature), ("seconds", (int) (_timing.CurTime - comp.CastAt).TotalSeconds));

    private void OnTraceExamined(EntityUid uid, BookMagicTraceComponent comp, ExaminedEvent args)
    {
        if (Knows(args.Examiner, "BookMagicTraces")) args.PushText(TraceText(comp));
    }

    private void ReadTraces(EntityUid user)
    {
        var lines = _lookup.GetEntitiesInRange(Transform(user).Coordinates, 6f)
            .Where(e => HasComp<BookMagicTraceComponent>(e) && _examine.InRangeUnOccluded(user, e, 6f))
            .Select(e => TraceText(Comp<BookMagicTraceComponent>(e))).ToArray();
        _popup.PopupEntity(lines.Length == 0 ? Loc.GetString("book-ability-no-traces") : string.Join("\n", lines), user, user);
    }

    private void Survey(EntityUid user, EntityUid sheet, PaperComponent paper)
    {
        var center = _transform.GetWorldPosition(user);
        var landmarks = _lookup.GetEntitiesInRange(Transform(user).Coordinates, 6f)
            .Where(e => e != user && Transform(e).Anchored && _examine.InRangeUnOccluded(user, e, 6f))
            .OrderBy(e => (_transform.GetWorldPosition(e) - center).LengthSquared()).Take(20).ToArray();
        var grid = new char[13, 13];
        for (var y = 0; y < 13; y++) for (var x = 0; x < 13; x++) grid[x, y] = '.';
        grid[6, 6] = '@';
        var text = new StringBuilder(paper.Content);
        text.AppendLine().AppendLine(Loc.GetString("book-ability-survey-title", ("x", (int) center.X), ("y", (int) center.Y)));
        for (var i = 0; i < landmarks.Length; i++)
        {
            var offset = _transform.GetWorldPosition(landmarks[i]) - center;
            var x = Math.Clamp((int) MathF.Round(offset.X) + 6, 0, 12);
            var y = Math.Clamp(6 - (int) MathF.Round(offset.Y), 0, 12);
            var letter = (char) ('A' + i);
            grid[x, y] = letter;
            text.Append(letter).Append(": ").AppendLine(Name(landmarks[i]));
        }
        for (var y = 0; y < 13; y++)
        {
            for (var x = 0; x < 13; x++) text.Append(grid[x, y]);
            text.AppendLine();
        }
        if (text.Length <= paper.ContentSize) _paper.SetContent(sheet, text.ToString());
    }
}
