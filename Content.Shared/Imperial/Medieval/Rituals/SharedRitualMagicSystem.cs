using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Shared.Imperial.Medieval.Rituals;

/// <summary>One authoritative boundary check for casting, magic gifts and transport.</summary>
public sealed class SharedRitualMagicSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IGameTiming _time = default!;

    public bool IsSuppressed(EntityUid uid) => Exists(uid) && IsSuppressed(_transform.GetMapCoordinates(uid));

    public bool IsSuppressed(MapCoordinates point)
    {
        var query = EntityQueryEnumerator<MedievalAntimagicFieldComponent>();
        while (query.MoveNext(out var uid, out var field))
            if (field.Until > _time.CurTime && Inside(point, _transform.GetMapCoordinates(uid), field.Radius))
                return true;
        return false;
    }

    public bool BlocksCast(EntityUid caster, EntityCoordinates target) =>
        BlocksMagic(_transform.GetMapCoordinates(caster), _transform.ToMapCoordinates(target));

    public bool BlocksMagic(MapCoordinates from, MapCoordinates to)
    {
        if (IsSuppressed(from)) return true;
        return BlocksMagicPassage(from, to);
    }

    /// <summary>Already existing magic is stopped only by a full field, never by the small cast-only circle.</summary>
    public bool BlocksMagicPassage(MapCoordinates from, MapCoordinates to)
    {
        var query = EntityQueryEnumerator<MedievalAntimagicFieldComponent>();
        while (query.MoveNext(out var uid, out var field))
            if (field.Until > _time.CurTime && field.BlockPassage &&
                Intersects(from, to, _transform.GetMapCoordinates(uid), field.Radius))
                return true;
        return BlocksHostility(from, to);
    }

    /// <summary>Soma permits ordinary movement but no attacks through the dome in either direction.</summary>
    public bool BlocksHostility(MapCoordinates from, MapCoordinates to)
    {
        var query = EntityQueryEnumerator<MedievalSomaSanctuaryComponent>();
        while (query.MoveNext(out var uid, out var field))
        {
            if (field.Until <= _time.CurTime) continue;
            var center = _transform.GetMapCoordinates(uid);
            if (CrossesBoundary(from, to, center, field.Radius)) return true;
        }
        return false;
    }

    public static bool Inside(MapCoordinates point, MapCoordinates center, float radius) =>
        point.MapId == center.MapId && Vector2.DistanceSquared(point.Position, center.Position) <= radius * radius;

    public static bool Intersects(MapCoordinates from, MapCoordinates to, MapCoordinates center, float radius)
    {
        if (from.MapId != to.MapId) return Inside(from, center, radius) || Inside(to, center, radius);
        if (from.MapId != center.MapId) return false;
        var segment = to.Position - from.Position;
        var length = segment.LengthSquared();
        var fraction = length > 0 ? Math.Clamp(Vector2.Dot(center.Position - from.Position, segment) / length, 0, 1) : 0;
        return Vector2.DistanceSquared(from.Position + segment * fraction, center.Position) <= radius * radius;
    }

    public static bool CrossesBoundary(MapCoordinates from, MapCoordinates to, MapCoordinates center, float radius) =>
        !(Inside(from, center, radius) && Inside(to, center, radius)) && Intersects(from, to, center, radius);
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class MedievalAntimagicFieldComponent : Component
{
    [DataField, AutoNetworkedField] public float Radius = 3;
    [DataField, AutoNetworkedField] public TimeSpan Until;
    [DataField, AutoNetworkedField] public bool BlockPassage;
}
