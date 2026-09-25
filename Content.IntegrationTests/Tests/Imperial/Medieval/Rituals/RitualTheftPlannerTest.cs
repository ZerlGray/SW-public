using System.Linq;
using Content.Shared.Imperial.Medieval.Rituals;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.Imperial.Medieval.Rituals;

[TestFixture]
public sealed class RitualTheftPlannerTest
{
    [Test]
    public void DestinationWallReturnsToTheSource()
    {
        var plan = RitualTheftPlanner.Plan(new[]
        {
            new RitualTheftPlanner.Body(1, new(0, 0), true, true, false),
            new RitualTheftPlanner.Body(2, new(10, 0), false, true, false)
        }, new(10, 0), new(0, 0), 3);
        Assert.That(plan[1], Is.EqualTo(new Vector2i(10, 0)));
        Assert.That(plan[2], Is.EqualTo(new Vector2i(0, 0)));
    }

    [Test]
    public void ProtectedGroupDoesNotBlockIndependentSpoils()
    {
        var plan = RitualTheftPlanner.Plan(new[]
        {
            new RitualTheftPlanner.Body(1, new(0, 0), true, true, false),
            new RitualTheftPlanner.Body(2, new(10, 0), false, true, true),
            new RitualTheftPlanner.Body(3, new(0, 1), true, false, false)
        }, new(10, 0), new(0, 0), 3);
        Assert.That(plan.ContainsKey(1), Is.False);
        Assert.That(plan.ContainsKey(2), Is.False);
        Assert.That(plan[3], Is.EqualTo(new Vector2i(10, 1)));
    }

    [Test]
    public void ExcludedSourcePersonStaysInsideTheSourceArea()
    {
        var plan = RitualTheftPlanner.Plan(new[]
        {
            new RitualTheftPlanner.Body(1, new(0, 0), true, false, false),
            new RitualTheftPlanner.Body(2, new(10, 0), false, true, false),
            new RitualTheftPlanner.Body(3, new(0, 0), false, true, false, true)
        }, new(10, 0), new(0, 0), 3);
        Assert.That(plan[1], Is.EqualTo(new Vector2i(10, 0)));
        Assert.That(plan[2], Is.EqualTo(new Vector2i(0, 0)));
        Assert.That(plan[3].X * plan[3].X + plan[3].Y * plan[3].Y, Is.LessThanOrEqualTo(9));
        Assert.That(plan[3], Is.Not.EqualTo(plan[2]));
    }

    [Test]
    public void OverlappingAreasMoveAChainWithoutDuplicatingOrLosingBodies()
    {
        var plan = RitualTheftPlanner.Plan(new[]
        {
            new RitualTheftPlanner.Body(1, new(0, 0), true, true, false),
            new RitualTheftPlanner.Body(2, new(1, 0), true, true, false),
            new RitualTheftPlanner.Body(3, new(2, 0), false, true, false)
        }, new(1, 0), new(0, 0), 3);
        Assert.That(plan[1], Is.EqualTo(new Vector2i(1, 0)));
        Assert.That(plan[2], Is.EqualTo(new Vector2i(2, 0)));
        Assert.That(plan[3], Is.EqualTo(new Vector2i(0, 0)));
        Assert.That(plan.Values.Distinct().Count(), Is.EqualTo(3));
    }

    [Test]
    public void EqualCentersDoNothing()
    {
        var plan = RitualTheftPlanner.Plan(new[]
        {
            new RitualTheftPlanner.Body(1, new(0, 0), true, true, false)
        }, Vector2i.Zero, Vector2i.Zero, 3);
        Assert.That(plan, Is.Empty);
    }
}
