using System.Numerics;
using System.Linq;

namespace Content.Shared.Imperial.Medieval.Rituals;

/// <summary>
/// Plans the entire exchange before mutating the world. Coordinates are aligned to cells;
/// movable blockers return to the source, while protected connected groups stay put.
/// Contains no entity-system calls so overlap, cycles and exclusions can be tested independently.
/// </summary>
public static class RitualTheftPlanner
{
    public sealed record Body(int Id, Vector2i Cell, bool Selected, bool Solid, bool Protected,
        bool KeepAtSource = false);

    public static Dictionary<int, Vector2i> Plan(IReadOnlyList<Body> bodies, Vector2i offset,
        Vector2i source, int radius)
    {
        var result = new Dictionary<int, Vector2i>();
        if (offset == Vector2i.Zero)
            return result;

        var indexed = bodies.ToDictionary(body => body.Id);
        foreach (var body in bodies.Where(body => body.Selected && !body.Protected).OrderBy(body => body.Id))
        {
            if (result.ContainsKey(body.Id))
                continue;
            var candidate = new Dictionary<int, Vector2i>(result);
            var visiting = new HashSet<int>();
            if (Place(body.Id, body.Cell + offset, candidate, visiting))
                result = candidate;
        }
        return result;

        bool Place(int id, Vector2i target, Dictionary<int, Vector2i> plan, HashSet<int> visiting)
        {
            var moving = indexed[id];
            if (moving.Protected)
                return target == moving.Cell;
            if (plan.TryGetValue(id, out var previous))
                return previous == target;
            if (!visiting.Add(id))
                return false;
            plan[id] = target;

            foreach (var other in bodies)
            {
                if (other.Id == id || (!moving.Solid && !other.Solid))
                    continue;
                var position = plan.GetValueOrDefault(other.Id, other.Cell);
                if (position != target)
                    continue;
                if (plan.ContainsKey(other.Id) || other.Protected)
                    return false;

                if (other.KeepAtSource)
                {
                    // Excluded source inhabitants never become stolen cargo just because a
                    // reverse blocker arrived. Find a nearby free source cell deterministically.
                    var placed = false;
                    for (var distance = 1; distance <= radius * 2 && !placed; distance++)
                    for (var x = -distance; x <= distance && !placed; x++)
                    for (var y = -distance; y <= distance && !placed; y++)
                    {
                        if (Math.Max(Math.Abs(x), Math.Abs(y)) != distance)
                            continue;
                        var free = other.Cell + new Vector2i(x, y);
                        if (((Vector2) (free - source)).LengthSquared() > radius * radius)
                            continue;
                        if (bodies.Any(test => test.Id != other.Id &&
                                (other.Solid || test.Solid) && plan.GetValueOrDefault(test.Id, test.Cell) == free))
                            continue;
                        plan[other.Id] = free;
                        placed = true;
                    }
                    if (!placed)
                        return false;
                }
                else
                {
                    var next = other.Selected ? other.Cell + offset : other.Cell - offset;
                    // In overlapping circles, a blocker may return into a cell that receives
                    // another selected object. Follow the same correspondence back to the
                    // beginning of that chain instead of rejecting a valid exchange.
                    if (!other.Selected)
                    {
                        var steps = 0;
                        while (bodies.Any(selected => selected.Selected && !selected.Protected && selected.Cell + offset == next))
                        {
                            next -= offset;
                            if (++steps > bodies.Count) return false;
                        }
                    }
                    if (!Place(other.Id, next, plan, visiting))
                        return false;
                }
            }
            visiting.Remove(id);
            return true;
        }
    }
}
