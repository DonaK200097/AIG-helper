using GHelper.Mode;
using PawnIO;

namespace GHelper.AiMode
{
    /// <summary>
    /// Entry points called from existing G-Helper code. Keeps upstream files to one-line hooks.
    /// Stage 1: only a debug override from config.json (ai_debug_cap in MHz, ai_debug_epp 0-100)
    /// plus snapshot / restore around it.
    /// </summary>
    internal static class AiModeHooks
    {
        static readonly Guid OVERLAY_SILENT = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
        static readonly Guid OVERLAY_TURBO = new("ded574b5-45a0-4f42-8737-46345c09c238");

        static readonly Lazy<AiPowerPlan> _plan = new(() =>
            new AiPowerPlan(new PowrProfSettings(), Path.Combine(Logger.appPath, "ai_restore.json"), Logger.WriteLine));

        public static AiPowerPlan Plan => _plan.Value;

        public static bool IsSupported => !CpuInfo.IsAMD;

        /// <summary>
        /// Called once on startup before modes are applied: puts back values left by a crashed or killed run.
        /// </summary>
        public static void RecoverOnStartup()
        {
            if (!IsSupported) return;
            try
            {
                Plan.RestoreFromFile();
            }
            catch (Exception ex)
            {
                Logger.WriteLine("AI Recovery: " + ex.Message);
            }
        }

        /// <summary>
        /// Called after a performance mode (and its Windows power plan / mode) was set.
        /// </summary>
        public static void OnModeChanged()
        {
            if (!IsSupported) return;
            try
            {
                int cap = AppConfig.Get("ai_debug_cap");
                int epp = AppConfig.Get("ai_debug_epp");
                bool debug = cap >= 0 || epp >= 0;

                // Mode switch may have changed the active scheme: original values go back to the old one
                if (Plan.HasSnapshot && (!debug || PowerSchemeChanged())) Plan.Restore("mode change");
                if (!debug) return;

                if (!Plan.TakeSnapshot()) return;

                bool ok = Plan.Apply(cap >= 0 ? cap : null, epp >= 0 ? epp : null);
                Logger.WriteLine($"AI Debug: cap={cap} epp={epp} {(ok ? "OK" : "FAILED")} | {Plan.DescribeActive()}");
                LogOverlay();
            }
            catch (Exception ex)
            {
                Logger.WriteLine("AI Debug: " + ex.Message);
            }
        }

        /// <summary>
        /// Exit, shutdown, unhandled exception.
        /// </summary>
        public static void Shutdown(string reason)
        {
            if (!IsSupported || !_plan.IsValueCreated) return;
            try
            {
                Plan.Restore(reason);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("AI Restore: " + ex.Message);
            }
        }

        private static bool PowerSchemeChanged()
        {
            var snap = Plan.Snapshot;
            return snap is not null && new PowrProfSettings().GetActiveScheme() != snap.Scheme;
        }

        /// <summary>
        /// Diagnostics for the EPP overlay question: which Windows power mode is effective
        /// and whether overlay schemes carry their own EPP values.
        /// </summary>
        private static void LogOverlay()
        {
            var power = new PowrProfSettings();
            PowerNative.PowerGetEffectiveOverlayScheme(out Guid overlay);
            string line = $"AI Overlay: effective {overlay} ({PowerNative.powerModes.GetValueOrDefault(overlay.ToString(), "?")})";
            foreach (var scheme in new[] { OVERLAY_SILENT, OVERLAY_TURBO })
                line += $" | {scheme}: EPP={power.ReadAC(scheme, AiPowerPlan.GUID_CPU, AiPowerPlan.EPP)?.ToString() ?? "-"} EPP1={power.ReadAC(scheme, AiPowerPlan.GUID_CPU, AiPowerPlan.EPP_1)?.ToString() ?? "-"}";
            Logger.WriteLine(line);
        }
    }
}
