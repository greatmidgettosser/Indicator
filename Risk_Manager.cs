using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Native;
using TradingPlatform.PresentationLayer.Plugins;
using TradingPlatform.PresentationLayer.Renderers;

namespace RiskManager
{
    public class RiskManagerPlugin : Plugin
    {
        // --- Settings ---
        private Account _account;
        private double _dailyLossLimit = -120.0;
        private double _dailyProfitTarget = 360.0;
        private TimeSpan _sessionResetTime = new TimeSpan(15, 30, 0);
        private bool _includeUnrealized = false;
        private bool _calculateFees = false;
        private double _feePerMicro = 0.0;
        private double _feePerMini = 0.0;

        // --- State ---
        private double _dailyPnL = 0.0;         // realized only
        private double _unrealizedPnL = 0.0;    // live open position(s)
        private bool _triggered = false;
        private Timer _resetTimer;
        private Timer _relockTimer; // polls lock state while triggered, relocks if manually unlocked
        private readonly HashSet<string> _countedTradeIds = new HashSet<string>();

        // The UTC moment the current session started — trades before this are ignored.
        // If we're past today's reset time, session started today at that time;
        // otherwise it started yesterday at that time.
        // Stamped on startup by BackfillTodayPnL() and at the moment of each live reset.
        private DateTime _sessionStartUtc = DateTime.UtcNow;

        // --- Renderer ---
        private RiskManagerRenderer _renderer;

        public static PluginInfo GetInfo()
        {
            var windowParameters = NativeWindowParameters.Panel;
            windowParameters.BrowserUsageType = BrowserUsageType.None;
            windowParameters.AllowDrop = false;

            return new PluginInfo
            {
                Name = "RiskManager",
                Title = "Risk Manager",
                Group = PluginGroup.Misc,
                ShortName = "RM",
                SortIndex = 10,
                AllowSettings = true,
                WindowParameters = windowParameters,
                CustomProperties = new Dictionary<string, object>
                {
                    { PluginInfo.Const.ALLOW_MANUAL_CREATION, true }
                }
            };
        }

        public override Size DefaultSize => new Size(UnitSize.Width * 2, UnitSize.Height * 3);

        public override void Initialize()
        {
            base.Initialize();
            _renderer = new RiskManagerRenderer(this.Window.CreateRenderingControl("RiskManagerRenderer"), this);
            Core.Instance.TradeAdded += OnTradeAdded;
            Core.Instance.PositionAdded += OnPositionAdded;
            Core.Instance.PositionRemoved += OnPositionRemoved;
            Core.Instance.Connections.ConnectionStateChanged += OnConnectionStateChanged;
            ScheduleResetTimer();
        }

        private void OnConnectionStateChanged(object sender, ConnectionStateChangedEventArgs e)
        {
            if (e.NewState != ConnectionState.Connected) return;
            if (!_triggered) return;
            if (_account == null) return;

            // Connection is now live — safe to backfill and evaluate whether to unlock.
            // Only unlock if reset time has already passed today AND P&L is within limits.
            if (DateTime.Now.TimeOfDay < _sessionResetTime) return;

            BackfillTodayPnL();
            RefreshUnrealizedPnL();

            double total = TotalPnL;
            if (total > _dailyLossLimit && total < _dailyProfitTarget)
            {
                Core.Instance.Loggers.Log($"[RiskManager] Connection established: reset time passed, P&L ({total:C}) within limits — unlocking.");
                _relockTimer?.Dispose();
                _relockTimer = null;
                _triggered = false;
                _dailyPnL = 0.0;
                _unrealizedPnL = 0.0;
                _countedTradeIds.Clear();
                try
                {
                    if (_account.IsLocked())
                    {
                        Core.Instance.UnLockAccount(_account);
                        Core.Instance.Loggers.Log($"[RiskManager] Account {_account.Name} unlocked.");
                    }
                }
                catch (Exception ex)
                {
                    Core.Instance.Loggers.Log($"[RiskManager] Unlock error: {ex.Message}");
                }
                _renderer?.RedrawBufferedGraphic();
            }
            else
            {
                Core.Instance.Loggers.Log($"[RiskManager] Connection established: P&L ({total:C}) still exceeds threshold — remaining locked.");
            }
        }

        private void OnPositionAdded(Position pos)
        {
            if (_account == null || !pos.Account.Equals(_account)) return;
            if (_includeUnrealized)
                pos.Updated += OnPositionUpdated;
        }

        private void OnPositionRemoved(Position pos)
        {
            if (_account == null || !pos.Account.Equals(_account)) return;
            pos.Updated -= OnPositionUpdated;
            RefreshUnrealizedPnL();
            _renderer?.RedrawBufferedGraphic();
        }

        public override void Populate(PluginParameters args = null)
        {
            base.Populate(args);
            if (_account == null)
                _account = Core.Instance.Accounts.FirstOrDefault();

            // Restore _triggered from lock state immediately so status shows LOCKED correctly.
            // Unlock logic runs only after the connection is confirmed live (OnConnectionStateChanged).
            if (_account != null && _account.IsLocked())
                _triggered = true;

            BackfillTodayPnL();
            RefreshUnrealizedPnL();
            SubscribePositionUpdates();

            _renderer?.RedrawBufferedGraphic();
            Core.Instance.Loggers.Log($"[RiskManager] Monitoring: {_account?.Name ?? "none"} | Loss: {_dailyLossLimit:C} | Profit: {_dailyProfitTarget:C} | Backfilled PnL: {_dailyPnL:C} | IncludeUnrealized: {_includeUnrealized}");
        }

        /// <summary>
        /// Returns the UTC start of the current session based on _sessionResetTime.
        /// If the local reset time has already passed today  → session started today at that time.
        /// If it has not yet passed today                    → session started yesterday at that time.
        /// This means opening the platform after the reset time correctly sees a fresh session,
        /// and reopening it later still reads only trades that occurred after the last reset.
        /// </summary>
        private DateTime ComputeSessionStartUtc()
        {
            var nowLocal = DateTime.Now;
            var todayReset = nowLocal.Date + _sessionResetTime;

            var lastReset = nowLocal >= todayReset
                ? todayReset            // past reset today  → session started today
                : todayReset.AddDays(-1); // before reset today → session started yesterday

            return lastReset.ToUniversalTime();
        }

        private void BackfillTodayPnL()
        {
            if (_account == null) return;

            _dailyPnL = 0.0;
            _countedTradeIds.Clear();

            // Pin the session boundary now so OnTradeAdded uses the same reference.
            _sessionStartUtc = ComputeSessionStartUtc();

            try
            {
                var trades = Core.Instance.GetTrades(new TradesHistoryRequestParameters
                {
                    From = _sessionStartUtc,
                    To = DateTime.UtcNow,
                });

                if (trades == null)
                {
                    Core.Instance.Loggers.Log("[RiskManager] GetTrades returned null — no backfill");
                    return;
                }

                foreach (var trade in trades)
                {
                    // Belt-and-suspenders: skip anything the broker returned before our boundary
                    if (trade.DateTime.ToUniversalTime() < _sessionStartUtc) continue;
                    if (!trade.Account.Equals(_account)) continue;

                    double pnl = GetTradePnL(trade);
                    if (double.IsNaN(pnl)) continue;

                    _countedTradeIds.Add(trade.Id);
                    _dailyPnL += pnl - GetTradeFee(trade);
                }

                Core.Instance.Loggers.Log($"[RiskManager] Session start (UTC): {_sessionStartUtc:u} | Backfilled {_countedTradeIds.Count} trades | Realized PnL: {_dailyPnL:C}");
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[RiskManager] Backfill error: {ex.Message}");
            }
        }

        // --- Unrealized P&L ---

        /// <summary>
        /// Subscribe Position.Updated on every open position for this account.
        /// Called whenever a position is opened or the account/setting changes.
        /// </summary>
        private void SubscribePositionUpdates()
        {
            // Unsubscribe everything first to avoid duplicates
            foreach (var pos in Core.Instance.Positions)
                pos.Updated -= OnPositionUpdated;

            if (!_includeUnrealized || _account == null) return;

            foreach (var pos in Core.Instance.Positions.Where(p => p.Account.Equals(_account)))
                pos.Updated += OnPositionUpdated;
        }

        /// <summary>
        /// Fired by Position.Updated every time Quantower recalculates P&L for a position.
        /// Recalculates the total unrealized P&L across all open positions for the account.
        /// </summary>
        private void OnPositionUpdated(Position pos)
        {
            if (!_includeUnrealized || _triggered) return;
            if (_account == null || !pos.Account.Equals(_account)) return;

            double total = 0.0;
            try
            {
                foreach (var p in Core.Instance.Positions.Where(p => p.Account.Equals(_account)))
                {
                    if (p.NetPnL != null)
                        total += p.NetPnL.Value;
                    else if (p.GrossPnL != null)
                        total += p.GrossPnL.Value;
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[RiskManager] Unrealized recalc error: {ex.Message}");
                return;
            }

            double prev = _unrealizedPnL;
            _unrealizedPnL = total;

            if (Math.Abs(_unrealizedPnL - prev) < 0.001) return;

            _renderer?.RedrawBufferedGraphic();

            double totalPnL = TotalPnL;
            if (totalPnL <= _dailyLossLimit)
                _ = FlattenThenLock($"Loss limit hit (total {totalPnL:C}, unrealized {_unrealizedPnL:C})");
            else if (totalPnL >= _dailyProfitTarget)
                _ = FlattenThenLock($"Profit target hit (total {totalPnL:C}, unrealized {_unrealizedPnL:C})");
        }

        /// <summary>
        /// Recalculates unrealized P&L synchronously (used on startup/backfill/account change).
        /// </summary>
        private void RefreshUnrealizedPnL()
        {
            if (!_includeUnrealized || _account == null)
            {
                _unrealizedPnL = 0.0;
                return;
            }

            double total = 0.0;
            try
            {
                foreach (var pos in Core.Instance.Positions.Where(p => p.Account.Equals(_account)))
                {
                    if (pos.NetPnL != null)
                        total += pos.NetPnL.Value;
                    else if (pos.GrossPnL != null)
                        total += pos.GrossPnL.Value;
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[RiskManager] Unrealized refresh error: {ex.Message}");
            }

            _unrealizedPnL = total;
        }

        /// <summary>
        /// Total P&L used for limit checks: realized + (unrealized if enabled).
        /// </summary>
        public double TotalPnL => _dailyPnL + (_includeUnrealized ? _unrealizedPnL : 0.0);

        // --- Existing helpers ---

        private double GetTradePnL(Trade trade)
        {
            if (trade.NetPnl != null)
                return trade.NetPnl.Value;
            if (trade.GrossPnl != null && trade.Fee != null)
                return trade.GrossPnl.Value - trade.Fee.Value;
            if (trade.GrossPnl != null)
                return trade.GrossPnl.Value;
            return double.NaN;
        }

        private double GetTradeFee(Trade trade)
        {
            if (!_calculateFees) return 0.0;

            string symbol = trade.Symbol?.Name ?? string.Empty;
            double feePerContract;

            if (symbol.StartsWith("MES") || symbol.StartsWith("MNQ") || symbol.StartsWith("M2K"))
                feePerContract = _feePerMicro;
            else if (symbol.StartsWith("ES") || symbol.StartsWith("NQ") || symbol.StartsWith("RTY"))
                feePerContract = _feePerMini;
            else
            {
                Core.Instance.Loggers.Log($"[RiskManager] Unrecognized symbol '{symbol}' — no fee applied.");
                return 0.0;
            }

            return feePerContract * trade.Quantity;
        }

        public override void Dispose()
        {
            Core.Instance.TradeAdded -= OnTradeAdded;
            Core.Instance.PositionAdded -= OnPositionAdded;
            Core.Instance.PositionRemoved -= OnPositionRemoved;
            Core.Instance.Connections.ConnectionStateChanged -= OnConnectionStateChanged;

            // Unsubscribe Position.Updated from any still-open positions
            foreach (var pos in Core.Instance.Positions)
                pos.Updated -= OnPositionUpdated;

            _resetTimer?.Dispose();
            _resetTimer = null;
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

        public override IList<SettingItem> Settings
        {
            get
            {
                var result = base.Settings;
                result.Add(new SettingItemAccount("Account", _account)
                { Text = "Account to Monitor", SortIndex = 0 });
                result.Add(new SettingItemDouble("DailyLossLimit", _dailyLossLimit)
                { Text = "Daily Loss Limit ($)", SortIndex = 1 });
                result.Add(new SettingItemDouble("DailyProfitTarget", _dailyProfitTarget)
                { Text = "Daily Profit Target ($)", SortIndex = 2 });
                result.Add(new SettingItemString("SessionResetTime", _sessionResetTime.ToString(@"hh\:mm"))
                { Text = "Session Reset Time 24hr local (HH:MM)", SortIndex = 3 });
                result.Add(new SettingItemBoolean("IncludeUnrealized", _includeUnrealized)
                { Text = "Include Unrealized P&L in Limits", SortIndex = 4 });
                result.Add(new SettingItemBoolean("CalculateFees", _calculateFees)
                { Text = "Calculate Commissions & Fees", SortIndex = 5 });
                result.Add(new SettingItemDouble("FeePerMicro", _feePerMicro)
                { Text = "Fee Per Micro Contract (Round Trip $)", SortIndex = 6, Increment = 0.01, DecimalPlaces = 2 });
                result.Add(new SettingItemDouble("FeePerMini", _feePerMini)
                { Text = "Fee Per Mini Contract (Round Trip $)", SortIndex = 7, Increment = 0.01, DecimalPlaces = 2 });
                return result;
            }
            set
            {
                base.Settings = value;
                foreach (var item in value)
                {
                    switch (item.Name)
                    {
                        case "Account":
                            _account = item.Value as Account;
                            BackfillTodayPnL();
                            RefreshUnrealizedPnL();
                            SubscribePositionUpdates();
                            break;
                        case "DailyLossLimit":
                            _dailyLossLimit = (double)item.Value;
                            CheckLimits();
                            break;
                        case "DailyProfitTarget":
                            _dailyProfitTarget = (double)item.Value;
                            CheckLimits();
                            break;
                        case "SessionResetTime":
                            if (TimeSpan.TryParse((string)item.Value, out var parsed))
                            {
                                _sessionResetTime = parsed;
                                // Recompute the session boundary and re-backfill so the
                                // new reset time takes effect immediately without a restart.
                                BackfillTodayPnL();
                                ScheduleResetTimer();
                            }
                            break;
                        case "IncludeUnrealized":
                            _includeUnrealized = (bool)item.Value;
                            RefreshUnrealizedPnL();
                            SubscribePositionUpdates();
                            CheckLimits();
                            break;
                        case "CalculateFees":
                            _calculateFees = (bool)item.Value;
                            BackfillTodayPnL();
                            break;
                        case "FeePerMicro":
                            _feePerMicro = (double)item.Value;
                            if (_calculateFees) BackfillTodayPnL();
                            break;
                        case "FeePerMini":
                            _feePerMini = (double)item.Value;
                            if (_calculateFees) BackfillTodayPnL();
                            break;
                    }
                }
                Core.Instance.Loggers.Log($"[RiskManager] Settings applied — Loss: {_dailyLossLimit:C} | Profit: {_dailyProfitTarget:C} | IncludeUnrealized: {_includeUnrealized}");
                _renderer?.RedrawBufferedGraphic();
            }
        }

        private void ScheduleResetTimer()
        {
            _resetTimer?.Dispose();
            TimeSpan timeUntilReset = _sessionResetTime - DateTime.Now.TimeOfDay;
            if (timeUntilReset < TimeSpan.Zero)
                timeUntilReset = timeUntilReset.Add(TimeSpan.FromDays(1));
            Core.Instance.Loggers.Log($"[RiskManager] Next reset in {timeUntilReset:hh\\:mm\\:ss}");
            _resetTimer = new Timer(OnSessionReset, null,
                (long)timeUntilReset.TotalMilliseconds,
                (long)TimeSpan.FromDays(1).TotalMilliseconds);
        }

        private void OnSessionReset(object state)
        {
            _sessionStartUtc = DateTime.UtcNow;

            Core.Instance.Loggers.Log($"[RiskManager] Session reset at {_sessionStartUtc:u} — P&L cleared.");
            _dailyPnL = 0.0;
            _unrealizedPnL = 0.0;
            _triggered = false;
            _countedTradeIds.Clear();

            // Stop the relock monitor before unlocking so it doesn't immediately relock
            _relockTimer?.Dispose();
            _relockTimer = null;

            // Unlock the account so trading resumes after reset
            try
            {
                if (_account != null && _account.IsLocked())
                {
                    Core.Instance.UnLockAccount(_account);
                    Core.Instance.Loggers.Log($"[RiskManager] Account {_account.Name} unlocked on session reset.");
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[RiskManager] Unlock on reset error: {ex.Message}");
            }

            RefreshUnrealizedPnL();
            _renderer?.RedrawBufferedGraphic();
        }

        private void OnTradeAdded(Trade trade)
        {
            if (_triggered) return;
            if (_account == null) return;
            if (!trade.Account.Equals(_account)) return;

            // Ignore trades that arrived before the current session boundary.
            // This replaces the old "is it today?" check and correctly handles
            // the reset-time boundary whether the platform was open or just launched.
            if (trade.DateTime.ToUniversalTime() < _sessionStartUtc) return;

            if (_countedTradeIds.Contains(trade.Id)) return;

            _countedTradeIds.Add(trade.Id);

            double pnl = GetTradePnL(trade);
            if (double.IsNaN(pnl))
            {
                Core.Instance.Loggers.Log($"[RiskManager] Trade {trade.Id} — no PnL fields populated, skipping.");
                return;
            }

            _dailyPnL += pnl - GetTradeFee(trade);

            // When a trade closes, positions update — refresh unrealized immediately
            // so we don't double-count the just-closed leg
            if (_includeUnrealized)
                RefreshUnrealizedPnL();

            _renderer?.RedrawBufferedGraphic();

            Core.Instance.Loggers.Log($"[RiskManager] Trade: {pnl:C} | Realized PnL: {_dailyPnL:C} | Unrealized: {_unrealizedPnL:C} | Total: {TotalPnL:C}");

            double total = TotalPnL;
            if (total <= _dailyLossLimit)
                _ = FlattenThenLock($"Loss limit hit ({total:C})");
            else if (total >= _dailyProfitTarget)
                _ = FlattenThenLock($"Profit target hit ({total:C})");
        }

        private async Task FlattenThenLock(string reason)
        {
            if (_triggered) return;
            _triggered = true;
            _renderer?.RedrawBufferedGraphic();

            Core.Instance.Loggers.Log($"[RiskManager] TRIGGERED — {reason}. Flattening {_account.Name}...");
            Core.Instance.AdvancedTradingOperations.Flatten(_account);

            int attempts = 0;
            while (attempts < 20)
            {
                bool hasPositions = Core.Instance.Positions.Any(p => p.Account.Equals(_account));
                bool hasOrders = Core.Instance.Orders.Any(o => o.Account.Equals(_account));
                if (!hasPositions && !hasOrders) break;
                await Task.Delay(500);
                attempts++;
            }

            Core.Instance.LockAccount(_account);
            _renderer?.RedrawBufferedGraphic();

            Core.Instance.Loggers.Log($"[RiskManager] Account {_account.Name} locked. Reason: {reason}");
            Core.Instance.Alert($"Risk Manager: {reason}. Account locked.");

            StartRelockMonitor();
        }

        /// <summary>
        /// Checks current TotalPnL against both limits immediately.
        /// Called after settings change so a threshold lowered below the current P&L fires right away.
        /// </summary>
        private void CheckLimits()
        {
            if (_triggered) return;
            double total = TotalPnL;
            if (total <= _dailyLossLimit)
                _ = FlattenThenLock($"Loss limit hit ({total:C})");
            else if (total >= _dailyProfitTarget)
                _ = FlattenThenLock($"Profit target hit ({total:C})");
        }

        /// <summary>
        /// Starts a 2-second polling timer that relocks the account if it gets manually unlocked
        /// while _triggered is true. Stops automatically on session reset.
        /// </summary>
        private void StartRelockMonitor()
        {
            _relockTimer?.Dispose();
            _relockTimer = new Timer(_ =>
            {
                try
                {
                    if (!_triggered || _account == null) return;
                    if (!_account.IsLocked())
                    {
                        Core.Instance.Loggers.Log($"[RiskManager] Account was manually unlocked — relocking.");
                        Core.Instance.LockAccount(_account);
                        _renderer?.RedrawBufferedGraphic();
                    }
                }
                catch (Exception ex)
                {
                    Core.Instance.Loggers.Log($"[RiskManager] Relock monitor error: {ex.Message}");
                }
            }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        // Expose state to renderer
        public Account MonitoredAccount => _account;
        public double DailyPnL => _dailyPnL;
        public double UnrealizedPnL => _unrealizedPnL;
        public double DailyLossLimit => _dailyLossLimit;
        public double DailyProfitTarget => _dailyProfitTarget;
        public TimeSpan SessionResetTime => _sessionResetTime;
        public bool IsTriggered => _triggered;
        public bool IncludeUnrealized => _includeUnrealized;
    }

    public class RiskManagerRenderer : Renderer
    {
        private BufferedGraphic bufferedGraphic;
        private readonly RiskManagerPlugin _plugin;

        public RiskManagerRenderer(IRenderingNativeControl native, RiskManagerPlugin plugin)
            : base(native)
        {
            _plugin = plugin;
            bufferedGraphic = new BufferedGraphic(this.Draw, this.Refresh, native.DisposeImage, native.IsDisplayed, BufferedGraphicRequiredThreadType.LowPriority);
        }

        public void RedrawBufferedGraphic()
        {
            bufferedGraphic.IsDirty = true;
        }

        protected virtual void Draw(Graphics gr)
        {
            gr.Clear(Color.FromArgb(30, 30, 30));

            var labelFont = new Font("Arial", 9, FontStyle.Regular);
            var valueFont = new Font("Arial", 10, FontStyle.Bold);
            var titleFont = new Font("Arial", 11, FontStyle.Bold);
            var whiteBrush = new SolidBrush(Color.White);
            var grayBrush = new SolidBrush(Color.FromArgb(160, 160, 160));
            var greenBrush = new SolidBrush(Color.FromArgb(0, 200, 100));
            var redBrush = new SolidBrush(Color.FromArgb(220, 60, 60));
            var yellowBrush = new SolidBrush(Color.FromArgb(255, 200, 0));
            var divPen = new Pen(Color.FromArgb(60, 60, 60));

            int x = 15;
            int y = 12;
            int lineH = 28;
            int col2 = 150;

            gr.DrawString("Risk Manager", titleFont, whiteBrush, x, y);
            y += lineH;
            gr.DrawLine(divPen, x, y, Bounds.Width - x, y);
            y += 8;

            // Account
            gr.DrawString("Account", labelFont, grayBrush, x, y);
            string accountName = _plugin.MonitoredAccount?.Name ?? "None";
            string maskedName = accountName.Length > 4
                ? "****" + accountName.Substring(accountName.Length - 2)
                : accountName;
            gr.DrawString(maskedName, valueFont, whiteBrush, col2, y);
            y += lineH;

            // Loss Limit
            gr.DrawString("Loss Limit", labelFont, grayBrush, x, y);
            gr.DrawString($"{_plugin.DailyLossLimit:C}", valueFont, redBrush, col2, y);
            y += lineH;

            // Profit Target
            gr.DrawString("Profit Target", labelFont, grayBrush, x, y);
            gr.DrawString($"{_plugin.DailyProfitTarget:C}", valueFont, greenBrush, col2, y);
            y += lineH;

            // Realized P&L
            gr.DrawString("Realized P&L", labelFont, grayBrush, x, y);
            var realizedBrush = _plugin.DailyPnL >= 0 ? greenBrush : redBrush;
            gr.DrawString($"{_plugin.DailyPnL:C}", valueFont, realizedBrush, col2, y);
            y += lineH;

            // Unrealized P&L row — always show so user knows what mode they're in
            gr.DrawString("Unrealized P&L", labelFont, grayBrush, x, y);
            if (_plugin.IncludeUnrealized)
            {
                var unrealizedBrush = _plugin.UnrealizedPnL >= 0 ? greenBrush : redBrush;
                gr.DrawString($"{_plugin.UnrealizedPnL:C}", valueFont, unrealizedBrush, col2, y);
            }
            else
            {
                gr.DrawString("OFF", valueFont, grayBrush, col2, y);
            }
            y += lineH;

            // Total P&L (realized + unrealized when enabled)
            gr.DrawLine(divPen, x, y, Bounds.Width - x, y);
            y += 8;
            gr.DrawString("Total P&L", labelFont, grayBrush, x, y);
            double total = _plugin.TotalPnL;
            var totalBrush = total >= 0 ? greenBrush : redBrush;
            gr.DrawString($"{total:C}", valueFont, totalBrush, col2, y);
            y += lineH;

            // Reset Time
            gr.DrawString("Reset Time", labelFont, grayBrush, x, y);
            gr.DrawString(_plugin.SessionResetTime.ToString(@"hh\:mm"), valueFont, whiteBrush, col2, y);
            y += lineH;

            gr.DrawLine(divPen, x, y, Bounds.Width - x, y);
            y += 8;

            // Status
            gr.DrawString("Status", labelFont, grayBrush, x, y);
            var statusBrush = _plugin.IsTriggered ? redBrush : greenBrush;
            gr.DrawString(_plugin.IsTriggered ? "LOCKED" : "ACTIVE", valueFont, statusBrush, col2, y);

            titleFont.Dispose();
            labelFont.Dispose();
            valueFont.Dispose();
            whiteBrush.Dispose();
            grayBrush.Dispose();
            greenBrush.Dispose();
            redBrush.Dispose();
            yellowBrush.Dispose();
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