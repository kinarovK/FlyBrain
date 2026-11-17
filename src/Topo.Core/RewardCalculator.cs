namespace Topo.Core;

public sealed record RewardResult(double AngularDistanceDegrees, double Reward);

public static class RewardCalculator
{
    public const double DefaultScaleDegrees = 20d;

    public static RewardResult Calculate(GeoCoordinate target, GeoCoordinate prediction, double scaleDegrees = DefaultScaleDegrees)
    {
        if (!double.IsFinite(scaleDegrees) || scaleDegrees <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scaleDegrees));
        }

        var distance = GeoCoordinate.AngularDistanceDegrees(target, prediction);
        return new RewardResult(distance, Math.Clamp(Math.Exp(-distance / scaleDegrees), 0d, 1d));
    }
}
