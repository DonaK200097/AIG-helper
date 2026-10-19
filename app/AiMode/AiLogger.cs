using System.Globalization;
using System.Text;

namespace GHelper.AiMode
{
    /// <summary>
    /// Telemetry CSV: new file per start, rotation by size, only the newest files are kept.
    /// The column set is the training format, append new columns only at the end.
    /// </summary>
    public class AiLogger : IDisposable
    {
        public const string Header = "ts,cpu_temp,gpu_temp,gpu_power,gpu_util,cpu_fan,gpu_fan,cpu_util,cpu_freq_eff,cpu_power,mode,on_ac,cap_mhz,epp,zone,policy,pred_temp,read_ms";

        public const long MaxBytes = 50L * 1024 * 1024;
        public const int KeepFiles = 20;
        private const int FlushEvery = 10;

        private readonly string dir;
        private readonly long maxBytes;
        private readonly int keepFiles;
        private readonly string baseName;
        private readonly object fileLock = new();

        private StreamWriter? writer;
        private int part;
        private int pending;

        public string? CurrentFile { get; private set; }

        public AiLogger(string dir, DateTime? started = null, long maxBytes = MaxBytes, int keepFiles = KeepFiles)
        {
            this.dir = dir;
            this.maxBytes = maxBytes;
            this.keepFiles = keepFiles;
            baseName = (started ?? DateTime.Now).ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        }

        public void Write(AiSample s)
        {
            lock (fileLock)
            {
                if (writer is null || writer.BaseStream.Length >= maxBytes) Open();

                writer!.WriteLine(Format(s));
                if (++pending >= FlushEvery) Flush();
            }
        }

        public static string Format(AiSample s)
        {
            var sb = new StringBuilder(160);
            sb.Append(s.Ts).Append(',');
            Num(sb, s.CpuTemp); Num(sb, s.GpuTemp); Num(sb, s.GpuPower); Num(sb, s.GpuUtil);
            Num(sb, s.CpuFan); Num(sb, s.GpuFan);
            Num(sb, s.CpuUtil); Num(sb, s.CpuFreqEff); Num(sb, s.CpuPower);
            sb.Append(s.Mode).Append(',');
            sb.Append(s.OnAc ? 1 : 0).Append(',');
            Num(sb, s.CapMhz); Num(sb, s.Epp);
            sb.Append(s.Zone).Append(',');
            sb.Append(s.Policy).Append(',');
            Num(sb, s.PredTemp);
            sb.Append(s.ReadMs);
            return sb.ToString();
        }

        private static void Num(StringBuilder sb, float? value)
        {
            if (value is float v) sb.Append(Math.Round(v, 1).ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
        }

        private static void Num(StringBuilder sb, int? value)
        {
            if (value is int v) sb.Append(v.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
        }

        private void Open()
        {
            Close();
            Directory.CreateDirectory(dir);

            string name = part == 0 ? baseName : $"{baseName}_{part}";
            part++;

            CurrentFile = Path.Combine(dir, name + ".csv");
            writer = new StreamWriter(new FileStream(CurrentFile, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            writer.WriteLine(Header);

            Cleanup();
        }

        private void Cleanup()
        {
            try
            {
                var files = new DirectoryInfo(dir).GetFiles("*.csv").OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name).ToList();
                foreach (var old in files.Skip(keepFiles))
                    if (old.FullName != CurrentFile) old.Delete();
            }
            catch { }
        }

        public void Flush()
        {
            lock (fileLock)
            {
                writer?.Flush();
                pending = 0;
            }
        }

        private void Close()
        {
            writer?.Flush();
            writer?.Dispose();
            writer = null;
            pending = 0;
        }

        public void Dispose()
        {
            lock (fileLock) Close();
        }
    }
}
