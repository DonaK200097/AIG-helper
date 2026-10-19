namespace GHelper.AiMode
{
    public enum AiRunState { Stopped, Active, LogOnly, Paused }

    public sealed class AiStatus
    {
        public AiRunState State { get; init; }
        public string Reason { get; init; } = "";
        public string Policy { get; init; } = "";
        public AiZone? Zone { get; init; }
        public bool Idle { get; init; }
        public int? CapMhz { get; init; }
        public int? Epp { get; init; }
        public float? CpuTemp { get; init; }
        public float? CpuUtil { get; init; }
        public int? CeilingMhz { get; init; }
        public string? LogFile { get; init; }
    }

    /// <summary>
    /// What the controller needs from the rest of the app.
    /// </summary>
    public interface IAiEnvironment
    {
        bool OnAc();
        int Mode();
        bool IsArmouryRunning();
        void Notify(string message);
    }

    /// <summary>
    /// Control loop on a background thread: sensors → policy → plan → log.
    /// Safety: pauses (with restore) on battery and on a dead temperature sensor,
    /// stops on external changes to our values and on repeated write errors.
    /// </summary>
    public class AiController
    {
        public const int MaxBadTemp = 3;
        // External changes are accepted (rebase); this many within the window means a fight, then we stop
        public const int MaxConflicts = 3;
        public const int ConflictWindowMs = 60000;
        public const int MaxWriteErrors = 5;
        public const float MinValidTemp = 0;
        public const float MaxValidTemp = 110;
        public const int PausedTickMs = 1000;

        private readonly AiPowerPlan plan;
        private readonly ISensorSource sensors;
        private readonly IAiEnvironment env;
        private readonly Action<string> log;
        private readonly Func<AiSettings, string?, AiLogger?> loggerFactory;
        private readonly object tickLock = new();

        private AiSettings settings = new();
        private IAiPolicy policy;
        private ZonePiPolicy pi;
        private readonly AiHistory history = new();
        private AiLogger? csv;

        private bool active;
        private bool paused;
        private string pauseReason = "";
        private int badTemp, writeErrors;
        private readonly Queue<long> conflicts = new();

        private Thread? thread;
        private CancellationTokenSource? cts;

        private volatile AiStatus status = new() { State = AiRunState.Stopped };

        public event Action? StatusChanged;

        public AiController(AiPowerPlan plan, ISensorSource sensors, IAiEnvironment env, Action<string>? log = null,
            Func<AiSettings, string?, AiLogger?>? loggerFactory = null)
        {
            this.plan = plan;
            this.sensors = sensors;
            this.env = env;
            this.log = log ?? (_ => { });
            this.loggerFactory = loggerFactory ?? ((_, dir) => dir is null ? null : new AiLogger(dir));
            pi = new ZonePiPolicy(settings);
            policy = pi;
        }

        public AiStatus Status => status;
        public bool IsRunning => thread is not null;
        public AiHistory History => history;
        public AiSettings Settings => settings;

        /// <summary>
        /// Prepares state for a new session. Used by Start, and directly by tests that drive Tick().
        /// </summary>
        public void Begin(AiSettings newSettings, bool activeMode, string? logDir)
        {
            lock (tickLock)
            {
                settings = newSettings;
                pi = new ZonePiPolicy(settings);
                policy = settings.Explore ? new ExplorePolicy(pi, settings) : pi;
                history.Clear();
                active = activeMode;
                paused = false;
                pauseReason = "";
                badTemp = writeErrors = 0;
                conflicts.Clear();

                csv?.Dispose();
                csv = null;
                try
                {
                    csv = loggerFactory(settings, logDir);
                }
                catch (Exception ex)
                {
                    log("AI: can't open telemetry log: " + ex.Message);
                }

                if (active && env.IsArmouryRunning())
                {
                    active = false;
                    env.Notify("AI Mode: Armoury Crate is running");
                    SetStatus(AiRunState.Paused, "Armoury Crate");
                    log("AI: Armoury Crate is running, regulator not started");
                }
                else
                {
                    SetStatus(active ? AiRunState.Active : AiRunState.LogOnly, "");
                }

                log($"AI: start {(active ? "regulator" : "log only")}, target {settings.Target}°C, offset {settings.Offset}°C, {settings.MinMhz}-{settings.TargetMhz} MHz{(settings.Explore ? ", explore" : "")}");
            }
        }

        public void Start(AiSettings newSettings, bool activeMode, string? logDir, int initialDelayMs = 0)
        {
            Stop("restart");
            Begin(newSettings, activeMode, logDir);
            if (initialDelayMs > 0 && active) SetStatus(AiRunState.Paused, "starting");

            cts = new CancellationTokenSource();
            var token = cts.Token;
            thread = new Thread(() => Run(token, initialDelayMs)) { IsBackground = true, Name = "AI Mode", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        public void UpdateSettings(AiSettings newSettings)
        {
            lock (tickLock)
            {
                newSettings.Explore = settings.Explore;
                settings = newSettings;
                pi.UpdateSettings(newSettings);
                if (policy is ExplorePolicy explore) explore.UpdateSettings(newSettings);
                log($"AI: settings target {settings.Target}°C, offset {settings.Offset}°C, {settings.MinMhz}-{settings.TargetMhz} MHz");
            }
        }

        /// <summary>
        /// Stops the loop and restores original plan values.
        /// </summary>
        public void Stop(string reason)
        {
            var t = thread;
            if (t is not null)
            {
                cts?.Cancel();
                if (t != Thread.CurrentThread) t.Join(3000);
                thread = null;
                cts?.Dispose();
                cts = null;
            }

            lock (tickLock)
            {
                End(reason);
            }
        }

        private void End(string reason)
        {
            // Log-only sessions never wrote the plan: leave values (e.g. the debug override) alone
            if (active) plan.Restore(reason);
            active = false;
            csv?.Dispose();
            csv = null;
            if (status.State != AiRunState.Stopped) log($"AI: stop ({reason})");
            SetStatus(AiRunState.Stopped, reason);
        }

        private void Run(CancellationToken token, int initialDelayMs)
        {
            if (initialDelayMs > 0 && token.WaitHandle.WaitOne(initialDelayMs)) return;

            while (!token.IsCancellationRequested)
            {
                long started = Environment.TickCount64;
                int delay;
                bool stop;
                try
                {
                    delay = Tick(out stop);
                }
                catch (Exception ex)
                {
                    log("AI: tick failed: " + ex);
                    delay = PausedTickMs;
                    stop = false;
                }

                if (stop)
                {
                    // Tick already restored values; just leave the thread
                    thread = null;
                    return;
                }

                int wait = Math.Max(50, delay - (int)(Environment.TickCount64 - started));
                if (token.WaitHandle.WaitOne(wait)) return;
            }
        }

        public int Tick() => Tick(out _);

        /// <summary>
        /// One control step. Returns delay until the next one; stop = controller gave up and restored.
        /// </summary>
        public int Tick(out bool stop)
        {
            lock (tickLock)
            {
                stop = false;
                if (status.State == AiRunState.Stopped) { stop = true; return 0; }

                var sample = sensors.Read();
                sample.Mode = env.Mode();
                sample.OnAc = env.OnAc();

                if (!active)
                {
                    sample.Policy = "off";
                    Record(sample);
                    return settings.TickMs;
                }

                if (!sample.OnAc)
                {
                    Pause("battery");
                    sample.Policy = "paused";
                    Record(sample);
                    return PausedTickMs;
                }

                if (sample.CpuTemp is not float temp || temp <= MinValidTemp || temp > MaxValidTemp)
                {
                    sample.CpuTemp = null;
                    if (++badTemp >= MaxBadTemp) Pause("sensor");
                    sample.Policy = paused ? "paused" : policy.Name;
                    Record(sample);
                    return settings.TickMs;
                }
                badTemp = 0;

                if (paused && !Resume())
                {
                    sample.Policy = "paused";
                    Record(sample);
                    return PausedTickMs;
                }

                if (!plan.HasSnapshot && !Resume())
                {
                    Record(sample);
                    return PausedTickMs;
                }

                if ((plan.LastCap is not null || plan.LastEpp is not null) && plan.IsChangedExternally())
                {
                    conflicts.Enqueue(sample.Ts);
                    while (conflicts.Count > 0 && sample.Ts - conflicts.Peek() > ConflictWindowMs) conflicts.Dequeue();

                    log($"AI: external change #{conflicts.Count}: {plan.DescribeExternalChange()}");

                    if (conflicts.Count >= MaxConflicts)
                    {
                        env.Notify("AI Mode stopped: power plan keeps being changed by another program");
                        log("AI: conflict, values keep changing externally");
                        End("conflict");
                        stop = true;
                        return 0;
                    }

                    if (!plan.Rebase())
                    {
                        Pause("snapshot");
                        Record(sample);
                        return PausedTickMs;
                    }
                    policy.Reset();
                }

                var state = new AiPlanState(plan.LastCap ?? plan.OriginalCap, plan.LastEpp ?? plan.OriginalEpp, plan.OriginalEpp);
                var decision = policy.Decide(sample, history, state);

                if (plan.Apply(decision.CapMhz, decision.Epp))
                {
                    writeErrors = 0;
                }
                else if (++writeErrors >= MaxWriteErrors)
                {
                    env.Notify("AI Mode stopped: can't write power plan");
                    log("AI: too many write errors");
                    End("write errors");
                    stop = true;
                    return 0;
                }

                sample.CapMhz = plan.LastCap;
                sample.Epp = plan.LastEpp;
                sample.CeilingMhz = decision.CeilingMhz;
                sample.Zone = decision.Idle ? "idle" : decision.Zone.ToString().ToLowerInvariant();
                sample.Policy = policy.Name;
                Record(sample);

                status = new AiStatus
                {
                    State = AiRunState.Active,
                    Policy = policy.Name,
                    Zone = decision.Zone,
                    Idle = decision.Idle,
                    CapMhz = plan.LastCap,
                    Epp = plan.LastEpp,
                    CpuTemp = sample.CpuTemp,
                    CpuUtil = sample.CpuUtil,
                    CeilingMhz = decision.CeilingMhz,
                    LogFile = csv?.CurrentFile,
                };
                StatusChanged?.Invoke();

                return decision.NextTickMs;
            }
        }

        private void Record(AiSample sample)
        {
            history.Add(sample);
            try
            {
                csv?.Write(sample);
            }
            catch (Exception ex)
            {
                log("AI: log write failed: " + ex.Message);
                csv?.Dispose();
                csv = null;
            }

            if (status.State != AiRunState.Active)
            {
                status = new AiStatus
                {
                    State = status.State,
                    Reason = status.Reason,
                    CpuTemp = sample.CpuTemp,
                    CpuUtil = sample.CpuUtil,
                    LogFile = csv?.CurrentFile,
                };
                StatusChanged?.Invoke();
            }
        }

        private void Pause(string reason)
        {
            if (paused && pauseReason == reason) return;
            plan.Restore(reason);
            paused = true;
            pauseReason = reason;
            log($"AI: paused ({reason})");
            SetStatus(AiRunState.Paused, reason);
        }

        private bool Resume()
        {
            if (!plan.TakeSnapshot())
            {
                if (!paused) log("AI: can't take snapshot");
                paused = true;
                pauseReason = "snapshot";
                SetStatus(AiRunState.Paused, "snapshot");
                return false;
            }

            if (paused) log($"AI: resumed after {pauseReason}");
            paused = false;
            pauseReason = "";
            policy.Reset();
            history.Clear();
            writeErrors = 0;
            return true;
        }

        private void SetStatus(AiRunState state, string reason)
        {
            status = new AiStatus { State = state, Reason = reason, Policy = active ? policy.Name : "", LogFile = csv?.CurrentFile };
            StatusChanged?.Invoke();
        }
    }
}
