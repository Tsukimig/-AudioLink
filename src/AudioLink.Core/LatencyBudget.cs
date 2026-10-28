namespace AudioLink.Core;

/// <summary>Keep enough captured future samples for an entire render request, plus scheduling slack.</summary>
public static class LatencyBudget
{
    public static double Calculate(double outputBufferMs, double driverLatencyMs, double capturePeriodMs, double safetyMs, int sourceRate, double captureLagMs = 0)
    {
        if (!double.IsFinite(outputBufferMs) || outputBufferMs < 0 || !double.IsFinite(driverLatencyMs) || driverLatencyMs < 0 ||
            !double.IsFinite(capturePeriodMs) || capturePeriodMs <= 0 || !double.IsFinite(safetyMs) || safetyMs < 0 || sourceRate <= 0 || !double.IsFinite(captureLagMs) || captureLagMs < 0)
            throw new ArgumentOutOfRangeException(nameof(outputBufferMs));
        double reserve = Math.Max(safetyMs, capturePeriodMs + captureLagMs + 2);
        return outputBufferMs + driverLatencyMs + reserve + SincKernel.Half * 1000d / sourceRate;
    }
}
