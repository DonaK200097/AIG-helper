using GHelper.Helpers;
using GHelper.Mode;
using PawnIO;

namespace GHelper.AiMode
{
    /// <summary>
    /// Entry points called from existing G-Helper code. Keeps upstream files to one-line hooks.
    /// Per mode: ai_mode_N (on/off), ai_target_N, ai_offset_N, ai_min_mhz_N, ai_target_mhz_N. Global: ai_log, ai_explore, regulator coefficients.
    /// Debug: ai_debug_cap / ai_debug_epp apply fixed values while AI Mode is off for the mode.
    /// </summary>
    internal static class AiModeHooks
    {
        static readonly Guid OVERLAY_SILENT = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
        static readonly Guid OVERLAY_TURBO = new("ded574b5-45a0-4f42-8737-46345c09c238");

        const int ResumeDelayMs = 5000;
        // G-Helper applies fans and power limits ~1-3.5 s after a mode switch, and firmware may rewrite the plan after that
        static int ModeDelayMs => Math.Clamp(AppConfig.Get("ai_mode_delay", 5000), 0, 30000);

        static readonly Lazy<AiPowerPlan> _plan = new(() =>
            new AiPowerPlan(new PowrProfSettings(), Path.Combine(Logger.appPath, "ai_restore.json"), Logger.WriteLine));

        static readonly Lazy<AiController> _controller = new(() =>
            new AiController(Plan, new HardwareSensorSource(), new AppEnvironment(), Logger.WriteLine));

        static readonly object lifeLock = new();
        static long resumedAt;
        static bool shutdown;

        public static AiPowerPlan Plan => _plan.Value;
        public static AiController Controller => _controller.Value;

        public static bool IsSupported => !CpuInfo.IsAMD;

        public static string LogDir => Path.Combine(Logger.appPath, "ai_logs");

        public static bool IsEnabled(int mode) => IsSupported && AppConfig.Get("ai_mode_" + mode) == 1;
        public static bool IsEnabled() => IsEnabled(Modes.GetCurrent());

        /// <summary>
        /// Suffix for the performance mode label on the main window.
        /// </summary>
        public static string ModeLabelSuffix => IsEnabled() ? " · AI" : "";

        public static AiSettings LoadSettings()
        {
            var (target, offset) = AiSettings.Normalize(AppConfig.GetMode("ai_target", 87), AppConfig.GetMode("ai_offset", 5));
            var (minMhz, targetMhz) = AiSettings.NormalizeFreq(AppConfig.GetMode("ai_min_mhz", 3000), AppConfig.GetMode("ai_target_mhz", 4400));
            var d = new AiSettings();
            return new AiSettings
            {
                Target = target,
                Offset = offset,
                MinMhz = minMhz,
                TargetMhz = targetMhz,
                LightUtil = AppConfig.Get("ai_light_util", (int)d.LightUtil),
                HeavyUtil = AppConfig.Get("ai_heavy_util", (int)d.HeavyUtil),
                Kp = AppConfig.Get("ai_kp", (int)d.Kp),
                Ki = AppConfig.Get("ai_ki", (int)d.Ki),
                ColdStep = AppConfig.Get("ai_cold_step", d.ColdStep),
                PiMaxStep = AppConfig.Get("ai_pi_step", d.PiMaxStep),
                AggressiveStep = AppConfig.Get("ai_aggressive_step", d.AggressiveStep),
                EmergencyCap = AppConfig.Get("ai_emergency_cap", d.EmergencyCap),
                EmergencyTemp = AppConfig.Get("ai_emergency_temp", d.EmergencyTemp),
                EmergencyRise = AppConfig.Get("ai_emergency_rise", (int)d.EmergencyRise),
                EmaAlpha = Math.Clamp(AppConfig.Get("ai_ema", (int)(d.EmaAlpha * 100)), 5, 100) / 100.0,
                MaxMhz = AppConfig.Get("ai_max_mhz", d.MaxMhz),
                GpuRiseW = AppConfig.Get("ai_gpu_rise", (int)d.GpuRiseW),
                GpuMhzPerW = AppConfig.Get("ai_gpu_mhz_per_w", (int)d.GpuMhzPerW),
                IdleUtil = AppConfig.Get("ai_idle_util", (int)d.IdleUtil),
                TickMs = Math.Max(250, AppConfig.Get("ai_tick", d.TickMs)),
                FastTickMs = Math.Max(250, AppConfig.Get("ai_fast_tick", d.FastTickMs)),
                Explore = AppConfig.Is("ai_explore"),
            };
        }

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
        /// Called after a performance mode (and its Windows power plan / mode) was set, and after AI settings change.
        /// Restores values of the previous mode and starts the controller for the current one.
        /// </summary>
        public static void OnModeChanged()
        {
            if (!IsSupported) return;
            try
            {
                lock (lifeLock)
                {
                    if (shutdown) return;

                    bool active = IsEnabled();
                    bool log = AppConfig.Is("ai_log");

                    if (_controller.IsValueCreated) Controller.Stop("mode change");
                    Plan.Restore("mode change");

                    if (!active) ApplyDebugOverride();

                    if (active || log)
                    {
                        long sinceResume = Environment.TickCount64 - resumedAt;
                        int delay = resumedAt > 0 && sinceResume < ResumeDelayMs ? (int)(ResumeDelayMs - sinceResume) : 0;
                        if (active) delay = Math.Max(delay, ModeDelayMs);
                        Controller.Start(LoadSettings(), active, log ? LogDir : null, delay);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("AI Mode: " + ex);
            }

            Program.modeControl?.SetModeLabel();
        }

        /// <summary>
        /// Target / offset changed in UI: no restart needed.
        /// </summary>
        public static void OnSettingsChanged()
        {
            if (!IsSupported || !_controller.IsValueCreated) return;
            if (Controller.IsRunning) Controller.UpdateSettings(LoadSettings());
        }

        public static void OnResume()
        {
            resumedAt = Environment.TickCount64;
            if (!IsSupported) return;

            // Normally the mode is re-applied on wake and starts the controller; this covers a skipped re-apply
            Task.Delay(ResumeDelayMs * 2).ContinueWith(_ =>
            {
                bool wanted = IsEnabled() || AppConfig.Is("ai_log");
                if (wanted && !shutdown && !Controller.IsRunning) OnModeChanged();
            });
        }

        /// <summary>
        /// Exit, shutdown, suspend, unhandled exception: stop the loop and put original values back.
        /// After suspend the next mode apply (on wake) starts it again.
        /// </summary>
        public static void Shutdown(string reason)
        {
            if (!IsSupported) return;
            try
            {
                lock (lifeLock)
                {
                    if (reason != "suspend") shutdown = true;
                    if (_controller.IsValueCreated) Controller.Stop(reason);
                    if (_plan.IsValueCreated) Plan.Restore(reason);
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("AI Restore: " + ex.Message);
            }
        }

        /// <summary>
        /// Warnings shown when the user enables AI Mode.
        /// </summary>
        public static List<string> GetWarnings()
        {
            var warnings = new List<string>();
            if (AsusService.IsArmouryRunning())
                warnings.Add(AiText.ArmouryWarning);

            string powerMode = AppConfig.GetModeString("powermode") ?? PowerNative.GetDefaultPowerMode(Modes.GetCurrentBase());
            if (!AppConfig.Is("skip_powermode") && powerMode != "00000000-0000-0000-0000-000000000000")
                warnings.Add(AiText.OverlayWarning);

            if (SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Online)
                warnings.Add(AiText.BatteryWarning);

            return warnings;
        }

        private static void ApplyDebugOverride()
        {
            int cap = AppConfig.Get("ai_debug_cap");
            int epp = AppConfig.Get("ai_debug_epp");
            if (cap < 0 && epp < 0) return;

            if (!Plan.TakeSnapshot()) return;

            bool ok = Plan.Apply(cap >= 0 ? cap : null, epp >= 0 ? epp : null);
            Logger.WriteLine($"AI Debug: cap={cap} epp={epp} {(ok ? "OK" : "FAILED")} | {Plan.DescribeActive()}");
            LogOverlay();
        }

        /// <summary>
        /// Diagnostics for the EPP overlay question: which Windows power mode is effective
        /// and whether overlay schemes carry their own EPP values.
        /// </summary>
        private static void LogOverlay()
        {
            try
            {
                var power = new PowrProfSettings();
                PowerNative.PowerGetEffectiveOverlayScheme(out Guid overlay);
                string line = $"AI Overlay: effective {overlay} ({PowerNative.powerModes.GetValueOrDefault(overlay.ToString(), "?")})";
                foreach (var scheme in new[] { OVERLAY_SILENT, OVERLAY_TURBO })
                    line += $" | {scheme}: EPP={power.ReadAC(scheme, AiPowerPlan.GUID_CPU, AiPowerPlan.EPP)?.ToString() ?? "-"} EPP1={power.ReadAC(scheme, AiPowerPlan.GUID_CPU, AiPowerPlan.EPP_1)?.ToString() ?? "-"}";
                Logger.WriteLine(line);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("AI Overlay: " + ex.Message);
            }
        }

        private class AppEnvironment : IAiEnvironment
        {
            public bool OnAc() => SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;

            public int Mode() => Modes.GetCurrent();

            public bool IsArmouryRunning() => AsusService.IsArmouryRunning();

            public void Notify(string message)
            {
                Logger.WriteLine(message);
                // RunToast invokes the UI thread synchronously; the loop must not wait on it
                Task.Run(() =>
                {
                    try { Program.toast?.RunToast(message); } catch { }
                });
            }
        }
    }
}
