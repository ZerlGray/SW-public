using System.Linq;
using Content.Server.Imperial.Medieval.MedievalMap;
using Content.Shared.Imperial.Medieval.BookAbilities;
using Content.Shared.Imperial.Medieval.Knowledge;
using Content.Shared.Imperial.Medieval.MedievalMap;

namespace Content.Server.Imperial.Medieval.BookAbilities;

public sealed partial class MedievalBookAbilitySystem
{
    [Dependency] private readonly MedievalMapSystem _fieldMaps = default!;

    private void InitializeCartography()
    {
        SubscribeLocalEvent<LearnedKnowledgeComponent, BookSurveyActionEvent>(OnSurvey);
    }

    private void OnSurvey(EntityUid uid, LearnedKnowledgeComponent comp, BookSurveyActionEvent args)
    {
        if (args.Handled || !Knows(uid, "BookCartography"))
            return;

        var map = _hands.EnumerateHeld(uid).FirstOrDefault(item => HasComp<MedievalMapComponent>(item));
        if (map == default || !_fieldMaps.CanSurvey(uid, map))
        {
            _popup.PopupEntity(Loc.GetString("book-cartography-requirements"), uid, uid);
            return;
        }

        args.Handled = Start(uid, map, "BookCartography", 8,
            () => _fieldMaps.CanSurvey(uid, map), () => _fieldMaps.BeginSurvey(uid, map));
    }
}
