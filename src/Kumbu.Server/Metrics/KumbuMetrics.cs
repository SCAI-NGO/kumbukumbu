// SPDX-License-Identifier: AGPL-3.0-or-later
using Prometheus;

namespace Kumbu.Server.Metrics;

public static class KumbuMetrics
{
    private static readonly Lazy<bool> Configured = new(() =>
    {
        // Only the process and runtime basics: no event counters, no prometheus-net self-metrics, and of the meters
        // only System.Runtime (the adapter must stay on for that, so SuppressMeters is false).
        // The default registry is process-wide and these may be set once, though a test process builds many hosts.
        Prometheus.Metrics.SuppressDefaultMetrics(new SuppressDefaultMetricOptions
        {
            SuppressEventCounters = true,
            SuppressMeters = false,
            SuppressDebugMetrics = true,
        });
        Prometheus.Metrics.ConfigureMeterAdapter(o => o.InstrumentFilterPredicate = i => i.Meter.Name == "System.Runtime");
        Prometheus.Metrics.DefaultFactory.ExemplarBehavior = ExemplarBehavior.NoExemplars();
        return true;
    });

    /// <summary>Sets up the default collectors; call before anything uses the default registry. Idempotent.</summary>
    public static void Configure() => _ = Configured.Value;
}
