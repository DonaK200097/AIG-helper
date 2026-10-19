using GHelper.AiMode;
using Xunit;

namespace AiMode.Tests
{
    public class ZonePiPolicyTests
    {
        readonly AiSettings settings = new() { Target = 87, Offset = 5 };
        readonly AiHistory history = new();
        long ts = 1_000_000;

        AiSample Sample(float temp, float? util = 50, float? gpu = null)
        {
            ts += 1000;
            return new AiSample { Ts = ts, CpuTemp = temp, CpuUtil = util, GpuPower = gpu };
        }

        AiDecision Step(ZonePiPolicy policy, ref AiPlanState state, AiSample sample)
        {
            var d = policy.Decide(sample, history, state);
            history.Add(sample);
            state = state with { CapMhz = d.CapMhz, Epp = d.Epp };
            return d;
        }

        [Fact]
        public void ColdZoneRaisesCapAndLowersEppToModeValue()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(3000, 40, 30);

            var d = Step(policy, ref state, Sample(70));
            Assert.Equal(AiZone.Cold, d.Zone);
            Assert.Equal(3200, d.CapMhz);
            Assert.Equal(35, d.Epp);

            Step(policy, ref state, Sample(70));
            d = Step(policy, ref state, Sample(70));
            Assert.Equal(30, d.Epp);
            Assert.Equal(1000, d.NextTickMs);
        }

        [Fact]
        public void ColdZoneReachesNoLimitUnderLightLoad()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(5200, 30, 30);

            var d = Step(policy, ref state, Sample(60, util: 20));
            Assert.Equal(AiPowerPlan.NoLimit, d.CapMhz);

            d = Step(policy, ref state, Sample(60, util: 20));
            Assert.Equal(AiPowerPlan.NoLimit, d.CapMhz);
        }

        [Fact]
        public void HeavyLoadCapsAtTargetFrequencyRightAway()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(AiPowerPlan.NoLimit, 30, 30);

            var d = Step(policy, ref state, Sample(60, util: 95));
            Assert.Equal(AiZone.Cold, d.Zone);
            Assert.Equal(4400, d.CapMhz);
            Assert.Equal(4400, d.CeilingMhz);

            for (int i = 0; i < 5; i++) d = Step(policy, ref state, Sample(60, util: 95));
            Assert.Equal(4400, d.CapMhz);
        }

        [Fact]
        public void CeilingScalesWithLoad()
        {
            Assert.Equal(5400, settings.LoadCeiling(10));
            Assert.Equal(5400, settings.LoadCeiling(30));
            Assert.Equal(4900, settings.LoadCeiling(50));
            Assert.Equal(4400, settings.LoadCeiling(70));
            Assert.Equal(4400, settings.LoadCeiling(100));
            Assert.Equal(4400, settings.LoadCeiling(null));
        }

        [Fact]
        public void LoadDropRaisesCeilingGradually()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(4400, 30, 30);

            Step(policy, ref state, Sample(60, util: 95));
            var d = Step(policy, ref state, Sample(60, util: 20));
            // Load is smoothed: one light tick doesn't jump to no limit
            Assert.NotEqual(AiPowerPlan.NoLimit, d.CeilingMhz);
            Assert.True(d.CapMhz <= 4600);
        }

        [Fact]
        public void WorkingZoneAboveTargetLowersCapWithLimitedStep()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(4000, 30, 30);

            var d = Step(policy, ref state, Sample(91));
            Assert.Equal(AiZone.Working, d.Zone);
            Assert.True(d.CapMhz < 4000);
            Assert.True(d.CapMhz >= 4000 - settings.PiMaxStep);
            Assert.Equal(30, d.Epp);
        }

        [Fact]
        public void WorkingZoneAtTargetHoldsCap()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(3500, 30, 30);

            for (int i = 0; i < 10; i++)
            {
                var d = Step(policy, ref state, Sample(87));
                Assert.Equal(3500, d.CapMhz);
            }
        }

        [Fact]
        public void SmallErrorsAccumulateDespiteRounding()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(3500, 30, 30);

            // 1 °C below target: Ki·e·dt = 20 MHz per tick, below the 100 MHz rounding step
            for (int i = 0; i < 10; i++) Step(policy, ref state, Sample(86));
            Assert.True(state.CapMhz >= 3600);
        }

        [Fact]
        public void PiConvergesOnSimulatedPlant()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(AiPowerPlan.NoLimit, 30, 30);
            double temp = 75;
            var temps = new List<double>();
            var caps = new List<int>();

            // Toy plant: steady temperature 45 °C + 11 °C per GHz, first-order lag of ~8 s
            for (int i = 0; i < 400; i++)
            {
                var d = Step(policy, ref state, Sample((float)Math.Round(temp)));
                int cap = d.CapMhz == 0 ? settings.MaxMhz : d.CapMhz;
                double steady = 45 + 11 * cap / 1000.0;
                temp += (steady - temp) / 8 * (d.NextTickMs / 1000.0);
                temps.Add(temp);
                caps.Add(cap);
            }

            var tail = temps.Skip(300).ToList();
            Assert.All(tail, t => Assert.InRange(t, settings.Target - 2, settings.Target + 2));
            var tailCaps = caps.Skip(300).ToList();
            Assert.True(tailCaps.Max() - tailCaps.Min() <= 400);
        }

        [Fact]
        public void AggressiveZoneCutsHardAndRaisesEpp()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(4000, 30, 30);

            var d = Step(policy, ref state, Sample(93));
            Assert.Equal(AiZone.Aggressive, d.Zone);
            Assert.Equal(3500, d.CapMhz);
            Assert.Equal(40, d.Epp);
            Assert.Equal(500, d.NextTickMs);
        }

        [Fact]
        public void AggressiveZoneHasHysteresis()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(4000, 30, 30);

            Step(policy, ref state, Sample(93));
            // ema = 0.5·91 + 0.5·93 = 92 → still above T+Δ−2
            Assert.Equal(AiZone.Aggressive, Step(policy, ref state, Sample(91)).Zone);
            // ema = 90.5
            Assert.Equal(AiZone.Aggressive, Step(policy, ref state, Sample(89)).Zone);
            // ema = 89.25 < 90
            Assert.Equal(AiZone.Working, Step(policy, ref state, Sample(88)).Zone);
        }

        [Fact]
        public void EppNeverExceedsMax()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(5000, 75, 30);
            Step(policy, ref state, Sample(94));
            Assert.Equal(AiPowerPlan.MaxEpp, Step(policy, ref state, Sample(94)).Epp);
        }

        [Fact]
        public void AggressiveZoneStopsAtMinimumFrequency()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(3600, 30, 30);
            Step(policy, ref state, Sample(95));
            Assert.Equal(3000, Step(policy, ref state, Sample(95)).CapMhz);
        }

        [Fact]
        public void EmergencyGoesBelowMinimumFrequency()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(3000, 30, 30);
            Assert.Equal(settings.EmergencyCap, Step(policy, ref state, Sample(99)).CapMhz);
        }

        [Fact]
        public void WorkingZoneRespectsMinimumFrequency()
        {
            var s = new AiSettings { MinMhz = 3500 };
            var policy = new ZonePiPolicy(s);
            var state = new AiPlanState(3600, 30, 30);
            for (int i = 0; i < 10; i++) Step(policy, ref state, Sample(91));
            Assert.Equal(3500, state.CapMhz);
        }

        [Fact]
        public void EmergencyAt98()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(AiPowerPlan.NoLimit, 30, 30);

            var d = Step(policy, ref state, Sample(98));
            Assert.Equal(AiZone.Emergency, d.Zone);
            Assert.Equal(settings.EmergencyCap, d.CapMhz);
            Assert.Equal(AiPowerPlan.MaxEpp, d.Epp);
        }

        [Fact]
        public void EmergencyExitsOnlyBelowTarget()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(AiPowerPlan.NoLimit, 30, 30);

            Step(policy, ref state, Sample(98));
            Assert.Equal(AiZone.Emergency, Step(policy, ref state, Sample(90)).Zone);
            Assert.Equal(AiZone.Emergency, Step(policy, ref state, Sample(86)).Zone);
            // ema: 98 → 94 → 90 → 86.5
            Assert.Equal(AiZone.Working, Step(policy, ref state, Sample(83)).Zone);
        }

        [Fact]
        public void FastRiseInAggressiveZoneIsEmergency()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(4000, 30, 30);

            Step(policy, ref state, Sample(93));
            var d = Step(policy, ref state, Sample(97));
            Assert.Equal(AiZone.Emergency, d.Zone);
        }

        [Fact]
        public void FastRiseOutsideAggressiveZoneIsNotEmergency()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(4000, 30, 30);

            Step(policy, ref state, Sample(80));
            Assert.NotEqual(AiZone.Emergency, Step(policy, ref state, Sample(86)).Zone);
        }

        [Fact]
        public void IdleCpuLeavesCapAlone()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(3000, 30, 30);

            var d = Step(policy, ref state, Sample(93, util: 5));
            Assert.True(d.Idle);
            Assert.Equal(3000, d.CapMhz);
        }

        [Fact]
        public void IdleDoesNotSkipEmergency()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(AiPowerPlan.NoLimit, 30, 30);

            var d = Step(policy, ref state, Sample(99, util: 2));
            Assert.False(d.Idle);
            Assert.Equal(settings.EmergencyCap, d.CapMhz);
        }

        [Fact]
        public void GpuPowerJumpCutsCapOnce()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(4000, 30, 30);

            for (int i = 0; i < 6; i++) Step(policy, ref state, Sample(87, gpu: 30));
            Assert.Equal(4000, state.CapMhz);

            // +30 W over the window → 30 · 20 = 600 MHz
            var d = Step(policy, ref state, Sample(87, gpu: 60));
            Assert.Equal(3400, d.CapMhz);

            d = Step(policy, ref state, Sample(87, gpu: 60));
            Assert.Equal(3400, d.CapMhz);
        }

        [Fact]
        public void SmallGpuRiseIsIgnored()
        {
            var policy = new ZonePiPolicy(settings);
            var state = new AiPlanState(4000, 30, 30);

            for (int i = 0; i < 6; i++) Step(policy, ref state, Sample(87, gpu: 30));
            Assert.Equal(4000, Step(policy, ref state, Sample(87, gpu: 40)).CapMhz);
        }

        [Fact]
        public void ExplorePolicyHoldsRandomCapButKeepsSafetyZones()
        {
            var pi = new ZonePiPolicy(settings);
            var policy = new ExplorePolicy(pi, settings, new Random(1));
            var state = new AiPlanState(4000, 30, 30);

            var first = policy.Decide(Sample(85), history, state);
            var second = policy.Decide(Sample(85), history, state);
            Assert.Equal(first.CapMhz, second.CapMhz);
            Assert.True(first.CapMhz == 0 || first.CapMhz >= AiPowerPlan.MinCapMhz);

            var hot = policy.Decide(Sample(99), history, state);
            Assert.Equal(AiZone.Emergency, hot.Zone);
            Assert.True(hot.CapMhz <= settings.EmergencyCap);
        }

        [Theory]
        [InlineData(87, 5, 87, 5)]
        [InlineData(95, 10, 95, 5)]
        [InlineData(60, 1, 70, 2)]
        [InlineData(99, 20, 95, 5)]
        public void SettingsNormalize(int target, int offset, int expectedTarget, int expectedOffset)
        {
            Assert.Equal((expectedTarget, expectedOffset), AiSettings.Normalize(target, offset));
        }

        [Theory]
        [InlineData(3000, 4400, 3000, 4400)]
        [InlineData(4400, 3500, 3500, 3500)]
        [InlineData(1000, 9000, 1800, 5400)]
        [InlineData(3049, 4451, 3000, 4500)]
        public void FrequencyNormalize(int min, int target, int expectedMin, int expectedTarget)
        {
            Assert.Equal((expectedMin, expectedTarget), AiSettings.NormalizeFreq(min, target));
        }
    }
}
