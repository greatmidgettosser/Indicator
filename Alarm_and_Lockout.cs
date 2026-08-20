using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Native;
using TradingPlatform.BusinessLayer.Utils;
using TradingPlatform.BusinessLayer.Utils.Sounds;
using TradingPlatform.PresentationLayer.Plugins;
using TradingPlatform.PresentationLayer.Renderers;

namespace AlarmLockout
{
    /// <summary>
    /// One configurable alarm slot. Live fields are what's actually running;
    /// Pending fields are what's shown/edited in the settings panel until
    /// "Save Settings" is clicked (same staging pattern as Risk Manager).
    /// </summary>
    public class AlarmSlot
    {
        // --- Live ---
        public string Name = "";
        public TimeSpan? Time = null;      // null = alarm disabled
        public bool LockEnabled = false;   // false = alert-only (log + sound)
        public int LockSeconds = 60;       // duration to keep account locked
        public Account Account = null;     // only used when LockEnabled

        // --- Pending (staged) ---
        public string PendingName = "";
        public TimeSpan? PendingTime = null;
        public bool PendingLockEnabled = false;
        public int PendingLockSeconds = 60;
        public Account PendingAccount = null;

        // --- Runtime state ---
        public DateTime LastFiredDate = DateTime.MinValue; // local date the alarm last fired (prevents re-firing same day)
        public bool LockActive = false;                    // true while this alarm's lock is in effect
        public DateTime LockEndUtc;                         // moment the lock lifts

        public void SyncPendingFromLive()
        {
            PendingName = Name;
            PendingTime = Time;
            PendingLockEnabled = LockEnabled;
            PendingLockSeconds = LockSeconds;
            PendingAccount = Account;
        }

        public void ApplyPending()
        {
            Name = PendingName;
            Time = PendingTime;
            LockEnabled = PendingLockEnabled;
            LockSeconds = PendingLockSeconds;
            Account = LockEnabled ? PendingAccount : null;
        }

        public string DisplayName(int index) => string.IsNullOrWhiteSpace(Name) ? $"Alarm {index}" : Name;
    }

    public class AlarmLockoutPlugin : Plugin
    {
        private const int ALARM_COUNT = 10;
        public AlarmSlot[] Alarms { get; } = new AlarmSlot[ALARM_COUNT];

        private bool _initialSettingsApplied = false;

        // Shared engine timer — ticks every second: checks trigger times and drives lock countdowns.
        private Timer _engineTimer;

        // Re-lock monitor — re-applies the lock if a locked account gets manually unlocked
        // while its alarm's lock window is still active.
        private Timer _relockTimer;

        private AlarmLockoutRenderer _renderer;

        public static PluginInfo GetInfo()
        {
            var windowParameters = NativeWindowParameters.Panel;
            windowParameters.BrowserUsageType = BrowserUsageType.None;
            windowParameters.AllowDrop = false;

            return new PluginInfo
            {
                Name = "AlarmLockout",
                Title = "Alarm Lockout",
                Group = PluginGroup.Misc,
                ShortName = "AL",
                SortIndex = 10,
                AllowSettings = true,
                WindowParameters = windowParameters,
                CustomProperties = new Dictionary<string, object>
                {
                    { PluginInfo.Const.ALLOW_MANUAL_CREATION, true }
                }
            };
        }

        public override Size DefaultSize => new Size(UnitSize.Width * 4, UnitSize.Height * 6);

        public AlarmLockoutPlugin()
        {
            for (int i = 0; i < ALARM_COUNT; i++)
                Alarms[i] = new AlarmSlot();
        }

        public override void Initialize()
        {
            base.Initialize();
            foreach (var a in Alarms)
                a.SyncPendingFromLive();

            _renderer = new AlarmLockoutRenderer(this.Window.CreateRenderingControl("AlarmLockoutRenderer"), this);
            _renderer.RedrawBufferedGraphic();

            _engineTimer = new Timer(EngineTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            _relockTimer = new Timer(RelockTick, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        public override void Populate(PluginParameters args = null)
        {
            base.Populate(args);
            _renderer?.RedrawBufferedGraphic();
        }

        public override void Dispose()
        {
            _engineTimer?.Dispose();
            _engineTimer = null;
            _relockTimer?.Dispose();
            _relockTimer = null;

            if (_renderer != null)
            {
                _renderer.Dispose();
                _renderer = null;
            }
            base.Dispose();
        }

        protected override void OnLayoutUpdated()
        {
            base.OnLayoutUpdated();
            if (_renderer != null)
                _renderer.Layout.Margin = this.NonClientMargin;
        }

        // ------------------------------------------------------------------
        // Engine: fires alarms at their scheduled time, drives lock expiry.
        // ------------------------------------------------------------------
        private void EngineTick(object state)
        {
            try
            {
                var now = DateTime.Now;
                var today = now.Date;
                bool needRedraw = false;

                for (int i = 0; i < ALARM_COUNT; i++)
                {
                    var alarm = Alarms[i];

                    // Reset the daily fired-flag at midnight rollover.
                    if (alarm.LastFiredDate != DateTime.MinValue && alarm.LastFiredDate != today && !alarm.LockActive)
                        alarm.LastFiredDate = DateTime.MinValue;

                    // --- Check trigger ---
                    if (alarm.Time.HasValue && alarm.LastFiredDate != today)
                    {
                        var target = today.Add(alarm.Time.Value);
                        if (now >= target && now < target.AddMinutes(1)) // 1-minute firing window
                        {
                            alarm.LastFiredDate = today;
                            FireAlarm(i, alarm);
                            needRedraw = true;
                        }
                    }

                    // --- Drive lock countdown / expiry ---
                    if (alarm.LockActive)
                    {
                        needRedraw = true;
                        if (DateTime.UtcNow >= alarm.LockEndUtc)
                        {
                            ExpireLock(i, alarm);
                        }
                    }
                }

                if (needRedraw)
                    _renderer?.RedrawBufferedGraphic();
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[AlarmLockout] Engine tick error: {ex.Message}");
            }
        }

        private void FireAlarm(int index, AlarmSlot alarm)
        {
            string label = alarm.DisplayName(index + 1);

            if (alarm.LockEnabled && alarm.Account != null)
            {
                _ = FlattenThenLock(index, alarm, label);
            }
            else
            {
                // Alert-only: log + popup alert log with built-in platform sound.
                Core.Instance.Loggers.Log($"[AlarmLockout] {label} triggered.");
                RaiseAlert($"Alarm: {label}");
            }
        }

        /// <summary>
        /// Pushes a popup into Quantower's Alerts Log using the platform's built-in
        /// DefaultAlert sound (same sound catalog used by order/position alerts), and
        /// also fires a direct system beep as a guaranteed-audible fallback, since the
        /// platform alert sound depends on the user's Alerts sound settings/mapping.
        /// Runs on a background thread so Console.Beep's blocking duration never stalls
        /// the engine timer or UI.
        /// </summary>
        private void RaiseAlert(string text)
        {
            try
            {
                Core.Instance.Alert(new Alert
                {
                    Text = text,
                    Name = Sound.DefaultAlert,
                    AutoOpenAlertsLog = true
                });
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[AlarmLockout] Alert error: {ex.Message}");
            }

            Task.Run(() =>
            {
                try
                {
                    Console.Beep(1000, 250);
                    Console.Beep(1300, 250);
                }
                catch { /* Console.Beep can fail on non-Windows/headless hosts — ignore */ }
            });
        }

        private async Task FlattenThenLock(int index, AlarmSlot alarm, string label)
        {
            try
            {
                Core.Instance.Loggers.Log($"[AlarmLockout] {label} triggered — flattening {alarm.Account.Name}...");
                Core.Instance.AdvancedTradingOperations.Flatten(alarm.Account);

                int attempts = 0;
                while (attempts < 5)
                {
                    await Task.Delay(200);
                    bool hasPositions = Core.Instance.Positions.Any(p => p.Account.Equals(alarm.Account));
                    bool hasOrders = Core.Instance.Orders.Any(o => o.Account.Equals(alarm.Account));
                    if (!hasPositions && !hasOrders) break;
                    attempts++;
                }

                if (!alarm.Account.IsLocked())
                    Core.Instance.LockAccount(alarm.Account);

                alarm.LockActive = true;
                alarm.LockEndUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, alarm.LockSeconds));

                Core.Instance.Loggers.Log($"[AlarmLockout] {label} — account {alarm.Account.Name} locked for {alarm.LockSeconds}s.");
                RaiseAlert($"Alarm: {label} — {alarm.Account.Name} flattened & locked.");

                _renderer?.RedrawBufferedGraphic();
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[AlarmLockout] {label} flatten/lock error: {ex.Message}");
            }
        }

        private void ExpireLock(int index, AlarmSlot alarm)
        {
            alarm.LockActive = false;
            try
            {
                // Only unlock if no OTHER active alarm still needs this same account locked.
                bool stillNeeded = Alarms.Where((a, i) => i != index)
                                          .Any(a => a.LockActive && a.Account != null && alarm.Account != null && a.Account.Equals(alarm.Account));
                if (!stillNeeded && alarm.Account != null && alarm.Account.IsLocked())
                {
                    Core.Instance.UnLockAccount(alarm.Account);
                    Core.Instance.Loggers.Log($"[AlarmLockout] {alarm.DisplayName(index + 1)} lock expired — {alarm.Account.Name} unlocked.");
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[AlarmLockout] Unlock error: {ex.Message}");
            }
        }

        // Re-locks accounts that get manually unlocked while their alarm's lock window is still active.
        private void RelockTick(object state)
        {
            try
            {
                bool needRedraw = false;
                foreach (var alarm in Alarms)
                {
                    if (!alarm.LockActive || alarm.Account == null) continue;
                    if (!alarm.Account.IsLocked())
                    {
                        Core.Instance.LockAccount(alarm.Account);
                        needRedraw = true;
                    }
                }
                if (needRedraw)
                    _renderer?.RedrawBufferedGraphic();
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[AlarmLockout] Relock monitor error: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------
        // Settings
        // ------------------------------------------------------------------
        public override IList<SettingItem> Settings
        {
            get
            {
                var result = base.Settings;

                for (int i = 0; i < ALARM_COUNT; i++)
                {
                    var a = Alarms[i];
                    int n = i + 1;
                    int baseSort = i * 10;
                    string p = $"Alarm{n}";

                    result.Add(new SettingItemString($"{p}Name", a.PendingName)
                    { Text = $"Alarm {n} - Custom Name", SortIndex = baseSort + 0 });

                    result.Add(new SettingItemString($"{p}Time", a.PendingTime.HasValue ? a.PendingTime.Value.ToString(@"hh\:mm") : "")
                    { Text = $"Alarm {n} - Trigger Time 24hr local (HH:MM, blank = off)", SortIndex = baseSort + 1 });

                    result.Add(new SettingItemBoolean($"{p}LockEnabled", a.PendingLockEnabled)
                    { Text = $"Alarm {n} - Flatten & Lock Account", SortIndex = baseSort + 2 });

                    result.Add(new SettingItemInteger($"{p}LockSeconds", a.PendingLockSeconds)
                    {
                        Text = $"Alarm {n} - Lock Duration (seconds)",
                        SortIndex = baseSort + 3,
                        Minimum = 1,
                        Maximum = 86400,
                        Relation = new SettingItemRelationVisibility($"{p}LockEnabled", true)
                    });

                    result.Add(new SettingItemAccount($"{p}Account", a.PendingAccount)
                    {
                        Text = $"Alarm {n} - Account to Lock",
                        SortIndex = baseSort + 4,
                        Relation = new SettingItemRelationVisibility($"{p}LockEnabled", true)
                    });
                }

                result.Add(new SettingItemAction("SaveSettings", new SettingItemActionDelegate((s) => { ApplyPendingSettings(); return null; }), 100)
                { Text = "Save Settings" });

                return result;
            }
            set
            {
                base.Settings = value;
                foreach (var item in value)
                {
                    for (int i = 0; i < ALARM_COUNT; i++)
                    {
                        var a = Alarms[i];
                        int n = i + 1;
                        string p = $"Alarm{n}";

                        if (item.Name == $"{p}Name")
                            a.PendingName = item.Value as string ?? "";
                        else if (item.Name == $"{p}Time")
                        {
                            string s = item.Value as string;
                            a.PendingTime = TimeSpan.TryParse(s, out var t) ? (TimeSpan?)t : null;
                        }
                        else if (item.Name == $"{p}LockEnabled")
                            a.PendingLockEnabled = (bool)item.Value;
                        else if (item.Name == $"{p}LockSeconds")
                            a.PendingLockSeconds = Convert.ToInt32(item.Value);
                        else if (item.Name == $"{p}Account")
                            a.PendingAccount = item.Value as Account;
                    }
                }

                // First call is the platform restoring saved state from disk — commit immediately
                // so restored alarms are live right away. Every later call (user editing the
                // panel) stays staged-only until "Save Settings" is clicked.
                if (!_initialSettingsApplied)
                {
                    _initialSettingsApplied = true;
                    ApplyPendingSettings();
                }
            }
        }

        private void ApplyPendingSettings()
        {
            foreach (var a in Alarms)
            {
                a.ApplyPending();
                // Let a newly-set or newly-changed time fire today if it hasn't already.
                a.LastFiredDate = DateTime.MinValue;
            }
            _renderer?.RedrawBufferedGraphic();
            Core.Instance.Loggers.Log("[AlarmLockout] Settings saved.");
        }
    }

    public class AlarmLockoutRenderer : Renderer
    {
        private BufferedGraphic bufferedGraphic;
        private readonly AlarmLockoutPlugin _plugin;

        public AlarmLockoutRenderer(IRenderingNativeControl native, AlarmLockoutPlugin plugin)
            : base(native)
        {
            _plugin = plugin;
            bufferedGraphic = new BufferedGraphic(this.Draw, this.Refresh, native.DisposeImage, native.IsDisplayed, BufferedGraphicRequiredThreadType.LowPriority);
        }

        public void RedrawBufferedGraphic()
        {
            bufferedGraphic.IsDirty = true;
        }

        private static string FormatCountdown(TimeSpan remaining)
        {
            int totalSeconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
            if (totalSeconds >= 3600)
                return $"{totalSeconds / 3600}:{(totalSeconds % 3600) / 60:D2}:{totalSeconds % 60:D2}";
            return $"{totalSeconds / 60}:{totalSeconds % 60:D2}";
        }

        protected virtual void Draw(Graphics gr)
        {
            gr.Clear(Color.FromArgb(30, 30, 30));

            int fontSize = 9;
            var labelFont = new Font("Arial", fontSize, FontStyle.Regular);
            var valueFont = new Font("Arial", fontSize + 1, FontStyle.Bold);
            var headerFont = new Font("Arial", fontSize + 1, FontStyle.Bold);
            var whiteBrush = new SolidBrush(Color.White);
            var grayBrush = new SolidBrush(Color.FromArgb(160, 160, 160));
            var yellowBrush = new SolidBrush(Color.FromArgb(255, 200, 0));
            var greenBrush = new SolidBrush(Color.FromArgb(0, 200, 100));
            var redBrush = new SolidBrush(Color.FromArgb(220, 60, 60));
            var divPen = new Pen(Color.FromArgb(60, 60, 60));

            int lineH = 20;

            // Two-column layout, same split style as Risk Manager: left half = Alarms 1-5,
            // right half = Alarms 6-10. Each column keeps its own label/value offsets and
            // its own vertical cursor.
            int mid = Bounds.Width / 2;
            int lLabelX = 10;
            int lValueX = lLabelX + 60;
            int rLabelX = mid + 2;
            int rValueX = rLabelX + 60;

            void DrawAlarmBlock(int index, int labelX, int valueX, ref int y)
            {
                var a = _plugin.Alarms[index];
                bool enabled = a.Time.HasValue;

                // Header: "Alarm N"
                gr.DrawString($"Alarm {index + 1}", headerFont, whiteBrush, labelX, y);
                y += lineH + 2;

                // Name
                gr.DrawString("Name", labelFont, grayBrush, labelX, y);
                gr.DrawString(a.DisplayName(index + 1), valueFont, enabled ? whiteBrush : grayBrush, valueX, y);
                y += lineH;

                // Time (or countdown while locked)
                gr.DrawString("Time", labelFont, grayBrush, labelX, y);
                if (a.LockActive)
                {
                    var remaining = a.LockEndUtc - DateTime.UtcNow;
                    gr.DrawString(FormatCountdown(remaining), valueFont, yellowBrush, valueX, y);
                }
                else if (enabled)
                {
                    gr.DrawString(DateTime.Today.Add(a.Time.Value).ToString("h:mm tt"), valueFont, whiteBrush, valueX, y);
                }
                else
                {
                    gr.DrawString("OFF", valueFont, grayBrush, valueX, y);
                }
                y += lineH;

                // Lock
                gr.DrawString("Lock", labelFont, grayBrush, labelX, y);
                if (a.LockActive)
                    gr.DrawString("LOCKED", valueFont, redBrush, valueX, y);
                else
                    gr.DrawString(a.LockEnabled ? "Yes" : "No", valueFont, a.LockEnabled ? greenBrush : grayBrush, valueX, y);
                y += lineH + 4;

                // Divider between alarms within the column
                int dividerRight = labelX == lLabelX ? mid - 4 : Bounds.Width - 10;
                gr.DrawLine(divPen, labelX, y, dividerRight, y);
                y += 8;
            }

            int leftY = 10;
            int rightY = 10;
            for (int i = 0; i < 5 && i < _plugin.Alarms.Length; i++)
                DrawAlarmBlock(i, lLabelX, lValueX, ref leftY);
            for (int i = 5; i < 10 && i < _plugin.Alarms.Length; i++)
                DrawAlarmBlock(i, rLabelX, rValueX, ref rightY);

            labelFont.Dispose();
            valueFont.Dispose();
            headerFont.Dispose();
            whiteBrush.Dispose();
            grayBrush.Dispose();
            yellowBrush.Dispose();
            greenBrush.Dispose();
            redBrush.Dispose();
            divPen.Dispose();
        }

        public override IntPtr Render() => bufferedGraphic.CurrentImage;

        public override void Dispose()
        {
            if (bufferedGraphic != null)
            {
                bufferedGraphic.Dispose();
                bufferedGraphic = null;
            }
            base.Dispose();
        }

        public override void OnResize()
        {
            base.OnResize();
            Rectangle bounds = Bounds;
            if (bounds.Width == 0 || bounds.Height == 0) return;
            try
            {
                bufferedGraphic.Resize(bounds.Width, bounds.Height);
                bufferedGraphic.IsDirty = true;
            }
            catch { }
        }
    }
}