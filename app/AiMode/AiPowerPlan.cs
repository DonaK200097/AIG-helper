using System.Text.Json;

namespace GHelper.AiMode
{
    public sealed class AiSettingValue
    {
        public uint? Ac { get; set; }
        public uint? Dc { get; set; }
    }

    /// <summary>
    /// Original values of the controlled settings, kept in memory and in ai_restore.json.
    /// </summary>
    public sealed class AiPlanSnapshot
    {
        public Guid Scheme { get; set; }
        public long Ts { get; set; }
        public Dictionary<Guid, AiSettingValue> Values { get; set; } = new();

        public uint? GetAC(Guid setting) => Values.TryGetValue(setting, out var v) ? v.Ac : null;
    }

    /// <summary>
    /// Writes max frequency and EPP for P- and E-cores into the active power scheme,
    /// keeps a snapshot of the original values and restores them.
    /// Only AC values are written; restore puts back both AC and DC.
    /// </summary>
    public class AiPowerPlan
    {
        public static readonly Guid GUID_CPU = new("54533251-82be-4824-96c1-47b60b740d00");

        public static readonly Guid FREQ_MAX = new("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
        public static readonly Guid FREQ_MAX_1 = new("75b0ae3f-bce0-45a7-8c89-c9611c25e101");
        public static readonly Guid EPP = new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
        public static readonly Guid EPP_1 = new("36687f9e-e3a5-4dbf-b1dc-15eb381c6864");

        public static readonly Guid[] Controlled = [FREQ_MAX, FREQ_MAX_1, EPP, EPP_1];

        public const int NoLimit = 0;
        public const int MinCapMhz = 1800;
        public const int CapStepMhz = 100;
        public const int MaxEpp = 80;

        private readonly IPowerSettings power;
        private readonly string restoreFile;
        private readonly Action<string> log;
        private readonly object planLock = new();

        private AiPlanSnapshot? snapshot;

        public AiPowerPlan(IPowerSettings power, string restoreFile, Action<string>? log = null)
        {
            this.power = power;
            this.restoreFile = restoreFile;
            this.log = log ?? (_ => { });
        }

        public AiPlanSnapshot? Snapshot => snapshot;
        public bool HasSnapshot => snapshot is not null;

        public int? LastCap { get; private set; }
        public int? LastEpp { get; private set; }

        /// <summary>
        /// Captures original values of the active scheme and persists them before anything is written.
        /// If ai_restore.json is still on disk (previous run did not restore), its values are the originals and win.
        /// </summary>
        public bool TakeSnapshot()
        {
            lock (planLock)
            {
                if (snapshot is not null) return true;

                var saved = LoadRestoreFile();
                if (saved is not null)
                {
                    snapshot = saved;
                    log($"AI Plan: snapshot reused from {restoreFile}");
                    return true;
                }

                Guid? scheme = power.GetActiveScheme();
                if (scheme is null)
                {
                    log("AI Plan: can't read active scheme");
                    return false;
                }

                var taken = new AiPlanSnapshot { Scheme = scheme.Value, Ts = DateTimeOffset.Now.ToUnixTimeMilliseconds() };
                foreach (var setting in Controlled)
                    taken.Values[setting] = new AiSettingValue
                    {
                        Ac = power.ReadAC(scheme.Value, GUID_CPU, setting),
                        Dc = power.ReadDC(scheme.Value, GUID_CPU, setting),
                    };

                if (taken.GetAC(FREQ_MAX) is null || taken.GetAC(EPP) is null)
                {
                    log("AI Plan: max frequency / EPP settings are not readable");
                    return false;
                }

                try
                {
                    SaveRestoreFile(taken);
                }
                catch (Exception ex)
                {
                    // Without a restore file a crash would leave the cap in place, so we don't write anything
                    log("AI Plan: can't save restore file: " + ex.Message);
                    return false;
                }

                snapshot = taken;
                log("AI Plan: snapshot " + Describe(taken));
                return true;
            }
        }

        public static int ClampCap(int capMhz)
        {
            if (capMhz <= NoLimit) return NoLimit;
            int rounded = (int)Math.Round(capMhz / (double)CapStepMhz, MidpointRounding.AwayFromZero) * CapStepMhz;
            return Math.Max(MinCapMhz, rounded);
        }

        public static int ClampEpp(int epp, int modeEpp)
        {
            int floor = Math.Min(modeEpp, MaxEpp);
            return Math.Clamp(epp, floor, MaxEpp);
        }

        /// <summary>
        /// Writes cap (MHz, 0 = no limit) and EPP to both core classes. Null leaves a parameter untouched.
        /// Requires a snapshot. Returns false if any write failed.
        /// </summary>
        public bool Apply(int? capMhz, int? epp)
        {
            lock (planLock)
            {
                if (snapshot is null)
                {
                    log("AI Plan: apply without snapshot");
                    return false;
                }

                Guid? active = power.GetActiveScheme();
                if (active != snapshot.Scheme)
                {
                    log($"AI Plan: active scheme {active} differs from snapshot {snapshot.Scheme}");
                    return false;
                }

                bool ok = true;
                bool changed = false;

                if (capMhz is not null)
                {
                    int cap = ClampCap(capMhz.Value);
                    ok &= WriteIfChanged(FREQ_MAX, (uint)cap, ref changed);
                    ok &= WriteIfChanged(FREQ_MAX_1, (uint)cap, ref changed);
                    LastCap = cap;
                }

                if (epp is not null)
                {
                    int modeEpp = (int)(snapshot.GetAC(EPP) ?? 0);
                    int value = ClampEpp(epp.Value, modeEpp);
                    ok &= WriteIfChanged(EPP, (uint)value, ref changed);
                    ok &= WriteIfChanged(EPP_1, (uint)value, ref changed);
                    LastEpp = value;
                }

                if (changed && !power.SetActiveScheme(snapshot.Scheme))
                {
                    log("AI Plan: can't re-apply active scheme");
                    ok = false;
                }

                return ok;
            }
        }

        private bool WriteIfChanged(Guid setting, uint value, ref bool changed)
        {
            // Settings that were missing at snapshot time (e.g. no E-core class) are skipped
            if (snapshot!.GetAC(setting) is null) return true;
            if (power.ReadAC(snapshot.Scheme, GUID_CPU, setting) == value) return true;

            if (!power.WriteAC(snapshot.Scheme, GUID_CPU, setting, value))
            {
                log($"AI Plan: write {Name(setting)}={value} failed");
                return false;
            }

            changed = true;
            return true;
        }

        /// <summary>
        /// Writes original AC and DC values back and deletes ai_restore.json.
        /// The snapshot scheme is re-activated only if it is still the active one.
        /// </summary>
        public bool Restore(string reason)
        {
            lock (planLock)
            {
                if (snapshot is null) return true;

                bool ok = WriteBack(snapshot);
                log($"AI Plan: restore ({reason}) " + (ok ? "OK" : "with errors") + ": " + Describe(snapshot));

                if (ok)
                {
                    snapshot = null;
                    LastCap = LastEpp = null;
                    DeleteRestoreFile();
                }

                return ok;
            }
        }

        /// <summary>
        /// Startup recovery: if a previous run left ai_restore.json behind, puts those values back.
        /// </summary>
        public bool RestoreFromFile()
        {
            lock (planLock)
            {
                if (snapshot is not null) return true;

                var saved = LoadRestoreFile();
                if (saved is null) return true;

                log("AI Plan: found restore file from previous run");
                snapshot = saved;
            }

            return Restore("recovery");
        }

        private bool WriteBack(AiPlanSnapshot snap)
        {
            bool ok = true;
            foreach (var (setting, value) in snap.Values)
            {
                if (value.Ac is uint ac) ok &= power.WriteAC(snap.Scheme, GUID_CPU, setting, ac);
                if (value.Dc is uint dc) ok &= power.WriteDC(snap.Scheme, GUID_CPU, setting, dc);
            }

            if (power.GetActiveScheme() == snap.Scheme)
                ok &= power.SetActiveScheme(snap.Scheme);

            return ok;
        }

        /// <summary>
        /// Current AC values of the active scheme, for logging and debugging.
        /// </summary>
        public string DescribeActive()
        {
            Guid? scheme = power.GetActiveScheme();
            if (scheme is null) return "no active scheme";
            return scheme + " " + string.Join(", ", Controlled.Select(s => $"{Name(s)}={power.ReadAC(scheme.Value, GUID_CPU, s)?.ToString() ?? "-"}"));
        }

        private static string Describe(AiPlanSnapshot snap) =>
            snap.Scheme + " " + string.Join(", ", snap.Values.Select(v => $"{Name(v.Key)}={v.Value.Ac?.ToString() ?? "-"}/{v.Value.Dc?.ToString() ?? "-"}"));

        public static string Name(Guid setting)
        {
            if (setting == FREQ_MAX) return "FreqMax";
            if (setting == FREQ_MAX_1) return "FreqMax1";
            if (setting == EPP) return "EPP";
            if (setting == EPP_1) return "EPP1";
            return setting.ToString();
        }

        private void SaveRestoreFile(AiPlanSnapshot snap)
        {
            string? dir = Path.GetDirectoryName(restoreFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmp = restoreFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, restoreFile, overwrite: true);
        }

        private AiPlanSnapshot? LoadRestoreFile()
        {
            if (!File.Exists(restoreFile)) return null;
            try
            {
                var snap = JsonSerializer.Deserialize<AiPlanSnapshot>(File.ReadAllText(restoreFile));
                if (snap is null || snap.Scheme == Guid.Empty || snap.Values.Count == 0) throw new InvalidDataException("empty snapshot");
                return snap;
            }
            catch (Exception ex)
            {
                log($"AI Plan: broken restore file {restoreFile}: {ex.Message}");
                return null;
            }
        }

        private void DeleteRestoreFile()
        {
            try
            {
                File.Delete(restoreFile);
            }
            catch (Exception ex)
            {
                log("AI Plan: can't delete restore file: " + ex.Message);
            }
        }
    }
}
