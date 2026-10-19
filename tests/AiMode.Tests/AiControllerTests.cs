using GHelper.AiMode;
using Xunit;
using static GHelper.AiMode.AiPowerPlan;

namespace AiMode.Tests
{
    internal class FakeSensors : ISensorSource
    {
        public float? Temp = 70;
        public float? Util = 50;
        public long Ts = 1_000_000;

        public AiSample Read()
        {
            Ts += 1000;
            return new AiSample { Ts = Ts, CpuTemp = Temp, CpuUtil = Util };
        }
    }

    internal class FakeEnvironment : IAiEnvironment
    {
        public bool Ac = true;
        public bool Armoury;
        public readonly List<string> Notes = new();

        public bool OnAc() => Ac;
        public int Mode() => 1;
        public bool IsArmouryRunning() => Armoury;
        public void Notify(string message) => Notes.Add(message);
    }

    public class AiControllerTests : IDisposable
    {
        static readonly Guid Scheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");

        readonly string dir = Path.Combine(Path.GetTempPath(), "aimode-ctl-" + Guid.NewGuid());
        readonly FakePowerSettings power = new();
        readonly FakeSensors sensors = new();
        readonly FakeEnvironment env = new();
        readonly AiPowerPlan plan;
        readonly AiController controller;

        public AiControllerTests()
        {
            power.Active = Scheme;
            foreach (var s in new[] { FREQ_MAX, FREQ_MAX_1 }) { power.AC[(Scheme, s)] = 0; power.DC[(Scheme, s)] = 0; }
            foreach (var s in new[] { EPP, EPP_1 }) { power.AC[(Scheme, s)] = 30; power.DC[(Scheme, s)] = 50; }

            plan = new AiPowerPlan(power, Path.Combine(dir, "ai_restore.json"));
            controller = new AiController(plan, sensors, env);
        }

        public void Dispose()
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        uint? Cap => power.ReadAC(Scheme, GUID_CPU, FREQ_MAX);

        void Begin(bool active = true, string? logDir = null) => controller.Begin(new AiSettings(), active, logDir);

        [Fact]
        public void HotCpuGetsCapped()
        {
            Begin();
            sensors.Temp = 95;
            controller.Tick();

            Assert.True(plan.HasSnapshot);
            Assert.Equal((uint)(5400 - 500), Cap);
            Assert.Equal(AiRunState.Active, controller.Status.State);
            Assert.Equal(AiZone.Aggressive, controller.Status.Zone);
        }

        [Fact]
        public void StopRestoresOriginals()
        {
            Begin();
            sensors.Temp = 95;
            controller.Tick();
            controller.Stop("test");

            Assert.Equal(0u, Cap);
            Assert.Equal(30u, power.ReadAC(Scheme, GUID_CPU, EPP));
            Assert.Equal(AiRunState.Stopped, controller.Status.State);
        }

        [Fact]
        public void BatteryPausesAndRestoresThenResumes()
        {
            Begin();
            sensors.Temp = 95;
            controller.Tick();
            Assert.NotEqual(0u, Cap);

            env.Ac = false;
            controller.Tick();
            Assert.Equal(0u, Cap);
            Assert.Equal(AiRunState.Paused, controller.Status.State);
            Assert.Equal("battery", controller.Status.Reason);

            env.Ac = true;
            controller.Tick();
            Assert.Equal(AiRunState.Active, controller.Status.State);
            Assert.NotEqual(0u, Cap);
        }

        [Fact]
        public void DeadSensorPausesAfterThreeTicks()
        {
            Begin();
            sensors.Temp = 95;
            controller.Tick();

            sensors.Temp = null;
            controller.Tick();
            controller.Tick();
            Assert.Equal(AiRunState.Active, controller.Status.State);
            controller.Tick();
            Assert.Equal(AiRunState.Paused, controller.Status.State);
            Assert.Equal(0u, Cap);

            sensors.Temp = 95;
            controller.Tick();
            Assert.Equal(AiRunState.Active, controller.Status.State);
        }

        [Fact]
        public void OutOfRangeTemperatureCountsAsDead()
        {
            Begin();
            sensors.Temp = 200;
            for (int i = 0; i < 3; i++) controller.Tick();
            Assert.Equal(AiRunState.Paused, controller.Status.State);
        }

        [Fact]
        public void SingleExternalChangeIsAdopted()
        {
            Begin();
            sensors.Temp = 95;
            controller.Tick();

            // Someone sets EPP once (e.g. firmware after a mode switch)
            power.AC[(Scheme, EPP)] = 55;
            power.AC[(Scheme, EPP_1)] = 55;
            for (int i = 0; i < 5; i++) controller.Tick();

            Assert.Equal(AiRunState.Active, controller.Status.State);
            Assert.Empty(env.Notes);
            Assert.Equal(55, plan.OriginalEpp);

            controller.Stop("test");
            Assert.Equal(55u, power.ReadAC(Scheme, GUID_CPU, EPP));
            Assert.Equal(0u, Cap);
        }

        [Fact]
        public void SchemeSwitchRestoresOldSchemeAndFollowsNewOne()
        {
            var other = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
            foreach (var s in new[] { FREQ_MAX, FREQ_MAX_1 }) { power.AC[(other, s)] = 0; power.DC[(other, s)] = 0; }
            foreach (var s in new[] { EPP, EPP_1 }) { power.AC[(other, s)] = 20; power.DC[(other, s)] = 20; }

            Begin();
            sensors.Temp = 95;
            controller.Tick();
            Assert.NotEqual(0u, Cap);

            power.Active = other;
            controller.Tick();
            controller.Tick();

            Assert.Equal(AiRunState.Active, controller.Status.State);
            Assert.Equal(0u, Cap);
            Assert.Equal(other, plan.Snapshot!.Scheme);
            Assert.NotEqual(0u, power.ReadAC(other, GUID_CPU, FREQ_MAX));
        }

        [Fact]
        public void RepeatedExternalChangesStop()
        {
            Begin();
            sensors.Temp = 95;
            controller.Tick();

            bool stop = false;
            for (int i = 0; i < 10 && !stop; i++)
            {
                power.AC[(Scheme, FREQ_MAX)] = 4321;
                controller.Tick(out stop);
            }

            Assert.True(stop);
            Assert.Equal(AiRunState.Stopped, controller.Status.State);
            Assert.Equal("conflict", controller.Status.Reason);
            Assert.Single(env.Notes);
        }

        [Fact]
        public void SparseExternalChangesDontStop()
        {
            Begin();
            sensors.Temp = 95;
            controller.Tick();

            for (int i = 0; i < 5; i++)
            {
                power.AC[(Scheme, FREQ_MAX)] = 4321;
                controller.Tick();
                sensors.Ts += AiController.ConflictWindowMs;
            }

            Assert.Equal(AiRunState.Active, controller.Status.State);
        }

        [Fact]
        public void WriteErrorsStopAfterFive()
        {
            Begin();
            sensors.Temp = 95;
            controller.Tick();

            power.FailWrites = true;
            bool stop = false;
            for (int i = 0; i < 5 && !stop; i++) controller.Tick(out stop);

            Assert.True(stop);
            Assert.Equal("write errors", controller.Status.Reason);
        }

        [Fact]
        public void ArmouryCrateBlocksRegulator()
        {
            env.Armoury = true;
            Begin();
            sensors.Temp = 95;
            controller.Tick();

            Assert.Equal(AiRunState.Paused, controller.Status.State);
            Assert.Equal(0, power.Writes);
            Assert.False(plan.HasSnapshot);
        }

        [Fact]
        public void LogOnlyWritesCsvButNotPlan()
        {
            Begin(active: false, logDir: dir);
            sensors.Temp = 95;
            controller.Tick();
            controller.Tick();
            controller.Stop("test");

            Assert.Equal(0, power.Writes);
            var file = Directory.GetFiles(dir, "*.csv").Single();
            var lines = File.ReadAllLines(file);
            Assert.Equal(AiLogger.Header, lines[0]);
            Assert.Equal(3, lines.Length);
        }

        [Fact]
        public void LogOnlyStopKeepsValuesWrittenByOthers()
        {
            // Debug override applied while AI Mode is off for the mode
            plan.TakeSnapshot();
            plan.Apply(3000, null);

            Begin(active: false);
            controller.Tick();
            controller.Stop("restart");

            Assert.Equal(3000u, Cap);
        }

        [Fact]
        public void ActiveModeLogsDecisions()
        {
            Begin(logDir: dir);
            sensors.Temp = 95;
            controller.Tick();
            controller.Stop("test");

            var row = File.ReadAllLines(Directory.GetFiles(dir, "*.csv").Single())[1].Split(',');
            var header = AiLogger.Header.Split(',');
            Assert.Equal(header.Length, row.Length);
            Assert.Equal("4900", row[Array.IndexOf(header, "cap_mhz")]);
            Assert.Equal("aggressive", row[Array.IndexOf(header, "zone")]);
            Assert.Equal("pi", row[Array.IndexOf(header, "policy")]);
        }

        [Fact]
        public void BackgroundLoopRunsAndStops()
        {
            sensors.Temp = 95;
            controller.Start(new AiSettings { TickMs = 50, FastTickMs = 50 }, true, null);
            var deadline = DateTime.Now.AddSeconds(5);
            while (Cap == 0 && DateTime.Now < deadline) Thread.Sleep(20);
            Assert.NotEqual(0u, Cap);

            controller.Stop("test");
            Assert.False(controller.IsRunning);
            Assert.Equal(0u, Cap);
        }
    }

    public class AiLoggerTests : IDisposable
    {
        readonly string dir = Path.Combine(Path.GetTempPath(), "aimode-log-" + Guid.NewGuid());

        public void Dispose()
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        [Fact]
        public void EmptySensorsAreEmptyFields()
        {
            var line = AiLogger.Format(new AiSample { Ts = 5, CpuTemp = 80.25f, Mode = 1, OnAc = true, Zone = "working", Policy = "pi" });
            var fields = line.Split(',');
            Assert.Equal(AiLogger.Header.Split(',').Length, fields.Length);
            Assert.Equal("80.2", fields[1]);
            Assert.Equal("", fields[2]);
        }

        [Fact]
        public void RotatesBySizeAndKeepsNewestFiles()
        {
            using (var logger = new AiLogger(dir, new DateTime(2026, 10, 19, 12, 0, 0), maxBytes: 300, keepFiles: 3))
            {
                for (int i = 0; i < 100; i++)
                {
                    logger.Write(new AiSample { Ts = i, CpuTemp = 80, Zone = "working", Policy = "pi" });
                    logger.Flush();
                }
            }

            var files = Directory.GetFiles(dir, "*.csv");
            Assert.Equal(3, files.Length);
            Assert.All(files, f => Assert.Equal(AiLogger.Header, File.ReadLines(f).First()));
        }
    }
}
