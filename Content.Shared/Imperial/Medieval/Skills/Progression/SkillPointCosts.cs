namespace Content.Shared.Imperial.Medieval.Skills;

/// <summary>Character creation prices. Positive values refund points; negative values spend them.</summary>
public static class SkillPointCosts
{
    public static int GetCost(int level)
    {
        level = Math.Clamp(level, 1, SkillScaling.Legendary);
        if (level <= SkillScaling.Baseline)
            return SkillScaling.Baseline - level + (level <= SkillScaling.Basic ? 1 : 0) + (level == 1 ? 1 : 0);

        var cost = 0;
        for (var next = SkillScaling.Baseline + 1; next <= level; next++)
            cost += next <= SkillScaling.Expert ? 1 : next <= SkillScaling.Master ? 2 : next < SkillScaling.Legendary ? 3 : 4;
        return -cost;
    }
}
