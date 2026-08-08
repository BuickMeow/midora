using System.Globalization;

namespace Midora.Audio;

public static class HeldPreviewPlanExchange
{
    public static string GetFileName(long planGeneration)
    {
        if (planGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(planGeneration));
        }
        return $"held-preview-plan-{planGeneration.ToString("D20", CultureInfo.InvariantCulture)}.mdap";
    }
}
