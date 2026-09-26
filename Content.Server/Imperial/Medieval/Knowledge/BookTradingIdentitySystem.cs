using Content.Server.Imperial.Medieval.Trading;
using Content.Shared.Imperial.Medieval.Knowledge;

namespace Content.Server.Imperial.Medieval.Knowledge;

/// <summary>Keeps lessons with different contents or remaining uses in separate market lots.</summary>
public sealed class BookTradingIdentitySystem : EntitySystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<LearnableBookComponent, TradingItemIdentityEvent>(OnIdentity);
    }

    private void OnIdentity(EntityUid uid, LearnableBookComponent comp, TradingItemIdentityEvent args)
    {
        args.ForceUnique = true;
        args.Parts.Add(comp.Knowledge);
        args.Parts.Add(comp.Language);
        args.Parts.Add(comp.Original.ToString());
        args.Parts.Add(comp.Encrypted.ToString());
        args.Parts.Add(comp.Spent.ToString());
    }
}
