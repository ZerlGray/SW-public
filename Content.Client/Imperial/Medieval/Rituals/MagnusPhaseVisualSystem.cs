using Content.Shared.Imperial.Medieval.Rituals;
using Robust.Client.GameObjects;

namespace Content.Client.Imperial.Medieval.Rituals;

public sealed class MagnusPhaseVisualSystem : EntitySystem
{
    [Dependency] private readonly SpriteSystem _sprites = default!;
    private readonly Dictionary<EntityUid, Color> _original = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<MagnusPhasedComponent, AfterAutoHandleStateEvent>(OnState);
        SubscribeLocalEvent<MagnusPhasedComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnState(EntityUid uid, MagnusPhasedComponent comp, ref AfterAutoHandleStateEvent args)
    {
        if (!TryComp<SpriteComponent>(uid, out var sprite)) return;
        _original.TryAdd(uid, sprite.Color);
        var color = _original[uid];
        _sprites.SetColor((uid, sprite), comp.Active ? color.WithAlpha(.35f) : color);
    }

    private void OnShutdown(EntityUid uid, MagnusPhasedComponent comp, ComponentShutdown args)
    {
        if (_original.Remove(uid, out var color) && TryComp<SpriteComponent>(uid, out var sprite))
            _sprites.SetColor((uid, sprite), color);
    }
}
