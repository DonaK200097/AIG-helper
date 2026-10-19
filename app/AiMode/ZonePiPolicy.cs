namespace GHelper.AiMode
{
    public enum AiZone { Cold, Working, Aggressive, Emergency }

    /// <summary>
    /// Applied values: cap in MHz (0 = no limit), EPP 0-100, EPP of the mode (lower bound).
    /// </summary>
    public readonly record struct AiPlanState(int CapMhz, int Epp, int ModeEpp);

    public readonly record struct AiDecision(int CapMhz, int Epp, AiZone Zone, bool Idle, int NextTickMs);

    public interface IAiPolicy
    {
        string Name { get; }
        AiDecision Decide(AiSample sample, AiHistory history, AiPlanState state);
        void Reset();
    }

    /// <summary>
    /// Zone regulator. Below T-3 the cap climbs, between T-3 and T+Δ a PI loop holds T,
    /// above T+Δ the cap drops hard and EPP rises, at 98 °C (or fast rise in aggressive zone) it jumps to the emergency cap.
    /// PI is in velocity form: the cap itself is the integrator, so it's frozen outside the working zone
    /// and can't wind up beyond the cap range.
    /// </summary>
    public class ZonePiPolicy : IAiPolicy
    {
        private AiSettings settings;

        private double? ema;
        private AiZone zone = AiZone.Working;
        private bool zoneKnown;
        private double? prevError;
        private double capExact;
        private bool capSynced;
        private long lastTs;
        private double gpuCompensated;

        public ZonePiPolicy(AiSettings settings)
        {
            this.settings = settings;
        }

        public string Name => "pi";

        public AiZone Zone => zone;
        public double? SmoothedTemp => ema;

        public void UpdateSettings(AiSettings newSettings) => settings = newSettings;

        public void Reset()
        {
            ema = null;
            zone = AiZone.Working;
            zoneKnown = false;
            prevError = null;
            capSynced = false;
            lastTs = 0;
            gpuCompensated = 0;
        }

        public AiDecision Decide(AiSample sample, AiHistory history, AiPlanState state)
        {
            var s = settings;
            double t = sample.CpuTemp ?? throw new ArgumentException("CPU temperature is required");

            double dt = lastTs > 0 ? Math.Clamp((sample.Ts - lastTs) / 1000.0, 0.1, 5) : s.TickMs / 1000.0;
            lastTs = sample.Ts;

            ema = ema is null ? t : s.EmaAlpha * t + (1 - s.EmaAlpha) * ema.Value;

            double rise = 0;
            var before = history.At(sample.Ts, 900);
            if (before?.CpuTemp is float tBefore && sample.Ts > before.Ts)
                rise = (t - tBefore) / ((sample.Ts - before.Ts) / 1000.0);

            AiZone previous = zone;
            zone = NextZone(t, ema.Value, rise);
            bool zoneChanged = !zoneKnown || zone != previous;
            zoneKnown = true;

            // Resync with what's actually applied (start, external clamp, rounding drift)
            int applied = state.CapMhz <= AiPowerPlan.NoLimit ? s.MaxMhz : state.CapMhz;
            if (!capSynced || Math.Abs(Math.Round(capExact) - applied) >= AiPowerPlan.CapStepMhz)
            {
                capExact = applied;
                capSynced = true;
            }

            double cap = capExact;
            int epp = state.Epp;
            bool idle = sample.CpuUtil is float util && util < s.IdleUtil;

            switch (zone)
            {
                case AiZone.Emergency:
                    cap = Math.Min(cap, s.EmergencyCap);
                    epp = AiPowerPlan.MaxEpp;
                    break;

                case AiZone.Aggressive:
                    if (idle) break;
                    cap -= s.AggressiveStep;
                    epp += s.EppAggressiveStep;
                    break;

                case AiZone.Working:
                    if (idle) break;
                    double error = s.Target - ema.Value;
                    if (zoneChanged || prevError is null) prevError = error;
                    double delta = s.Kp * (error - prevError.Value) + s.Ki * error * dt;
                    cap += Math.Clamp(delta, -s.PiMaxStep, s.PiMaxStep);
                    prevError = error;
                    break;

                case AiZone.Cold:
                    if (idle) break;
                    cap += s.ColdStep;
                    epp -= s.EppColdStep;
                    break;
            }

            // Derivative kick protection: after idle or other zones PI starts from the current error
            if (idle || zone != AiZone.Working) prevError = null;

            if (!idle && zone is AiZone.Cold or AiZone.Working)
                cap -= GpuFeedForward(sample, history);
            else
                gpuCompensated = 0;

            cap = Math.Clamp(cap, AiPowerPlan.MinCapMhz, s.MaxMhz);
            capExact = cap;

            int capOut = cap >= s.MaxMhz ? AiPowerPlan.NoLimit : AiPowerPlan.ClampCap((int)Math.Round(cap));
            int eppOut = AiPowerPlan.ClampEpp(epp, state.ModeEpp);
            int next = zone is AiZone.Aggressive or AiZone.Emergency ? s.FastTickMs : s.TickMs;

            return new AiDecision(capOut, eppOut, zone, idle && zone != AiZone.Emergency, next);
        }

        private AiZone NextZone(double raw, double smooth, double rise)
        {
            var s = settings;
            bool hot = zone is AiZone.Aggressive or AiZone.Emergency;

            if (raw >= s.EmergencyTemp) return AiZone.Emergency;
            if (hot && rise > s.EmergencyRise) return AiZone.Emergency;
            if (zone == AiZone.Emergency && smooth >= s.Target) return AiZone.Emergency;

            if (smooth > s.Threshold) return AiZone.Aggressive;
            if (hot && smooth >= s.Threshold - s.AggressiveHysteresis) return AiZone.Aggressive;

            if (smooth < s.Target - s.ColdMargin) return AiZone.Cold;
            return AiZone.Working;
        }

        /// <summary>
        /// GPU power jump over the window: cut the cap once per watt of new rise, before the shared cooler heats up.
        /// </summary>
        private double GpuFeedForward(AiSample sample, AiHistory history)
        {
            var s = settings;
            var before = history.At(sample.Ts, s.GpuWindowMs);
            if (sample.GpuPower is not float now || before?.GpuPower is not float then)
            {
                gpuCompensated = 0;
                return 0;
            }

            double rise = now - then;
            if (rise <= s.GpuRiseW)
            {
                gpuCompensated = 0;
                return 0;
            }

            double fresh = rise - gpuCompensated;
            if (fresh <= 0) return 0;

            gpuCompensated = rise;
            return fresh * s.GpuMhzPerW;
        }
    }

    /// <summary>
    /// Exploration for training data: in cold and working zones holds a random cap for 20-40 s.
    /// Aggressive and emergency zones keep the regulator's decision.
    /// </summary>
    public class ExplorePolicy : IAiPolicy
    {
        private readonly ZonePiPolicy inner;
        private readonly Random random;
        private AiSettings settings;
        private int target;
        private long until;

        public ExplorePolicy(ZonePiPolicy inner, AiSettings settings, Random? random = null)
        {
            this.inner = inner;
            this.settings = settings;
            this.random = random ?? new Random();
        }

        public string Name => "explore";

        public void UpdateSettings(AiSettings newSettings)
        {
            settings = newSettings;
            inner.UpdateSettings(newSettings);
        }

        public void Reset()
        {
            inner.Reset();
            until = 0;
        }

        public AiDecision Decide(AiSample sample, AiHistory history, AiPlanState state)
        {
            var decision = inner.Decide(sample, history, state);
            if (decision.Zone is AiZone.Aggressive or AiZone.Emergency) return decision;

            if (sample.Ts >= until)
            {
                int steps = (settings.MaxMhz - AiPowerPlan.MinCapMhz) / AiPowerPlan.CapStepMhz;
                int pick = random.Next(steps + 2);
                target = pick > steps ? AiPowerPlan.NoLimit : AiPowerPlan.MinCapMhz + pick * AiPowerPlan.CapStepMhz;
                until = sample.Ts + random.Next(settings.ExploreMinSec, settings.ExploreMaxSec + 1) * 1000L;
            }

            return decision with { CapMhz = target };
        }
    }
}
