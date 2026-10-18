using GHelper.AiMode;
using Xunit;
using static GHelper.AiMode.AiPowerPlan;

namespace AiMode.Tests
{
    internal class FakePowerSettings : IPowerSettings
    {
        public Guid? Active;
        public readonly Dictionary<(Guid, Guid), uint> AC = new();
        public readonly Dictionary<(Guid, Guid), uint> DC = new();
        public int Writes;
        public int Activations;
        public bool FailWrites;

        public Guid? GetActiveScheme() => Active;
        public bool SetActiveScheme(Guid scheme) { Activations++; return true; }

        public uint? ReadAC(Guid scheme, Guid subgroup, Guid setting) => AC.TryGetValue((scheme, setting), out var v) ? v : null;
        public uint? ReadDC(Guid scheme, Guid subgroup, Guid setting) => DC.TryGetValue((scheme, setting), out var v) ? v : null;

        public bool WriteAC(Guid scheme, Guid subgroup, Guid setting, uint value)
        {
            if (FailWrites) return false;
            Writes++;
            AC[(scheme, setting)] = value;
            return true;
        }

        public bool WriteDC(Guid scheme, Guid subgroup, Guid setting, uint value)
        {
            if (FailWrites) return false;
            Writes++;
            DC[(scheme, setting)] = value;
            return true;
        }
    }

    public class AiPowerPlanTests : IDisposable
    {
        static readonly Guid Scheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");
        static readonly Guid Other = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

        readonly string dir = Path.Combine(Path.GetTempPath(), "aimode-tests-" + Guid.NewGuid());
        string RestoreFile => Path.Combine(dir, "ai_restore.json");

        readonly FakePowerSettings power = new();

        public AiPowerPlanTests()
        {
            power.Active = Scheme;
            Set(FREQ_MAX, 0, 0);
            Set(FREQ_MAX_1, 0, 0);
            Set(EPP, 25, 50);
            Set(EPP_1, 25, 50);
        }

        public void Dispose()
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        void Set(Guid setting, uint ac, uint dc)
        {
            power.AC[(Scheme, setting)] = ac;
            power.DC[(Scheme, setting)] = dc;
        }

        uint? AC(Guid setting) => power.ReadAC(Scheme, GUID_CPU, setting);
        uint? DC(Guid setting) => power.ReadDC(Scheme, GUID_CPU, setting);

        AiPowerPlan NewPlan() => new(power, RestoreFile);

        [Fact]
        public void ApplyWritesAcForBothCoreClasses()
        {
            var plan = NewPlan();
            Assert.True(plan.TakeSnapshot());
            Assert.True(File.Exists(RestoreFile));

            Assert.True(plan.Apply(3000, 60));

            Assert.Equal(3000u, AC(FREQ_MAX));
            Assert.Equal(3000u, AC(FREQ_MAX_1));
            Assert.Equal(60u, AC(EPP));
            Assert.Equal(60u, AC(EPP_1));
            Assert.Equal(0u, DC(FREQ_MAX));
            Assert.Equal(50u, DC(EPP));
            Assert.Equal(1, power.Activations);
        }

        [Fact]
        public void ApplyRequiresSnapshot()
        {
            Assert.False(NewPlan().Apply(3000, null));
            Assert.Equal(0, power.Writes);
        }

        [Fact]
        public void ApplySkipsUnchangedValues()
        {
            var plan = NewPlan();
            plan.TakeSnapshot();
            plan.Apply(3000, 60);
            int writes = power.Writes, activations = power.Activations;

            Assert.True(plan.Apply(3000, 60));
            Assert.Equal(writes, power.Writes);
            Assert.Equal(activations, power.Activations);
        }

        [Fact]
        public void NullLeavesParameterUntouched()
        {
            var plan = NewPlan();
            plan.TakeSnapshot();
            plan.Apply(2500, null);

            Assert.Equal(2500u, AC(FREQ_MAX));
            Assert.Equal(25u, AC(EPP));
        }

        [Fact]
        public void ApplyRefusedWhenActiveSchemeChanged()
        {
            var plan = NewPlan();
            plan.TakeSnapshot();
            power.Active = Other;

            Assert.False(plan.Apply(3000, 60));
            Assert.Equal(0, power.Writes);
        }

        [Fact]
        public void RestorePutsBackAcAndDcAndDeletesFile()
        {
            var plan = NewPlan();
            plan.TakeSnapshot();
            plan.Apply(2000, 80);
            power.DC[(Scheme, EPP)] = 99;

            Assert.True(plan.Restore("test"));

            Assert.Equal(0u, AC(FREQ_MAX));
            Assert.Equal(0u, AC(FREQ_MAX_1));
            Assert.Equal(25u, AC(EPP));
            Assert.Equal(50u, DC(EPP));
            Assert.False(File.Exists(RestoreFile));
            Assert.False(plan.HasSnapshot);
        }

        [Fact]
        public void RestoreDoesNotActivateSchemeThatIsNoLongerActive()
        {
            var plan = NewPlan();
            plan.TakeSnapshot();
            plan.Apply(2000, null);
            int activations = power.Activations;
            power.Active = Other;

            Assert.True(plan.Restore("test"));
            Assert.Equal(0u, AC(FREQ_MAX));
            Assert.Equal(activations, power.Activations);
        }

        [Fact]
        public void FailedRestoreKeepsFileForNextStart()
        {
            var plan = NewPlan();
            plan.TakeSnapshot();
            plan.Apply(2000, null);
            power.FailWrites = true;

            Assert.False(plan.Restore("test"));
            Assert.True(File.Exists(RestoreFile));
            Assert.True(plan.HasSnapshot);
        }

        [Fact]
        public void RecoveryAfterCrashRestoresOriginals()
        {
            var crashed = NewPlan();
            crashed.TakeSnapshot();
            crashed.Apply(1800, 80);

            var next = NewPlan();
            Assert.True(next.RestoreFromFile());

            Assert.Equal(0u, AC(FREQ_MAX));
            Assert.Equal(25u, AC(EPP));
            Assert.False(File.Exists(RestoreFile));
        }

        [Fact]
        public void RecoveryWithoutFileDoesNothing()
        {
            Assert.True(NewPlan().RestoreFromFile());
            Assert.Equal(0, power.Writes);
        }

        [Fact]
        public void SnapshotReusesRestoreFileInsteadOfModifiedValues()
        {
            var crashed = NewPlan();
            crashed.TakeSnapshot();
            crashed.Apply(1800, 80);

            var next = NewPlan();
            Assert.True(next.TakeSnapshot());
            Assert.Equal(0u, next.Snapshot!.GetAC(FREQ_MAX));
            Assert.Equal(25u, next.Snapshot!.GetAC(EPP));
        }

        [Fact]
        public void BrokenRestoreFileIsIgnored()
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(RestoreFile, "{ not json");

            Assert.True(NewPlan().RestoreFromFile());
            Assert.Equal(0, power.Writes);
        }

        [Fact]
        public void SnapshotFailsWhenRestoreFileCantBeWritten()
        {
            Directory.CreateDirectory(dir);
            // A directory in place of the file makes the write fail
            Directory.CreateDirectory(RestoreFile);

            var plan = new AiPowerPlan(power, RestoreFile);
            Assert.False(plan.TakeSnapshot());
            Assert.False(plan.Apply(2000, null));
            Assert.Equal(0, power.Writes);
        }

        [Fact]
        public void SnapshotFailsWithoutFrequencySetting()
        {
            power.AC.Remove((Scheme, FREQ_MAX));
            Assert.False(NewPlan().TakeSnapshot());
        }

        [Fact]
        public void MissingECoreSettingIsSkipped()
        {
            power.AC.Remove((Scheme, FREQ_MAX_1));
            power.DC.Remove((Scheme, FREQ_MAX_1));

            var plan = NewPlan();
            Assert.True(plan.TakeSnapshot());
            Assert.True(plan.Apply(3000, null));
            Assert.Null(AC(FREQ_MAX_1));
            Assert.Equal(3000u, AC(FREQ_MAX));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(-5, 0)]
        [InlineData(1000, MinCapMhz)]
        [InlineData(1849, MinCapMhz)]
        [InlineData(3049, 3000)]
        [InlineData(3050, 3100)]
        [InlineData(5400, 5400)]
        public void CapIsRoundedAndClamped(int input, int expected)
        {
            Assert.Equal(expected, ClampCap(input));
        }

        [Theory]
        [InlineData(10, 25, 25)]
        [InlineData(60, 25, 60)]
        [InlineData(95, 25, MaxEpp)]
        [InlineData(10, 90, MaxEpp)]
        public void EppIsClampedBetweenModeValueAndMax(int input, int modeEpp, int expected)
        {
            Assert.Equal(expected, ClampEpp(input, modeEpp));
        }

        [Fact]
        public void EppBelowModeValueIsRaisedToIt()
        {
            var plan = NewPlan();
            plan.TakeSnapshot();
            plan.Apply(null, 10);
            Assert.Equal(25u, AC(EPP));
        }
    }
}
