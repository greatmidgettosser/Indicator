using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Native;
using TradingPlatform.PresentationLayer.Plugins;
using TradingPlatform.PresentationLayer.Renderers;

namespace RiskParameters
{
    public class RiskManagerPlugin : Plugin
    {
        // --- Settings ---
        private Account _account;
        private double _dailyLossLimit = -120.0;
        private double _dailyProfitTarget = 360.0;
        private static readonly TimeSpan _resetTimeEastern = new TimeSpan(17, 0, 0);
        private TimeSpan? _flattenLockTime = null;
        private static readonly TimeZoneInfo _easternTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        private bool _includeUnrealized = false;
        private bool _hideAccountName = false;
        private int _hideCharCount = 4;
        private double _feePerMicro = 0.0;
        private double _feePerMini = 0.0;
        private string _microSymbols = "MES, MNQ, M2K";
        private string _miniSymbols = "ES, NQ, RTY";

        // --- Pending (staged) settings ---
        private double _pendingDailyLossLimit = -120.0;
        private double _pendingDailyProfitTarget = 360.0;
        private TimeSpan? _pendingFlattenLockTime = null;
        private bool _pendingIncludeUnrealized = false;
        private bool _pendingHideAccountName = false;
        private int _pendingHideCharCount = 4;
        private double _pendingFeePerMicro = 0.0;
        private double _pendingFeePerMini = 0.0;
        private string _pendingMicroSymbols = "MES, MNQ, M2K";
        private string _pendingMiniSymbols = "ES, NQ, RTY";

        private bool _initialSettingsApplied = false;

        // --- State ---
        private double _dailyPnL = 0.0;
        private double _unrealizedPnL = 0.0;
        private double _sessionFees = 0.0;
        private int _microContractLimit = 0;
        private int _miniContractLimit = -1;
        private bool _limitPerSymbol = false;
        private bool _triggered = false;
        private bool _lockedBySchedule = false;
        private bool _backfillComplete = false;
        private bool _allowPositiveLossLimit = false;
        private bool _settingsLocked = false;

        // --- Trade Cooldown state ---
        private bool _cooldownActive = false;
        private DateTime _cooldownEndUtc;
        private Timer _cooldownTimer;

        private Timer _lockCountdownTimer;

        private bool _maxTradesEnabled = false;
        private int _maxTradesPerSession = 10;
        private bool _pendingMaxTradesEnabled = false;
        private int _pendingMaxTradesPerSession = 10;
        private bool _tradeCooldownEnabled = false;
        private int _tradeCooldownSeconds = 120;
        private bool _pendingTradeCooldownEnabled = false;
        private int _pendingTradeCooldownSeconds = 120;
        private bool _pendingAllowPositiveLossLimit = false;
        private int _fontSize = 9;

        private int _tradesTaken = 0;
        private readonly Dictionary<string, double> _rtQty = new Dictionary<string, double>();
        private readonly Dictionary<string, double> _rtValue = new Dictionary<string, double>();

        private Timer _resetTimer;
        private Timer _flattenLockTimer;
        private Timer _relockTimer;
        private Timer _startupPollTimer;
        private int _startupPollCount = 0;
        private const int StartupPollMaxTicks = 4;
        private readonly HashSet<string> _countedTradeIds = new HashSet<string>();

        private DateTime _sessionStartUtc = DateTime.UtcNow;

        private RiskManagerRenderer _renderer;

        public static PluginInfo GetInfo()
        {
            var windowParameters = NativeWindowParameters.Panel;
            windowParameters.BrowserUsageType = BrowserUsageType.None;
            windowParameters.AllowDrop = false;

            return new PluginInfo
            {
                Name = "RiskParametersMod",
                Title = "Risk Parameters and Lockout",
                Group = PluginGroup.Misc,
                ShortName = "RPLO",
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
            SyncPendingFromLive();
            _renderer = new RiskManagerRenderer(this.Window.CreateRenderingControl("RiskManagerRenderer"), this);
            Core.Instance.TradeAdded += OnTradeAdded;
            Core.Instance.PositionAdded += OnPositionAdded;
            Core.Instance.PositionRemoved += OnPositionRemoved;
            Core.Instance.Connections.ConnectionStateChanged += OnConnectionStateChanged;
            ScheduleResetTimer();
            ScheduleFlattenLockTimer();
            StartStartupPollTimer();
        }

        private void OnConnectionStateChanged(object sender, ConnectionStateChangedEventArgs e)
        {
            if (e.NewState != TradingPlatform.BusinessLayer.ConnectionState.Connected) return;
            if (!_triggered) return;
            if (_account == null) return;

            if (_lockedBySchedule)
            {
                Core.Instance.Loggers.Log($"[RiskParameters] Connection established: account locked by schedule — staying locked until reset time.");
                return;
            }

            BackfillTodayPnL();
            RefreshUnrealizedPnL();
            _renderer?.RedrawBufferedGraphic();

            if (TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _easternTz).TimeOfDay < _resetTimeEastern)
            {
                Core.Instance.Loggers.Log($"[RiskParameters] Connection established: PnL refreshed ({TotalPnL:C}) but reset time hasn't passed — staying locked.");
                return;
            }

            double total = TotalPnL;
            if (total > _dailyLossLimit && total < _dailyProfitTarget)
            {
                Core.Instance.Loggers.Log($"[RiskManager] Connection established: reset time passed, P&L ({total:C}) within limits — unlocking.");
                _relockTimer?.Dispose();
                _relockTimer = null;
                _triggered = false;
                _unrealizedPnL = 0.0;
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
            CheckContractLimit();
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

            if (_account != null && _account.IsLocked())
            {
                _triggered = true;
                StartRelockMonitor();
                StartLockCountdownTimer();
            }

            if (!_triggered && _flattenLockTime.HasValue && _account != null)
            {
                var nowLocal = DateTime.Now.TimeOfDay;
                var nowEastern = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _easternTz).TimeOfDay;
                bool pastLockTime = nowLocal >= _flattenLockTime.Value;
                bool beforeResetTime = nowEastern < _resetTimeEastern;

                bool inLockWindow = _flattenLockTime.Value.TotalHours < 17.0
                    ? pastLockTime && beforeResetTime
                    : pastLockTime || beforeResetTime;

                if (inLockWindow)
                {
                    Core.Instance.Loggers.Log($"[RiskParameters] Platform opened inside lock window — triggering scheduled lock.");
                    _lockedBySchedule = true;
                    _ = FlattenThenLock("Platform opened during scheduled lock window");
                }
            }

            BackfillTodayPnL();
            RefreshUnrealizedPnL();
            SubscribePositionUpdates();

            _renderer?.RedrawBufferedGraphic();
            Core.Instance.Loggers.Log($"[RiskManager] Monitoring: {_account?.Name ?? "none"} | Loss: {_dailyLossLimit:C} | Profit: {_dailyProfitTarget:C} | Backfilled PnL: {_dailyPnL:C} | IncludeUnrealized: {_includeUnrealized}");
        }

        private DateTime ComputeSessionStartUtc()
        {
            var nowEastern = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _easternTz);
            var todayResetEastern = nowEastern.Date + _resetTimeEastern;
            var lastResetEastern = nowEastern >= todayResetEastern
                ? todayResetEastern
                : todayResetEastern.AddDays(-1);
            return TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(lastResetEastern, DateTimeKind.Unspecified), _easternTz);
        }

        private void BackfillTodayPnL()
        {
            if (_account == null) return;

            var sessionStartUtc = ComputeSessionStartUtc();
            double dailyPnL = 0.0;
            double sessionFees = 0.0;
            int tradesTaken = 0;
            var rtQty = new Dictionary<string, double>();
            var rtValue = new Dictionary<string, double>();
            var countedTradeIds = new HashSet<string>();

            try
            {
                var trades = Core.Instance.GetTrades(new TradesHistoryRequestParameters
                {
                    From = sessionStartUtc,
                    To = DateTime.UtcNow,
                });

                if (trades == null)
                {
                    Core.Instance.Loggers.Log("[RiskManager] GetTrades returned null — no backfill");
                    return;
                }

                var ordered = trades
                    .Where(t => t.Account != null && t.Account.Equals(_account)
                             && t.DateTime.ToUniversalTime() >= sessionStartUtc)
                    .OrderBy(t => t.DateTime)
                    .ToList();

                foreach (var trade in ordered)
                {
                    UpdateRoundTripTracker(trade, rtQty, rtValue, ref tradesTaken);
                    double pnl = GetTradePnL(trade);
                    if (double.IsNaN(pnl)) continue;
                    double fee = GetTradeFee(trade);
                    countedTradeIds.Add(trade.Id);
                    dailyPnL += pnl - fee;
                    sessionFees += fee;
                }

                _sessionStartUtc = sessionStartUtc;
                _dailyPnL = dailyPnL;
                _sessionFees = sessionFees;
                _tradesTaken = tradesTaken;
                _rtQty.Clear(); foreach (var kv in rtQty) _rtQty[kv.Key] = kv.Value;
                _rtValue.Clear(); foreach (var kv in rtValue) _rtValue[kv.Key] = kv.Value;
                _countedTradeIds.Clear(); foreach (var id in countedTradeIds) _countedTradeIds.Add(id);

                Core.Instance.Loggers.Log($"[RiskManager] Backfill complete — session start: {_sessionStartUtc:u} | fills: {_countedTradeIds.Count} | round trips: {_tradesTaken} | realized PnL: {_dailyPnL:C}");

                _backfillComplete = true;

                var nowEasternCheck = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _easternTz).TimeOfDay;
                if (_triggered && !_lockedBySchedule && nowEasternCheck >= _resetTimeEastern)
                {
                    double totalCheck = TotalPnL;
                    bool pnlOk = totalCheck > _dailyLossLimit && totalCheck < _dailyProfitTarget;
                    bool tradesOk = !_maxTradesEnabled || _tradesTaken < _maxTradesPerSession;
                    if (pnlOk && tradesOk)
                    {
                        Core.Instance.Loggers.Log($"[RiskManager] Backfill: reset time passed, P&L within limits — unlocking.");
                        _relockTimer?.Dispose();
                        _relockTimer = null;
                        _lockCountdownTimer?.Dispose();
                        _lockCountdownTimer = null;
                        _triggered = false;
                        _unrealizedPnL = 0.0;
                        try
                        {
                            if (_account != null && _account.IsLocked())
                            {
                                Core.Instance.UnLockAccount(_account);
                                Core.Instance.Loggers.Log($"[RiskManager] Account {_account.Name} unlocked on backfill.");
                            }
                        }
                        catch (Exception unlockEx)
                        {
                            Core.Instance.Loggers.Log($"[RiskManager] Boot unlock error: {unlockEx.Message}");
                        }
                        _renderer?.RedrawBufferedGraphic();
                    }
                }

                CheckLimits();
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[RiskManager] Backfill error: {ex.Message} — keeping last known-good realized PnL: {_dailyPnL:C}");
            }
        }

        private void StartStartupPollTimer()
        {
            _startupPollCount = 0;
            _startupPollTimer?.Dispose();
            _startupPollTimer = new Timer(_ =>
            {
                _startupPollCount++;
                _renderer?.RedrawBufferedGraphic();
                bool balanceLive = _account != null && _account.Balance != 0.0;
                bool maxTicksReached = _startupPollCount >= StartupPollMaxTicks;
                if (balanceLive || maxTicksReached)
                {
                    _startupPollTimer?.Dispose();
                    _startupPollTimer = null;
                    if (balanceLive)
                        Core.Instance.Loggers.Log($"[RiskParameters] Balance available after {_startupPollCount * 30}s: {_account.Balance:C}");
                    else
                        Core.Instance.Loggers.Log("[RiskParameters] Startup poll timed out — balance still unavailable.");
                }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        private void UpdateRoundTripTracker(Trade trade)
        {
            int tradesTaken = _tradesTaken;
            bool roundTripCompleted = UpdateRoundTripTracker(trade, _rtQty, _rtValue, ref tradesTaken);
            _tradesTaken = tradesTaken;
            if (roundTripCompleted && _tradeCooldownEnabled && !_triggered)
                StartCooldown();
        }

        private bool UpdateRoundTripTracker(Trade trade, Dictionary<string, double> rtQty, Dictionary<string, double> rtValue, ref int tradesTaken)
        {
            string symbol = trade.Symbol?.Name ?? trade.Symbol?.Id ?? "UNKNOWN";
            bool isBuy = trade.Side.ToString().Equals("Buy", StringComparison.OrdinalIgnoreCase);
            double signedQty = isBuy ? trade.Quantity : -trade.Quantity;

            if (!rtQty.ContainsKey(symbol)) { rtQty[symbol] = 0; rtValue[symbol] = 0; }

            double prevQty = rtQty[symbol];
            double newQty = prevQty + signedQty;
            rtValue[symbol] += isBuy ? -trade.Quantity : trade.Quantity;

            if ((prevQty > 0 && newQty <= 0) || (prevQty < 0 && newQty >= 0))
            {
                tradesTaken++;
                Core.Instance.Loggers.Log($"[RiskManager] Round trip complete — {symbol} | trades taken: {tradesTaken}");
                rtQty[symbol] = newQty;
                rtValue[symbol] = newQty != 0 ? (isBuy ? -Math.Abs(newQty) : Math.Abs(newQty)) : 0;
                return true;
            }
            else
            {
                rtQty[symbol] = newQty;
                return false;
            }
        }

        private void SubscribePositionUpdates()
        {
            foreach (var pos in Core.Instance.Positions)
                pos.Updated -= OnPositionUpdated;
            if (!_includeUnrealized || _account == null) return;
            foreach (var pos in Core.Instance.Positions.Where(p => p.Account.Equals(_account)))
                pos.Updated += OnPositionUpdated;
        }

        private void OnPositionUpdated(Position pos)
        {
            if (!_includeUnrealized || _triggered) return;
            if (_account == null || !pos.Account.Equals(_account)) return;

            double total = 0.0;
            try
            {
                foreach (var p in Core.Instance.Positions.Where(p => p.Account.Equals(_account)))
                {
                    if (p.NetPnL != null) total += p.NetPnL.Value;
                    else if (p.GrossPnL != null) total += p.GrossPnL.Value;
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
            if (totalPnL < _dailyLossLimit)
                _ = FlattenThenLock($"Loss limit hit (total {totalPnL:C}, unrealized {_unrealizedPnL:C})", refreshPnLBeforeLock: true);
            else if (totalPnL > _dailyProfitTarget)
                _ = FlattenThenLock($"Profit target hit (total {totalPnL:C}, unrealized {_unrealizedPnL:C})", refreshPnLBeforeLock: true);
        }

        private void RefreshUnrealizedPnL()
        {
            if (!_includeUnrealized || _account == null) { _unrealizedPnL = 0.0; return; }
            double total = 0.0;
            try
            {
                foreach (var pos in Core.Instance.Positions.Where(p => p.Account.Equals(_account)))
                {
                    if (pos.NetPnL != null) total += pos.NetPnL.Value;
                    else if (pos.GrossPnL != null) total += pos.GrossPnL.Value;
                }
            }
            catch (Exception ex) { Core.Instance.Loggers.Log($"[RiskManager] Unrealized refresh error: {ex.Message}"); }
            _unrealizedPnL = total;
        }

        public double TotalPnL => _dailyPnL + (_includeUnrealized ? _unrealizedPnL : 0.0);

        private double GetTradePnL(Trade trade)
        {
            if (trade.NetPnl != null) return trade.NetPnl.Value;
            if (trade.GrossPnl != null && trade.Fee != null) return trade.GrossPnl.Value - trade.Fee.Value;
            if (trade.GrossPnl != null) return trade.GrossPnl.Value;
            return double.NaN;
        }

        private double GetTradeFee(Trade trade)
        {
            if (trade.Fee != null && trade.Fee.Value != 0.0) return 0.0;
            string symbol = trade.Symbol?.Name ?? string.Empty;
            if (SymbolMatchesList(symbol, _microSymbols)) return _feePerMicro * trade.Quantity;
            if (SymbolMatchesList(symbol, _miniSymbols)) return _feePerMini * trade.Quantity;
            Core.Instance.Loggers.Log($"[RiskParameters] Unrecognized symbol '{symbol}' — no fee applied.");
            return 0.0;
        }

        private static bool SymbolMatchesList(string symbol, string symbolList)
        {
            if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(symbolList)) return false;
            var entries = symbolList.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var entry in entries)
                if (symbol.StartsWith(entry.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public override void Dispose()
        {
            Core.Instance.TradeAdded -= OnTradeAdded;
            Core.Instance.PositionAdded -= OnPositionAdded;
            Core.Instance.PositionRemoved -= OnPositionRemoved;
            Core.Instance.Connections.ConnectionStateChanged -= OnConnectionStateChanged;
            foreach (var pos in Core.Instance.Positions)
                pos.Updated -= OnPositionUpdated;
            _resetTimer?.Dispose(); _resetTimer = null;
            _flattenLockTimer?.Dispose(); _flattenLockTimer = null;
            _relockTimer?.Dispose(); _relockTimer = null;
            _startupPollTimer?.Dispose(); _startupPollTimer = null;
            _cooldownTimer?.Dispose(); _cooldownTimer = null;
            _lockCountdownTimer?.Dispose(); _lockCountdownTimer = null;
            if (_renderer != null) { _renderer.Dispose(); _renderer = null; }
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
                result.Add(new SettingItemBoolean("HideAccountName", _hideAccountName)
                { Text = "Hide Account Name", SortIndex = 1 });
                result.Add(new SettingItemInteger("HideCharCount", _hideCharCount)
                { Text = "Characters to Hide", SortIndex = 2, Minimum = 0, Maximum = 100 });
                result.Add(new SettingItemInteger("MicroContractLimit", _microContractLimit)
                { Text = "Micro Contract Limit (0 = off)", SortIndex = 3, Minimum = 0, Maximum = 999 });
                result.Add(new SettingItemInteger("MiniContractLimit", _miniContractLimit)
                { Text = "Mini Contract Limit (-1 = off, 0 = no minis)", SortIndex = 4, Minimum = -1, Maximum = 999 });
                result.Add(new SettingItemBoolean("LimitPerSymbol", _limitPerSymbol)
                { Text = "Limit Per Symbol", SortIndex = 5 });
                result.Add(new SettingItemBoolean("AllowPositiveLossLimit", _pendingAllowPositiveLossLimit)
                { Text = "Allow Positive Daily Loss Limit", SortIndex = 6 });
                result.Add(new SettingItemDouble("DailyLossLimit", _pendingDailyLossLimit)
                { Text = "Daily Loss Limit ($)", SortIndex = 7 });
                result.Add(new SettingItemDouble("DailyProfitTarget", _pendingDailyProfitTarget)
                { Text = "Daily Profit Target ($)", SortIndex = 8 });
                result.Add(new SettingItemString("SessionLockTime", _pendingFlattenLockTime.HasValue ? _pendingFlattenLockTime.Value.ToString(@"hh\:mm") : "")
                { Text = "Session Lock Time 24hr local (HH:MM, blank = off)", SortIndex = 9 });
                result.Add(new SettingItemBoolean("IncludeUnrealized", _pendingIncludeUnrealized)
                { Text = "Include Unrealized P&L in Limits", SortIndex = 10 });
                result.Add(new SettingItemDouble("FeePerMicro", _pendingFeePerMicro)
                { Text = "Fee Per Micro Contract (Round Trip $)", SortIndex = 11, Increment = 0.01, DecimalPlaces = 2 });
                result.Add(new SettingItemDouble("FeePerMini", _pendingFeePerMini)
                { Text = "Fee Per Mini Contract (Round Trip $)", SortIndex = 12, Increment = 0.01, DecimalPlaces = 2 });
                result.Add(new SettingItemString("MicroSymbols", _pendingMicroSymbols)
                { Text = "Micro Symbols (comma-separated)", SortIndex = 13 });
                result.Add(new SettingItemString("MiniSymbols", _pendingMiniSymbols)
                { Text = "Mini Symbols (comma-separated)", SortIndex = 14 });
                result.Add(new SettingItemBoolean("MaxTradesEnabled", _pendingMaxTradesEnabled)
                { Text = "Enable Max Trades Per Session", SortIndex = 15 });
                result.Add(new SettingItemInteger("MaxTradesPerSession", _pendingMaxTradesPerSession)
                { Text = "Max Trades Per Session", SortIndex = 16, Minimum = 1, Maximum = 999 });
                result.Add(new SettingItemBoolean("TradeCooldownEnabled", _pendingTradeCooldownEnabled)
                { Text = "Enable Trade Cooldown", SortIndex = 17 });
                result.Add(new SettingItemInteger("TradeCooldownSeconds", _pendingTradeCooldownSeconds)
                { Text = "Cooldown Seconds Per Trade", SortIndex = 18, Minimum = 1, Maximum = 3600 });
                result.Add(new SettingItemInteger("FontSize", _fontSize)
                { Text = "Font Size (label pt)", SortIndex = 19, Minimum = 6, Maximum = 16 });
                result.Add(new SettingItemAction("SaveSettings", new SettingItemActionDelegate(ApplyPendingSettings), 20)
                { Text = _settingsLocked ? "⛔ Settings Locked" : "Save Settings" });
                result.Add(new SettingItemAction("LockSettings", new SettingItemActionDelegate(LockSettings), 22)
                { Text = "Lock Settings Until Reset" });
                result.Add(new SettingItemLabel("LockWarning", "", 28)
                { Text = "⚠ Locks trading until 5PM ET reset." });
                result.Add(new SettingItemLabel("LockWarning2", "", 29)
                { Text = "Cannot be undone." });
                result.Add(new SettingItemAction("ManualLock", new SettingItemActionDelegate(ManualLockUntilReset), 30)
                { Text = "⛔ LOCK TRADING UNTIL 5PM ET" });
                return result;
            }
            set
            {
                base.Settings = value;
                if (_settingsLocked) return;
                foreach (var item in value)
                {
                    switch (item.Name)
                    {
                        case "Account":
                            if (_triggered)
                            {
                                Core.Instance.Loggers.Log("[RiskParameters] Ignored account change — trading is locked.");
                                break;
                            }
                            _account = item.Value as Account;
                            BackfillTodayPnL(); RefreshUnrealizedPnL(); SubscribePositionUpdates();
                            break;
                        case "MicroContractLimit":
                            _microContractLimit = Convert.ToInt32(item.Value);
                            CheckContractLimit(); _renderer?.RedrawBufferedGraphic();
                            break;
                        case "MiniContractLimit":
                            _miniContractLimit = Convert.ToInt32(item.Value);
                            CheckContractLimit(); _renderer?.RedrawBufferedGraphic();
                            break;
                        case "LimitPerSymbol":
                            _limitPerSymbol = (bool)item.Value;
                            CheckContractLimit(); _renderer?.RedrawBufferedGraphic();
                            break;
                        case "AllowPositiveLossLimit":
                            _pendingAllowPositiveLossLimit = (bool)item.Value;
                            break;
                        case "DailyLossLimit":
                            { double v = (double)item.Value; _pendingDailyLossLimit = (!_pendingAllowPositiveLossLimit && v > 0) ? -v : v; break; }
                        case "DailyProfitTarget":
                            _pendingDailyProfitTarget = Math.Abs((double)item.Value);
                            break;
                        case "SessionLockTime":
                            var lockRaw = (string)item.Value;
                            if (string.IsNullOrWhiteSpace(lockRaw)) _pendingFlattenLockTime = null;
                            else if (TimeSpan.TryParse(lockRaw, out var parsedLock)) _pendingFlattenLockTime = parsedLock;
                            break;
                        case "IncludeUnrealized": _pendingIncludeUnrealized = (bool)item.Value; break;
                        case "FeePerMicro": _pendingFeePerMicro = (double)item.Value; break;
                        case "FeePerMini": _pendingFeePerMini = (double)item.Value; break;
                        case "MicroSymbols": _pendingMicroSymbols = (string)item.Value; break;
                        case "MiniSymbols": _pendingMiniSymbols = (string)item.Value; break;
                        case "MaxTradesEnabled": _pendingMaxTradesEnabled = (bool)item.Value; break;
                        case "MaxTradesPerSession": _pendingMaxTradesPerSession = Convert.ToInt32(item.Value); break;
                        case "TradeCooldownEnabled": _pendingTradeCooldownEnabled = (bool)item.Value; break;
                        case "TradeCooldownSeconds": _pendingTradeCooldownSeconds = Convert.ToInt32(item.Value); break;
                        case "FontSize":
                            _fontSize = Convert.ToInt32(item.Value); _renderer?.RedrawBufferedGraphic();
                            break;
                        case "HideAccountName":
                            _hideAccountName = (bool)item.Value; _renderer?.RedrawBufferedGraphic();
                            break;
                        case "HideCharCount":
                            _hideCharCount = Convert.ToInt32(item.Value); _renderer?.RedrawBufferedGraphic();
                            break;
                    }
                }
                _renderer?.RedrawBufferedGraphic();
                if (!_initialSettingsApplied) { _initialSettingsApplied = true; ApplyPendingSettings(); }
            }
        }

        private object ApplyPendingSettings(object args = null)
        {
            bool lockTimeChanged = _pendingFlattenLockTime != _flattenLockTime;
            bool unrealizedChanged = _pendingIncludeUnrealized != _includeUnrealized;
            bool feesChanged = _pendingFeePerMicro != _feePerMicro || _pendingFeePerMini != _feePerMini
                            || _pendingMicroSymbols != _microSymbols || _pendingMiniSymbols != _miniSymbols;

            _allowPositiveLossLimit = _pendingAllowPositiveLossLimit;
            _dailyLossLimit = (!_allowPositiveLossLimit && _pendingDailyLossLimit > 0) ? -Math.Abs(_pendingDailyLossLimit) : _pendingDailyLossLimit;
            _dailyProfitTarget = Math.Abs(_pendingDailyProfitTarget);
            _flattenLockTime = _pendingFlattenLockTime;
            _includeUnrealized = _pendingIncludeUnrealized;
            _feePerMicro = _pendingFeePerMicro;
            _feePerMini = _pendingFeePerMini;
            _microSymbols = _pendingMicroSymbols;
            _miniSymbols = _pendingMiniSymbols;
            _maxTradesEnabled = _pendingMaxTradesEnabled;
            _maxTradesPerSession = _pendingMaxTradesPerSession;
            bool cooldownDisabledWhileActive = !_pendingTradeCooldownEnabled && _cooldownActive;
            _tradeCooldownEnabled = _pendingTradeCooldownEnabled;
            _tradeCooldownSeconds = _pendingTradeCooldownSeconds;

            if (cooldownDisabledWhileActive) EndCooldown();
            if (lockTimeChanged) ScheduleFlattenLockTimer();
            if (unrealizedChanged) { RefreshUnrealizedPnL(); SubscribePositionUpdates(); }
            if (feesChanged) BackfillTodayPnL();
            CheckLimits();

            Core.Instance.Loggers.Log($"[RiskManager] Settings saved — Loss: {_dailyLossLimit:C} | Profit: {_dailyProfitTarget:C} | LockTime: {_flattenLockTime?.ToString() ?? "OFF"} | IncludeUnrealized: {_includeUnrealized}");
            SyncPendingFromLive();
            _renderer?.RedrawBufferedGraphic();
            return null;
        }

        private void SyncPendingFromLive()
        {
            _pendingDailyLossLimit = _dailyLossLimit;
            _pendingDailyProfitTarget = _dailyProfitTarget;
            _pendingFlattenLockTime = _flattenLockTime;
            _pendingIncludeUnrealized = _includeUnrealized;
            _pendingFeePerMicro = _feePerMicro;
            _pendingFeePerMini = _feePerMini;
            _pendingMicroSymbols = _microSymbols;
            _pendingMiniSymbols = _miniSymbols;
            _pendingMaxTradesEnabled = _maxTradesEnabled;
            _pendingMaxTradesPerSession = _maxTradesPerSession;
            _pendingTradeCooldownEnabled = _tradeCooldownEnabled;
            _pendingTradeCooldownSeconds = _tradeCooldownSeconds;
            _pendingAllowPositiveLossLimit = _allowPositiveLossLimit;
        }

        private void ScheduleResetTimer()
        {
            _resetTimer?.Dispose();
            var nowEastern = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _easternTz);
            TimeSpan timeUntilReset = _resetTimeEastern - nowEastern.TimeOfDay;
            if (timeUntilReset < TimeSpan.Zero) timeUntilReset = timeUntilReset.Add(TimeSpan.FromDays(1));
            Core.Instance.Loggers.Log($"[RiskParameters] Next reset in {timeUntilReset:hh\\:mm\\:ss} (5:00 PM ET)");
            _resetTimer = new Timer(OnSessionReset, null, (long)timeUntilReset.TotalMilliseconds, (long)TimeSpan.FromDays(1).TotalMilliseconds);
        }

        private void ScheduleFlattenLockTimer()
        {
            _flattenLockTimer?.Dispose();
            _flattenLockTimer = null;
            if (!_flattenLockTime.HasValue) return;
            TimeSpan timeUntilLock = _flattenLockTime.Value - DateTime.Now.TimeOfDay;
            if (timeUntilLock < TimeSpan.Zero) timeUntilLock = timeUntilLock.Add(TimeSpan.FromDays(1));
            Core.Instance.Loggers.Log($"[RiskParameters] Next session lock in {timeUntilLock:hh\\:mm\\:ss}");
            _flattenLockTimer = new Timer(OnFlattenLockTimerFired, null, (long)timeUntilLock.TotalMilliseconds, (long)TimeSpan.FromDays(1).TotalMilliseconds);
        }

        private void OnFlattenLockTimerFired(object state)
        {
            Core.Instance.Loggers.Log("[RiskParameters] Session Lock Time reached — triggering scheduled flatten & lock.");
            _lockedBySchedule = true;
            _ = FlattenThenLock("Session lock time reached");
        }

        private void OnSessionReset(object state)
        {
            _sessionStartUtc = DateTime.UtcNow;
            Core.Instance.Loggers.Log($"[RiskManager] Session reset at {_sessionStartUtc:u} — P&L and trade counter cleared.");
            _dailyPnL = 0.0; _unrealizedPnL = 0.0; _sessionFees = 0.0; _tradesTaken = 0;
            _rtQty.Clear(); _rtValue.Clear(); _triggered = false; _backfillComplete = false; _countedTradeIds.Clear();
            _relockTimer?.Dispose(); _relockTimer = null;
            _lockedBySchedule = false;
            _settingsLocked = false;
            _cooldownActive = false;
            _cooldownTimer?.Dispose(); _cooldownTimer = null;
            _lockCountdownTimer?.Dispose(); _lockCountdownTimer = null;

            try
            {
                if (_account != null && _account.IsLocked())
                {
                    Core.Instance.UnLockAccount(_account);
                    Core.Instance.Loggers.Log($"[RiskManager] Account {_account.Name} unlocked on session reset.");
                }
            }
            catch (Exception ex) { Core.Instance.Loggers.Log($"[RiskManager] Unlock on reset error: {ex.Message}"); }

            BackfillTodayPnL();
            RefreshUnrealizedPnL();
            _renderer?.RedrawBufferedGraphic();
        }

        private void OnTradeAdded(Trade trade)
        {
            if (_triggered) return;
            if (_account == null) return;
            if (!trade.Account.Equals(_account)) return;
            if (trade.DateTime.ToUniversalTime() < _sessionStartUtc) return;
            if (_countedTradeIds.Contains(trade.Id)) return;

            _countedTradeIds.Add(trade.Id);
            UpdateRoundTripTracker(trade);

            if (_maxTradesEnabled && _tradesTaken >= _maxTradesPerSession)
            {
                _ = FlattenThenLock($"Max trades reached ({_tradesTaken}/{_maxTradesPerSession})");
                return;
            }

            double pnl = GetTradePnL(trade);
            if (double.IsNaN(pnl))
            {
                Core.Instance.Loggers.Log($"[RiskManager] Trade {trade.Id} — no PnL fields populated, skipping P&L update.");
                _renderer?.RedrawBufferedGraphic();
                return;
            }

            _dailyPnL += pnl - GetTradeFee(trade);
            _unrealizedPnL = 0.0;
            _renderer?.RedrawBufferedGraphic();
            Core.Instance.Loggers.Log($"[RiskManager] Trade: {pnl:C} | Realized PnL: {_dailyPnL:C} | Total: {TotalPnL:C} | Trades taken: {_tradesTaken}");

            double total = TotalPnL;
            if (_backfillComplete)
            {
                if (total < _dailyLossLimit) _ = FlattenThenLock($"Loss limit hit ({total:C})", refreshPnLBeforeLock: true);
                else if (total > _dailyProfitTarget) _ = FlattenThenLock($"Profit target hit ({total:C})", refreshPnLBeforeLock: true);
                else CheckContractLimit();
            }
        }

        private async Task FlattenThenLock(string reason, bool refreshPnLBeforeLock = false)
        {
            if (_triggered) return;
            if (_cooldownActive) EndCooldown(relockAfter: false);

            _triggered = true;
            _renderer?.RedrawBufferedGraphic();
            Core.Instance.Loggers.Log($"[RiskManager] TRIGGERED — {reason}. Flattening {_account.Name}...");
            Core.Instance.AdvancedTradingOperations.Flatten(_account);

            int attempts = 0;
            while (attempts < 5)
            {
                await Task.Delay(200);
                bool hasPositions = Core.Instance.Positions.Any(p => p.Account.Equals(_account));
                bool hasOrders = Core.Instance.Orders.Any(o => o.Account.Equals(_account));
                if (!hasPositions && !hasOrders) break;
                attempts++;
            }

            if (refreshPnLBeforeLock)
            {
                BackfillTodayPnL();
                Core.Instance.Loggers.Log($"[RiskManager] Post-flatten PnL refresh — realized: {_dailyPnL:C}");
                _renderer?.RedrawBufferedGraphic();
            }

            Core.Instance.LockAccount(_account);
            _renderer?.RedrawBufferedGraphic();
            Core.Instance.Loggers.Log($"[RiskManager] Account {_account.Name} locked. Reason: {reason}");
            Core.Instance.Alert($"Risk Manager: {reason}. Account locked.");
            StartRelockMonitor();
            StartLockCountdownTimer();
        }

        private void StartLockCountdownTimer()
        {
            _lockCountdownTimer?.Dispose();
            _lockCountdownTimer = new Timer(_ =>
            {
                try
                {
                    if (!_triggered) { _lockCountdownTimer?.Dispose(); _lockCountdownTimer = null; return; }
                    var remaining = LockRemaining;
                    if (remaining.TotalMinutes < 1.0 || (int)remaining.TotalSeconds % 60 == 0)
                        _renderer?.RedrawBufferedGraphic();
                }
                catch (Exception ex) { Core.Instance.Loggers.Log($"[RiskManager] Lock countdown timer error: {ex.Message}"); }
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        private void CheckLimits()
        {
            if (_triggered) return;
            if (!_backfillComplete) return;
            double total = TotalPnL;
            if (total < _dailyLossLimit) { _ = FlattenThenLock($"Loss limit hit ({total:C})", refreshPnLBeforeLock: true); return; }
            if (total > _dailyProfitTarget) { _ = FlattenThenLock($"Profit target hit ({total:C})", refreshPnLBeforeLock: true); return; }
            if (_maxTradesEnabled && _tradesTaken >= _maxTradesPerSession)
                _ = FlattenThenLock($"Max trades reached ({_tradesTaken}/{_maxTradesPerSession})");
        }

        private void CheckContractLimit()
        {
            if (_account == null) return;
            if (_microContractLimit <= 0 && _miniContractLimit < 0) return;

            try
            {
                var positions = Core.Instance.Positions.Where(p => p.Account.Equals(_account)).ToList();

                if (_limitPerSymbol)
                {
                    var bySymbol = positions.GroupBy(p => p.Symbol?.Name ?? string.Empty);
                    foreach (var group in bySymbol)
                    {
                        string sym = group.Key;
                        int limit;
                        if (SymbolMatchesList(sym, _microSymbols)) { if (_microContractLimit <= 0) continue; limit = _microContractLimit; }
                        else if (SymbolMatchesList(sym, _miniSymbols)) { if (_miniContractLimit < 0) continue; limit = _miniContractLimit; }
                        else continue;
                        double total = group.Sum(p => p.Quantity);
                        if (total <= limit) continue;
                        double excess = total - limit;
                        Core.Instance.Loggers.Log($"[RiskManager] Contract limit ({limit}) exceeded for {sym} — total: {total}, excess: {excess}");
                        CloseExcess(group.OrderByDescending(p => p.OpenTime).ToList(), excess);
                    }
                }
                else
                {
                    if (_microContractLimit > 0)
                    {
                        var micros = positions.Where(p => SymbolMatchesList(p.Symbol?.Name ?? string.Empty, _microSymbols)).ToList();
                        double total = micros.Sum(p => p.Quantity);
                        if (total > _microContractLimit)
                        {
                            double excess = total - _microContractLimit;
                            Core.Instance.Loggers.Log($"[RiskManager] Micro contract limit exceeded — total: {total}, excess: {excess}");
                            CloseExcess(micros.OrderByDescending(p => p.OpenTime).ToList(), excess);
                        }
                    }
                    if (_miniContractLimit >= 0)
                    {
                        var minis = positions.Where(p => SymbolMatchesList(p.Symbol?.Name ?? string.Empty, _miniSymbols)).ToList();
                        double total = minis.Sum(p => p.Quantity);
                        if (total > _miniContractLimit)
                        {
                            double excess = total - _miniContractLimit;
                            Core.Instance.Loggers.Log($"[RiskManager] Mini contract limit exceeded — total: {total}, excess: {excess}");
                            CloseExcess(minis.OrderByDescending(p => p.OpenTime).ToList(), excess);
                        }
                    }
                }
                _renderer?.RedrawBufferedGraphic();
            }
            catch (Exception ex) { Core.Instance.Loggers.Log($"[RiskManager] Contract limit check error: {ex.Message}"); }
        }

        private void CloseExcess(List<Position> positions, double excess)
        {
            foreach (var pos in positions)
            {
                if (excess <= 0) break;
                double closeQty = Math.Min(pos.Quantity, excess);
                Side closeSide = pos.Side == Side.Buy ? Side.Sell : Side.Buy;
                Core.Instance.Loggers.Log($"[RiskManager] Closing excess {closeQty} of {pos.Quantity} on {pos.Symbol?.Name} via {closeSide}");
                try
                {
                    var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
                    {
                        Account = _account,
                        Symbol = pos.Symbol,
                        Side = closeSide,
                        OrderTypeId = "Market",
                        Quantity = closeQty,
                        PositionId = pos.Id,
                        Comment = "RiskManager excess close"
                    });
                    if (result.Status != TradingOperationResultStatus.Success)
                        Core.Instance.Loggers.Log($"[RiskManager] Excess close failed for {pos.Symbol?.Name}: {result.Message}");
                }
                catch (Exception ex) { Core.Instance.Loggers.Log($"[RiskManager] Excess close error: {ex.Message}"); }
                excess -= closeQty;
            }
        }

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
                catch (Exception ex) { Core.Instance.Loggers.Log($"[RiskManager] Relock monitor error: {ex.Message}"); }
            }, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        }

        private void StartCooldown()
        {
            _cooldownEndUtc = DateTime.UtcNow.AddSeconds(_tradeCooldownSeconds);
            _cooldownActive = true;
            try { if (_account != null && !_account.IsLocked()) Core.Instance.LockAccount(_account); }
            catch (Exception ex) { Core.Instance.Loggers.Log($"[RiskParameters] Cooldown lock error: {ex.Message}"); }
            Core.Instance.Loggers.Log($"[RiskParameters] Trade Cooldown started — {_tradeCooldownSeconds}s, ends {_cooldownEndUtc:u}");
            _renderer?.RedrawBufferedGraphic();

            _cooldownTimer?.Dispose();
            _cooldownTimer = new Timer(_ =>
            {
                try
                {
                    if (!_cooldownActive) return;
                    if (DateTime.UtcNow >= _cooldownEndUtc) { EndCooldown(relockAfter: false); return; }
                    if (_account != null && !_account.IsLocked()) Core.Instance.LockAccount(_account);
                    _renderer?.RedrawBufferedGraphic();
                }
                catch (Exception ex) { Core.Instance.Loggers.Log($"[RiskParameters] Cooldown timer error: {ex.Message}"); }
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        private void EndCooldown(bool relockAfter = false)
        {
            _cooldownActive = false;
            _cooldownTimer?.Dispose(); _cooldownTimer = null;
            try
            {
                if (_account != null && _account.IsLocked() && !_triggered)
                {
                    Core.Instance.UnLockAccount(_account);
                    Core.Instance.Loggers.Log($"[RiskParameters] Trade Cooldown ended — account {_account.Name} unlocked.");
                }
            }
            catch (Exception ex) { Core.Instance.Loggers.Log($"[RiskParameters] Cooldown unlock error: {ex.Message}"); }
            _renderer?.RedrawBufferedGraphic();
        }

        private object ManualLockUntilReset(object args = null)
        {
            if (_triggered) return null;
            Application.Instance.Notifications.ShowMessageBox(new MessageBoxParameters
            {
                Message = "Lock trading until 5PM ET reset?\nThis cannot be undone.",
                Buttons = MessageBoxButton.YesNo,
                IconType = MessageBoxIconType.Alert,
                Callback = result =>
                {
                    if (result != MessageBoxResult.Yes) return;
                    Core.Instance.Loggers.Log("[RiskParameters] Manual lock-until-reset confirmed by user.");
                    _lockedBySchedule = true;
                    _ = FlattenThenLock("Manual lock until reset");
                }
            }, this.Marker);
            return null;
        }

        private object LockSettings(object args = null)
        {
            Application.Instance.Notifications.ShowMessageBox(new MessageBoxParameters
            {
                Message = "Lock settings until 5PM ET reset?\nNo changes can be saved until then.",
                Buttons = MessageBoxButton.YesNo,
                IconType = MessageBoxIconType.Alert,
                Callback = result =>
                {
                    if (result != MessageBoxResult.Yes) return;
                    ApplyPendingSettings();
                    _settingsLocked = true;
                    Core.Instance.Loggers.Log("[RiskParameters] Settings locked until 5 PM ET reset.");
                    _renderer?.RedrawBufferedGraphic();
                }
            }, this.Marker);
            return null;
        }

        // Expose state to renderer
        public Account MonitoredAccount => _account;
        public double DailyPnL => _dailyPnL;
        public double UnrealizedPnL => _unrealizedPnL;
        public double DailyLossLimit => _dailyLossLimit;
        public double DailyProfitTarget => _dailyProfitTarget;
        public TimeSpan? FlattenLockTime => _flattenLockTime;
        public int MicroContractLimit => _microContractLimit;
        public int MiniContractLimit => _miniContractLimit;
        public bool LimitPerSymbol => _limitPerSymbol;
        public int TradesTaken => _tradesTaken;
        public int DisplayContractLimit => (_miniContractLimit >= 0) ? _miniContractLimit : _microContractLimit;
        public int OpenContracts => (_account == null) ? 0 : (int)Core.Instance.Positions.Where(p => p.Account.Equals(_account)).Sum(p => p.Quantity);
        public int OpenMicroContracts => (_account == null) ? 0 : (int)Core.Instance.Positions.Where(p => p.Account.Equals(_account) && SymbolMatchesList(p.Symbol?.Name ?? string.Empty, _microSymbols)).Sum(p => p.Quantity);
        public int OpenMiniContracts => (_account == null) ? 0 : (int)Core.Instance.Positions.Where(p => p.Account.Equals(_account) && SymbolMatchesList(p.Symbol?.Name ?? string.Empty, _miniSymbols)).Sum(p => p.Quantity);
        public bool IsTriggered => _triggered;
        public bool IncludeUnrealized => _includeUnrealized;
        public bool HideAccountName => _hideAccountName;
        public int HideCharCount => _hideCharCount;
        public int FontSize => _fontSize;
        public bool MaxTradesEnabled => _maxTradesEnabled;
        public int MaxTradesPerSession => _maxTradesPerSession;
        public bool CooldownActive => _cooldownActive;

        public TimeSpan CooldownRemaining
        {
            get
            {
                if (!_cooldownActive) return TimeSpan.Zero;
                var remaining = _cooldownEndUtc - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        public TimeSpan LockRemaining
        {
            get
            {
                var nowEastern = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _easternTz);
                var todayReset = nowEastern.Date + _resetTimeEastern;
                var nextReset = nowEastern < todayReset ? todayReset : todayReset.AddDays(1);
                var remaining = nextReset - nowEastern;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
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

        public void RedrawBufferedGraphic() { bufferedGraphic.IsDirty = true; }

        protected virtual void Draw(Graphics gr)
        {
            gr.Clear(Color.FromArgb(30, 30, 30));

            int fontSize = _plugin.FontSize;
            var labelFont = new Font("Arial", fontSize, FontStyle.Regular);
            var valueFont = new Font("Arial", fontSize + 1, FontStyle.Bold);
            var whiteBrush = new SolidBrush(Color.White);
            var grayBrush = new SolidBrush(Color.FromArgb(160, 160, 160));
            var greenBrush = new SolidBrush(Color.FromArgb(0, 200, 100));
            var redBrush = new SolidBrush(Color.FromArgb(220, 60, 60));
            var yellowBrush = new SolidBrush(Color.FromArgb(255, 200, 0));
            var divPen = new Pen(Color.FromArgb(60, 60, 60));

            int lineH = 26;
            int y = 12;
            int mid = Bounds.Width / 2;
            int lx = 10; int lv = lx + 95; int rx = mid + 8; int rv = rx + 95;

            gr.DrawLine(divPen, lx, y, Bounds.Width - lx, y);
            y += 8;

            // --- Row 1: Account | Trades Taken ---
            gr.DrawString("Account", labelFont, grayBrush, lx, y);
            string accountName = _plugin.MonitoredAccount?.Name ?? "None";
            string displayName;
            if (_plugin.HideAccountName && accountName != "None")
            {
                int charsToHide = Math.Min(_plugin.HideCharCount, accountName.Length);
                displayName = new string('*', charsToHide) + accountName.Substring(charsToHide);
            }
            else displayName = accountName;
            gr.DrawString(displayName, valueFont, whiteBrush, lv, y);
            gr.DrawString("Trades Taken", labelFont, grayBrush, rx, y);
            string tradesTakenStr = _plugin.MaxTradesEnabled ? $"{_plugin.TradesTaken} / {_plugin.MaxTradesPerSession}" : _plugin.TradesTaken.ToString();
            var tradesBrush = (_plugin.MaxTradesEnabled && _plugin.TradesTaken >= _plugin.MaxTradesPerSession) ? redBrush : whiteBrush;
            gr.DrawString(tradesTakenStr, valueFont, tradesBrush, rv, y);
            y += lineH;

            // --- Row 2: Loss Limit | Contract Limit ---
            gr.DrawString("Loss Limit", labelFont, grayBrush, lx, y);
            gr.DrawString($"{_plugin.DailyLossLimit:C}", valueFont, redBrush, lv, y);
            gr.DrawString("Contract Limit", labelFont, grayBrush, rx, y);
            bool miniActive = _plugin.MiniContractLimit >= 0;
            bool microActive = _plugin.MicroContractLimit > 0;
            if (miniActive || microActive)
            {
                string miniStr = miniActive ? $"{_plugin.MiniContractLimit} Mini" : "-- Mini";
                string microStr = microActive ? $"{_plugin.MicroContractLimit} Micro" : "-- Micro";
                gr.DrawString($"{miniStr}  {microStr}", valueFont, whiteBrush, rv, y);
            }
            else gr.DrawString("OFF", valueFont, grayBrush, rv, y);
            y += lineH;

            // --- Row 3: Profit Target | Open Contracts ---
            gr.DrawString("Profit Target", labelFont, grayBrush, lx, y);
            gr.DrawString($"{_plugin.DailyProfitTarget:C}", valueFont, greenBrush, lv, y);
            gr.DrawString("Open Contracts", labelFont, grayBrush, rx, y);
            int openMini = _plugin.OpenMiniContracts; int openMicro = _plugin.OpenMicroContracts;
            bool miniOver = miniActive && openMini > _plugin.MiniContractLimit;
            bool microOver = microActive && openMicro > _plugin.MicroContractLimit;
            string openMiniStr = $"{openMini} Mini  "; string openMicroStr = $"{openMicro} Micro";
            float miniW = gr.MeasureString(openMiniStr, valueFont).Width;
            gr.DrawString(openMiniStr, valueFont, miniOver ? redBrush : whiteBrush, rv, y);
            gr.DrawString(openMicroStr, valueFont, microOver ? redBrush : whiteBrush, rv + miniW, y);
            y += lineH;

            // --- Row 4: Realized P&L | Lock Time ---
            gr.DrawString("Realized P&L", labelFont, grayBrush, lx, y);
            gr.DrawString($"{_plugin.DailyPnL:C}", valueFont, _plugin.DailyPnL >= 0 ? greenBrush : redBrush, lv, y);
            gr.DrawString("Lock Time", labelFont, grayBrush, rx, y);
            if (_plugin.FlattenLockTime.HasValue)
                gr.DrawString(DateTime.Today.Add(_plugin.FlattenLockTime.Value).ToString("h:mm tt"), valueFont, whiteBrush, rv, y);
            else gr.DrawString("OFF", valueFont, grayBrush, rv, y);
            y += lineH;

            // --- Row 5: Unrealized P&L | Reset Time ---
            gr.DrawString("Unrealized P&L", labelFont, grayBrush, lx, y);
            if (_plugin.IncludeUnrealized)
                gr.DrawString($"{_plugin.UnrealizedPnL:C}", valueFont, _plugin.UnrealizedPnL >= 0 ? greenBrush : redBrush, lv, y);
            else gr.DrawString("OFF", valueFont, grayBrush, lv, y);
            gr.DrawString("Reset Time", labelFont, grayBrush, rx, y);
            gr.DrawString("5:00 PM ET", valueFont, whiteBrush, rv, y);
            y += lineH;

            // --- Divider ---
            gr.DrawLine(divPen, lx, y, Bounds.Width - lx, y);
            y += 6;

            // --- Row 6: Total P&L | Status ---
            gr.DrawString("Total P&L", labelFont, grayBrush, lx, y);
            double total = _plugin.TotalPnL;
            gr.DrawString($"{total:C}", valueFont, total >= 0 ? greenBrush : redBrush, lv, y);
            gr.DrawString("Status", labelFont, grayBrush, rx, y);
            gr.DrawString(_plugin.IsTriggered ? "LOCKED" : "ACTIVE", valueFont, _plugin.IsTriggered ? redBrush : greenBrush, rv, y);
            y += lineH;

            // --- P&L Progress Bar ---
            {
                double lossLimit = _plugin.DailyLossLimit;
                double profitTarget = _plugin.DailyProfitTarget;
                double pnl = _plugin.TotalPnL;
                double range = profitTarget - lossLimit;
                int barX = lx; int barW = Bounds.Width - lx * 2; int barH = 10;
                double clampedPnl = Math.Max(lossLimit, Math.Min(profitTarget, pnl));
                int zeroX = barX + (int)(((0.0 - lossLimit) / range) * barW);
                int pnlX = barX + (int)(((clampedPnl - lossLimit) / range) * barW);

                var trackBrush = new SolidBrush(Color.FromArgb(60, 60, 60));
                gr.FillRectangle(trackBrush, barX, y, barW, barH);
                trackBrush.Dispose();

                if (pnl < 0) { int fl = Math.Min(pnlX, zeroX); int fr = Math.Max(pnlX, zeroX); if (fr > fl) gr.FillRectangle(redBrush, fl, y, fr - fl, barH); }
                else if (pnl > 0) { int fl = Math.Min(pnlX, zeroX); int fr = Math.Max(pnlX, zeroX); if (fr > fl) gr.FillRectangle(greenBrush, fl, y, fr - fl, barH); }

                gr.FillRectangle(new SolidBrush(Color.FromArgb(180, 180, 180)), zeroX - 1, y - 2, 2, barH + 4);
                gr.FillPolygon(pnl >= 0 ? greenBrush : redBrush, new[] { new Point(pnlX, y - 1), new Point(pnlX - 5, y - 7), new Point(pnlX + 5, y - 7) });

                int labelY = y + barH + 6;
                var tinyFont = new Font("Arial", 9, FontStyle.Regular);
                string profitLabelStr = $"{profitTarget:C}";
                float profitLabelW = gr.MeasureString(profitLabelStr, tinyFont).Width;
                gr.DrawString($"{lossLimit:C}", tinyFont, redBrush, barX, labelY);
                gr.DrawString(profitLabelStr, tinyFont, greenBrush, barX + barW - profitLabelW, labelY);
                tinyFont.Dispose();
            }

            labelFont.Dispose(); valueFont.Dispose(); whiteBrush.Dispose(); grayBrush.Dispose();
            greenBrush.Dispose(); redBrush.Dispose(); yellowBrush.Dispose(); divPen.Dispose();
        }

        public override IntPtr Render() => bufferedGraphic.CurrentImage;

        public override void Dispose()
        {
            if (bufferedGraphic != null) { bufferedGraphic.Dispose(); bufferedGraphic = null; }
            base.Dispose();
        }

        public override void OnResize()
        {
            base.OnResize();
            Rectangle bounds = Bounds;
            if (bounds.Width == 0 || bounds.Height == 0) return;
            try { bufferedGraphic.Resize(bounds.Width, bounds.Height); bufferedGraphic.IsDirty = true; }
            catch { }
        }
    }
}
