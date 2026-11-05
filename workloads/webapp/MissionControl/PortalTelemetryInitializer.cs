using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace AmlabHello.MissionControl;

public sealed class PortalTelemetryInitializer : ITelemetryInitializer
{
    public void Initialize(ITelemetry telemetry)
    {
        var portal = telemetry switch
        {
            EventTelemetry item => item.Name == "PortalOrder",
            DependencyTelemetry item => item.Name == "core-ledger",
            RequestTelemetry item => item.Url?.AbsolutePath.StartsWith("/api/portal/", StringComparison.Ordinal) == true,
            _ => false
        };
        // Explicit sample percentages bypass SDK adaptive sampling for measured mission evidence only.
        if (portal && telemetry is ISupportSampling sampled) sampled.SamplingPercentage = 100;
    }
}
