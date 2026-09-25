using System;
using System.Linq;
using Content.Server.Imperial.Medieval.Rituals;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.FixedPoint;
using Content.Shared.Imperial.Medieval.Rituals;
using NUnit.Framework;

namespace Content.Tests.Shared.Imperial.Medieval;

[TestFixture]
public sealed class RitualLiquidTest
{
    [Test]
    public void PouringAndDischargePreserveVolumeAndMixtureWithoutEnchantingDilution()
    {
        var gift = new RitualLiquidData { Kind = "liquidLife", Tier = 2, Until = TimeSpan.FromMinutes(2) };
        var source = new Solution();
        source.AddReagent(new ReagentId("Water", new() { gift }), 10);
        source.AddReagent(new ReagentId("Saline", new() { gift.Clone() }), 10);
        source.AddReagent("Water", 20);
        var poured = source.SplitSolution(20);
        var dose = MedievalRitualLiquidSystem.ExtractGift(poured, gift, 4);
        Assert.Multiple(() =>
        {
            Assert.That(source.Volume + poured.Volume + dose.Volume, Is.EqualTo((FixedPoint2)40));
            Assert.That(dose.Volume, Is.EqualTo((FixedPoint2)4));
            Assert.That(dose.GetTotalPrototypeQuantity("Water"), Is.EqualTo((FixedPoint2)2));
            Assert.That(dose.GetTotalPrototypeQuantity("Saline"), Is.EqualTo((FixedPoint2)2));
            Assert.That(dose.Contents.Any(entry => entry.Reagent.Data?.OfType<RitualLiquidData>().Any() == true), Is.False);
            Assert.That(poured.Contents.Where(entry => entry.Reagent.Data?.Contains(gift) == true).Select(entry => entry.Quantity).Sum(), Is.EqualTo((FixedPoint2)6));
        });
        var rest = MedievalRitualLiquidSystem.ExtractGift(poured, gift, 100);
        Assert.That(rest.Volume, Is.EqualTo((FixedPoint2)6));
        Assert.That(poured.Volume, Is.EqualTo((FixedPoint2)10));
        Assert.That(MedievalRitualLiquidSystem.ExtractGift(poured, gift, 100).Volume, Is.EqualTo(FixedPoint2.Zero));
    }
}
