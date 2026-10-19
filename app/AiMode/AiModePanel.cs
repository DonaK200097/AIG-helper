using GHelper.Helpers;
using GHelper.UI;
using System.Diagnostics;

namespace GHelper.AiMode
{
    /// <summary>
    /// AI Mode section in the CPU tab of Fans + Power, built in code so Fans.Designer.cs stays untouched.
    /// Layout values are in 192 DPI units like the designer file and scaled to the form's DPI.
    /// </summary>
    internal class AiModePanel : Panel
    {
        readonly RCheckBox checkEnable = new();
        readonly RTrackBar trackTarget = new();
        readonly RTrackBar trackOffset = new();
        readonly Label labelTargetValue = new();
        readonly Label labelOffsetValue = new();
        readonly Label labelOffsetTitle = new();
        readonly Label labelStatus = new();
        readonly RCheckBox checkLog = new();
        readonly RButton buttonLogs = new();
        readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 1000 };

        bool loading;

        public static AiModePanel? Attach(Form form, Panel parent)
        {
            if (!AiModeHooks.IsSupported) return null;

            var panel = new AiModePanel();
            parent.Controls.Add(panel);
            // Docked panels stack in reverse z-order: front-most ends up at the bottom, below "Apply Power Limits"
            panel.BringToFront();

            float scale = form.CurrentAutoScaleDimensions.Width / 192f;
            if (Math.Abs(scale - 1) > 0.01) panel.Scale(new SizeF(scale, scale));

            form.FormClosed += (_, _) => panel.statusTimer.Dispose();
            panel.Init();
            return panel;
        }

        AiModePanel()
        {
            Name = "panelAiMode";
            Dock = DockStyle.Top;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Size = new Size(520, 520);

            // Added bottom to top, as the designer does for DockStyle.Top
            Controls.Add(Row("panelAiLog", 76, 15, LogRow()));
            Controls.Add(Row("panelAiStatus", 84, 0, StatusRow()));
            Controls.Add(Slider("panelAiOffset", labelOffsetTitle, labelOffsetValue, trackOffset, AiSettings.MinOffset, AiSettings.MaxOffset, 1));
            Controls.Add(Slider("panelAiTarget", new Label { Text = AiText.Target }, labelTargetValue, trackTarget, AiSettings.MinTarget, AiSettings.MaxTarget, 5));
            Controls.Add(Row("panelAiEnable", 76, 15, CheckBox(checkEnable, AiText.Enable)));
            Controls.Add(Title());

            checkEnable.Click += CheckEnable_Click;
            checkLog.Click += CheckLog_Click;
            buttonLogs.Click += ButtonLogs_Click;

            trackTarget.Scroll += (_, _) => SliderScroll(trackTarget);
            trackOffset.Scroll += (_, _) => SliderScroll(trackOffset);
            foreach (var track in new[] { trackTarget, trackOffset })
            {
                track.MouseUp += (_, _) => SaveSliders();
                track.KeyUp += (_, _) => SaveSliders();
            }

            statusTimer.Tick += (_, _) => { if (Visible) UpdateStatus(); };
            statusTimer.Start();
        }

        public void Init()
        {
            loading = true;

            var (target, offset) = AiSettings.Normalize(AppConfig.GetMode("ai_target", 87), AppConfig.GetMode("ai_offset", 5));
            trackTarget.Value = target;
            trackOffset.Value = offset;
            checkEnable.Checked = AiModeHooks.IsEnabled();
            checkLog.Checked = AppConfig.Is("ai_log");

            loading = false;

            VisualiseSliders();
            UpdateStatus();
        }

        void CheckEnable_Click(object? sender, EventArgs e)
        {
            AppConfig.SetMode("ai_mode", checkEnable.Checked ? 1 : 0);
            VisualiseSliders();

            if (checkEnable.Checked)
            {
                var warnings = AiModeHooks.GetWarnings();
                if (warnings.Count > 0)
                    MessageBox.Show(string.Join("\n\n", warnings), AiText.WarningTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            Restart();
        }

        void CheckLog_Click(object? sender, EventArgs e)
        {
            AppConfig.Set("ai_log", checkLog.Checked ? 1 : 0);
            Restart();
        }

        void ButtonLogs_Click(object? sender, EventArgs e)
        {
            try
            {
                Directory.CreateDirectory(AiModeHooks.LogDir);
                Process.Start(new ProcessStartInfo(AiModeHooks.LogDir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.WriteLine("AI Logs: " + ex.Message);
            }
        }

        static void Restart()
        {
            Task.Run(AiModeHooks.OnModeChanged);
        }

        void SliderScroll(RTrackBar source)
        {
            if (loading) return;

            // Target + offset can't go above 100 °C: the offset gives way
            var (target, offset) = AiSettings.Normalize(trackTarget.Value, trackOffset.Value);
            if (source == trackOffset && offset != trackOffset.Value) trackOffset.Value = offset;
            if (source == trackTarget && offset != trackOffset.Value) trackOffset.Value = offset;

            VisualiseSliders();
        }

        void SaveSliders()
        {
            if (loading) return;
            var (target, offset) = AiSettings.Normalize(trackTarget.Value, trackOffset.Value);
            AppConfig.SetMode("ai_target", target);
            AppConfig.SetMode("ai_offset", offset);
            AiModeHooks.OnSettingsChanged();
        }

        void VisualiseSliders()
        {
            labelTargetValue.Text = TempHelper.FormatTemp(trackTarget.Value);
            labelOffsetValue.Text = "+" + trackOffset.Value + "°";
            labelOffsetTitle.Text = AiText.Offset + " · " + AiText.AggressiveAbove(trackTarget.Value + trackOffset.Value);

            trackTarget.Enabled = trackOffset.Enabled = checkEnable.Checked;
        }

        void UpdateStatus()
        {
            string text;
            bool enabled = AiModeHooks.IsEnabled();
            bool log = AppConfig.Is("ai_log");

            if (!enabled && !log)
            {
                text = AiText.Off;
            }
            else
            {
                var s = AiModeHooks.Controller.Status;
                string temp = s.CpuTemp is float t ? " · " + TempHelper.FormatTemp((int)Math.Round(t)) : "";

                text = s.State switch
                {
                    AiRunState.Active =>
                        $"{AiText.Source}: {(s.Policy == "explore" ? AiText.Explore : AiText.Regulator)} · {AiText.Zone(s.Zone, s.Idle)}{temp}\n" +
                        $"{AiText.Cap}: {(s.CapMhz is null or 0 ? AiText.NoLimit : s.CapMhz + " " + AiText.MHz)} · EPP {s.Epp}",
                    AiRunState.Paused => $"{AiText.Paused}: {AiText.Reason(s.Reason)}{temp}",
                    AiRunState.LogOnly => $"{AiText.LogOnly}{temp}",
                    _ => s.Reason is "conflict" or "write errors" ? $"{AiText.Stopped}: {AiText.Reason(s.Reason)}" : AiText.Off,
                };
            }

            if (labelStatus.Text != text) labelStatus.Text = text;
        }

        Panel Title()
        {
            var panel = new Panel { Name = "panelAiTitle", Dock = DockStyle.Top, Size = new Size(520, 60) };
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Location = new Point(10, 16),
                Text = AiText.Title,
            });
            return panel;
        }

        static Panel Row(string name, int height, int padding, params Control[] controls)
        {
            var panel = new Panel { Name = name, Dock = DockStyle.Top, Size = new Size(520, height), Padding = new Padding(padding) };
            panel.Controls.AddRange(controls);
            return panel;
        }

        static RCheckBox CheckBox(RCheckBox check, string text)
        {
            check.BackColor = SystemColors.ControlLight;
            check.Dock = DockStyle.Top;
            check.Margin = new Padding(0);
            check.Padding = new Padding(16, 6, 16, 6);
            check.Size = new Size(490, 46);
            check.Text = text;
            check.UseVisualStyleBackColor = false;
            return check;
        }

        Control[] LogRow()
        {
            CheckBox(checkLog, AiText.Log);
            checkLog.Dock = DockStyle.Fill;

            buttonLogs.Secondary = true;
            buttonLogs.Dock = DockStyle.Right;
            buttonLogs.Size = new Size(110, 46);
            buttonLogs.Text = AiText.OpenLogs;
            buttonLogs.FlatStyle = FlatStyle.Flat;

            var gap = new Panel { Dock = DockStyle.Right, Width = 10 };
            // Fill must be added first so it takes what's left after the right-docked controls
            return [checkLog, gap, buttonLogs];
        }

        Control[] StatusRow()
        {
            labelStatus.Location = new Point(10, 4);
            labelStatus.Size = new Size(500, 76);
            labelStatus.Text = AiText.Off;
            return [labelStatus];
        }

        static Panel Slider(string name, Label title, Label value, RTrackBar track, int min, int max, int tick)
        {
            var panel = new Panel { Name = name, Dock = DockStyle.Top, Size = new Size(520, 124) };

            value.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            value.Location = new Point(396, 10);
            value.Size = new Size(116, 32);
            value.TextAlign = ContentAlignment.TopRight;

            title.AutoSize = true;
            title.Location = new Point(10, 10);

            track.Location = new Point(6, 48);
            track.Size = new Size(508, 90);
            track.Minimum = min;
            track.Maximum = max;
            track.TickFrequency = tick;
            track.TickStyle = TickStyle.TopLeft;
            track.AccessibleName = title.Text;

            panel.Controls.Add(value);
            panel.Controls.Add(title);
            panel.Controls.Add(track);
            return panel;
        }
    }
}
