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

namespace RiskManager
{
    public class RiskManagerPlugin : Plugin
    {
        // --- Settings ---
        private Account _account;
        private double _dailyLossLimit = -120.0;
        private double _dailyProfitTarget = 360.0;
        private TimeSpan? _flattenLockTime = null; // null = disabled
        private TimeSpan _sessionResetTime = new TimeSpan(15, 30, 0);
        private bool _includeUnrealized = false;
        private bool _hideAccountName = false;
        private int _hideCharCount = 4;
        private int _fontSize = 9;           // base label font size; value font is +1
        private double _feePerMicro = 0.0;
        private double _feePerMini = 0.0;
        private string _microSymbols = "MES, MNQ, M2K";
        private string _miniSymbols = "ES, NQ, RTY";
        private bool _maxTradesEnabled = false;
        private int _maxTradesPerSession = 10;
        private bool _tradeCooldownEnabled = false;
        private int _tradeCooldownSeconds = 120;
        private bool _allowPositiveLossLimit = false;

        // --- Pending (staged) settings â€” copied to live only when Save is clicked ---
        private double _pendingDailyLossLimit = -120.0;
        private double _pendingDailyProfitTarget = 360.0;
        private TimeSpan? _pendingFlattenLockTime = null;
        private TimeSpan _pendingSessionResetTime = new TimeSpan(15, 30, 0);
        private bool _pendingIncludeUnrealized = false;
        private bool _pendingHideAccountName = false;
        private int _pendingHideCharCount = 4;
        private double _pendingFeePerMicro = 0.0;
        private double _pendingFeePerMini = 0.0;
        private string _pendingMicroSymbols = "MES, MNQ, M2K";
        private string _pendingMiniSymbols = "ES, NQ, RTY";
        private bool _pendingMaxTradesEnabled = false;
        private int _pendingMaxTradesPerSession = 10;
        private bool _pendingTradeCooldownEnabled = false;
        private int _pendingTradeCooldownSeconds = 120;
        private bool _pendingAllowPositiveLossLimit = false;

        // Set true after the very first time the Settings setter runs (the platform's
        // restore-from-disk call on load). That first call is auto-committed to live
        // fields so restored state takes effect immediately on reboot â€” every later
        // call (user editing the panel) stays staged-only until Save Settings is clicked.
        private bool _initialSettingsApplied = false;

        // --- State ---
        private double _dailyPnL = 0.0;         // realized only
        private double _unrealizedPnL = 0.0;    // live open position(s)
        private double _sessionFees = 0.0;      // cumulative fees since session reset (for balance display)
        private int _microContractLimit = 0;     // 0 = disabled; applies to micro symbols
        private int _miniContractLimit = -1;     // -1 = disabled; 0 = no minis allowed; >0 = limit
        private bool _limitPerSymbol = false;    // true = limit checked per individual symbol; false = pooled
        private bool _triggered = false;
        private bool _lockedBySchedule = false; // true when lock was triggered by FlattenLockTime (not P&L limit)
        private bool _backfillComplete = false;  // true once BackfillTodayPnL() has finished; gates OnTradeAdded limit checks

        // --- Trade Cooldown state ---
        private bool _cooldownActive = false;   // true while the account is locked purely for cooldown
        private DateTime _cooldownEndUtc;       // moment the cooldown lock lifts
        private Timer _cooldownTimer;           // ticks every second while cooldown is active (redraw + relock/expiry check)

        // --- Lock countdown timer ---
        // Ticks every second while _triggered is true so the LockRemaining display
        // updates consistently (every minute normally, every second on the last minute).
        private Timer _lockCountdownTimer;
        private Timer _tradeClockTimer; // ticks every second while a position is open so the Trade Clock row updates live
        private DateTime? _tradeClockStartLocal;

        // --- Round-trip trade counter (FIFO, mirrors Trade Journal AggregateDayStats) ---
        private int _tradesTaken = 0;
        private readonly Dictionary<string, double> _rtQty = new Dictionary<string, double>();   // running net position qty per symbol
        private readonly Dictionary<string, double> _rtValue = new Dictionary<string, double>(); // running accumulated fill value per symbol

        private Timer _resetTimer;
        private Timer _flattenLockTimer; // fires once daily at _flattenLockTime to auto-flatten & lock
        private Timer _relockTimer; // polls lock state while triggered, relocks if manually unlocked
        private Timer _startupPollTimer; // polls balance every 30s for first 2 min until a real value arrives
        private int _startupPollCount = 0;
        private const int StartupPollMaxTicks = 4; // 4 x 30s = 2 minutes
        private readonly HashSet<string> _countedTradeIds = new HashSet<string>();

        // The UTC moment the current session started â€” trades before this are ignored.
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
            // Sync pending fields to live defaults so the settings panel opens
            // showing the current values rather than uninitialized defaults.
            SyncPendingFromLive();
            _renderer = new RiskManagerRenderer(this.Window.CreateRenderingControl("RiskManagerRenderer"), this);
            Core.Instance.TradeAdded += OnTradeAdded;
            Core.Instance.PositionAdded += OnPositionAdded;
            Core.Instance.PositionRemoved += OnPositionRemoved;
            Core.Instance.Connections.ConnectionStateChanged += OnConnectionStateChanged;
            ScheduleResetTimer();
            ScheduleFlattenLockTimer();
            StartStartupPollTimer();
            StartTradeClockTimer();
        }

        private void OnConnectionStateChanged(object sender, ConnectionStateChangedEventArgs e)
        {
            if (e.NewState != TradingPlatform.BusinessLayer.ConnectionState.Connected) return;
            if (!_triggered) return;
            if (_account == null) return;

            // Connection is now live â€” safe to backfill and evaluate whether to unlock.
            // Only unlock if reset time has already passed today AND P&L is within limits.
            // Never auto-unlock if the lock was triggered by the scheduled FlattenLockTime;
            // in that case the account must remain locked until the session reset fires.
            if (_lockedBySchedule)
            {
                Core.Instance.Loggers.Log($"[RiskManager] Connection established: account locked by schedule â€” staying locked until reset time.");
                return;
            }

            // Refresh now regardless of reset time so the displayed PnL is never
            // stale after a reconnect. Only the unlock decision itself waits for reset.
            BackfillTodayPnL();
            RefreshUnrealizedPnL();
            _renderer?.RedrawBufferedGraphic();

            if (DateTime.Now.TimeOfDay < _sessionResetTime)
            {
                Core.Instance.Loggers.Log($"[RiskManager] Connection established: PnL refreshed ({TotalPnL:C}) but reset time hasn't passed â€” staying locked.");
                return;
            }

            double total = TotalPnL;
            if (total > _dailyLossLimit && total < _dailyProfitTarget)
            {
                Core.Instance.Loggers.Log($"[RiskManager] Connection established: reset time passed, P&L ({total:C}) within limits â€” unlocking.");
                _relockTimer?.Dispose();
                _relockTimer = null;
                _triggered = false;
                // Keep backfilled _dailyPnL and _countedTradeIds intact -- do NOT zero them.
                // Zeroing _dailyPnL causes a false-lock on the next trade if loss limit = 0.
                // Clearing _countedTradeIds causes OnTradeAdded to double-count backfilled fills.
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
                Core.Instance.Loggers.Log($"[RiskManager] Connection established: P&L ({total:C}) still exceeds threshold â€” remaining locked.");
            }
        }

        private void OnPositionAdded(Position pos)
        {
            if (_account == null || !pos.Account.Equals(_account)) return;
            if (_includeUnrealized)
                pos.Updated += OnPositionUpdated;

            // Start the trade clock immediately when the first position opens.
            // Use the position's own OpenTime so the clock is accurate even if
            // this event fires slightly after the fill (e.g. during a fast market).
            if (_tradeClockStartLocal == null)
            {
                var openTime = pos.OpenTime;
                _tradeClockStartLocal = (openTime != default && openTime.Kind != DateTimeKind.Unspecified)
                    ? openTime.ToLocalTime()
                    : DateTime.Now;
            }

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

            // Restore _triggered from lock state immediately so status shows LOCKED correctly.
            // Unlock logic runs only after the connection is confirmed live (OnConnectionStateChanged).
            if (_account != null && _account.IsLocked())
                _triggered = true;

            // If the platform was opened during the window between FlattenLockTime and SessionResetTime,
            // treat it as if the scheduled lock had fired â€” flatten and lock immediately.
            if (!_triggered && _flattenLockTime.HasValue && _account != null)
            {
                var now = DateTime.Now.TimeOfDay;
                bool inLockWindow = _flattenLockTime.Value <= _sessionResetTime
                    ? now >= _flattenLockTime.Value && now < _sessionResetTime   // same-day window (e.g. 11:00-15:30)
                    : now >= _flattenLockTime.Value || now < _sessionResetTime;  // overnight window (e.g. 23:00-00:30)

                if (inLockWindow)
                {
                    Core.Instance.Loggers.Log($"[RiskManager] Platform opened inside lock window ({_flattenLockTime.Value:hh\\:mm}-{_sessionResetTime:hh\\:mm}) -- triggering scheduled lock.");
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

        /// <summary>
        /// Returns the UTC start of the current session based on _sessionResetTime.
        /// If the local reset time has already passed today  â†’ session started today at that time.
        /// If it has not yet passed today                    â†’ session started yesterday at that time.
        /// This means opening the platform after the reset time correctly sees a fresh session,
        /// and reopening it later still reads only trades that occurred after the last reset.
        /// </summary>
        private DateTime ComputeSessionStartUtc()
        {
            var nowLocal = DateTime.Now;
            var todayReset = nowLocal.Date + _sessionResetTime;

            var lastReset = nowLocal >= todayReset
                ? todayReset            // past reset today  â†’ session started today
                : todayReset.AddDays(-1); // before reset today â†’ session started yesterday

            return lastReset.ToUniversalTime();
        }

        /// <summary>
        /// Polls account.Balance every 30 seconds for the first 2 minutes after startup.
        /// Stops as soon as a non-zero balance is detected (feed connected) or after max ticks.
        /// Redraws on every tick so the balance field updates as soon as the feed is live.
        /// </summary>
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
                        Core.Instance.Loggers.Log($"[RiskManager] Balance available after {_startupPollCount * 30}s: {_account.Balance:C}");
                    else
                        Core.Instance.Loggers.Log("[RiskManager] Startup poll timed out â€” balance still unavailable.");
                }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        private void BackfillTodayPnL()
        {
            if (_account == null) return;

            // Compute into locals first. Only commit to the real fields once the
            // whole pass succeeds, so a mid-method exception (e.g. a trade record
            // arriving mid-reconnect with a null Account) leaves the last known-good
            // _dailyPnL/_tradesTaken/etc intact instead of silently zeroing them.
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
                    Core.Instance.Loggers.Log("[RiskManager] GetTrades returned null â€” no backfill");
                    return;
                }

                var ordered = trades
                    .Where(t => t.Account != null && t.Account.Equals(_account)
                             && t.DateTime.ToUniversalTime() >= sessionStartUtc)
                    .OrderBy(t => t.DateTime)
                    .ToList();

                foreach (var trade in ordered)
                {
                    // Always feed every fill into the round-trip tracker â€”
                    // opening legs have no PnL but still need to build up qty
                    UpdateRoundTripTracker(trade, rtQty, rtValue, ref tradesTaken);

                    double pnl = GetTradePnL(trade);
                    if (double.IsNaN(pnl)) continue;

                    double fee = GetTradeFee(trade);
                    countedTradeIds.Add(trade.Id);
                    dailyPnL += pnl - fee;
                    sessionFees += fee;
                }

                // Commit only after the whole pass completed without throwing.
                _sessionStartUtc = sessionStartUtc;
                _dailyPnL = dailyPnL;
                _sessionFees = sessionFees;
                _tradesTaken = tradesTaken;
                _rtQty.Clear(); foreach (var kv in rtQty) _rtQty[kv.Key] = kv.Value;
                _rtValue.Clear(); foreach (var kv in rtValue) _rtValue[kv.Key] = kv.Value;
                _countedTradeIds.Clear(); foreach (var id in countedTradeIds) _countedTradeIds.Add(id);

                Core.Instance.Loggers.Log($"[RiskManager] Backfill complete â€” session start: {_sessionStartUtc:u} | fills: {_countedTradeIds.Count} | round trips: {_tradesTaken} | realized PnL: {_dailyPnL:C}");

                _backfillComplete = true;

                // --- Boot-time unlock for max-trades lock ---
                // On a fresh Quantower startup, Populate() sees the account is locked and
                // sets _triggered = true before BackfillTodayPnL runs.  The normal unlock
                // path (OnConnectionStateChanged) only fires when connection state changes;
                // if the platform is already connected when the plugin loads, that event
                // never fires and the account stays locked indefinitely.
                //
                // Loss/profit limits reuse the same OnConnectionStateChanged path and have
                // the same exposure, but in practice they get unlocked there because the
                // connection is still settling when the plugin loads.  Max-trades does not
                // benefit from that timing accident.
                //
                // Fix: after backfill we know the true state of this session.  If the
                // account is triggered, the schedule lock is not active, reset time has
                // passed (new session), P&L is within limits, AND the new session's trade
                // count is below the max-trades limit, unlock here exactly as
                // OnConnectionStateChanged does for P&L locks.
                if (_triggered && !_lockedBySchedule && DateTime.Now.TimeOfDay >= _sessionResetTime)
                {
                    double totalCheck = TotalPnL;
                    bool pnlOk = totalCheck > _dailyLossLimit && totalCheck < _dailyProfitTarget;
                    bool tradesOk = !_maxTradesEnabled || _tradesTaken < _maxTradesPerSession;
                    if (pnlOk && tradesOk)
                    {
                        Core.Instance.Loggers.Log($"[RiskManager] Backfill: reset time passed, P&L ({totalCheck:C}) within limits, trades ({_tradesTaken}/{(_maxTradesEnabled ? _maxTradesPerSession.ToString() : "N/A")}) within limit -- unlocking.");
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
                                Core.Instance.Loggers.Log($"[RiskManager] Account {_account.Name} unlocked on backfill (boot unlock).");
                            }
                        }
                        catch (Exception unlockEx)
                        {
                            Core.Instance.Loggers.Log($"[RiskManager] Boot unlock error: {unlockEx.Message}");
                        }
                        _renderer?.RedrawBufferedGraphic();
                    }
                }

                // Evaluate all limits from a known-good baseline now that every session
                // trade is counted. CheckLimits() is gated on _backfillComplete so it
                // cannot fire before real trade data is available -- this call is the
                // first time it will actually evaluate limits.
                CheckLimits();
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[RiskManager] Backfill error: {ex.Message} â€” keeping last known-good realized PnL: {_dailyPnL:C}");
            }
        }

        /// <summary>
        /// FIFO round-trip counter.
        /// Maintains a running open quantity per symbol.
        /// Every time a closing leg reduces open qty to zero, _tradesTaken increments.
        /// Scale in 3x, scale out 3x = 1 trade. Simple.
        /// </summary>
        /// <summary>
        /// FIFO round-trip counter â€” direct port of Trade Journal's AggregateDayStats loop.
        /// Increments _tradesTaken each time net qty crosses or returns to zero.
        /// Scale in 3x, scale out 3x = 1 round trip. Reversals count as 1 and start fresh.
        /// </summary>
        private void UpdateRoundTripTracker(Trade trade)
        {
            int tradesTaken = _tradesTaken;
            bool roundTripCompleted = UpdateRoundTripTracker(trade, _rtQty, _rtValue, ref tradesTaken);
            _tradesTaken = tradesTaken;

            // Trade Cooldown: start the lockout the instant this LIVE trade is fully closed.
            // Kept out of the shared core method below â€” BackfillTodayPnL calls that method
            // directly against scratch dictionaries to replay already-closed historical trades
            // (on startup and whenever settings are saved), and those replays must never
            // re-trigger a cooldown lock. Only this live wrapper (reached via OnTradeAdded)
            // starts the cooldown. Skipped entirely if the account is already locked for any
            // other reason â€” that lock always takes priority and cooldown is overruled/not started.
            if (roundTripCompleted && _tradeCooldownEnabled && !_triggered)
                StartCooldown();
        }

        /// <summary>
        /// Core round-trip logic, parameterized so BackfillTodayPnL can run it against
        /// scratch/local dictionaries and only commit to instance state on success.
        /// Returns true when this trade completed a round trip (net qty crossed/returned to zero).
        /// Must stay free of side effects beyond the qty/value dictionaries and tradesTaken â€”
        /// it is replayed against historical trades during backfill.
        /// </summary>
        private bool UpdateRoundTripTracker(Trade trade, Dictionary<string, double> rtQty, Dictionary<string, double> rtValue, ref int tradesTaken)
        {
            string symbol = trade.Symbol?.Name ?? trade.Symbol?.Id ?? "UNKNOWN";

            // Mirror Trade Journal's ToFillRecord: side compared as string, not enum
            bool isBuy = trade.Side.ToString().Equals("Buy", StringComparison.OrdinalIgnoreCase);
            double signedQty = isBuy ? trade.Quantity : -trade.Quantity;

            if (!rtQty.ContainsKey(symbol))
            {
                rtQty[symbol] = 0;
                rtValue[symbol] = 0;
            }

            double prevQty = rtQty[symbol];
            double newQty = prevQty + signedQty;

            rtValue[symbol] += isBuy ? -trade.Quantity : trade.Quantity; // FillValue sign: Sell +, Buy -

            // Mirror Trade Journal AggregateDayStats line 1225:
            // round trip completes when net qty crosses or returns to zero
            if ((prevQty > 0 && newQty <= 0) || (prevQty < 0 && newQty >= 0))
            {
                tradesTaken++;
                Core.Instance.Loggers.Log($"[RiskManager] Round trip complete â€” {symbol} | trades taken: {tradesTaken}");

                // Reset; if qty overshot zero the remainder starts a new position
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
            if (totalPnL < _dailyLossLimit)
                _ = FlattenThenLock($"Loss limit hit (total {totalPnL:C}, unrealized {_unrealizedPnL:C})", refreshPnLBeforeLock: true);
            else if (totalPnL > _dailyProfitTarget)
                _ = FlattenThenLock($"Profit target hit (total {totalPnL:C}, unrealized {_unrealizedPnL:C})", refreshPnLBeforeLock: true);
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
            // Always accounted for â€” a fee-per-contract of 0 (the default when the
            // user leaves FeePerMicro/FeePerMini blank) naturally results in no fee.

            // If the broker/feed already populated a real fee, GetTradePnl() has already
            // netted it out of GrossPnl â€” contribute nothing extra here or it double-counts.
            if (trade.Fee != null && trade.Fee.Value != 0.0)
                return 0.0;

            // No broker-supplied fee (e.g. AMP, which only posts fees on the end-of-day
            // statement) â€” fall back to the manual per-contract fee, matched against the
            // user-defined symbol lists.
            string symbol = trade.Symbol?.Name ?? string.Empty;
            double feePerContract;

            if (SymbolMatchesList(symbol, _microSymbols))
                feePerContract = _feePerMicro;
            else if (SymbolMatchesList(symbol, _miniSymbols))
                feePerContract = _feePerMini;
            else
            {
                Core.Instance.Loggers.Log($"[RiskManager] Unrecognized symbol '{symbol}' â€” no fee applied.");
                return 0.0;
            }

            return feePerContract * trade.Quantity;
        }

        /// <summary>
        /// Checks whether a trade symbol starts with any entry in a comma/space-separated
        /// user-defined symbol list (e.g. "MES, MNQ, M2K"). Case-insensitive.
        /// </summary>
        private static bool SymbolMatchesList(string symbol, string symbolList)
        {
            if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(symbolList))
                return false;

            var entries = symbolList.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var entry in entries)
            {
                if (symbol.StartsWith(entry.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
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
            _flattenLockTimer?.Dispose();
            _flattenLockTimer = null;
            _relockTimer?.Dispose();
            _relockTimer = null;
            _startupPollTimer?.Dispose();
            _startupPollTimer = null;
            _cooldownTimer?.Dispose();
            _cooldownTimer = null;
            _lockCountdownTimer?.Dispose();
            _lockCountdownTimer = null;
            _tradeClockTimer?.Dispose();
            _tradeClockTimer = null;

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
                result.Add(new SettingItemBoolean("HideAccountName", _hideAccountName)
                { Text = "Hide Account Name", SortIndex = 1 });
                result.Add(new SettingItemInteger("HideCharCount", _hideCharCount)
                { Text = "Characters to Hide", SortIndex = 2, Minimum = 0, Maximum = 100 });
                result.Add(new SettingItemInteger("FontSize", _fontSize)
                { Text = "Font Size (label pt)", SortIndex = 3, Minimum = 6, Maximum = 16 });
                result.Add(new SettingItemBoolean("AllowPositiveLossLimit", _pendingAllowPositiveLossLimit)
                { Text = "Allow Positive Daily Loss Limit", SortIndex = 3 });
                result.Add(new SettingItemDouble("DailyLossLimit", _pendingDailyLossLimit)
                { Text = "Daily Loss Limit ($)", SortIndex = 4 });
                result.Add(new SettingItemDouble("DailyProfitTarget", _pendingDailyProfitTarget)
                { Text = "Daily Profit Target ($)", SortIndex = 5, Minimum = 0.01 });
                result.Add(new SettingItemString("FlattenLockTime", _pendingFlattenLockTime.HasValue ? _pendingFlattenLockTime.Value.ToString(@"hh\:mm") : "")
                { Text = "Session Lock Time 24hr local (HH:MM, blank = off)", SortIndex = 5 });
                result.Add(new SettingItemString("SessionResetTime", _pendingSessionResetTime.ToString(@"hh\:mm"))
                { Text = "Session Reset Time 24hr local (HH:MM)", SortIndex = 6 });
                result.Add(new SettingItemBoolean("IncludeUnrealized", _pendingIncludeUnrealized)
                { Text = "Include Unrealized P&L in Limits", SortIndex = 7 });
                result.Add(new SettingItemDouble("FeePerMicro", _pendingFeePerMicro)
                { Text = "Fee Per Micro Contract (Round Trip)", SortIndex = 8, Increment = 0.01, DecimalPlaces = 2 });
                result.Add(new SettingItemDouble("FeePerMini", _pendingFeePerMini)
                { Text = "Fee Per Mini Contract (Round Trip)", SortIndex = 9, Increment = 0.01, DecimalPlaces = 2 });
                result.Add(new SettingItemString("MicroSymbols", _pendingMicroSymbols)
                { Text = "Micro Symbols (comma-separated)", SortIndex = 10 });
                result.Add(new SettingItemString("MiniSymbols", _pendingMiniSymbols)
                { Text = "Mini Symbols (comma-separated)", SortIndex = 11 });
                result.Add(new SettingItemInteger("MicroContractLimit", _microContractLimit)
                { Text = "Micro Contract Limit (0 = off)", SortIndex = 12, Minimum = 0, Maximum = 999 });
                result.Add(new SettingItemInteger("MiniContractLimit", _miniContractLimit)
                { Text = "Mini Contract Limit (-1 = off, 0 = no minis)", SortIndex = 13, Minimum = -1, Maximum = 999 });
                result.Add(new SettingItemBoolean("LimitPerSymbol", _limitPerSymbol)
                { Text = "Limit Per Symbol", SortIndex = 14 });
                result.Add(new SettingItemBoolean("MaxTradesEnabled", _pendingMaxTradesEnabled)
                { Text = "Enable Max Trades Lockout", SortIndex = 15 });
                result.Add(new SettingItemInteger("MaxTradesPerSession", _pendingMaxTradesPerSession)
                { Text = "Max Round-Trip Trades Per Session", SortIndex = 16, Minimum = 1, Maximum = 999 });
                result.Add(new SettingItemBoolean("TradeCooldownEnabled", _pendingTradeCooldownEnabled)
                { Text = "Enable Trade Cooldown", SortIndex = 17 });
                result.Add(new SettingItemInteger("TradeCooldownSeconds", _pendingTradeCooldownSeconds)
                { Text = "Cooldown Seconds Per Trade", SortIndex = 18, Minimum = 1, Maximum = 3600 });
                result.Add(new SettingItemAction("SaveSettings", new SettingItemActionDelegate(ApplyPendingSettings), 19)
                { Text = "Save Settings" });
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
                            // Account applies immediately — no staging
                            _account = item.Value as Account;
                            BackfillTodayPnL();
                            RefreshUnrealizedPnL();
                            SubscribePositionUpdates();
                            break;
                        case "DailyLossLimit":
                            {
                                double v = (double)item.Value;
                                // If positive values are not allowed, silently negate.
                                _pendingDailyLossLimit = (!_pendingAllowPositiveLossLimit && v > 0) ? -v : v;
                                break;
                            }
                        case "DailyProfitTarget":
                            // Always positive — negate if user somehow enters negative.
                            _pendingDailyProfitTarget = Math.Abs((double)item.Value);
                            break;
                        case "AllowPositiveLossLimit":
                            _pendingAllowPositiveLossLimit = (bool)item.Value;
                            break;
                        case "FlattenLockTime":
                            var flattenRaw = (string)item.Value;
                            if (string.IsNullOrWhiteSpace(flattenRaw))
                                _pendingFlattenLockTime = null;
                            else if (TimeSpan.TryParse(flattenRaw, out var parsedFlatten))
                                _pendingFlattenLockTime = parsedFlatten;
                            break;
                        case "SessionResetTime":
                            if (TimeSpan.TryParse((string)item.Value, out var parsed))
                                _pendingSessionResetTime = parsed;
                            break;
                        case "IncludeUnrealized":
                            _pendingIncludeUnrealized = (bool)item.Value;
                            break;
                        case "FeePerMicro":
                            _pendingFeePerMicro = (double)item.Value;
                            break;
                        case "FeePerMini":
                            _pendingFeePerMini = (double)item.Value;
                            break;
                        case "MicroSymbols":
                            _pendingMicroSymbols = (string)item.Value;
                            break;
                        case "MiniSymbols":
                            _pendingMiniSymbols = (string)item.Value;
                            break;
                        case "MicroContractLimit":
                            _microContractLimit = Convert.ToInt32(item.Value);
                            CheckContractLimit();
                            _renderer?.RedrawBufferedGraphic();
                            break;
                        case "MiniContractLimit":
                            _miniContractLimit = Convert.ToInt32(item.Value);
                            CheckContractLimit();
                            _renderer?.RedrawBufferedGraphic();
                            break;
                        case "LimitPerSymbol":
                            _limitPerSymbol = (bool)item.Value;
                            CheckContractLimit();
                            _renderer?.RedrawBufferedGraphic();
                            break;
                        case "MaxTradesEnabled":
                            _pendingMaxTradesEnabled = (bool)item.Value;
                            break;
                        case "MaxTradesPerSession":
                            _pendingMaxTradesPerSession = Convert.ToInt32(item.Value);
                            break;
                        case "TradeCooldownEnabled":
                            _pendingTradeCooldownEnabled = (bool)item.Value;
                            break;
                        case "TradeCooldownSeconds":
                            _pendingTradeCooldownSeconds = Convert.ToInt32(item.Value);
                            break;
                        case "HideAccountName":
                            // Applies instantly â€” no staging
                            _hideAccountName = (bool)item.Value;
                            _renderer?.RedrawBufferedGraphic();
                            break;
                        case "HideCharCount":
                            // Applies instantly â€” no staging
                            _hideCharCount = Convert.ToInt32(item.Value);
                            _renderer?.RedrawBufferedGraphic();
                            break;
                        case "FontSize":
                            // Applies instantly â€” no staging
                            _fontSize = Convert.ToInt32(item.Value);
                            _renderer?.RedrawBufferedGraphic();
                            break;
                    }
                }
                _renderer?.RedrawBufferedGraphic();

                // The first time this setter runs is the platform restoring saved state
                // on load â€” commit it straight to live fields so a reboot doesn't leave
                // the monitor running on hardcoded defaults until the user re-saves.
                if (!_initialSettingsApplied)
                {
                    _initialSettingsApplied = true;
                    ApplyPendingSettings();
                }
            }
        }

        /// <summary>
        /// Copies all staged/pending values to the live fields and runs every
        /// side effect that previously ran immediately on each setting change.
        /// Called when the user clicks Save Settings, and also once automatically
        /// on the very first Settings restore (platform startup) â€” see the setter.
        /// </summary>
        private object ApplyPendingSettings(object args = null)
        {
            bool resetTimeChanged = _pendingSessionResetTime != _sessionResetTime;
            bool lockTimeChanged = _pendingFlattenLockTime != _flattenLockTime;
            bool unrealizedChanged = _pendingIncludeUnrealized != _includeUnrealized;
            bool feesChanged = _pendingFeePerMicro != _feePerMicro
                            || _pendingFeePerMini != _feePerMini
                            || _pendingMicroSymbols != _microSymbols
                            || _pendingMiniSymbols != _miniSymbols;

            _allowPositiveLossLimit = _pendingAllowPositiveLossLimit;
            // Enforce sign constraints on commit:
            //   Loss limit  — must be negative unless user explicitly opted in.
            //   Profit target — always positive, never negative.
            _dailyLossLimit = (!_allowPositiveLossLimit && _pendingDailyLossLimit > 0)
                ? -Math.Abs(_pendingDailyLossLimit)
                : _pendingDailyLossLimit;
            _dailyProfitTarget = Math.Abs(_pendingDailyProfitTarget);
            _flattenLockTime = _pendingFlattenLockTime;
            _sessionResetTime = _pendingSessionResetTime;
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

            // If the user disables the feature while a cooldown lock is currently in effect,
            // lift it immediately rather than leaving the account stuck locked with the
            // feature now nominally off.
            if (cooldownDisabledWhileActive)
                EndCooldown();

            if (resetTimeChanged)
            {
                BackfillTodayPnL();
                ScheduleResetTimer();
            }

            if (lockTimeChanged)
                ScheduleFlattenLockTimer();

            if (unrealizedChanged)
            {
                RefreshUnrealizedPnL();
                SubscribePositionUpdates();
            }

            if (feesChanged)
                BackfillTodayPnL();

            CheckLimits();

            Core.Instance.Loggers.Log($"[RiskManager] Settings saved â€” Loss: {_dailyLossLimit:C} | Profit: {_dailyProfitTarget:C} | ResetTime: {_sessionResetTime} | LockTime: {_flattenLockTime?.ToString() ?? "OFF"} | IncludeUnrealized: {_includeUnrealized}");
            SyncPendingFromLive();
            _renderer?.RedrawBufferedGraphic();
            return null;
        }

        /// <summary>
        /// Copies live fields â†’ pending so the settings panel always opens
        /// showing the currently active values. Called on Initialize and after
        /// each successful ApplyPendingSettings.
        /// </summary>
        private void SyncPendingFromLive()
        {
            _pendingDailyLossLimit = _dailyLossLimit;
            _pendingDailyProfitTarget = _dailyProfitTarget;
            _pendingFlattenLockTime = _flattenLockTime;
            _pendingSessionResetTime = _sessionResetTime;
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
            TimeSpan timeUntilReset = _sessionResetTime - DateTime.Now.TimeOfDay;
            if (timeUntilReset < TimeSpan.Zero)
                timeUntilReset = timeUntilReset.Add(TimeSpan.FromDays(1));
            Core.Instance.Loggers.Log($"[RiskManager] Next reset in {timeUntilReset:hh\\:mm\\:ss}");
            _resetTimer = new Timer(OnSessionReset, null,
                (long)timeUntilReset.TotalMilliseconds,
                (long)TimeSpan.FromDays(1).TotalMilliseconds);
        }

        /// <summary>
        /// Schedules (or cancels) the daily flatten & lock timer.
        /// If _flattenLockTime is null the timer is left disposed (feature disabled).
        /// Mirrors ScheduleResetTimer exactly â€” fires once at the designated local time,
        /// then repeats every 24 hours. The unlock path is unchanged (reset time only).
        /// </summary>
        private void ScheduleFlattenLockTimer()
        {
            _flattenLockTimer?.Dispose();
            _flattenLockTimer = null;

            if (!_flattenLockTime.HasValue) return;

            TimeSpan timeUntilFlatten = _flattenLockTime.Value - DateTime.Now.TimeOfDay;
            if (timeUntilFlatten < TimeSpan.Zero)
                timeUntilFlatten = timeUntilFlatten.Add(TimeSpan.FromDays(1));

            Core.Instance.Loggers.Log($"[RiskManager] Next flatten & lock in {timeUntilFlatten:hh\\:mm\\:ss}");
            _flattenLockTimer = new Timer(OnFlattenLockTimerFired, null,
                (long)timeUntilFlatten.TotalMilliseconds,
                (long)TimeSpan.FromDays(1).TotalMilliseconds);
        }

        private void OnFlattenLockTimerFired(object state)
        {
            Core.Instance.Loggers.Log("[RiskManager] Lock Time reached â€” triggering scheduled flatten & lock.");
            _lockedBySchedule = true;
            _ = FlattenThenLock("Scheduled flatten & lock time reached");
        }

        private void OnSessionReset(object state)
        {
            _sessionStartUtc = DateTime.UtcNow;

            Core.Instance.Loggers.Log($"[RiskManager] Session reset at {_sessionStartUtc:u} â€” P&L and trade counter cleared.");
            _dailyPnL = 0.0;
            _unrealizedPnL = 0.0;
            _tradesTaken = 0;
            _rtQty.Clear();
            _rtValue.Clear();
            _triggered = false;
            _backfillComplete = false;
            _countedTradeIds.Clear();

            _relockTimer?.Dispose();
            _relockTimer = null;
            _lockedBySchedule = false;
            _cooldownActive = false;
            _cooldownTimer?.Dispose();
            _cooldownTimer = null;
            _lockCountdownTimer?.Dispose();
            _lockCountdownTimer = null;

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

            BackfillTodayPnL();
            RefreshUnrealizedPnL();
            _renderer?.RedrawBufferedGraphic();
        }

        private void OnTradeAdded(Trade trade)
        {
            if (_triggered) return;
            if (_account == null) return;
            if (!trade.Account.Equals(_account)) return;

            // Ignore trades from backtest sessions. The backtester fires TradeAdded
            // for every simulated fill when it closes. Since backtest fills are
            // historical they always predate the current session start, so the date
            // guard below drops them. The _countedTradeIds set provides a second layer.
            if (trade.DateTime.ToUniversalTime() < _sessionStartUtc) return;

            if (_countedTradeIds.Contains(trade.Id)) return;

            _countedTradeIds.Add(trade.Id);

            // Always update round-trip tracker â€” opening legs have no PnL
            // but must be counted to build up qty so the close can fire
            UpdateRoundTripTracker(trade);

            // Check max trades lockout after each round-trip completes
            if (_maxTradesEnabled && _tradesTaken >= _maxTradesPerSession)
            {
                _ = FlattenThenLock($"Max trades reached ({_tradesTaken}/{_maxTradesPerSession})");
                return;
            }

            double pnl = GetTradePnL(trade);
            if (double.IsNaN(pnl))
            {
                Core.Instance.Loggers.Log($"[RiskManager] Trade {trade.Id} â€” no PnL fields populated, skipping P&L update.");
                _renderer?.RedrawBufferedGraphic();
                return;
            }

            _dailyPnL += pnl - GetTradeFee(trade);

            // Zero unrealized for the limit check below. Do NOT call RefreshUnrealizedPnL()
            // here: when a closing fill arrives, the position object may still exist in
            // Core.Instance.Positions for a brief moment before Quantower removes it. Reading
            // it now would add the stale unrealized loss back on top of the just-realized PnL
            // and produce a false double-count (e.g. -$60 realized + -$60 still-open position =
            // -$120 total, tripping a -$150 limit at -$110 actual). Leave it zeroed — the
            // Position.Updated and OnPositionRemoved events will restore the correct unrealized
            // value once the position is fully settled.
            _unrealizedPnL = 0.0;

            _renderer?.RedrawBufferedGraphic();

            Core.Instance.Loggers.Log($"[RiskManager] Trade: {pnl:C} | Realized PnL: {_dailyPnL:C} | Total: {TotalPnL:C} | Trades taken: {_tradesTaken}");

            double total = TotalPnL;
            // Only evaluate limits once backfill is complete. OnTradeAdded can fire
            // during platform restore before BackfillTodayPnL() has finished, which
            // would see a stale (low/zero) PnL and false-lock. Backfill calls
            // CheckLimits() itself when done, so nothing is skipped.
            if (_backfillComplete)
            {
                if (total < _dailyLossLimit)
                    _ = FlattenThenLock($"Loss limit hit ({total:C})", refreshPnLBeforeLock: true);
                else if (total > _dailyProfitTarget)
                    _ = FlattenThenLock($"Profit target hit ({total:C})", refreshPnLBeforeLock: true);
                else
                    CheckContractLimit();
            }
        }

        private async Task FlattenThenLock(string reason, bool refreshPnLBeforeLock = false)
        {
            if (_triggered) return;

            // Any real lock reason overrules/cancels an in-progress Trade Cooldown â€”
            // the real lock takes over completely and the cooldown display disappears.
            if (_cooldownActive)
                EndCooldown(relockAfter: false);

            _triggered = true;
            _renderer?.RedrawBufferedGraphic();

            Core.Instance.Loggers.Log($"[RiskManager] TRIGGERED â€” {reason}. Flattening {_account.Name}...");
            Core.Instance.AdvancedTradingOperations.Flatten(_account);

            // Poll until flat: 200 ms intervals, max 5 attempts (1 second ceiling).
            // Delay-first so the flatten order has time to reach the broker before
            // we check positions/orders on the first pass.
            int attempts = 0;
            while (attempts < 5)
            {
                await Task.Delay(200);
                bool hasPositions = Core.Instance.Positions.Any(p => p.Account.Equals(_account));
                bool hasOrders = Core.Instance.Orders.Any(o => o.Account.Equals(_account));
                if (!hasPositions && !hasOrders) break;
                attempts++;
            }

            // For P&L-triggered locks: backfill now so the closing fill from the
            // flatten is captured in _dailyPnL before we lock and redraw.
            // Scheduled-lock and max-trades paths skip this (refreshPnLBeforeLock = false).
            if (refreshPnLBeforeLock)
            {
                BackfillTodayPnL();
                Core.Instance.Loggers.Log($"[RiskManager] Post-flatten PnL refresh â€” realized: {_dailyPnL:C}");
                _renderer?.RedrawBufferedGraphic();
            }

            Core.Instance.LockAccount(_account);
            _renderer?.RedrawBufferedGraphic();

            Core.Instance.Loggers.Log($"[RiskManager] Account {_account.Name} locked. Reason: {reason}");
            Core.Instance.Alert($"Risk Manager: {reason}. Account locked.");

            StartRelockMonitor();
            StartLockCountdownTimer();
        }

        /// <summary>
        /// Checks current TotalPnL against both limits immediately.
        /// Called after settings change so a threshold lowered below the current P&L fires right away.
        /// </summary>
        private void CheckLimits()
        {
            if (_triggered) return;
            // Do not evaluate limits before backfill has completed: trade data isn't
            // live yet, so PnL = 0 against a loss limit of 0 would false-lock.
            // The one exception is when CheckLimits is called from inside
            // BackfillTodayPnL itself (after _backfillComplete is set true) — that
            // call always has real data and must be allowed through.
            if (!_backfillComplete) return;
            double total = TotalPnL;
            if (total < _dailyLossLimit)
            {
                _ = FlattenThenLock($"Loss limit hit ({total:C})", refreshPnLBeforeLock: true);
                return;
            }
            if (total > _dailyProfitTarget)
            {
                _ = FlattenThenLock($"Profit target hit ({total:C})", refreshPnLBeforeLock: true);
                return;
            }
            if (_maxTradesEnabled && _tradesTaken >= _maxTradesPerSession)
                _ = FlattenThenLock($"Max trades reached ({_tradesTaken}/{_maxTradesPerSession})");
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
                        Core.Instance.Loggers.Log($"[RiskManager] Account was manually unlocked â€” relocking.");
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

        /// <summary>
        /// Ticks every second while _triggered is true so the LockRemaining countdown
        /// in the renderer updates consistently. On the last minute it counts by seconds;
        /// before that it redraws once per minute (every 60 ticks) to avoid redundant work.
        /// Stopped automatically when the session resets or the plugin is disposed.
        /// </summary>
        /// <summary>
        /// Ticks every second so the live "Trade Clock" row keeps counting up while a
        /// position is open. Only forces a redraw when something's actually open;
        /// when flat, the row is static (showing the reset time) and doesn't need it.
        /// </summary>
        private void StartTradeClockTimer()
        {
            _tradeClockTimer?.Dispose();
            _tradeClockTimer = new Timer(_ =>
            {
                try
                {
                    if (HasOpenPositions)
                    {
                        // If we're already in a trade when the plugin loads (e.g. after a
                        // restart mid-trade), seed the clock from the earliest open position's
                        // OpenTime so the elapsed display is accurate, not reset to 0.
                        if (_tradeClockStartLocal == null)
                        {
                            var earliest = Core.Instance.Positions
                                .Where(p => _account != null && p.Account.Equals(_account) && p.OpenTime != default)
                                .OrderBy(p => p.OpenTime)
                                .FirstOrDefault();

                            if (earliest != null)
                            {
                                var openTime = earliest.OpenTime;
                                _tradeClockStartLocal = (openTime.Kind != DateTimeKind.Unspecified)
                                    ? openTime.ToLocalTime()
                                    : openTime; // assume local if unspecified
                            }
                            else
                            {
                                _tradeClockStartLocal = DateTime.Now;
                            }
                        }
                        _renderer?.RedrawBufferedGraphic();
                    }
                    else
                    {
                        // Flat again — reset so the next trade starts back at 0.
                        _tradeClockStartLocal = null;
                    }
                }
                catch (Exception ex)
                {
                    Core.Instance.Loggers.Log($"[RiskManager] Trade clock timer error: {ex.Message}");
                }
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        private void StartLockCountdownTimer()
        {
            _lockCountdownTimer?.Dispose();
            _lockCountdownTimer = new Timer(_ =>
            {
                try
                {
                    if (!_triggered)
                    {
                        _lockCountdownTimer?.Dispose();
                        _lockCountdownTimer = null;
                        return;
                    }

                    var remaining = LockRemaining;

                    // Always redraw on the last minute (counts by seconds).
                    // Before that, only redraw once per minute to keep it light.
                    if (remaining.TotalMinutes < 1.0 || (int)remaining.TotalSeconds % 60 == 0)
                        _renderer?.RedrawBufferedGraphic();
                }
                catch (Exception ex)
                {
                    Core.Instance.Loggers.Log($"[RiskManager] Lock countdown timer error: {ex.Message}");
                }
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// Locks the account for a fixed cooldown period following a fully closed trade.
        /// Does not set _triggered â€” this is a separate, lighter lockout that a real
        /// loss/profit/schedule/max-trades lock always overrules (see FlattenThenLock).
        /// </summary>
        private void StartCooldown()
        {
            _cooldownEndUtc = DateTime.UtcNow.AddSeconds(_tradeCooldownSeconds);
            _cooldownActive = true;

            try
            {
                if (_account != null && !_account.IsLocked())
                    Core.Instance.LockAccount(_account);
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[RiskManager] Cooldown lock error: {ex.Message}");
            }

            Core.Instance.Loggers.Log($"[RiskManager] Trade Cooldown started â€” {_tradeCooldownSeconds}s, ends {_cooldownEndUtc:u}");
            _renderer?.RedrawBufferedGraphic();

            _cooldownTimer?.Dispose();
            _cooldownTimer = new Timer(_ =>
            {
                try
                {
                    if (!_cooldownActive) return;

                    // If something else locked the account for a real reason mid-cooldown,
                    // that already called EndCooldown(relockAfter: false) â€” nothing to do here.

                    if (DateTime.UtcNow >= _cooldownEndUtc)
                    {
                        EndCooldown(relockAfter: false);
                        return;
                    }

                    // Relock if manually unlocked while cooldown is still counting down.
                    if (_account != null && !_account.IsLocked())
                        Core.Instance.LockAccount(_account);

                    _renderer?.RedrawBufferedGraphic();
                }
                catch (Exception ex)
                {
                    Core.Instance.Loggers.Log($"[RiskManager] Cooldown timer error: {ex.Message}");
                }
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// Ends the cooldown lock: stops the timer and unlocks the account (unless a real
        /// lock is now in effect, in which case the account should stay locked for that reason).
        /// </summary>
        private void EndCooldown(bool relockAfter = false)
        {
            _cooldownActive = false;
            _cooldownTimer?.Dispose();
            _cooldownTimer = null;

            try
            {
                if (_account != null && _account.IsLocked() && !_triggered)
                {
                    Core.Instance.UnLockAccount(_account);
                    Core.Instance.Loggers.Log($"[RiskManager] Trade Cooldown ended â€” account {_account.Name} unlocked.");
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[RiskManager] Cooldown unlock error: {ex.Message}");
            }

            _renderer?.RedrawBufferedGraphic();
        }

        /// <summary>
        /// Checks open contracts against micro and mini limits independently.
        /// If _limitPerSymbol is true, each symbol is checked separately against the limit.
        /// If false, all micro positions are pooled together and all mini positions pooled together.
        /// Excess contracts are closed from the most recently opened position first.
        /// No lock or flatten â€” position up to the limit stays open.
        /// </summary>
        private void CheckContractLimit()
        {
            if (_account == null) return;
            if (_microContractLimit <= 0 && _miniContractLimit < 0) return;

            try
            {
                var positions = Core.Instance.Positions
                    .Where(p => p.Account.Equals(_account))
                    .ToList();

                if (_limitPerSymbol)
                {
                    var bySymbol = positions.GroupBy(p => p.Symbol?.Name ?? string.Empty);
                    foreach (var group in bySymbol)
                    {
                        string sym = group.Key;
                        int limit;
                        if (SymbolMatchesList(sym, _microSymbols))
                        {
                            if (_microContractLimit <= 0) continue;
                            limit = _microContractLimit;
                        }
                        else if (SymbolMatchesList(sym, _miniSymbols))
                        {
                            if (_miniContractLimit < 0) continue;
                            limit = _miniContractLimit;
                        }
                        else continue;

                        double total = group.Sum(p => p.Quantity);
                        if (total <= limit) continue;

                        double excess = total - limit;
                        Core.Instance.Loggers.Log($"[RiskManager] Contract limit ({limit}) exceeded for {sym} â€” total: {total}, excess: {excess}");
                        CloseExcess(group.OrderByDescending(p => p.OpenTime).ToList(), excess);
                    }
                }
                else
                {
                    if (_microContractLimit > 0)
                    {
                        var micros = positions
                            .Where(p => SymbolMatchesList(p.Symbol?.Name ?? string.Empty, _microSymbols))
                            .ToList();
                        double total = micros.Sum(p => p.Quantity);
                        if (total > _microContractLimit)
                        {
                            double excess = total - _microContractLimit;
                            Core.Instance.Loggers.Log($"[RiskManager] Micro contract limit ({_microContractLimit}) exceeded â€” total: {total}, excess: {excess}");
                            CloseExcess(micros.OrderByDescending(p => p.OpenTime).ToList(), excess);
                        }
                    }

                    if (_miniContractLimit >= 0)
                    {
                        var minis = positions
                            .Where(p => SymbolMatchesList(p.Symbol?.Name ?? string.Empty, _miniSymbols))
                            .ToList();
                        double total = minis.Sum(p => p.Quantity);
                        if (total > _miniContractLimit)
                        {
                            double excess = total - _miniContractLimit;
                            Core.Instance.Loggers.Log($"[RiskManager] Mini contract limit ({_miniContractLimit}) exceeded â€” total: {total}, excess: {excess}");
                            CloseExcess(minis.OrderByDescending(p => p.OpenTime).ToList(), excess);
                        }
                    }
                }

                _renderer?.RedrawBufferedGraphic();
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[RiskManager] Contract limit check error: {ex.Message}");
            }
        }

        /// <summary>
        /// Closes exactly 'excess' contracts from the provided list of positions,
        /// working from the first entry (most recent) forward until excess is consumed.
        /// </summary>
        private void CloseExcess(List<Position> positions, double excess)
        {
            foreach (var pos in positions)
            {
                if (excess <= 0) break;

                double closeQty = Math.Min(pos.Quantity, excess);
                Side closeSide = pos.Side == Side.Buy ? Side.Sell : Side.Buy;

                Core.Instance.Loggers.Log(
                    $"[RiskManager] Closing excess {closeQty} of {pos.Quantity} contract(s) " +
                    $"on {pos.Symbol?.Name} via {closeSide} market order (position opened {pos.OpenTime:HH:mm:ss})");

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
                        Core.Instance.Loggers.Log(
                            $"[RiskManager] Excess close failed for {pos.Symbol?.Name}: {result.Message}");
                }
                catch (Exception ex)
                {
                    Core.Instance.Loggers.Log($"[RiskManager] Excess close error: {ex.Message}");
                }

                excess -= closeQty;
            }
        }

        // Expose state to renderer
        public Account MonitoredAccount => _account;
        public int TradesTaken => _tradesTaken;
        public bool MaxTradesEnabled => _maxTradesEnabled;
        public int MaxTradesPerSession => _maxTradesPerSession;
        public bool CooldownActive => _cooldownActive;
        /// <summary>Time remaining in the current Trade Cooldown lock, or TimeSpan.Zero if not active.</summary>
        public TimeSpan CooldownRemaining
        {
            get
            {
                if (!_cooldownActive) return TimeSpan.Zero;
                var remaining = _cooldownEndUtc - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
        /// <summary>
        /// Time remaining until Session Reset Time â€” the moment that lifts any lock
        /// triggered by loss limit, profit target, schedule, or max trades.
        /// </summary>
        public TimeSpan LockRemaining
        {
            get
            {
                var nowLocal = DateTime.Now;
                var todayReset = nowLocal.Date + _sessionResetTime;
                var nextReset = nowLocal < todayReset ? todayReset : todayReset.AddDays(1);
                var remaining = nextReset - nowLocal;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
        public bool HideAccountName => _hideAccountName;
        public int HideCharCount => _hideCharCount;
        public int FontSize => _fontSize;
        public double DailyPnL => _dailyPnL;
        public double UnrealizedPnL => _unrealizedPnL;
        public double DailyLossLimit => _dailyLossLimit;
        public double DailyProfitTarget => _dailyProfitTarget;
        public TimeSpan? FlattenLockTime => _flattenLockTime;
        public TimeSpan SessionResetTime => _sessionResetTime;
        public bool IsTriggered => _triggered;
        public bool IncludeUnrealized => _includeUnrealized;
        public int MicroContractLimit => _microContractLimit;
        public int MiniContractLimit => _miniContractLimit;
        /// <summary>Open contracts in micro symbols only.</summary>
        public int OpenMicroContracts => (_account == null) ? 0 :
            (int)Core.Instance.Positions
                .Where(p => p.Account.Equals(_account) && SymbolMatchesList(p.Symbol?.Name ?? string.Empty, _microSymbols))
                .Sum(p => p.Quantity);
        /// <summary>Open contracts in mini symbols only.</summary>
        public int OpenMiniContracts => (_account == null) ? 0 :
            (int)Core.Instance.Positions
                .Where(p => p.Account.Equals(_account) && SymbolMatchesList(p.Symbol?.Name ?? string.Empty, _miniSymbols))
                .Sum(p => p.Quantity);
        /// <summary>True when the account has any open position (any symbol).</summary>
        public bool HasOpenPositions => _account != null &&
            Core.Instance.Positions.Any(p => p.Account.Equals(_account));
        public bool TradeCooldownEnabled => _tradeCooldownEnabled;
        public int TradeCooldownSeconds => _tradeCooldownSeconds;

        /// <summary>
        /// Elapsed time since a position was first detected open on the monitored account —
        /// a plain stopwatch we start/reset ourselves (see StartTradeClockTimer), not a read
        /// of any position's OpenTime field. Starts at 0 and counts up second by second while
        /// any position is open; null when flat.
        /// </summary>
        public TimeSpan? TradeClockElapsed
        {
            get
            {
                if (!HasOpenPositions || !_tradeClockStartLocal.HasValue) return null;
                var elapsed = DateTime.Now - _tradeClockStartLocal.Value;
                return elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;
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

        public void RedrawBufferedGraphic()
        {
            bufferedGraphic.IsDirty = true;
        }

        /// <summary>Formats a TimeSpan as m:ss, switching to h:mm:ss once it reaches an hour.</summary>
        private static string FormatTradeClock(TimeSpan ts)
        {
            if (ts.TotalHours >= 1.0)
                return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            return $"{ts.Minutes}:{ts.Seconds:D2}";
        }

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

            // Column layout â€” left half and right half split at panel midpoint
            int mid = Bounds.Width / 2;
            int lx = 10;        // left label x
            int lv = lx + 88;   // left value x
            int rx = mid + 2;   // right label x
            int rv = rx + 78;   // right value x

            gr.DrawLine(divPen, lx, y, Bounds.Width - lx, y);
            y += 8;

            // --- Row 1: Account | Balance ---
            gr.DrawString("Account", labelFont, grayBrush, lx, y);
            string accountName = _plugin.MonitoredAccount?.Name ?? "None";
            string displayName;
            if (_plugin.HideAccountName && accountName != "None")
            {
                int charsToHide = Math.Min(_plugin.HideCharCount, accountName.Length);
                displayName = new string('*', charsToHide) + accountName.Substring(charsToHide);
            }
            else
            {
                displayName = accountName;
            }
            gr.DrawString(displayName, valueFont, whiteBrush, lv, y);

            gr.DrawString("Trades Taken", labelFont, grayBrush, rx, y);
            string tradesTakenStr = _plugin.MaxTradesEnabled
                ? $"{_plugin.TradesTaken} / {_plugin.MaxTradesPerSession}"
                : _plugin.TradesTaken.ToString();
            // Color yellow when approaching limit (â‰¥80%), red when at/over limit
            Brush tradesTakenBrush = whiteBrush;
            if (_plugin.MaxTradesEnabled)
            {
                if (_plugin.TradesTaken >= _plugin.MaxTradesPerSession)
                    tradesTakenBrush = redBrush;
                else if (_plugin.TradesTaken >= (int)(_plugin.MaxTradesPerSession * 0.8))
                    tradesTakenBrush = yellowBrush;
            }
            gr.DrawString(tradesTakenStr, valueFont, tradesTakenBrush, rv, y);
            y += lineH;

            // --- Row 2: Loss Limit | Contract Limit ---
            gr.DrawString("Loss Limit", labelFont, grayBrush, lx, y);
            gr.DrawString($"{_plugin.DailyLossLimit:C}", valueFont, redBrush, lv, y);

            gr.DrawString("Contract Limit", labelFont, grayBrush, rx, y);
            bool miniActive = _plugin.MiniContractLimit >= 0;
            bool microActive = _plugin.MicroContractLimit > 0;
            if (miniActive || microActive)
            {
                string miniLimitStr = miniActive ? $"{_plugin.MiniContractLimit} Mini" : "-- Mini";
                string microLimitStr = microActive ? $"{_plugin.MicroContractLimit} Micro" : "-- Micro";
                gr.DrawString($"{miniLimitStr}  {microLimitStr}", valueFont, whiteBrush, rv, y);
            }
            else
                gr.DrawString("OFF", valueFont, grayBrush, rv, y);
            y += lineH;

            // --- Row 3: Profit Target | Open Contracts ---
            gr.DrawString("Profit Target", labelFont, grayBrush, lx, y);
            gr.DrawString($"{_plugin.DailyProfitTarget:C}", valueFont, greenBrush, lv, y);

            // Open Contracts value starts closer to label so Mini+Micro counts have room.
            int rvOC = rx + 76;

            if (_plugin.CooldownActive)
            {
                // Trade Cooldown lockout: live seconds countdown, yellow.
                gr.DrawString("Cooldown", labelFont, grayBrush, rx, y);
                var cd = _plugin.CooldownRemaining;
                int cdTotalSeconds = (int)Math.Ceiling(cd.TotalSeconds);
                string cooldownStr = $"{cdTotalSeconds / 60}:{cdTotalSeconds % 60:D2}";
                gr.DrawString(cooldownStr, valueFont, yellowBrush, rv, y);
            }
            else if (_plugin.IsTriggered)
            {
                // Locked for any other reason (loss limit, profit target, schedule, max trades):
                // countdown to Session Reset Time.
                // >= 1 hour   -> H:MM  (whole minutes)
                // >= 1 minute -> M     (whole minutes)
                // < 1 minute  -> 0:SS  (seconds precision on last minute)
                gr.DrawString("Cooldown", labelFont, grayBrush, rx, y);
                var lr = _plugin.LockRemaining;
                string lockStr;
                if (lr.TotalMinutes >= 60)
                {
                    int lrTotalMinutes = (int)Math.Ceiling(lr.TotalMinutes);
                    lockStr = $"{lrTotalMinutes / 60}:{lrTotalMinutes % 60:D2}";
                }
                else if (lr.TotalMinutes >= 1.0)
                {
                    lockStr = $"{(int)Math.Ceiling(lr.TotalMinutes)}";
                }
                else
                {
                    int totalSecs = (int)Math.Ceiling(lr.TotalSeconds);
                    lockStr = $"0:{totalSecs:D2}";
                }
                gr.DrawString(lockStr, valueFont, whiteBrush, rv, y);
            }
            else if (_plugin.TradeCooldownEnabled && _plugin.HasOpenPositions)
            {
                // Trade is open and cooldown is enabled: switch label to Open Contracts.
                gr.DrawString("Open Contracts", labelFont, grayBrush, rx, y);
                int openMini = _plugin.OpenMiniContracts;
                int openMicro = _plugin.OpenMicroContracts;
                bool miniOver = miniActive && openMini > _plugin.MiniContractLimit;
                bool microOver = microActive && openMicro > _plugin.MicroContractLimit;
                string openMiniStr = $"{openMini} Mini  ";
                string openMicroStr = $"{openMicro} Micro";
                float miniW = gr.MeasureString(openMiniStr, valueFont).Width;
                gr.DrawString(openMiniStr, valueFont, miniOver ? redBrush : whiteBrush, rvOC, y);
                gr.DrawString(openMicroStr, valueFont, microOver ? redBrush : whiteBrush, rvOC + miniW, y);
            }
            else if (_plugin.TradeCooldownEnabled)
            {
                // Cooldown enabled, no open trade, no active cooldown lock:
                // display the configured cooldown duration so the user knows what to expect.
                gr.DrawString("Cooldown", labelFont, grayBrush, rx, y);
                gr.DrawString($"{_plugin.TradeCooldownSeconds} seconds", valueFont, grayBrush, rv, y);
            }
            else
            {
                gr.DrawString("Open Contracts", labelFont, grayBrush, rx, y);
                int openMini = _plugin.OpenMiniContracts;
                int openMicro = _plugin.OpenMicroContracts;
                bool miniOver = miniActive && openMini > _plugin.MiniContractLimit;
                bool microOver = microActive && openMicro > _plugin.MicroContractLimit;
                string openMiniStr = $"{openMini} Mini  ";
                string openMicroStr = $"{openMicro} Micro";
                float miniW = gr.MeasureString(openMiniStr, valueFont).Width;
                gr.DrawString(openMiniStr, valueFont, miniOver ? redBrush : whiteBrush, rvOC, y);
                gr.DrawString(openMicroStr, valueFont, microOver ? redBrush : whiteBrush, rvOC + miniW, y);
            }
            y += lineH;

            // --- Row 4: Realized P&L | Session Lock Time ---
            gr.DrawString("Realized P&L", labelFont, grayBrush, lx, y);
            var realizedBrush = _plugin.DailyPnL >= 0 ? greenBrush : redBrush;
            gr.DrawString($"{_plugin.DailyPnL:C}", valueFont, realizedBrush, lv, y);

            gr.DrawString("Lock Time", labelFont, grayBrush, rx, y);
            if (_plugin.FlattenLockTime.HasValue)
                gr.DrawString(DateTime.Today.Add(_plugin.FlattenLockTime.Value).ToString("h:mm tt"), valueFont, whiteBrush, rv, y);
            else
                gr.DrawString("OFF", valueFont, grayBrush, rv, y);
            y += lineH;

            // --- Row 5: Unrealized P&L | Reset Time / Trade Clock ---
            gr.DrawString("Unrealized P&L", labelFont, grayBrush, lx, y);
            if (_plugin.IncludeUnrealized)
            {
                var unrealizedBrush = _plugin.UnrealizedPnL >= 0 ? greenBrush : redBrush;
                gr.DrawString($"{_plugin.UnrealizedPnL:C}", valueFont, unrealizedBrush, lv, y);
            }
            else
            {
                gr.DrawString("OFF", valueFont, grayBrush, lv, y);
            }

            var tradeClockElapsed = _plugin.TradeClockElapsed;
            if (tradeClockElapsed.HasValue)
            {
                // In a trade — show a live-ticking clock timed off the earliest open position.
                gr.DrawString("Trade Clock", labelFont, grayBrush, rx, y);
                gr.DrawString(FormatTradeClock(tradeClockElapsed.Value), valueFont, whiteBrush, rv, y);
            }
            else
            {
                // Flat — show the fixed daily reset time.
                gr.DrawString("Reset Time", labelFont, grayBrush, rx, y);
                gr.DrawString(DateTime.Today.Add(_plugin.SessionResetTime).ToString("h:mm tt"), valueFont, whiteBrush, rv, y);
            }
            y += lineH;

            // --- Divider ---
            gr.DrawLine(divPen, lx, y, Bounds.Width - lx, y);
            y += 6;

            // --- Row 6: Total P&L | Status ---
            gr.DrawString("Total P&L", labelFont, grayBrush, lx, y);
            double total = _plugin.TotalPnL;
            var totalBrush = total >= 0 ? greenBrush : redBrush;
            gr.DrawString($"{total:C}", valueFont, totalBrush, lv, y);

            gr.DrawString("Status", labelFont, grayBrush, rx, y);
            var statusBrush = _plugin.IsTriggered ? redBrush : greenBrush;
            gr.DrawString(_plugin.IsTriggered ? "LOCKED" : "ACTIVE", valueFont, statusBrush, rv, y);
            y += lineH;

            // --- P&L Progress Bar ---
            // Range: DailyLossLimit (left) â†’ DailyProfitTarget (right), 0 as color pivot.
            {
                double lossLimit = _plugin.DailyLossLimit;
                double profitTarget = _plugin.DailyProfitTarget;
                double pnl = _plugin.TotalPnL;
                double range = profitTarget - lossLimit;

                int barX = lx;
                int barW = Bounds.Width - lx * 2;
                int barH = 10;

                double clampedPnl = Math.Max(lossLimit, Math.Min(profitTarget, pnl));
                double zeroFrac = (0.0 - lossLimit) / range;
                double pnlFrac = (clampedPnl - lossLimit) / range;
                int zeroX = barX + (int)(zeroFrac * barW);
                int pnlX = barX + (int)(pnlFrac * barW);

                var trackBrush = new SolidBrush(Color.FromArgb(60, 60, 60));
                gr.FillRectangle(trackBrush, barX, y, barW, barH);
                trackBrush.Dispose();

                if (pnl < 0)
                {
                    int fillLeft = Math.Min(pnlX, zeroX);
                    int fillRight = Math.Max(pnlX, zeroX);
                    if (fillRight > fillLeft)
                        gr.FillRectangle(redBrush, fillLeft, y, fillRight - fillLeft, barH);
                }
                else if (pnl > 0)
                {
                    int fillLeft = Math.Min(pnlX, zeroX);
                    int fillRight = Math.Max(pnlX, zeroX);
                    if (fillRight > fillLeft)
                        gr.FillRectangle(greenBrush, fillLeft, y, fillRight - fillLeft, barH);
                }

                // Zero tick
                gr.FillRectangle(new SolidBrush(Color.FromArgb(180, 180, 180)), zeroX - 1, y - 2, 2, barH + 4);

                // Indicator triangle
                var indicatorBrush = pnl >= 0 ? greenBrush : redBrush;
                gr.FillPolygon(indicatorBrush, new[] {
                    new Point(pnlX,     y - 1),
                    new Point(pnlX - 5, y - 7),
                    new Point(pnlX + 5, y - 7)
                });

                // Threshold labels below bar
                int labelY = y + barH + 6;
                var lossLabelStr = $"{lossLimit:C}";
                var profitLabelStr = $"{profitTarget:C}";
                var tinyFont = new Font("Arial", 9, FontStyle.Regular);
                float profitLabelW = gr.MeasureString(profitLabelStr, tinyFont).Width;
                gr.DrawString(lossLabelStr, tinyFont, redBrush, barX, labelY);
                gr.DrawString(profitLabelStr, tinyFont, greenBrush, barX + barW - profitLabelW, labelY);
                tinyFont.Dispose();
            }

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