using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Timers;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Native;
using TradingPlatform.PresentationLayer.Plugins;
using TradingPlatform.PresentationLayer.Renderers;

namespace TradeJournal
{
    // Lightweight container for per-day trade statistics
    public struct DayStats
    {
        public double PnL;        // net P&L for the day
        public int RoundTrips; // number of closed trades
        public bool HasData;    // true when at least one closed trade exists
    }

    // Per-side (long/short) breakdown of round trips for a single selected day
    public struct SideMetrics
    {
        public int RoundTrips;
        public int Wins;
        public double TotalPnl;
        public double LargestWin;
        public double LargestLoss; // stored as a negative number (or 0 if no losers)
        public bool HasData;

        public double TotalDurationSeconds;
        public int DurationSampleCount;
        public double TotalWinDurationSeconds;
        public int WinDurationCount;
        public double TotalLossDurationSeconds;
        public int LossDurationCount;

        // Running totals for winners/losers separately, so we can report Avg Win and Avg Loss
        public double TotalWinPnl;
        public int WinCount;
        public double TotalLossPnl; // sum of negative pnls
        public int LossCount;

        // Longest streak of consecutive winning / losing round trips, in chronological order
        public int WinStreak;
        public int LossStreak;

        public double WinRate => RoundTrips > 0 ? (double)Wins / RoundTrips * 100.0 : 0.0;
        public double AvgPnl => RoundTrips > 0 ? TotalPnl / RoundTrips : 0.0;
        public double AvgWin => WinCount > 0 ? TotalWinPnl / WinCount : 0.0;
        public double AvgLoss => LossCount > 0 ? TotalLossPnl / LossCount : 0.0;
        public double AvgDurationSeconds => DurationSampleCount > 0 ? TotalDurationSeconds / DurationSampleCount : 0.0;
        public double AvgWinDurationSeconds => WinDurationCount > 0 ? TotalWinDurationSeconds / WinDurationCount : 0.0;
        public double AvgLossDurationSeconds => LossDurationCount > 0 ? TotalLossDurationSeconds / LossDurationCount : 0.0;
    }

    // Win/loss/breakeven counts for the day's pie chart. Breakeven uses a +/- $2 band;
    // this is intentionally separate from SideMetrics.Wins (which has no band).
    public struct PieBuckets
    {
        public int Wins;
        public int Losses;
        public int Breakevens;
        public int Total => Wins + Losses + Breakevens;
    }

    public struct DayMetrics
    {
        public SideMetrics Long;
        public SideMetrics Short;
        public PieBuckets Pie;
        public bool HasData;
    }

    // Aggregated metrics for an entire month (all trading days combined)
    public struct MonthMetrics
    {
        public SideMetrics Long;
        public SideMetrics Short;
        public PieBuckets Pie;
        public double TotalPnL;
        public bool HasData;
    }

    // Common shape for a single fill, regardless of whether it came from the live
    // platform (Core.Instance.GetTrades) or a manually-exported archive CSV. All FIFO
    // round-trip/fee logic operates on this instead of the platform's Trade type, so
    // archived days and live days always compute identically.
    public struct FillRecord
    {
        public DateTime DateTime;  // local time
        public string Symbol;      // root/contract ticker, e.g. "MNQ" or "MNQU26"
        public double SignedQty;   // positive = buy, negative = sell
        public double FillValue;   // matches GetFillValue's sign convention (Sell +, Buy -)
    }

    public class TradeJournalPlugin : Plugin
    {
        // Notes and the manually-exported trade archive both live outside Quantower's
        // own AppData tree (which Quantower itself can wipe on update/reset) — under
        // a user-owned Documents folder instead, so this data is never at the mercy
        // of another app's housekeeping.
        private static readonly string RootFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Quantower", "Trade Journal");
        private static readonly string JournalFolder = Path.Combine(RootFolder, "Notes");
        private static readonly string ArchiveFolder = Path.Combine(RootFolder, "TradeArchive");

        private string _selectedDate = DateTime.Today.ToString("yyyy-MM-dd");
        private int _currentMonth = DateTime.Today.Month - 1;
        private int _currentYear = DateTime.Today.Year;
        private System.Timers.Timer _saveDebounce;
        private TradeJournalCalendarRenderer _calRenderer;
        private bool _browserReady = false;
        private readonly HashSet<string> _loadedDates = new HashSet<string>();

        // --- Settings ---
        private Account _account;       // null = all accounts
        private int _cellW = 100;       // default cell width
        private int _cellH = 74;        // default cell height
        private int _calColumnWidth = 550; // raw pixel width of the calendar column
        private const int CalWidthMin = 300;
        private const int CalWidthMax = 1200;
        private bool _calculateFees = false;
        private double _feePerMicro = 0.0;
        private double _feePerMini = 0.0;

        // --- Trade archive (manually-exported CSVs) ---
        // For any day covered by the archive, it's trusted completely and the platform
        // is never queried for that day. Days not present in the archive fall back to
        // the platform, exactly as before. Reloaded automatically whenever a file in
        // the archive folder changes (new export dropped in, etc).
        private List<FillRecord> _archiveFillsCache = new List<FillRecord>();
        private HashSet<string> _archiveCoveredDays = new HashSet<string>();
        private bool _archiveLoadedOnce = false;
        private DateTime _archiveScanStamp = DateTime.MinValue;

        // Cache so we don't re-query on every redraw. Keyed by (year, month) so the
        // dimmed leading/trailing days from adjacent months can show real stats too,
        // without invalidating the currently-displayed month's cache entry.
        private Dictionary<(int Year, int Month), Dictionary<string, DayStats>> _monthStatsCache
            = new Dictionary<(int Year, int Month), Dictionary<string, DayStats>>();

        public static PluginInfo GetInfo()
        {
            var windowParameters = new NativeWindowParameters(NativeWindowParameters.Panel)
            {
                BrowserUsageType = BrowserUsageType.Default
            };

            return new PluginInfo
            {
                Name = "TradeJournal",
                Title = "Trade Journal",
                Group = PluginGroup.Misc,
                ShortName = "TJ",
                SortIndex = 11,
                AllowSettings = true,
                TemplateName = "layout.html",
                WindowParameters = windowParameters,
                CustomProperties = new Dictionary<string, object>
                {
                    { PluginInfo.Const.ALLOW_MANUAL_CREATION, true }
                }
            };
        }

        public override Size DefaultSize => new Size(800, 500);

        public override void Initialize()
        {
            base.Initialize();
            Directory.CreateDirectory(JournalFolder);
            Directory.CreateDirectory(ArchiveFolder);

            _saveDebounce = new System.Timers.Timer(1000);
            _saveDebounce.AutoReset = false;
            _saveDebounce.Elapsed += OnSaveDebounceElapsed;

            ApplyGridLayout();

            var calControl = this.Window.CreateRenderingControl("TradeJournalCalendar");
            calControl.Layout.Column = 0;
            _calRenderer = new TradeJournalCalendarRenderer(calControl, this);
            _calRenderer.OnDaySelected += OnDaySelected;
            _calRenderer.OnPrevMonth += OnPrevMonth;
            _calRenderer.OnNextMonth += OnNextMonth;

            this.Window.Browser.AddEventHandler("noteArea", "oninput", OnNoteInput);
            this.Window.Browser.Layout.Column = 1;

            Core.Instance.TradeAdded += OnTradeAdded;
        }

        /// <summary>
        /// (Re)applies the two-column grid layout using a star ratio derived from _calWidthPct.
        /// Column 0 (calendar) gets _calWidthPct stars; column 1 (notes) gets the remainder.
        /// This keeps the split proportional to the panel so the calendar can never overflow.
        /// </summary>
        private void ApplyGridLayout()
        {
            this.Window.ReinitializeGridStructure(new NativeGridDefinition
            {
                Columns = new List<NativeGridItemDefinitionDefinition>
                {
                    new NativeGridItemDefinitionDefinition(false, _calColumnWidth) { SizeType = NativeGridItemDefinitionSizeType.Pixel },
                    new NativeGridItemDefinitionDefinition(false, 1) { SizeType = NativeGridItemDefinitionSizeType.Star }
                },
                Rows = new List<NativeGridItemDefinitionDefinition>
                {
                    new NativeGridItemDefinitionDefinition(false, 1) { SizeType = NativeGridItemDefinitionSizeType.Star }
                }
            });

            // ReinitializeGridStructure rebuilds the column definitions, but controls already
            // parented into the grid don't appear to recompute their bounds against the new
            // definition until their Layout.Column is reasserted. Force that here.
            if (_calRenderer != null)
            {
                _calRenderer.Layout.Column = 0;
            }
            if (this.Window?.Browser != null)
            {
                this.Window.Browser.Layout.Column = 1;
            }
        }

        public override void Populate(PluginParameters args = null)
        {
            base.Populate(args);
            if (_account == null)
                _account = Core.Instance.Accounts.FirstOrDefault();
            this.Window.Browser.BrowserLoaded += OnBrowserLoaded;
        }

        private void OnBrowserLoaded(NativeWebBrowserEventArgs args)
        {
            this.Window.Browser.BrowserLoaded -= OnBrowserLoaded;
            AttemptInitialLoad(_selectedDate, attemptsLeft: 20);
        }

        private const string DomReadySentinel = "__tj_ready__";

        private void AttemptInitialLoad(string date, int attemptsLeft)
        {
            try
            {
                // SetValueString sets the textarea .value property directly through
                // Quantower's bridge — no JS needed, works before the bridge is warmed up.
                this.Window.Browser.UpdateHtml("noteArea", HtmlAction.SetValueString, DomReadySentinel);

                var response = this.Window.Browser.GetHtmlValue("noteArea", HtmlGetValueAction.GetProperty, "value");

                if (response?.Result is string actual && actual == DomReadySentinel)
                {
                    _browserReady = true;
                    LoadNote(date);
                    _calRenderer?.Redraw();
                    return;
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] DOM readiness check failed (attempt {21 - attemptsLeft}/20): {ex.Message}");
            }

            if (attemptsLeft <= 1)
            {
                Core.Instance.Loggers.Log("[TradeJournal] DOM readiness could not be confirmed after 20s; loading anyway.");
                _browserReady = true;
                LoadNote(date);
                _calRenderer?.Redraw();
                return;
            }

            var retryTimer = new System.Timers.Timer(1000) { AutoReset = false };
            retryTimer.Elapsed += (s, e) =>
            {
                retryTimer.Dispose();
                AttemptInitialLoad(date, attemptsLeft - 1);
            };
            retryTimer.Start();
        }

        public override void Dispose()
        {
            Core.Instance.TradeAdded -= OnTradeAdded;
            _saveDebounce?.Stop();
            _saveDebounce?.Dispose();
            _calRenderer?.Dispose();
            base.Dispose();
        }

        protected override void OnLayoutUpdated()
        {
            base.OnLayoutUpdated();
            if (_calRenderer != null)
                _calRenderer.Layout.Margin = this.NonClientMargin;
            this.Window.Browser.Layout.Margin = this.NonClientMargin;
        }

        // --- Settings ---

        public override IList<SettingItem> Settings
        {
            get
            {
                var result = base.Settings;
                result.Add(new SettingItemAccount("Account", _account)
                {
                    Text = "Account",
                    SortIndex = 0
                });
                result.Add(new SettingItemInteger("CellWidth", _cellW)
                {
                    Text = "Cell Width",
                    SortIndex = 1,
                    Minimum = 44,
                    Maximum = 110
                });
                result.Add(new SettingItemInteger("CellHeight", _cellH)
                {
                    Text = "Cell Height",
                    SortIndex = 2,
                    Minimum = 52,
                    Maximum = 130
                });
                result.Add(new SettingItemInteger("CalendarColumnWidth", _calColumnWidth)
                {
                    Text = "Calendar Width (px)",
                    SortIndex = 3,
                    Minimum = CalWidthMin,
                    Maximum = CalWidthMax
                });
                result.Add(new SettingItemBoolean("CalculateFees", _calculateFees)
                { Text = "Calculate Commissions & Fees", SortIndex = 4 });
                result.Add(new SettingItemDouble("FeePerMicro", _feePerMicro)
                { Text = "Fee Per Micro Contract (Round Trip $)", SortIndex = 5, Increment = 0.01, DecimalPlaces = 2 });
                result.Add(new SettingItemDouble("FeePerMini", _feePerMini)
                { Text = "Fee Per Mini Contract (Round Trip $)", SortIndex = 6, Increment = 0.01, DecimalPlaces = 2 });
                return result;
            }
            set
            {
                base.Settings = value;

                bool layoutChanged = false;

                foreach (var item in value)
                {
                    switch (item.Name)
                    {
                        case "Account":
                            _account = item.Value as Account;
                            InvalidateStatsCache();
                            break;
                        case "CellWidth":
                            int newW = Convert.ToInt32(item.Value);
                            if (newW != _cellW)
                            {
                                _cellW = newW;
                                layoutChanged = true;
                            }
                            break;
                        case "CellHeight":
                            int newH = Convert.ToInt32(item.Value);
                            if (newH != _cellH)
                            {
                                _cellH = newH;
                                layoutChanged = true;
                            }
                            break;
                        case "CalendarColumnWidth":
                            int newCalW = Convert.ToInt32(item.Value);
                            if (newCalW != _calColumnWidth)
                            {
                                _calColumnWidth = newCalW;
                                layoutChanged = true;
                            }
                            break;
                        case "CalculateFees":
                            _calculateFees = (bool)item.Value;
                            InvalidateStatsCache();
                            break;
                        case "FeePerMicro":
                            _feePerMicro = (double)item.Value;
                            if (_calculateFees) InvalidateStatsCache();
                            break;
                        case "FeePerMini":
                            _feePerMini = (double)item.Value;
                            if (_calculateFees) InvalidateStatsCache();
                            break;
                    }
                }

                if (layoutChanged)
                    ApplyGridLayout();

                _calRenderer?.Redraw();
            }
        }

        // --- Events ---

        private void OnTradeAdded(Trade trade)
        {
            try
            {
                if (_account != null && !trade.Account.Equals(_account)) return;

                DateTime tradeDate = trade.DateTime.ToLocalTime().Date;

                // Invalidate just the specific month this trade falls in — adjacent
                // months shown as dimmed fill days are cached independently and will
                // pick this up next time they're queried.
                _monthStatsCache.Remove((tradeDate.Year, tradeDate.Month));

                string dateKey = tradeDate.ToString("yyyy-MM-dd");
                _dayMetricsCache.Remove(dateKey);
                _monthMetricsCache.Remove((tradeDate.Year, tradeDate.Month));

                _calRenderer?.Redraw();
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] OnTradeAdded error: {ex.Message}");
            }
        }

        private void OnDaySelected(string date)
        {
            SaveNoteFromBrowser(_selectedDate);
            _selectedDate = date;
            _currentYear = int.Parse(date.Split('-')[0]);
            _currentMonth = int.Parse(date.Split('-')[1]) - 1;
            _dayMetricsCache.Remove(date);
            LoadNote(_selectedDate);
        }

        private void OnPrevMonth()
        {
            SaveNoteFromBrowser(_selectedDate);
            _currentMonth--;
            if (_currentMonth < 0) { _currentMonth = 11; _currentYear--; }
            _selectedDate = $"{_currentYear}-{(_currentMonth + 1):D2}-01";
            InvalidateStatsCache();
            LoadNote(_selectedDate);
            _calRenderer.Redraw();
        }

        private void OnNextMonth()
        {
            SaveNoteFromBrowser(_selectedDate);
            _currentMonth++;
            if (_currentMonth > 11) { _currentMonth = 0; _currentYear++; }
            _selectedDate = $"{_currentYear}-{(_currentMonth + 1):D2}-01";
            InvalidateStatsCache();
            LoadNote(_selectedDate);
            _calRenderer.Redraw();
        }

        private void OnNoteInput(string elementId, object args)
        {
            _saveDebounce.Stop();
            _saveDebounce.Start();
            this.Window.Browser.UpdateHtml("saveIndicator", HtmlAction.SetTextContent, "typing...");
            this.Window.Browser.UpdateHtml("saveIndicator", HtmlAction.SetClass, "save-indicator");
        }

        private void OnSaveDebounceElapsed(object sender, ElapsedEventArgs e)
        {
            SaveNoteFromBrowser(_selectedDate);
        }

        private void SaveNoteFromBrowser(string date)
        {
            if (!_browserReady) return;
            if (string.IsNullOrEmpty(date)) return;

            // Never write to a date whose real content we haven't actually loaded into
            // the browser yet this session — this is what prevents a stray/premature
            // input event (e.g. during initial browser attach) from ever overwriting a
            // real note with an empty one. A date is only trustworthy to save once
            // LoadNote has run for it at least once, regardless of whether that file
            // existed or was genuinely blank.
            if (!_loadedDates.Contains(date)) return;

            try
            {
                var response = this.Window.Browser.GetHtmlValue(
                    "noteArea", HtmlGetValueAction.GetProperty, "value");

                if (response?.Result is string content)
                {
                    WriteNoteToDisk(date, content);
                    this.Window.Browser.UpdateHtml("saveIndicator", HtmlAction.SetTextContent, "saved");
                    this.Window.Browser.UpdateHtml("saveIndicator", HtmlAction.SetClass, "save-indicator saved");
                    _calRenderer?.Redraw();
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] SaveNoteFromBrowser error: {ex.Message}");
            }
        }

        private void WriteNoteToDisk(string date, string content)
        {
            string path = Path.Combine(JournalFolder, $"{date}.txt");
            File.WriteAllText(path, content ?? string.Empty);
        }

        private void LoadNote(string date)
        {
            try
            {
                string path = Path.Combine(JournalFolder, $"{date}.txt");
                string content = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

                var parts = date.Split('-');
                string[] monthNames = { "January","February","March","April","May","June",
                    "July","August","September","October","November","December" };
                string label = monthNames[int.Parse(parts[1]) - 1] + " " +
                               int.Parse(parts[2]) + ", " + parts[0];

                this.Window.Browser.UpdateHtml("selectedDateLabel", HtmlAction.SetTextContent, label);
                this.Window.Browser.UpdateHtml("noteArea", HtmlAction.SetValueString, content);
                this.Window.Browser.UpdateHtml("saveIndicator", HtmlAction.SetTextContent, "");
                this.Window.Browser.UpdateHtml("saveIndicator", HtmlAction.SetClass, "save-indicator");

                _loadedDates.Add(date);
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] Load error: {ex.Message}");
            }
        }

        // --- Trade statistics ---

        private void InvalidateStatsCache()
        {
            _monthStatsCache.Clear();
            _dayMetricsCache.Clear();
            _monthMetricsCache.Clear();
        }

        public Dictionary<string, DayStats> GetMonthlyTradeStats()
        {
            return GetTradeStatsForMonth(_currentYear, _currentMonth + 1);
        }

        // Computes (and caches) per-day trade stats for an arbitrary month. Used both
        // for the currently-displayed month and for the dimmed leading/trailing days
        // pulled in from adjacent months at the edges of the calendar grid.
        public Dictionary<string, DayStats> GetTradeStatsForMonth(int year, int month)
        {
            var cacheKey = (year, month);
            if (_monthStatsCache.TryGetValue(cacheKey, out var cached))
                return cached;

            Dictionary<string, DayStats> stats;
            try
            {
                var fills = GetFillsForMonth(year, month);
                stats = AggregateDayStats(fills);
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] GetTradeStatsForMonth error: {ex.Message}");
                stats = new Dictionary<string, DayStats>();
            }

            _monthStatsCache[cacheKey] = stats;
            return stats;
        }

        // Returns every fill for the given month, preferring the archive for any day
        // it covers and falling back to the live platform for everything else.
        private List<FillRecord> GetFillsForMonth(int year, int month)
        {
            EnsureArchiveLoaded();
            var combined = new List<FillRecord>();

            var archiveDaysThisMonth = new HashSet<string>(_archiveCoveredDays.Where(d =>
            {
                var parts = d.Split('-');
                return int.Parse(parts[0], CultureInfo.InvariantCulture) == year
                    && int.Parse(parts[1], CultureInfo.InvariantCulture) == month;
            }));

            if (archiveDaysThisMonth.Count > 0)
            {
                combined.AddRange(_archiveFillsCache.Where(f =>
                    f.DateTime.Year == year && f.DateTime.Month == month &&
                    archiveDaysThisMonth.Contains(f.DateTime.ToString("yyyy-MM-dd"))));
            }

            try
            {
                var monthStart = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
                var monthEnd = monthStart.AddMonths(1);

                var trades = Core.Instance.GetTrades(new TradesHistoryRequestParameters
                {
                    From = monthStart,
                    To = monthEnd,
                });

                if (trades != null)
                {
                    foreach (var trade in trades)
                    {
                        if (_account != null && !trade.Account.Equals(_account)) continue;

                        DateTime localDate = trade.DateTime.ToLocalTime().Date;
                        if (localDate.Month != month || localDate.Year != year) continue;

                        // Archive already has real data for this day — don't mix in
                        // platform data too, or the day would be double-counted.
                        if (archiveDaysThisMonth.Contains(localDate.ToString("yyyy-MM-dd"))) continue;

                        var rec = ToFillRecord(trade);
                        if (rec.HasValue) combined.Add(rec.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] GetFillsForMonth platform query error: {ex.Message}");
            }

            return combined;
        }

        // Returns every fill for a single day, preferring the archive if it covers
        // that day and falling back to the live platform otherwise.
        private List<FillRecord> GetFillsForDay(DateTime day)
        {
            EnsureArchiveLoaded();
            string dayKey = day.ToString("yyyy-MM-dd");

            if (_archiveCoveredDays.Contains(dayKey))
                return _archiveFillsCache.Where(f => f.DateTime.Date == day.Date).ToList();

            var result = new List<FillRecord>();
            try
            {
                var dayStart = new DateTime(day.Year, day.Month, day.Day, 0, 0, 0, DateTimeKind.Utc);
                var dayEnd = dayStart.AddDays(1);

                var trades = Core.Instance.GetTrades(new TradesHistoryRequestParameters
                {
                    From = dayStart,
                    To = dayEnd,
                });

                if (trades != null)
                {
                    foreach (var trade in trades)
                    {
                        if (_account != null && !trade.Account.Equals(_account)) continue;
                        if (trade.DateTime.ToLocalTime().Date != day.Date) continue;

                        var rec = ToFillRecord(trade);
                        if (rec.HasValue) result.Add(rec.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] GetFillsForDay platform query error: {ex.Message}");
            }

            return result;
        }

        // Converts a live platform Trade into the common FillRecord shape.
        private static FillRecord? ToFillRecord(Trade trade)
        {
            double fillValue = GetFillValue(trade);
            if (double.IsNaN(fillValue)) return null;

            string side = trade.Side.ToString();
            bool isBuy = side.Equals("Buy", StringComparison.OrdinalIgnoreCase);
            double signedQty = isBuy ? trade.Quantity : -trade.Quantity;
            string symbol = trade.Symbol?.Name ?? trade.Symbol?.Id ?? "UNKNOWN";

            return new FillRecord
            {
                DateTime = trade.DateTime.ToLocalTime(),
                Symbol = symbol,
                SignedQty = signedQty,
                FillValue = fillValue
            };
        }

        // Shared FIFO round-trip aggregation used for the monthly calendar badges.
        // Operates on FillRecord so live-platform fills and archive-CSV fills are
        // processed through identical math.
        private Dictionary<string, DayStats> AggregateDayStats(List<FillRecord> fills)
        {
            var stats = new Dictionary<string, DayStats>();

            // Group fills by day then by symbol, process FIFO to build round trips.
            // Key: "yyyy-MM-dd|SYMBOL"  Value: running net qty and accumulated value
            var daySymbolQty = new Dictionary<string, double>();      // running net position qty
            var daySymbolValue = new Dictionary<string, double>();   // running accumulated trade value
            var daySymbolEntryQty = new Dictionary<string, double>(); // running round-trip contract count (entry side)

            foreach (var fill in fills.OrderBy(f => f.DateTime))
            {
                string dayKey = fill.DateTime.Date.ToString("yyyy-MM-dd");
                string posKey = $"{dayKey}|{fill.Symbol}";

                if (!daySymbolQty.ContainsKey(posKey))
                {
                    daySymbolQty[posKey] = 0;
                    daySymbolValue[posKey] = 0;
                    daySymbolEntryQty[posKey] = 0;
                }

                double qty = fill.SignedQty;
                double prevQty = daySymbolQty[posKey];
                double newQty = prevQty + qty;

                // Track contracts on the "entry" side of the round trip (fills that open
                // or add to the position), so the fee reflects the true round-trip size
                // rather than just the size of the fill that happens to close it.
                bool isEntryFill = prevQty == 0 || Math.Sign(prevQty) == Math.Sign(qty);
                if (isEntryFill)
                    daySymbolEntryQty[posKey] += Math.Abs(qty);

                daySymbolValue[posKey] += fill.FillValue;

                // A round trip completes when net qty crosses or returns to zero
                if ((prevQty > 0 && newQty <= 0) || (prevQty < 0 && newQty >= 0))
                {
                    double fee = GetFeePerContract(fill.Symbol) * daySymbolEntryQty[posKey];
                    double pnl = daySymbolValue[posKey] - fee;
                    if (!stats.TryGetValue(dayKey, out DayStats dayStats))
                        dayStats = new DayStats();

                    dayStats.PnL += pnl;
                    dayStats.RoundTrips++;
                    dayStats.HasData = true;
                    stats[dayKey] = dayStats;

                    // Reset accumulators; if qty overshot zero, the remainder starts a new position
                    daySymbolQty[posKey] = newQty;
                    daySymbolValue[posKey] = newQty != 0 ? fill.FillValue * (Math.Abs(newQty) / Math.Abs(qty)) : 0;
                    daySymbolEntryQty[posKey] = newQty != 0 ? Math.Abs(newQty) : 0;
                }
                else
                {
                    daySymbolQty[posKey] = newQty;
                }
            }

            return stats;
        }

        // Per-day long/short breakdown for the metrics panel. Cached per date string
        // since OnDaySelected/Prev/NextMonth already drive when this needs to refresh.
        private Dictionary<string, DayMetrics> _dayMetricsCache = new Dictionary<string, DayMetrics>();

        // Per-(year,month) aggregate metrics, invalidated alongside the monthly stats cache.
        private Dictionary<(int Year, int Month), MonthMetrics> _monthMetricsCache
            = new Dictionary<(int Year, int Month), MonthMetrics>();

        public DayMetrics GetDayMetrics(string date)
        {
            if (_dayMetricsCache.TryGetValue(date, out DayMetrics cached))
                return cached;

            var metrics = new DayMetrics
            {
                Long = new SideMetrics { LargestLoss = 0 },
                Short = new SideMetrics { LargestLoss = 0 },
                Pie = new PieBuckets()
            };

            try
            {
                if (DateTime.TryParse(date, out DateTime dayDate))
                {
                    var dayFills = GetFillsForDay(dayDate);
                    metrics = AggregateDayMetrics(dayFills);
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] GetDayMetrics error: {ex.Message}");
            }

            _dayMetricsCache[date] = metrics;
            return metrics;
        }

        // Aggregates all round trips for the entire month into a single MonthMetrics.
        // Uses the same fill data and FIFO logic as the per-day path so numbers always match.
        public MonthMetrics GetMonthMetrics(int year, int month)
        {
            var cacheKey = (year, month);
            if (_monthMetricsCache.TryGetValue(cacheKey, out MonthMetrics cached))
                return cached;

            var result = new MonthMetrics
            {
                Long = new SideMetrics { LargestLoss = 0 },
                Short = new SideMetrics { LargestLoss = 0 },
                Pie = new PieBuckets()
            };

            try
            {
                // Accumulate day-by-day so each day's fills are processed in isolation
                // (FIFO positions don't carry across midnight), matching DayMetrics logic.
                int daysInMonth = DateTime.DaysInMonth(year, month);
                var longM = result.Long;
                var shortM = result.Short;
                var pie = result.Pie;

                for (int day = 1; day <= daysInMonth; day++)
                {
                    var date = new DateTime(year, month, day);
                    if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
                        continue;

                    var dayFills = GetFillsForDay(date);
                    if (dayFills.Count == 0) continue;

                    var dm = AggregateDayMetrics(dayFills);
                    if (!dm.HasData) continue;

                    // Merge Long side
                    var lm = dm.Long;
                    longM.RoundTrips += lm.RoundTrips;
                    longM.Wins += lm.Wins;
                    longM.WinCount += lm.WinCount;
                    longM.LossCount += lm.LossCount;
                    longM.TotalPnl += lm.TotalPnl;
                    longM.TotalWinPnl += lm.TotalWinPnl;
                    longM.TotalLossPnl += lm.TotalLossPnl;
                    if (lm.LargestWin > longM.LargestWin) longM.LargestWin = lm.LargestWin;
                    if (lm.LargestLoss < longM.LargestLoss) longM.LargestLoss = lm.LargestLoss;
                    longM.TotalDurationSeconds += lm.TotalDurationSeconds;
                    longM.DurationSampleCount += lm.DurationSampleCount;
                    longM.TotalWinDurationSeconds += lm.TotalWinDurationSeconds;
                    longM.WinDurationCount += lm.WinDurationCount;
                    longM.TotalLossDurationSeconds += lm.TotalLossDurationSeconds;
                    longM.LossDurationCount += lm.LossDurationCount;
                    if (lm.WinStreak > longM.WinStreak) longM.WinStreak = lm.WinStreak;
                    if (lm.LossStreak > longM.LossStreak) longM.LossStreak = lm.LossStreak;
                    if (lm.HasData) longM.HasData = true;

                    // Merge Short side
                    var sm = dm.Short;
                    shortM.RoundTrips += sm.RoundTrips;
                    shortM.Wins += sm.Wins;
                    shortM.WinCount += sm.WinCount;
                    shortM.LossCount += sm.LossCount;
                    shortM.TotalPnl += sm.TotalPnl;
                    shortM.TotalWinPnl += sm.TotalWinPnl;
                    shortM.TotalLossPnl += sm.TotalLossPnl;
                    if (sm.LargestWin > shortM.LargestWin) shortM.LargestWin = sm.LargestWin;
                    if (sm.LargestLoss < shortM.LargestLoss) shortM.LargestLoss = sm.LargestLoss;
                    shortM.TotalDurationSeconds += sm.TotalDurationSeconds;
                    shortM.DurationSampleCount += sm.DurationSampleCount;
                    shortM.TotalWinDurationSeconds += sm.TotalWinDurationSeconds;
                    shortM.WinDurationCount += sm.WinDurationCount;
                    shortM.TotalLossDurationSeconds += sm.TotalLossDurationSeconds;
                    shortM.LossDurationCount += sm.LossDurationCount;
                    if (sm.WinStreak > shortM.WinStreak) shortM.WinStreak = sm.WinStreak;
                    if (sm.LossStreak > shortM.LossStreak) shortM.LossStreak = sm.LossStreak;
                    if (sm.HasData) shortM.HasData = true;

                    // Merge pie buckets
                    pie.Wins += dm.Pie.Wins;
                    pie.Losses += dm.Pie.Losses;
                    pie.Breakevens += dm.Pie.Breakevens;
                }

                result.Long = longM;
                result.Short = shortM;
                result.Pie = pie;
                result.TotalPnL = longM.TotalPnl + shortM.TotalPnl;
                result.HasData = longM.HasData || shortM.HasData;
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] GetMonthMetrics error: {ex.Message}");
            }

            _monthMetricsCache[cacheKey] = result;
            return result;
        }

        // Shared FIFO round-trip aggregation used for the per-day detail panel
        // (long/short breakdowns, win rate, streaks, hold times, pie chart). Operates
        // on FillRecord so live-platform fills and archive-CSV fills are processed
        // through identical math.
        private DayMetrics AggregateDayMetrics(List<FillRecord> dayFills)
        {
            var metrics = new DayMetrics
            {
                Long = new SideMetrics { LargestLoss = 0 },
                Short = new SideMetrics { LargestLoss = 0 },
                Pie = new PieBuckets()
            };

            // FIFO state per symbol: running net qty, accumulated value, and open-fill timestamp
            var netQty = new Dictionary<string, double>();
            var netValue = new Dictionary<string, double>();
            var entryQty = new Dictionary<string, double>(); // running round-trip contract count (entry side)
            var openTime = new Dictionary<string, DateTime>(); // timestamp of the first fill that opened the position

            var longM = metrics.Long;
            var shortM = metrics.Short;
            var pie = metrics.Pie;
            int longStreakWin = 0, longStreakLoss = 0;
            int shortStreakWin = 0, shortStreakLoss = 0;

            foreach (var fill in dayFills.OrderBy(f => f.DateTime))
            {
                string symbol = fill.Symbol;

                if (!netQty.ContainsKey(symbol))
                {
                    netQty[symbol] = 0;
                    netValue[symbol] = 0;
                    entryQty[symbol] = 0;
                }

                double signedQty = fill.SignedQty;
                double prevQty = netQty[symbol];
                double newQty = prevQty + signedQty;

                // If this fill opens a new position from flat, record the open timestamp
                if (prevQty == 0)
                    openTime[symbol] = fill.DateTime;

                // Track contracts on the "entry" side of the round trip (fills that open
                // or add to the position), so the fee reflects the true round-trip size
                // rather than just the size of the fill that happens to close it.
                bool isEntryFill = prevQty == 0 || Math.Sign(prevQty) == Math.Sign(signedQty);
                if (isEntryFill)
                    entryQty[symbol] += Math.Abs(signedQty);

                netValue[symbol] += fill.FillValue;

                // Check if a round trip closed (net qty crossed zero)
                bool closedRoundTrip = (prevQty > 0 && newQty <= 0) || (prevQty < 0 && newQty >= 0);

                if (closedRoundTrip)
                {
                    double fee = GetFeePerContract(symbol) * entryQty[symbol];
                    double pnl = netValue[symbol] - fee;
                    bool wasLong = prevQty > 0; // long position = opened with Buy fills

                    // Duration: time from open fill to this close fill
                    double? durationSecs = null;
                    if (openTime.TryGetValue(symbol, out DateTime ot))
                    {
                        var span = fill.DateTime - ot;
                        if (span.TotalSeconds >= 0)
                            durationSecs = span.TotalSeconds;
                    }

                    // Tally into the correct side bucket
                    ref SideMetrics bucket = ref (wasLong ? ref longM : ref shortM);
                    ref int streakWin = ref (wasLong ? ref longStreakWin : ref shortStreakWin);
                    ref int streakLoss = ref (wasLong ? ref longStreakLoss : ref shortStreakLoss);

                    bucket.RoundTrips++;
                    bucket.TotalPnl += pnl;
                    bucket.HasData = true;
                    if (pnl > bucket.LargestWin) bucket.LargestWin = pnl;
                    if (pnl < bucket.LargestLoss) bucket.LargestLoss = pnl;

                    if (pnl > 0)
                    {
                        bucket.Wins++;
                        bucket.TotalWinPnl += pnl;
                        bucket.WinCount++;
                        streakWin++;
                        streakLoss = 0;
                        if (streakWin > bucket.WinStreak) bucket.WinStreak = streakWin;
                        if (durationSecs.HasValue)
                        {
                            bucket.TotalWinDurationSeconds += durationSecs.Value;
                            bucket.WinDurationCount++;
                        }
                    }
                    else if (pnl < 0)
                    {
                        bucket.TotalLossPnl += pnl;
                        bucket.LossCount++;
                        streakLoss++;
                        streakWin = 0;
                        if (streakLoss > bucket.LossStreak) bucket.LossStreak = streakLoss;
                        if (durationSecs.HasValue)
                        {
                            bucket.TotalLossDurationSeconds += durationSecs.Value;
                            bucket.LossDurationCount++;
                        }
                    }
                    else
                    {
                        streakWin = 0;
                        streakLoss = 0;
                    }

                    if (durationSecs.HasValue)
                    {
                        bucket.TotalDurationSeconds += durationSecs.Value;
                        bucket.DurationSampleCount++;
                    }

                    // Pie bucket: ±$2 breakeven band
                    if (pnl > 2.0) pie.Wins++;
                    else if (pnl < -2.0) pie.Losses++;
                    else pie.Breakevens++;

                    // Reset FIFO state; if qty overshot zero, remainder starts a new position
                    netQty[symbol] = newQty;
                    netValue[symbol] = newQty != 0 ? fill.FillValue * (Math.Abs(newQty) / Math.Abs(signedQty)) : 0;
                    entryQty[symbol] = newQty != 0 ? Math.Abs(newQty) : 0;
                    if (newQty != 0)
                        openTime[symbol] = fill.DateTime; // new position opened by this same fill
                    else
                        openTime.Remove(symbol);

                    // Write back ref-struct mutations (C# ref locals on structs)
                    if (wasLong) longM = bucket; else shortM = bucket;
                }
                else
                {
                    netQty[symbol] = newQty;
                }
            }

            metrics.Long = longM;
            metrics.Short = shortM;
            metrics.Pie = pie;
            metrics.HasData = longM.HasData || shortM.HasData;
            return metrics;
        }

        // Returns the signed P&L contribution of a single fill using the symbol's tick cost/size
        // to derive the dollar-per-point value. This matches what Quantower shows as "Trade Value".
        // Sell fills contribute positive value, Buy fills negative.
        // PointValue = TickCost / TickSize (e.g. MNQ: $0.50/0.25 = $2/pt, MES: $1.25/0.25 = $5/pt)
        private static double GetFillValue(Trade trade)
        {
            double price = trade.Price;
            double qty = trade.Quantity;
            string side = trade.Side.ToString();

            double tickSize = trade.Symbol?.GetTickSize(price) ?? double.NaN;
            double tickCost = trade.Symbol?.GetTickCost(price) ?? double.NaN;

            double pointValue;
            if (!double.IsNaN(tickSize) && !double.IsNaN(tickCost) && tickSize > 0)
                pointValue = tickCost / tickSize;
            else
                pointValue = double.NaN;

            if (double.IsNaN(pointValue))
            {
                Core.Instance.Loggers.Log($"[TradeJournal] GetFillValue: could not determine PointValue for {trade.Symbol?.Name ?? "unknown"} at price {price}. TickSize={tickSize}, TickCost={tickCost}");
                return double.NaN;
            }

            double value = price * qty * pointValue;
            return side.Equals("Sell", StringComparison.OrdinalIgnoreCase) ? value : -value;
        }

        // Returns the round-trip fee per contract for a given symbol, or 0.0 if fee
        // calculation is disabled or the symbol isn't recognized as a micro/mini future.
        private double GetFeePerContract(string symbolName)
        {
            if (!_calculateFees) return 0.0;

            string symbol = symbolName ?? string.Empty;

            if (symbol.StartsWith("MES") || symbol.StartsWith("MNQ") || symbol.StartsWith("M2K"))
                return _feePerMicro;
            if (symbol.StartsWith("ES") || symbol.StartsWith("NQ") || symbol.StartsWith("RTY"))
                return _feePerMini;

            Core.Instance.Loggers.Log($"[TradeJournal] Unrecognized symbol '{symbol}' — no fee applied.");
            return 0.0;
        }

        // --- Trade archive (manually-exported CSVs) ---

        // (Re)loads the archive from disk if any file in ArchiveFolder is new or has
        // changed since the last scan. Cheap to call often — the timestamp check is
        // just a directory listing, so callers don't need to manage this themselves.
        private void EnsureArchiveLoaded()
        {
            DateTime latestWrite = DateTime.MinValue;
            var files = new List<string>();

            try
            {
                if (Directory.Exists(ArchiveFolder))
                {
                    files.AddRange(Directory.GetFiles(ArchiveFolder, "*.csv"));
                    foreach (var f in files)
                    {
                        var t = File.GetLastWriteTimeUtc(f);
                        if (t > latestWrite) latestWrite = t;
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] Archive folder scan error: {ex.Message}");
            }

            if (_archiveLoadedOnce && latestWrite == _archiveScanStamp)
                return; // nothing new since last load

            var fills = new List<FillRecord>();
            var coveredDays = new HashSet<string>();
            var seenTradeIds = new HashSet<string>();

            foreach (var file in files)
            {
                try
                {
                    foreach (var row in ParseArchiveCsv(file))
                    {
                        // A day counts as "covered" the moment any row for it appears,
                        // even if that specific row turns out to be a duplicate below.
                        coveredDays.Add(row.DayKey);

                        if (!string.IsNullOrEmpty(row.TradeId) && !seenTradeIds.Add(row.TradeId))
                            continue; // already saw this exact fill in another export file

                        fills.Add(row.Fill);
                    }
                }
                catch (Exception ex)
                {
                    Core.Instance.Loggers.Log($"[TradeJournal] Error parsing archive file '{Path.GetFileName(file)}': {ex.Message}");
                }
            }

            _archiveFillsCache = fills;
            _archiveCoveredDays = coveredDays;
            _archiveScanStamp = latestWrite;
            _archiveLoadedOnce = true;

            InvalidateStatsCache(); // archive contents changed — drop anything derived from the old data
        }

        // Parses one exported CSV into fill records. Column order is looked up by
        // header name (not fixed position) so re-ordered/re-exported columns still work.
        // Required columns: Date/Time, Quantity, Trade value, and either Underlier or
        // Symbol. Fee/Gross P/L/Net P/L columns are intentionally ignored — they're
        // unreliable in this export (often 0 even for real closed trades) and fees are
        // computed from the plugin's own settings instead, exactly like live trades.
        private IEnumerable<(FillRecord Fill, string TradeId, string DayKey)> ParseArchiveCsv(string path)
        {
            var lines = File.ReadAllLines(path); // auto-detects the UTF-8 BOM this export uses
            if (lines.Length == 0) yield break;

            var header = ParseCsvLine(lines[0]);
            var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Length; i++)
            {
                var name = header[i].Trim();
                if (!string.IsNullOrEmpty(name) && !col.ContainsKey(name))
                    col[name] = i;
            }

            if (!col.ContainsKey("Date/Time") || !col.ContainsKey("Quantity") || !col.ContainsKey("Trade value")
                || (!col.ContainsKey("Underlier") && !col.ContainsKey("Symbol")))
            {
                Core.Instance.Loggers.Log($"[TradeJournal] Archive file '{Path.GetFileName(path)}' is missing expected columns — skipped.");
                yield break;
            }

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;

                var fields = ParseCsvLine(lines[i]);
                if (fields.Length <= col["Trade value"]) continue;

                string symbol = col.TryGetValue("Underlier", out int uIdx) && uIdx < fields.Length && !string.IsNullOrWhiteSpace(fields[uIdx])
                    ? fields[uIdx].Trim()
                    : (col.TryGetValue("Symbol", out int sIdx) && sIdx < fields.Length ? fields[sIdx].Trim() : null);
                if (string.IsNullOrEmpty(symbol)) continue;

                if (!DateTime.TryParse(fields[col["Date/Time"]], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
                    continue;
                if (!double.TryParse(fields[col["Quantity"]], NumberStyles.Float, CultureInfo.InvariantCulture, out double qty))
                    continue;
                if (!double.TryParse(fields[col["Trade value"]], NumberStyles.Float, CultureInfo.InvariantCulture, out double tradeValue))
                    continue;

                string tradeId = col.TryGetValue("Trade ID", out int tIdx) && tIdx < fields.Length ? fields[tIdx].Trim() : null;

                var fill = new FillRecord
                {
                    DateTime = dt,
                    Symbol = symbol,
                    SignedQty = qty,          // export already signs this: + buy, - sell
                    FillValue = -tradeValue,  // Trade value's sign convention is inverted vs GetFillValue
                };

                yield return (fill, tradeId, dt.Date.ToString("yyyy-MM-dd"));
            }
        }

        // Minimal CSV line splitter that handles quoted fields (including embedded
        // commas and escaped "" quotes), since some columns like "Gross P/L,ticks"
        // contain commas inside quotes.
        private static string[] ParseCsvLine(string line)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(c);
                }
                else
                {
                    if (c == '"') inQuotes = true;
                    else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
                    else sb.Append(c);
                }
            }
            result.Add(sb.ToString());
            return result.ToArray();
        }

        // Expose state to renderer
        public int CurrentMonth => _currentMonth;
        public int CurrentYear => _currentYear;
        public string SelectedDate => _selectedDate;
        public int CellW => _cellW;
        public int CellH => _cellH;

        public HashSet<string> GetNoteDates()
        {
            var dates = new HashSet<string>();
            foreach (var file in Directory.GetFiles(JournalFolder, "*.txt"))
            {
                // Skip 0-byte files (a cleared note) so the calendar's note-underline
                // indicator only shows for days that actually have content.
                if (new FileInfo(file).Length == 0) continue;
                dates.Add(Path.GetFileNameWithoutExtension(file));
            }
            return dates;
        }
    }

    public class TradeJournalCalendarRenderer : Renderer
    {
        private readonly TradeJournalPlugin _plugin;
        private BufferedGraphic _bufferedGraphic;

        // Fixed layout values (not scaled)
        private const int PrevBtnX = 8;
        private const int HeaderY = 8;
        private const int HeaderH = 24;
        private const int DayNamesY = 40;
        private const int DayNamesH = 16;
        private const int GridStartY = 62;
        private const int GridStartX = 8;

        // These are derived from plugin settings at draw time
        // NextBtnX = GridStartX + 5 * CellW + 4  (small gap before arrow)
        private int NextBtnX => GridStartX + 5 * _plugin.CellW + 4;

        private Rectangle _prevBtnRect;
        private Rectangle _nextBtnRect;
        private Rectangle _headerRect;   // clickable area for the month title
        private bool _showMonthlyMetrics = false; // true = Monthly Metrics panel; false = Daily Metrics panel
        private readonly List<(Rectangle rect, string date)> _dayCells =
            new List<(Rectangle, string)>();

        public event Action<string> OnDaySelected;
        public event Action OnPrevMonth;
        public event Action OnNextMonth;

        public TradeJournalCalendarRenderer(IRenderingNativeControl native, TradeJournalPlugin plugin)
            : base(native)
        {
            _plugin = plugin;
            _bufferedGraphic = new BufferedGraphic(Draw, Refresh, native.DisposeImage,
                native.IsDisplayed, BufferedGraphicRequiredThreadType.LowPriority);

            NativeControl.MouseClickNative += OnMouseClick;
        }

        public void Redraw() => _bufferedGraphic.IsDirty = true;

        private void OnMouseClick(NativeMouseEventArgs e)
        {
            if (_prevBtnRect.Contains(e.Location)) { OnPrevMonth?.Invoke(); return; }
            if (_nextBtnRect.Contains(e.Location)) { OnNextMonth?.Invoke(); return; }

            // Clicking the month header switches to Monthly Metrics (stays if already there)
            if (_headerRect.Contains(e.Location))
            {
                if (!_showMonthlyMetrics)
                {
                    _showMonthlyMetrics = true;
                    Redraw();
                }
                return;
            }

            foreach (var (rect, date) in _dayCells)
            {
                if (rect.Contains(e.Location))
                {
                    // Clicking a day always switches to Daily Metrics
                    _showMonthlyMetrics = false;
                    OnDaySelected?.Invoke(date);
                    Redraw();
                    return;
                }
            }
        }

        // Compact PnL formatter: "+$1.2k" / "-$340.5"
        private static string FormatPnl(double pnl)
        {
            string sign = pnl < 0 ? "-" : "+";
            double abs = Math.Abs(pnl);
            return abs >= 1_000
                ? $"{sign}${abs / 1000.0:0.##}k"
                : $"{sign}${abs:0.##}";
        }

        // Compact duration formatter: "45s" / "12m" / "1h 5m"
        private static string FormatDuration(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
            if (ts.TotalMinutes >= 1)
                return $"{ts.Minutes}m {ts.Seconds}s";
            return $"{ts.Seconds}s";
        }

        // Draws a two-column (Long / Short) metrics breakdown for the currently selected day.
        private void DrawDailyMetricsPanel(Graphics gr, int panelY, string selectedDate,
            SolidBrush whiteBrush, SolidBrush grayBrush, SolidBrush lightGray,
            SolidBrush greenBrush, SolidBrush redBrush,
            Font fontHdr, Font fontNames, Font fontCount)
        {
            var bounds = Bounds;
            if (bounds.Width <= 0) return;

            var metrics = _plugin.GetDayMetrics(selectedDate);

            int panelX = GridStartX;
            int panelW = Math.Max(0, bounds.Width - GridStartX - 8);
            int colW = (int)(panelW / 2 * 0.75); // pull short column 25% closer to long column

            var sfLeft = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near };

            // Header
            var headerRect = new Rectangle(panelX, panelY, panelW, 18);
            gr.DrawString("Daily Metrics", fontHdr, whiteBrush, headerRect, sfLeft);

            int rowY = panelY + 22;
            int rowH = 16;

            var longCol = new Rectangle(panelX, rowY, colW - 6, rowH);
            var shortCol = new Rectangle(panelX + colW, rowY, colW - 6, rowH);

            // Column headers
            gr.DrawString("Long", fontNames, lightGray, longCol, sfLeft);
            gr.DrawString("Short", fontNames, lightGray, shortCol, sfLeft);
            rowY += rowH;

            void DrawMetricRow(string label, string longVal, string shortVal, SolidBrush longBrush, SolidBrush shortBrush)
            {
                var lRect = new Rectangle(panelX, rowY, colW - 6, rowH);
                var sRect = new Rectangle(panelX + colW, rowY, colW - 6, rowH);

                gr.DrawString($"{label}: {longVal}", fontCount, longBrush, lRect, sfLeft);
                gr.DrawString($"{label}: {shortVal}", fontCount, shortBrush, sRect, sfLeft);
                rowY += rowH;
            }

            var lm = metrics.Long;
            var sm = metrics.Short;

            DrawMetricRow("Trades", lm.RoundTrips.ToString(), sm.RoundTrips.ToString(), grayBrush, grayBrush);
            DrawMetricRow("Win Rate", lm.HasData ? $"{lm.WinRate:0.#}%" : "—", sm.HasData ? $"{sm.WinRate:0.#}%" : "—", grayBrush, grayBrush);
            DrawMetricRow("Avg P&L", lm.HasData ? FormatPnl(lm.AvgPnl) : "—", sm.HasData ? FormatPnl(sm.AvgPnl) : "—",
                lm.HasData && lm.AvgPnl >= 0 ? greenBrush : redBrush,
                sm.HasData && sm.AvgPnl >= 0 ? greenBrush : redBrush);
            DrawMetricRow("Avg Win", lm.WinCount > 0 ? FormatPnl(lm.AvgWin) : "—", sm.WinCount > 0 ? FormatPnl(sm.AvgWin) : "—", greenBrush, greenBrush);
            DrawMetricRow("Avg Loss", lm.LossCount > 0 ? FormatPnl(lm.AvgLoss) : "—", sm.LossCount > 0 ? FormatPnl(sm.AvgLoss) : "—", redBrush, redBrush);
            DrawMetricRow("Best Trade", lm.HasData ? FormatPnl(lm.LargestWin) : "—", sm.HasData ? FormatPnl(sm.LargestWin) : "—", greenBrush, greenBrush);
            DrawMetricRow("Worst Trade", lm.HasData ? FormatPnl(lm.LargestLoss) : "—", sm.HasData ? FormatPnl(sm.LargestLoss) : "—", redBrush, redBrush);
            DrawMetricRow("Win Streak", lm.HasData ? lm.WinStreak.ToString() : "—", sm.HasData ? sm.WinStreak.ToString() : "—", greenBrush, greenBrush);
            DrawMetricRow("Loss Streak", lm.HasData ? lm.LossStreak.ToString() : "—", sm.HasData ? sm.LossStreak.ToString() : "—", redBrush, redBrush);
            DrawMetricRow("Avg Hold Win",
                lm.WinDurationCount > 0 ? FormatDuration(lm.AvgWinDurationSeconds) : "—",
                sm.WinDurationCount > 0 ? FormatDuration(sm.AvgWinDurationSeconds) : "—",
                greenBrush, greenBrush);
            DrawMetricRow("Avg Hold Loss",
                lm.LossDurationCount > 0 ? FormatDuration(lm.AvgLossDurationSeconds) : "—",
                sm.LossDurationCount > 0 ? FormatDuration(sm.AvgLossDurationSeconds) : "—",
                redBrush, redBrush);

            // --- Pie chart (Win / Loss / Breakeven), placed to the right of the columns ---
            // Sized larger to fill more of the available right-hand space, and nudged
            // left/down slightly so it sits closer to the metric columns and centers
            // better against the taller row list above.
            int availableW = Math.Max(0, panelW - (panelX + 2 * colW) - 8);
            int pieSize = Math.Min(200, availableW);
            if (pieSize > 20)
            {
                int pieX = panelX + 2 * colW + 4;
                int pieY = panelY + 14;
                DrawWinLossPie(gr, pieX, pieY, pieSize, metrics.Pie, fontCount, whiteBrush);
            }

            sfLeft.Dispose();
        }

        // Draws a two-column (Long / Short) metrics breakdown for the entire displayed month.
        // Activated by clicking the month header; mirrors DrawDailyMetricsPanel's layout.
        private void DrawMonthlyMetricsPanel(Graphics gr, int panelY, int month, int year,
            SolidBrush whiteBrush, SolidBrush grayBrush, SolidBrush lightGray,
            SolidBrush greenBrush, SolidBrush redBrush,
            Font fontHdr, Font fontNames, Font fontCount)
        {
            var bounds = Bounds;
            if (bounds.Width <= 0) return;

            var metrics = _plugin.GetMonthMetrics(year, month);

            int panelX = GridStartX;
            int panelW = Math.Max(0, bounds.Width - GridStartX - 8);
            int colW = (int)(panelW / 2 * 0.75);

            var sfLeft = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near };

            // Header: "Monthly Metrics" label + total P&L on the same line
            var headerRect = new Rectangle(panelX, panelY, panelW, 18);
            gr.DrawString("Monthly Metrics", fontHdr, whiteBrush, headerRect, sfLeft);

            if (metrics.HasData)
            {
                string pnlText = FormatPnl(metrics.TotalPnL);
                var pnlBrush = metrics.TotalPnL >= 0 ? greenBrush : redBrush;
                // Measure the header label width so the P&L sits right after it with a small gap
                var headerSize = gr.MeasureString("Monthly Metrics", fontHdr);
                int pnlX = panelX + (int)headerSize.Width + 6;
                var pnlRect = new Rectangle(pnlX, panelY, panelW - (int)headerSize.Width - 6, 18);
                gr.DrawString(pnlText, fontHdr, pnlBrush, pnlRect, sfLeft);
            }

            int rowY = panelY + 22;
            int rowH = 16;

            var longCol = new Rectangle(panelX, rowY, colW - 6, rowH);
            var shortCol = new Rectangle(panelX + colW, rowY, colW - 6, rowH);

            gr.DrawString("Long", fontNames, lightGray, longCol, sfLeft);
            gr.DrawString("Short", fontNames, lightGray, shortCol, sfLeft);
            rowY += rowH;

            void DrawMetricRow(string label, string longVal, string shortVal, SolidBrush longBrush, SolidBrush shortBrush)
            {
                var lRect = new Rectangle(panelX, rowY, colW - 6, rowH);
                var sRect = new Rectangle(panelX + colW, rowY, colW - 6, rowH);
                gr.DrawString($"{label}: {longVal}", fontCount, longBrush, lRect, sfLeft);
                gr.DrawString($"{label}: {shortVal}", fontCount, shortBrush, sRect, sfLeft);
                rowY += rowH;
            }

            var lm = metrics.Long;
            var sm = metrics.Short;

            DrawMetricRow("Trades", lm.RoundTrips.ToString(), sm.RoundTrips.ToString(), grayBrush, grayBrush);
            DrawMetricRow("Win Rate", lm.HasData ? $"{lm.WinRate:0.#}%" : "—", sm.HasData ? $"{sm.WinRate:0.#}%" : "—", grayBrush, grayBrush);
            DrawMetricRow("Avg P&L", lm.HasData ? FormatPnl(lm.AvgPnl) : "—", sm.HasData ? FormatPnl(sm.AvgPnl) : "—",
                lm.HasData && lm.AvgPnl >= 0 ? greenBrush : redBrush,
                sm.HasData && sm.AvgPnl >= 0 ? greenBrush : redBrush);
            DrawMetricRow("Avg Win", lm.WinCount > 0 ? FormatPnl(lm.AvgWin) : "—", sm.WinCount > 0 ? FormatPnl(sm.AvgWin) : "—", greenBrush, greenBrush);
            DrawMetricRow("Avg Loss", lm.LossCount > 0 ? FormatPnl(lm.AvgLoss) : "—", sm.LossCount > 0 ? FormatPnl(sm.AvgLoss) : "—", redBrush, redBrush);
            DrawMetricRow("Best Trade", lm.HasData ? FormatPnl(lm.LargestWin) : "—", sm.HasData ? FormatPnl(sm.LargestWin) : "—", greenBrush, greenBrush);
            DrawMetricRow("Worst Trade", lm.HasData ? FormatPnl(lm.LargestLoss) : "—", sm.HasData ? FormatPnl(sm.LargestLoss) : "—", redBrush, redBrush);
            DrawMetricRow("Win Streak", lm.HasData ? lm.WinStreak.ToString() : "—", sm.HasData ? sm.WinStreak.ToString() : "—", greenBrush, greenBrush);
            DrawMetricRow("Loss Streak", lm.HasData ? lm.LossStreak.ToString() : "—", sm.HasData ? sm.LossStreak.ToString() : "—", redBrush, redBrush);
            DrawMetricRow("Avg Hold Win",
                lm.WinDurationCount > 0 ? FormatDuration(lm.AvgWinDurationSeconds) : "—",
                sm.WinDurationCount > 0 ? FormatDuration(sm.AvgWinDurationSeconds) : "—",
                greenBrush, greenBrush);
            DrawMetricRow("Avg Hold Loss",
                lm.LossDurationCount > 0 ? FormatDuration(lm.AvgLossDurationSeconds) : "—",
                sm.LossDurationCount > 0 ? FormatDuration(sm.AvgLossDurationSeconds) : "—",
                redBrush, redBrush);

            // Pie chart, same positioning as daily
            int availableW = Math.Max(0, panelW - (panelX + 2 * colW) - 8);
            int pieSize = Math.Min(200, availableW);
            if (pieSize > 20)
            {
                int pieX = panelX + 2 * colW + 4;
                int pieY = panelY + 14;
                DrawWinLossPie(gr, pieX, pieY, pieSize, metrics.Pie, fontCount, whiteBrush);
            }

            sfLeft.Dispose();
        }

        // Draws a simple win/loss/breakeven pie chart with in-slice percentage labels, no legend.
        private void DrawWinLossPie(Graphics gr, int x, int y, int size, PieBuckets pie, Font font, SolidBrush textBrush)
        {
            int total = pie.Total;
            if (total <= 0) return;

            var rect = new Rectangle(x, y, size, size);

            var winColor = Color.FromArgb(0, 200, 100);
            var lossColor = Color.FromArgb(220, 60, 60);
            var beColor = Color.FromArgb(230, 200, 60);

            float winPct = pie.Wins / (float)total;
            float lossPct = pie.Losses / (float)total;
            float bePct = pie.Breakevens / (float)total;

            float startAngle = -90f;
            float winSweep = winPct * 360f;
            float lossSweep = lossPct * 360f;
            float beSweep = bePct * 360f;

            var sfCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            using (var winBrush = new SolidBrush(winColor))
            using (var lossBrush = new SolidBrush(lossColor))
            using (var beBrush = new SolidBrush(beColor))
            {
                float angle = startAngle;

                if (pie.Wins > 0)
                {
                    gr.FillPie(winBrush, rect, angle, winSweep);
                    DrawPieLabel(gr, rect, angle, winSweep, $"{(winPct * 100):0.0}%", pie.Wins.ToString(), font, textBrush, sfCenter);
                    angle += winSweep;
                }
                if (pie.Losses > 0)
                {
                    gr.FillPie(lossBrush, rect, angle, lossSweep);
                    DrawPieLabel(gr, rect, angle, lossSweep, $"{(lossPct * 100):0.0}%", pie.Losses.ToString(), font, textBrush, sfCenter);
                    angle += lossSweep;
                }
                if (pie.Breakevens > 0)
                {
                    gr.FillPie(beBrush, rect, angle, beSweep);
                    DrawPieLabel(gr, rect, angle, beSweep, $"{(bePct * 100):0.0}%", null, font, textBrush, sfCenter);
                }
            }

            sfCenter.Dispose();
        }

        // Places a percentage label (and optional trade-count line beneath it) at the
        // midpoint radius/angle of a pie slice.
        private void DrawPieLabel(Graphics gr, Rectangle pieRect, float startAngle, float sweep, string text, string countText,
            Font font, SolidBrush brush, StringFormat sf)
        {
            if (sweep < 12f) return; // slice too thin to label cleanly

            double midAngleRad = (startAngle + sweep / 2.0) * Math.PI / 180.0;
            double cx = pieRect.X + pieRect.Width / 2.0;
            double cy = pieRect.Y + pieRect.Height / 2.0;
            double r = pieRect.Width / 2.0 * 0.6; // place label at 60% of radius

            double lx = cx + r * Math.Cos(midAngleRad);
            double ly = cy + r * Math.Sin(midAngleRad);

            if (string.IsNullOrEmpty(countText))
            {
                var labelRect = new Rectangle((int)lx - 20, (int)ly - 8, 40, 16);
                gr.DrawString(text, font, brush, labelRect, sf);
            }
            else
            {
                // Percentage on top, raw trade count directly beneath it, no extra label text
                var pctRect = new Rectangle((int)lx - 24, (int)ly - 16, 48, 16);
                var countRect = new Rectangle((int)lx - 24, (int)ly, 48, 14);
                gr.DrawString(text, font, brush, pctRect, sf);
                gr.DrawString(countText, font, brush, countRect, sf);
            }
        }

        private void Draw(Graphics gr)
        {
            gr.Clear(Color.FromArgb(37, 37, 37));
            _dayCells.Clear();

            // Read live settings from plugin each frame so changes take effect immediately
            int cellW = _plugin.CellW;
            int cellH = _plugin.CellH;
            int nextX = NextBtnX;

            // Scale fonts proportionally to cell size.
            // At the minimum size (cellW=44, cellH=52) we use the original sizes.
            // Scale factor is clamped to a smooth range.
            float scale = Math.Min(cellW / 44f, cellH / 52f);
            float fDay = Math.Max(6f, 8f * scale);
            float fPnl = Math.Max(5f, 7f * scale);
            float fCount = Math.Max(5f, 6f * scale);
            float fHdr = Math.Max(7f, 9f * scale);
            float fNames = Math.Max(5f, 7f * scale);
            float fArrow = Math.Max(10f, 14f * scale);

            var noteDates = _plugin.GetNoteDates();

            int month = _plugin.CurrentMonth;
            int year = _plugin.CurrentYear;
            string selected = _plugin.SelectedDate;

            // --- Fonts ---
            var fontDay = new Font("Arial", fDay, FontStyle.Bold);
            var fontPnl = new Font("Arial", fPnl, FontStyle.Bold);
            var fontCount = new Font("Arial", fCount, FontStyle.Regular);
            var fontHdr = new Font("Arial", fHdr, FontStyle.Bold);
            var fontNames = new Font("Arial", fNames, FontStyle.Regular);
            var fontArrow = new Font("Arial", fArrow, FontStyle.Bold);

            // --- Brushes / Pens ---
            var whiteBrush = new SolidBrush(Color.White);
            var grayBrush = new SolidBrush(Color.FromArgb(120, 120, 120));
            var lightGray = new SolidBrush(Color.FromArgb(170, 170, 170));
            var dimBrush = new SolidBrush(Color.FromArgb(75, 75, 75));
            var todayBrush = new SolidBrush(Color.FromArgb(42, 74, 107));
            var selectedBrush = new SolidBrush(Color.FromArgb(26, 107, 58));
            var greenBrush = new SolidBrush(Color.FromArgb(0, 200, 100));
            var redBrush = new SolidBrush(Color.FromArgb(220, 60, 60));
            var notePen = new Pen(Color.FromArgb(0, 200, 100), 2);
            var arrowBrush = new SolidBrush(Color.FromArgb(180, 180, 180));

            var sfCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var sfTopCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };

            string[] monthNames = { "January","February","March","April","May","June",
                "July","August","September","October","November","December" };

            // --- Nav arrows ---
            _prevBtnRect = new Rectangle(PrevBtnX, HeaderY, 24, HeaderH);
            _nextBtnRect = new Rectangle(nextX, HeaderY, 24, HeaderH);

            gr.DrawString("‹", fontArrow, arrowBrush, _prevBtnRect, sfCenter);
            gr.DrawString("›", fontArrow, arrowBrush, _nextBtnRect, sfCenter);

            var headerRect = new Rectangle(36, HeaderY, nextX - 36, HeaderH);
            _headerRect = headerRect; // stored so OnMouseClick can hit-test it
            gr.DrawString($"{monthNames[month]} {year}", fontHdr, whiteBrush, headerRect, sfCenter);

            // --- Day-name row ---
            string[] dayNames = { "Mo", "Tu", "We", "Th", "Fr" };
            for (int i = 0; i < 5; i++)
            {
                var r = new Rectangle(GridStartX + i * cellW, DayNamesY, cellW, DayNamesH);
                gr.DrawString(dayNames[i], fontNames, grayBrush, r, sfCenter);
            }

            // --- Day cells (Mon–Fri only), padded with leading/trailing days from
            // the adjacent months so every row is fully filled (no blank cells). ---
            int daysInMonth = DateTime.DaysInMonth(year, month + 1);
            var firstOfMonth = new DateTime(year, month + 1, 1);
            var lastOfMonth = new DateTime(year, month + 1, daysInMonth);
            var today = DateTime.Today;

            // Monday of the week containing a given date (weeks start Monday).
            DateTime WeekMonday(DateTime dt) => dt.AddDays(-(((int)dt.DayOfWeek + 6) % 7));

            // Grid start: only pull in the previous month's week if the 1st falls mid-week
            // (Tue-Fri). If the 1st is a Sat/Sun, the month cleanly starts the next Monday
            // with no gap, so there's nothing to pad and no blank row to show.
            DateTime gridStart;
            if (firstOfMonth.DayOfWeek == DayOfWeek.Saturday) gridStart = firstOfMonth.AddDays(2);
            else if (firstOfMonth.DayOfWeek == DayOfWeek.Sunday) gridStart = firstOfMonth.AddDays(1);
            else gridStart = WeekMonday(firstOfMonth);

            // Grid end: mirror of the above for the trailing edge — only pull in the next
            // month's week if the last day falls mid-week (Mon-Thu).
            DateTime gridEnd;
            if (lastOfMonth.DayOfWeek == DayOfWeek.Saturday) gridEnd = lastOfMonth.AddDays(-1);
            else if (lastOfMonth.DayOfWeek == DayOfWeek.Sunday) gridEnd = lastOfMonth.AddDays(-2);
            else gridEnd = WeekMonday(lastOfMonth).AddDays(4);

            int totalWeekRows = ((gridEnd - gridStart).Days / 7) + 1;

            // Trade stats are cached per (year, month) on the plugin side; keep a small
            // local lookup here too so a render pass that spans 2-3 months doesn't repeat
            // dictionary lookups per day.
            var statsByMonth = new Dictionary<(int Year, int Month), Dictionary<string, DayStats>>();
            Dictionary<string, DayStats> StatsFor(DateTime d)
            {
                var key = (d.Year, d.Month);
                if (!statsByMonth.TryGetValue(key, out var monthStats))
                {
                    monthStats = _plugin.GetTradeStatsForMonth(d.Year, d.Month);
                    statsByMonth[key] = monthStats;
                }
                return monthStats;
            }

            // Row offsets scale with cellH so content stays proportionally placed
            int pnlOffsetY = (int)(cellH * 0.35f);  // ~18px at cellH=52
            int countOffsetY = (int)(cellH * 0.63f);  // ~33px at cellH=52
            int dayNumH = (int)(cellH * 0.27f);  // ~14px at cellH=52

            for (var date = gridStart; date <= gridEnd; date = date.AddDays(1))
            {
                var dow = date.DayOfWeek;

                if (dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday)
                    continue;

                int weekRow = (date - gridStart).Days / 7;
                int col = (int)dow - 1; // Mon=0 … Fri=4
                bool inCurrentMonth = date.Month == month + 1 && date.Year == year;

                var cellRect = new Rectangle(
                    GridStartX + col * cellW,
                    GridStartY + weekRow * cellH,
                    cellW - 2,
                    cellH - 2);

                string dateStr = date.ToString("yyyy-MM-dd");
                bool isToday = date.Date == today;
                bool isSelected = dateStr == selected;
                bool hasNote = noteDates.Contains(dateStr);

                if (isSelected)
                    gr.FillRectangle(selectedBrush, cellRect);
                else if (isToday)
                    gr.FillRectangle(todayBrush, cellRect);

                // Day number — days from the adjacent month render dimmed
                var dayNumRect = new Rectangle(cellRect.X, cellRect.Y + 2, cellRect.Width, dayNumH);
                var dayBrush = isSelected || isToday ? whiteBrush : (inCurrentMonth ? lightGray : dimBrush);
                gr.DrawString(date.Day.ToString(),
                    isSelected || isToday ? fontDay : fontNames,
                    dayBrush,
                    dayNumRect, sfTopCenter);

                // Trade stats — shown for every visible day, including the dimmed
                // leading/trailing days pulled in from adjacent months.
                if (StatsFor(date).TryGetValue(dateStr, out DayStats stats) && stats.HasData)
                {
                    var pnlBrush = stats.PnL >= 0 ? greenBrush : redBrush;
                    var pnlRect = new Rectangle(cellRect.X, cellRect.Y + pnlOffsetY, cellRect.Width, dayNumH);
                    gr.DrawString(FormatPnl(stats.PnL), fontPnl, pnlBrush, pnlRect, sfTopCenter);

                    var cntRect = new Rectangle(cellRect.X, cellRect.Y + countOffsetY, cellRect.Width, dayNumH);
                    gr.DrawString(stats.RoundTrips.ToString(), fontCount, grayBrush, cntRect, sfTopCenter);
                }

                if (hasNote)
                    gr.DrawLine(notePen, cellRect.X + 4, cellRect.Bottom, cellRect.Right - 4, cellRect.Bottom);

                _dayCells.Add((cellRect, dateStr));
            }

            // --- Metrics panel: Monthly or Daily depending on toggle ---
            int panelY = GridStartY + totalWeekRows * cellH + 12;
            if (_showMonthlyMetrics)
                DrawMonthlyMetricsPanel(gr, panelY, month + 1, year, whiteBrush, grayBrush, lightGray,
                    greenBrush, redBrush, fontHdr, fontNames, fontCount);
            else
                DrawDailyMetricsPanel(gr, panelY, selected, whiteBrush, grayBrush, lightGray,
                    greenBrush, redBrush, fontHdr, fontNames, fontCount);

            // --- Dispose ---
            fontDay.Dispose(); fontPnl.Dispose(); fontCount.Dispose();
            fontHdr.Dispose(); fontNames.Dispose(); fontArrow.Dispose();
            whiteBrush.Dispose(); grayBrush.Dispose(); lightGray.Dispose(); dimBrush.Dispose();
            todayBrush.Dispose(); selectedBrush.Dispose();
            greenBrush.Dispose(); redBrush.Dispose();
            notePen.Dispose(); arrowBrush.Dispose();
            sfCenter.Dispose(); sfTopCenter.Dispose();
        }

        public override IntPtr Render() => _bufferedGraphic.CurrentImage;

        public override void OnResize()
        {
            base.OnResize();
            var bounds = Bounds;
            if (bounds.Width == 0 || bounds.Height == 0) return;
            try
            {
                _bufferedGraphic.Resize(bounds.Width, bounds.Height);
                _bufferedGraphic.IsDirty = true;
            }
            catch { }
        }

        public override void Dispose()
        {
            NativeControl.MouseClickNative -= OnMouseClick;
            if (_bufferedGraphic != null)
            {
                _bufferedGraphic.Dispose();
                _bufferedGraphic = null;
            }
            base.Dispose();
        }
    }
}