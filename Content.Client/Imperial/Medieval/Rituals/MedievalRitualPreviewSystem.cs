using Content.Shared.Imperial.Medieval.Rituals;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Enums;
using Robust.Shared.Timing;

namespace Content.Client.Imperial.Medieval.Rituals;

public sealed class MedievalRitualPreviewSystem : EntitySystem
{
    [Dependency] private readonly IOverlayManager _overlays = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IGameTiming _time = default!;

    public override void Initialize() => _overlays.AddOverlay(new MedievalRitualPreviewOverlay(EntityManager, _players, _transform, _time));
    public override void Shutdown() => _overlays.RemoveOverlay<MedievalRitualPreviewOverlay>();
}

public sealed class MedievalRitualPreviewOverlay(IEntityManager entities, IPlayerManager players, SharedTransformSystem transform, IGameTiming time) : Overlay
{
    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (players.LocalEntity is not { } player) return;
        var query = entities.AllEntityQueryEnumerator<MedievalRitualCenterComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var circle, out var xform))
        {
            if (circle.Owner != player || xform.MapID != args.Viewport.Eye?.Position.MapId) continue;
            var position = transform.GetWorldPosition(uid);
            args.WorldHandle.DrawCircle(position, circle.PreviewRadius, new Color(0.9f, 0.75f, 0.35f, 0.6f), false);
            args.WorldHandle.DrawCircle(position, 3, new Color(0.6f, 0.8f, 1, 0.25f), false);
            if (circle.PreviewTheft && circle.Target is { } marker && entities.TryGetComponent<TransformComponent>(marker, out var markerTransform) && markerTransform.MapID == args.Viewport.Eye?.Position.MapId)
                args.WorldHandle.DrawCircle(transform.GetWorldPosition(marker), circle.PreviewRadius, new Color(0.8f, 0.2f, 0.8f, 0.7f), false);
        }
        var antimagic = entities.AllEntityQueryEnumerator<MedievalAntimagicFieldComponent, TransformComponent>();
        while (antimagic.MoveNext(out var uid, out var field, out var xform))
        {
            if (field.Until <= time.CurTime || xform.MapID != args.Viewport.Eye?.Position.MapId) continue;
            var position = transform.GetWorldPosition(uid);
            args.WorldHandle.DrawCircle(position, field.Radius, new Color(0.5f, 0.2f, 0.8f, 0.06f));
            args.WorldHandle.DrawCircle(position, field.Radius, new Color(0.7f, 0.35f, 1, 0.8f), false);
            if (field.BlockPassage)
                args.WorldHandle.DrawCircle(position, field.Radius - 0.12f, new Color(0.7f, 0.35f, 1, 0.45f), false);
        }
        var sanctuaries = entities.AllEntityQueryEnumerator<MedievalSomaSanctuaryComponent, TransformComponent>();
        while (sanctuaries.MoveNext(out var uid, out var field, out var xform))
        {
            if (field.Until <= time.CurTime || xform.MapID != args.Viewport.Eye?.Position.MapId) continue;
            var position = transform.GetWorldPosition(uid);
            args.WorldHandle.DrawCircle(position, field.Radius, new Color(1, 0.95f, 0.7f, 0.05f));
            args.WorldHandle.DrawCircle(position, field.Radius, new Color(1, 0.95f, 0.7f, 0.9f), false);
            args.WorldHandle.DrawCircle(position, field.Radius - 0.12f, new Color(1, 1, 1, 0.5f), false);
        }
    }
}
