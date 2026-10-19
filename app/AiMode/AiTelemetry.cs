namespace GHelper.AiMode
{
    /// <summary>
    /// Sensor snapshot for one tick plus the decision applied on it. Null = sensor didn't answer.
    /// </summary>
    public sealed class AiSample
    {
        public long Ts { get; set; }

        public float? CpuTemp { get; set; }
        public float? GpuTemp { get; set; }
        public float? GpuPower { get; set; }
        public float? GpuUtil { get; set; }
        public int? CpuFan { get; set; }
        public int? GpuFan { get; set; }
        public float? CpuUtil { get; set; }
        public float? CpuFreqEff { get; set; }
        public float? CpuPower { get; set; }

        public int Mode { get; set; }
        public bool OnAc { get; set; }

        public int? CapMhz { get; set; }
        public int? Epp { get; set; }
        public string Zone { get; set; } = "";
        public string Policy { get; set; } = "";
        public float? PredTemp { get; set; }

        public int ReadMs { get; set; }

        public int? CeilingMhz { get; set; }
    }

    public interface ISensorSource
    {
        /// <summary>
        /// Reads all sensors once. Must not reuse stale values: a failed sensor is null.
        /// </summary>
        AiSample Read();
    }

    /// <summary>
    /// Samples of the last <see cref="WindowMs"/> milliseconds, oldest first.
    /// </summary>
    public class AiHistory
    {
        public const int WindowMs = 60000;

        private readonly LinkedList<AiSample> samples = new();

        public int Count => samples.Count;
        public AiSample? Latest => samples.Last?.Value;

        public void Add(AiSample sample)
        {
            samples.AddLast(sample);
            while (samples.First is not null && sample.Ts - samples.First.Value.Ts > WindowMs) samples.RemoveFirst();
        }

        public void Clear() => samples.Clear();

        public IEnumerable<AiSample> All => samples;

        /// <summary>
        /// Latest sample taken at least <paramref name="agoMs"/> before <paramref name="now"/>.
        /// </summary>
        public AiSample? At(long now, int agoMs)
        {
            AiSample? found = null;
            foreach (var s in samples)
            {
                if (now - s.Ts >= agoMs) found = s;
                else break;
            }
            return found;
        }
    }
}
