using Content.Shared.Imperial.Medieval.Rituals;
using Robust.Client.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Map;

namespace Content.Client.Imperial.Medieval.Rituals;

public sealed class ZaygoItemAppearanceSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    private readonly Dictionary<EntityUid, EntityUid> _originals = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<ZaygoItemAppearanceComponent, AfterAutoHandleStateEvent>(OnState);
        SubscribeLocalEvent<ZaygoItemAppearanceComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnState(EntityUid uid, ZaygoItemAppearanceComponent component, ref AfterAutoHandleStateEvent args)
    {
        if (!_originals.ContainsKey(uid) && TryComp<SpriteComponent>(uid, out var sprite))
        {
            var savedEntity = Spawn(null, MapCoordinates.Nullspace);
            var saved = AddComp<SpriteComponent>(savedEntity);
            saved.CopyFrom(sprite);
            _originals[uid] = savedEntity;
        }
        if (component.Invisible && TryComp<SpriteComponent>(uid, out var hidden)) hidden.Visible = false;
        else Apply(uid, component.Prototype);
    }

    private void OnShutdown(EntityUid uid, ZaygoItemAppearanceComponent component, ComponentShutdown args)
    {
        if (_originals.Remove(uid, out var saved))
        {
            if (TryComp<SpriteComponent>(saved, out var originalSprite) && TryComp<SpriteComponent>(uid, out var sprite)) sprite.CopyFrom(originalSprite);
            QueueDel(saved);
        }
        else if (MetaData(uid).EntityPrototype is {} original)
            Apply(uid, original.ID);
    }

    private void Apply(EntityUid uid, EntProtoId prototype)
    {
        if (_prototypes.TryIndex(prototype, out var definition) &&
            TryComp<SpriteComponent>(uid, out var sprite) &&
            definition.TryGetComponent<SpriteComponent>(out var visual, EntityManager.ComponentFactory))
            sprite.CopyFrom(visual);
    }
}
