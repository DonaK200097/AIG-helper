using System.Diagnostics;

namespace GHelper.AiMode
{
    /// <summary>
    /// Real sensors: ASUS ACPI for CPU temp and fans, HardwareControl for GPU and CPU power,
    /// Windows counters for CPU utility and effective frequency.
    /// CPU temperature is read from ACPI directly, HardwareControl.GetCPUTemp caches it for 2 s.
    /// </summary>
    internal class HardwareSensorSource : ISensorSource
    {
        const int CounterRetryMs = 60000;

        PerformanceCounter? utilCounter, perfCounter, freqCounter;
        long countersFailedAt = -CounterRetryMs;
        bool powerInit;

        public AiSample Read()
        {
            var watch = Stopwatch.StartNew();
            var sample = new AiSample { Ts = DateTimeOffset.Now.ToUnixTimeMilliseconds() };

            var acpi = Program.acpi;
            if (acpi is not null)
            {
                sample.CpuTemp = Try(() => acpi.DeviceGet(AsusACPI.Temp_CPU) is int t && t > 0 && t < 125 ? t : (float?)null);
                sample.CpuFan = Try(() => acpi.GetFan(AsusFan.CPU) is int f && f >= 0 ? f * 100 : (int?)null);
                sample.GpuFan = Try(() => acpi.GetFan(AsusFan.GPU) is int f && f >= 0 ? f * 100 : (int?)null);
            }

            sample.GpuTemp = Try(HardwareControl.GetGPUTemp);
            sample.GpuPower = Try(HardwareControl.GetGPUPower);
            sample.GpuUtil = Try(() => (float?)HardwareControl.GpuControl?.GetGpuUse());

            if (!powerInit)
            {
                powerInit = true;
                HardwareControl.InitCPUPowerAsync();
            }
            sample.CpuPower = Try(HardwareControl.GetCPUPower);

            ReadCounters(sample);

            sample.ReadMs = (int)watch.ElapsedMilliseconds;
            return sample;
        }

        void ReadCounters(AiSample sample)
        {
            if (utilCounter is null)
            {
                if (Environment.TickCount64 - countersFailedAt < CounterRetryMs) return;
                try
                {
                    utilCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", true);
                    perfCounter = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
                    freqCounter = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total", true);
                    utilCounter.NextValue();
                    perfCounter.NextValue();
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("AI Sensors: CPU counters unavailable: " + ex.Message);
                    DisposeCounters();
                    countersFailedAt = Environment.TickCount64;
                    return;
                }
            }

            try
            {
                sample.CpuUtil = Math.Min(100, utilCounter.NextValue());
                float baseMhz = freqCounter!.NextValue();
                if (baseMhz > 0) sample.CpuFreqEff = perfCounter!.NextValue() / 100f * baseMhz;
            }
            catch
            {
                DisposeCounters();
                countersFailedAt = Environment.TickCount64;
            }
        }

        void DisposeCounters()
        {
            utilCounter?.Dispose();
            perfCounter?.Dispose();
            freqCounter?.Dispose();
            utilCounter = perfCounter = freqCounter = null;
        }

        static T? Try<T>(Func<T?> read) where T : struct
        {
            try
            {
                return read();
            }
            catch
            {
                return null;
            }
        }
    }
}
