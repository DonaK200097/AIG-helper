namespace GHelper.AiMode
{
    /// <summary>
    /// Regulator settings. Target and offset come from the UI (per mode), the rest only from config.json.
    /// </summary>
    public sealed class AiSettings
    {
        public const int MinTarget = 70;
        public const int MaxTarget = 95;
        public const int MinOffset = 2;
        public const int MaxOffset = 10;
        public const int MaxThreshold = 100;

        public const int MinFreqLow = 1800;
        public const int MinFreqHigh = 4400;
        public const int TargetFreqLow = 3000;
        public const int TargetFreqHigh = 5400;

        public int Target { get; set; } = 87;
        public int Offset { get; set; } = 5;

        // Frequency range for the regulator (UI, per mode). Emergency zone may still go below MinMhz.
        public int MinMhz { get; set; } = 3000;
        public int TargetMhz { get; set; } = 4400;

        // CPU load → ceiling: at or below LightUtil the ceiling is "no limit", at or above HeavyUtil it's TargetMhz
        public double LightUtil { get; set; } = 30;
        public double HeavyUtil { get; set; } = 70;

        // Zones
        public int ColdMargin { get; set; } = 3;
        public int AggressiveHysteresis { get; set; } = 2;
        public int EmergencyTemp { get; set; } = 98;
        public double EmergencyRise { get; set; } = 3;      // °C/s while in aggressive zone
        public double EmaAlpha { get; set; } = 0.5;

        // Steps
        public int ColdStep { get; set; } = 200;
        public int PiMaxStep { get; set; } = 200;
        public int AggressiveStep { get; set; } = 500;
        public int EmergencyCap { get; set; } = 2400;
        public int EppColdStep { get; set; } = 5;
        public int EppAggressiveStep { get; set; } = 10;

        // PI
        public double Kp { get; set; } = 150;              // MHz/°C
        public double Ki { get; set; } = 20;               // MHz/(°C·s)

        // Cap above which the regulator writes "no limit"
        public int MaxMhz { get; set; } = 5400;

        // GPU feed-forward
        public int GpuWindowMs { get; set; } = 5000;
        public double GpuRiseW { get; set; } = 15;
        public double GpuMhzPerW { get; set; } = 20;

        public double IdleUtil { get; set; } = 10;

        public int TickMs { get; set; } = 1000;
        public int FastTickMs { get; set; } = 500;

        // Exploration (config only): random cap every ExploreMin..ExploreMax seconds
        public bool Explore { get; set; }
        public int ExploreMinSec { get; set; } = 20;
        public int ExploreMaxSec { get; set; } = 40;

        public int Threshold => Target + Offset;

        /// <summary>
        /// Clamps target and offset to the UI ranges; offset shrinks so target + offset stays within 100 °C.
        /// </summary>
        public static (int target, int offset) Normalize(int target, int offset)
        {
            target = Math.Clamp(target, MinTarget, MaxTarget);
            offset = Math.Clamp(offset, MinOffset, MaxOffset);
            if (target + offset > MaxThreshold) offset = MaxThreshold - target;
            return (target, offset);
        }

        /// <summary>
        /// Clamps frequencies to the UI ranges; the minimum can't exceed the target frequency.
        /// </summary>
        public static (int min, int target) NormalizeFreq(int min, int target)
        {
            target = Round(Math.Clamp(target, TargetFreqLow, TargetFreqHigh));
            min = Round(Math.Clamp(min, MinFreqLow, MinFreqHigh));
            if (min > target) min = target;
            return (min, target);
        }

        static int Round(int mhz) => (int)Math.Round(mhz / 100.0, MidpointRounding.AwayFromZero) * 100;

        /// <summary>
        /// Highest cap allowed for this CPU load. Unknown load counts as heavy.
        /// </summary>
        public double LoadCeiling(double? util)
        {
            double top = MaxMhz;
            double heavy = Math.Min(TargetMhz, MaxMhz);
            if (util is not double u) return heavy;
            if (u <= LightUtil) return top;
            if (u >= HeavyUtil || HeavyUtil <= LightUtil) return heavy;
            return top - (top - heavy) * (u - LightUtil) / (HeavyUtil - LightUtil);
        }
    }
}
