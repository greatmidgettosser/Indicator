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

    // A single closed round trip, used by the trade-list panel (as opposed to
    // SideMetrics, which only keeps aggregated stats). Entry/exit prices are
    // quantity-weighted averages so scaled-in/scaled-out positions show one
    // sensible number rather than the price of just the first or last fill.
    public struct RoundTripTrade
    {
        public string Symbol;
        public bool IsLong;
        public DateTime EntryTime;
        public DateTime ExitTime;
        public double AvgEntryPrice; // NaN if the underlying fills had no price data (e.g. some archive rows)
        public double AvgExitPrice;
        public double Pnl;
        public double Quantity; // contract size of the position (entry-side total, which equals the closed size)
        public string DayKey; // yyyy-MM-dd this trade's exit falls on

        // Stable identity for note storage/lookup: a symbol can only close one
        // round trip at any given instant, so symbol+exit-time is unique.
        public string TradeKey => $"{Symbol}|{ExitTime:yyyy-MM-ddTHH:mm:ss.fff}";
    }

    // Result of a day/week/month trade-list query: trades grouped by day (each
    // group and the trades within it are in chronological order), plus how many
    // older trades were dropped by the 300-trade cap, if any (0 = nothing dropped).
    public struct TradeListResult
    {
        public List<(string DayKey, List<RoundTripTrade> Trades)> Days;
        public int TruncatedCount;
    }

    public struct DayMetrics
    {
        public SideMetrics Long;
        public SideMetrics Short;
        public SideMetrics All; // combined long+short, used by the "All Trades" pie-toggle view
        public PieBuckets Pie;
        public bool HasData;
        public List<string> Symbols; // distinct root symbols traded, regardless of any active filter
        public List<RoundTripTrade> Trades; // individual round trips, chronological, for the trade-list panel
    }

    // Aggregated metrics for an entire month (all trading days combined)
    public struct MonthMetrics
    {
        public SideMetrics Long;
        public SideMetrics Short;
        public SideMetrics All; // combined long+short, used by the "All Trades" pie-toggle view
        public PieBuckets Pie;
        public double TotalPnL;
        public bool HasData;
        public List<string> Symbols; // distinct root symbols traded, regardless of any active filter
    }

    // Aggregated metrics for a calendar week (Mon–Fri), which may span two months
    public struct WeekMetrics
    {
        public SideMetrics Long;
        public SideMetrics Short;
        public SideMetrics All; // combined long+short, used by the "All Trades" pie-toggle view
        public PieBuckets Pie;
        public double TotalPnL;
        public DateTime WeekStart; // Monday of the week
        public DateTime WeekEnd;   // Friday of the week
        public bool HasData;
        public List<string> Symbols; // distinct root symbols traded, regardless of any active filter
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
        public double Price;       // raw contract fill price (not dollar value) — used only for
                                   // computing weighted avg entry/exit price on the trade list;
                                   // NaN if unavailable (e.g. an archive row with no price column)
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
        private string _lastLoadedHtml = null; // the htmlContent string last pushed into the div

        // Trade-list panel (bottom half of the notes column): a flat, non-interactive
        // table, rebuilt from scratch on every view change or new fill.
        private bool _tradeListReady = false;

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
        private string _microSymbols = "MES, MNQ, M2K";
        private string _miniSymbols = "ES, NQ, RTY";

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
            _calRenderer.OnWeekViewSelected += OnWeekViewSelected;
            _calRenderer.OnMonthViewSelected += OnMonthViewSelected;
            _calRenderer.OnSymbolFilterChanged += RenderTradeList;

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
                // Write sentinel into the contenteditable div using SetInnerHtml —
                // same path LoadNote uses, so this verifies the exact same mechanism.
                this.Window.Browser.UpdateHtml("noteArea", HtmlAction.SetInnerHtml, DomReadySentinel);

                // Read it back via GetProperty innerHTML — the same path SaveNoteFromBrowser uses.
                var response = this.Window.Browser.GetHtmlValue("noteArea", HtmlGetValueAction.GetProperty, "innerHTML");

                if (response?.Result is string actual && actual.Trim() == DomReadySentinel)
                {
                    _browserReady = true;
                    LoadNote(date);
                    InitializeTradeList();
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
                InitializeTradeList();
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
                { Text = "Fee Per Micro Contract (Round Trip)", SortIndex = 5, Increment = 0.01, DecimalPlaces = 2 });
                result.Add(new SettingItemDouble("FeePerMini", _feePerMini)
                { Text = "Fee Per Mini Contract (Round Trip)", SortIndex = 6, Increment = 0.01, DecimalPlaces = 2 });
                result.Add(new SettingItemString("MicroSymbols", _microSymbols)
                { Text = "Micro Symbols (comma-separated)", SortIndex = 7 });
                result.Add(new SettingItemString("MiniSymbols", _miniSymbols)
                { Text = "Mini Symbols (comma-separated)", SortIndex = 8 });
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
                        case "MicroSymbols":
                            _microSymbols = (string)item.Value;
                            if (_calculateFees) InvalidateStatsCache();
                            break;
                        case "MiniSymbols":
                            _miniSymbols = (string)item.Value;
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
                string weekKey = TradeJournalPlugin.GetWeekMonday(tradeDate).ToString("yyyy-MM-dd");
                int tYear = tradeDate.Year, tMonth = tradeDate.Month;

                // Cache keys now include a symbol-filter component; remove every filtered
                // variant for the affected date/month/week, not just the "all symbols" entry.
                foreach (var key in _dayMetricsCache.Keys.Where(k => k.Date == dateKey).ToList())
                    _dayMetricsCache.Remove(key);
                foreach (var key in _monthMetricsCache.Keys.Where(k => k.Year == tYear && k.Month == tMonth).ToList())
                    _monthMetricsCache.Remove(key);
                foreach (var key in _weekMetricsCache.Keys.Where(k => k.WeekMonday == weekKey).ToList())
                    _weekMetricsCache.Remove(key);

                _calRenderer?.Redraw();
                RenderTradeList(); // keep the trade list panel in sync with new fills, without
                                   // resetting expand/open-note state the way a view change would
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
            foreach (var key in _dayMetricsCache.Keys.Where(k => k.Date == date).ToList())
                _dayMetricsCache.Remove(key);
            LoadNote(_selectedDate);
            RenderTradeList();
        }

        private void OnWeekViewSelected(string date)
        {
            RenderTradeList();
        }

        private void OnMonthViewSelected()
        {
            RenderTradeList();
        }

        private void OnPrevMonth()
        {
            SaveNoteFromBrowser(_selectedDate);
            _currentMonth--;
            if (_currentMonth < 0) { _currentMonth = 11; _currentYear--; }
            _selectedDate = $"{_currentYear}-{(_currentMonth + 1):D2}-01";
            InvalidateStatsCache();
            LoadNote(_selectedDate);
            RenderTradeList();
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
            RenderTradeList();
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
            if (!_loadedDates.Contains(date)) return;

            try
            {
                var response = this.Window.Browser.GetHtmlValue(
                    "noteArea", HtmlGetValueAction.GetProperty, "innerHTML");

                if (response?.Result is string raw)
                {
                    // If the browser returns exactly what we last pushed in via SetInnerHtml,
                    // the div hasn't been touched by the user yet — but that's fine, we still
                    // save it (navigation save). The only case we block is when the read-back
                    // is empty string but the file has content, meaning SetInnerHtml didn't
                    // land yet and we'd be overwriting a real note with nothing.
                    if (raw.Length == 0 && _lastLoadedHtml != null && _lastLoadedHtml.Length > 0)
                        return;

                    string content = raw
                        .Replace("<br>", "\n")
                        .Replace("<br/>", "\n")
                        .Replace("<BR>", "\n")
                        .Replace("</div><div>", "\n")
                        .Replace("<div>", "\n")
                        .Replace("</div>", "")
                        .Replace("&amp;", "&")
                        .Replace("&lt;", "<")
                        .Replace("&gt;", ">")
                        .Replace("&nbsp;", " ");
                    content = System.Text.RegularExpressions.Regex.Replace(content, "<[^>]+>", "");
                    content = content.TrimEnd('\n', '\r');

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
            if (string.IsNullOrWhiteSpace(content))
            {
                // Remove empty files so the folder stays clean and GetNoteDates()
                // doesn't show a note indicator for days with no actual content.
                if (File.Exists(path))
                    File.Delete(path);
            }
            else
            {
                File.WriteAllText(path, content);
            }
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

                string htmlContent = content
                    .Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;");

                this.Window.Browser.UpdateHtml("selectedDateLabel", HtmlAction.SetTextContent, label);
                this.Window.Browser.UpdateHtml("noteArea", HtmlAction.SetInnerHtml, htmlContent);
                this.Window.Browser.UpdateHtml("saveIndicator", HtmlAction.SetTextContent, "");
                this.Window.Browser.UpdateHtml("saveIndicator", HtmlAction.SetClass, "save-indicator");

                _lastLoadedHtml = htmlContent;
                _loadedDates.Add(date);
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] Load error: {ex.Message}");
            }
        }

        // --- Trade-list panel: flat, non-interactive table ---
        // No per-day grouping/collapsing and no click handling — the browser bridge's
        // click support turned out to be unreliable for plain (non-form) elements, and
        // a flat table with a Date column serves the same purpose more simply anyway.

        private void InitializeTradeList()
        {
            _tradeListReady = true;
            RenderTradeList();
        }

        // Rebuilds the whole trade table from scratch — cheap enough to just redo
        // entirely rather than diff, since it's one UpdateHtml call either way.
        private void RenderTradeList()
        {
            if (!_tradeListReady) return;

            try
            {
                bool isMonthly = _calRenderer?.IsMonthlyView ?? false;
                bool isWeekly = _calRenderer?.IsWeeklyView ?? false;
                string symbolFilter = _calRenderer?.SelectedSymbolFilter;

                TradeListResult listResult;
                if (isMonthly)
                    listResult = GetTradesForMonth(_currentYear, _currentMonth + 1, symbolFilter);
                else if (isWeekly && _calRenderer.WeeklyViewDate != null)
                    listResult = GetTradesForWeek(_calRenderer.WeeklyViewDate, symbolFilter);
                else
                    listResult = GetTradesForDay(_selectedDate, symbolFilter);

                var allTrades = listResult.Days.SelectMany(d => d.Trades).OrderByDescending(t => t.ExitTime).ToList();
                bool showDaySeparators = isMonthly || isWeekly; // pointless in daily view — only one day shown

                var sb = new StringBuilder();
                string lastDayKey = null;

                foreach (var t in allTrades)
                {
                    if (showDaySeparators && t.DayKey != lastDayKey)
                    {
                        DateTime.TryParse(t.DayKey, out DateTime sepDate);
                        string sepLabel = sepDate != default ? sepDate.ToString("dddd, MMM d") : t.DayKey;
                        sb.Append($"<tr class=\"tl-day-sep\"><td colspan=\"10\">{sepLabel}</td></tr>");
                        lastDayKey = t.DayKey;
                    }

                    string sideClass = t.IsLong ? "tl-side-long" : "tl-side-short";
                    string sideText = t.IsLong ? "Long" : "Short";
                    string pnlClass = t.Pnl > 0 ? "tl-pnl-win" : t.Pnl < 0 ? "tl-pnl-loss" : "";
                    string entryPrice = double.IsNaN(t.AvgEntryPrice) ? "\u2014" : t.AvgEntryPrice.ToString("#,##0.##");
                    string exitPrice = double.IsNaN(t.AvgExitPrice) ? "\u2014" : t.AvgExitPrice.ToString("#,##0.##");
                    string holdTime = FormatHoldTime(t.ExitTime - t.EntryTime);

                    sb.Append("<tr>");
                    sb.Append($"<td>{t.ExitTime:MMM d}</td>");
                    sb.Append($"<td>{t.EntryTime:HH:mm:ss}</td>");
                    sb.Append($"<td>{t.ExitTime:HH:mm:ss}</td>");
                    sb.Append($"<td>{GetSymbolRoot(t.Symbol)}</td>");
                    sb.Append($"<td>{t.Quantity:0.#}</td>");
                    sb.Append($"<td class=\"{sideClass}\">{sideText}</td>");
                    sb.Append($"<td>{entryPrice}</td>");
                    sb.Append($"<td>{exitPrice}</td>");
                    sb.Append($"<td>{holdTime}</td>");
                    sb.Append($"<td class=\"{pnlClass}\">{FormatPnlCompact(t.Pnl)}</td>");
                    sb.Append("</tr>");
                }

                this.Window.Browser.UpdateHtml("tlTableBody", HtmlAction.SetInnerHtml, sb.ToString());

                if (listResult.TruncatedCount > 0)
                {
                    this.Window.Browser.UpdateHtml("tlTruncationNotice", HtmlAction.SetTextContent,
                        $"+{listResult.TruncatedCount} earlier trade{(listResult.TruncatedCount == 1 ? "" : "s")} not shown");
                    this.Window.Browser.UpdateHtml("tlTruncationNotice", HtmlAction.SetClass, "tl-truncation");
                }
                else
                {
                    this.Window.Browser.UpdateHtml("tlTruncationNotice", HtmlAction.SetClass, "tl-truncation tl-hidden");
                }

                this.Window.Browser.UpdateHtml("tlEmptyState", HtmlAction.SetClass,
                    allTrades.Count == 0 ? "tl-empty" : "tl-empty tl-hidden");
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] RenderTradeList error: {ex.Message}");
            }
        }

        // Compact hold-time formatter for the trade list (e.g. "42s", "3m 12s", "1h 05m").
        private static string FormatHoldTime(TimeSpan ts)
        {
            if (ts.TotalSeconds < 0) ts = TimeSpan.Zero; // defensive — shouldn't happen
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}h {ts.Minutes:00}m";
            if (ts.TotalMinutes >= 1)
                return $"{(int)ts.TotalMinutes}m {ts.Seconds:00}s";
            return $"{(int)ts.TotalSeconds}s";
        }

        // Compact PnL formatter, same convention as the calendar renderer's own
        // (private, so duplicated here rather than shared across classes).
        private static string FormatPnlCompact(double pnl)
        {
            string sign = pnl < 0 ? "-" : "+";
            double abs = Math.Abs(pnl);
            return abs >= 1_000 ? $"{sign}${abs / 1000.0:0.##}k" : $"{sign}${abs:0.##}";
        }



        // --- Trade statistics ---

        private void InvalidateStatsCache()
        {
            _monthStatsCache.Clear();
            _dayMetricsCache.Clear();
            _monthMetricsCache.Clear();
            _weekMetricsCache.Clear();
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
                FillValue = fillValue,
                Price = trade.Price
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

        // Per-day long/short breakdown for the metrics panel. Cached per (date, symbol
        // filter) since OnDaySelected/Prev/NextMonth already drive when this needs to
        // refresh. An empty string for Symbol means "all symbols" (no filter).
        private Dictionary<(string Date, string Symbol), DayMetrics> _dayMetricsCache
            = new Dictionary<(string Date, string Symbol), DayMetrics>();

        // Per-(year,month,symbol) aggregate metrics, invalidated alongside the monthly stats cache.
        private Dictionary<(int Year, int Month, string Symbol), MonthMetrics> _monthMetricsCache
            = new Dictionary<(int Year, int Month, string Symbol), MonthMetrics>();

        // Per-(week,symbol) aggregate metrics keyed by the Monday of that week (yyyy-MM-dd)
        private Dictionary<(string WeekMonday, string Symbol), WeekMetrics> _weekMetricsCache
            = new Dictionary<(string WeekMonday, string Symbol), WeekMetrics>();

        // Extracts the root ticker from a full contract symbol by stripping a trailing
        // "<month code><2-digit year>" suffix, e.g. "MNQU26" -> "MNQ", "M2KZ25" -> "M2K".
        // Symbols with no expiration suffix (or that don't match the pattern) pass through
        // unchanged. This is what's shown in the "Traded Symbols" filter list, matching
        // how symbols are typed in the Micro/Mini Symbols settings.
        private static readonly char[] ContractMonthCodes =
            { 'F', 'G', 'H', 'J', 'K', 'M', 'N', 'Q', 'U', 'V', 'X', 'Z' };

        public static string GetSymbolRoot(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return symbol ?? string.Empty;
            if (symbol.Length >= 3)
            {
                char monthChar = symbol[symbol.Length - 3];
                string yearPart = symbol.Substring(symbol.Length - 2);
                if (Array.IndexOf(ContractMonthCodes, monthChar) >= 0 && yearPart.All(char.IsDigit))
                    return symbol.Substring(0, symbol.Length - 3);
            }
            return symbol;
        }

        public DayMetrics GetDayMetrics(string date, string symbolFilter = null)
        {
            var cacheKey = (date, symbolFilter ?? string.Empty);
            if (_dayMetricsCache.TryGetValue(cacheKey, out DayMetrics cached))
                return cached;

            var metrics = new DayMetrics
            {
                Long = new SideMetrics { LargestLoss = 0 },
                Short = new SideMetrics { LargestLoss = 0 },
                All = new SideMetrics { LargestLoss = 0 },
                Pie = new PieBuckets(),
                Symbols = new List<string>(),
                Trades = new List<RoundTripTrade>()
            };

            try
            {
                if (DateTime.TryParse(date, out DateTime dayDate))
                {
                    var dayFills = GetFillsForDay(dayDate);
                    metrics = AggregateDayMetrics(dayFills, symbolFilter);
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] GetDayMetrics error: {ex.Message}");
            }

            _dayMetricsCache[cacheKey] = metrics;
            return metrics;
        }

        // Aggregates all round trips for the entire month into a single MonthMetrics.
        // Uses the same fill data and FIFO logic as the per-day path so numbers always match.
        public MonthMetrics GetMonthMetrics(int year, int month, string symbolFilter = null)
        {
            var cacheKey = (year, month, symbolFilter ?? string.Empty);
            if (_monthMetricsCache.TryGetValue(cacheKey, out MonthMetrics cached))
                return cached;

            var result = new MonthMetrics
            {
                Long = new SideMetrics { LargestLoss = 0 },
                Short = new SideMetrics { LargestLoss = 0 },
                All = new SideMetrics { LargestLoss = 0 },
                Pie = new PieBuckets()
            };

            var symbolSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // Accumulate day-by-day so each day's fills are processed in isolation
                // (FIFO positions don't carry across midnight), matching DayMetrics logic.
                int daysInMonth = DateTime.DaysInMonth(year, month);
                var longM = result.Long;
                var shortM = result.Short;
                var allM = result.All;
                var pie = result.Pie;

                for (int day = 1; day <= daysInMonth; day++)
                {
                    var date = new DateTime(year, month, day);
                    if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
                        continue;

                    var dayFills = GetFillsForDay(date);
                    if (dayFills.Count == 0) continue;

                    var dm = AggregateDayMetrics(dayFills, symbolFilter);
                    foreach (var sym in dm.Symbols) symbolSet.Add(sym);
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

                    // Merge All (combined long+short) side, same day-by-day pattern as above
                    var am = dm.All;
                    allM.RoundTrips += am.RoundTrips;
                    allM.Wins += am.Wins;
                    allM.WinCount += am.WinCount;
                    allM.LossCount += am.LossCount;
                    allM.TotalPnl += am.TotalPnl;
                    allM.TotalWinPnl += am.TotalWinPnl;
                    allM.TotalLossPnl += am.TotalLossPnl;
                    if (am.LargestWin > allM.LargestWin) allM.LargestWin = am.LargestWin;
                    if (am.LargestLoss < allM.LargestLoss) allM.LargestLoss = am.LargestLoss;
                    allM.TotalDurationSeconds += am.TotalDurationSeconds;
                    allM.DurationSampleCount += am.DurationSampleCount;
                    allM.TotalWinDurationSeconds += am.TotalWinDurationSeconds;
                    allM.WinDurationCount += am.WinDurationCount;
                    allM.TotalLossDurationSeconds += am.TotalLossDurationSeconds;
                    allM.LossDurationCount += am.LossDurationCount;
                    if (am.WinStreak > allM.WinStreak) allM.WinStreak = am.WinStreak;
                    if (am.LossStreak > allM.LossStreak) allM.LossStreak = am.LossStreak;
                    if (am.HasData) allM.HasData = true;

                    // Merge pie buckets
                    pie.Wins += dm.Pie.Wins;
                    pie.Losses += dm.Pie.Losses;
                    pie.Breakevens += dm.Pie.Breakevens;
                }

                result.Long = longM;
                result.Short = shortM;
                result.All = allM;
                result.Pie = pie;
                result.TotalPnL = longM.TotalPnl + shortM.TotalPnl;
                result.HasData = longM.HasData || shortM.HasData;
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] GetMonthMetrics error: {ex.Message}");
            }

            result.Symbols = symbolSet.ToList();
            _monthMetricsCache[cacheKey] = result;
            return result;
        }

        // Returns the Monday of the week containing the given date (weeks start Monday)
        public static DateTime GetWeekMonday(DateTime date)
            => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

        // Aggregates all round trips for the Mon–Fri week containing the given date.
        // The week can span two months; all trading days in the range are included.
        public WeekMetrics GetWeekMetrics(string dateStr, string symbolFilter = null)
        {
            if (!DateTime.TryParse(dateStr, out DateTime date))
                return new WeekMetrics();

            DateTime weekMonday = GetWeekMonday(date);
            var cacheKey = (weekMonday.ToString("yyyy-MM-dd"), symbolFilter ?? string.Empty);

            if (_weekMetricsCache.TryGetValue(cacheKey, out WeekMetrics cached))
                return cached;

            var result = new WeekMetrics
            {
                Long = new SideMetrics { LargestLoss = 0 },
                Short = new SideMetrics { LargestLoss = 0 },
                All = new SideMetrics { LargestLoss = 0 },
                Pie = new PieBuckets(),
                WeekStart = weekMonday,
                WeekEnd = weekMonday.AddDays(4)
            };

            var symbolSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var longM = result.Long;
                var shortM = result.Short;
                var allM = result.All;
                var pie = result.Pie;

                for (int d = 0; d < 5; d++) // Mon=0 … Fri=4
                {
                    DateTime day = weekMonday.AddDays(d);
                    var dayFills = GetFillsForDay(day);
                    if (dayFills.Count == 0) continue;

                    var dm = AggregateDayMetrics(dayFills, symbolFilter);
                    foreach (var sym in dm.Symbols) symbolSet.Add(sym);
                    if (!dm.HasData) continue;

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

                    // Merge All (combined long+short) side, same day-by-day pattern as above
                    var am = dm.All;
                    allM.RoundTrips += am.RoundTrips;
                    allM.Wins += am.Wins;
                    allM.WinCount += am.WinCount;
                    allM.LossCount += am.LossCount;
                    allM.TotalPnl += am.TotalPnl;
                    allM.TotalWinPnl += am.TotalWinPnl;
                    allM.TotalLossPnl += am.TotalLossPnl;
                    if (am.LargestWin > allM.LargestWin) allM.LargestWin = am.LargestWin;
                    if (am.LargestLoss < allM.LargestLoss) allM.LargestLoss = am.LargestLoss;
                    allM.TotalDurationSeconds += am.TotalDurationSeconds;
                    allM.DurationSampleCount += am.DurationSampleCount;
                    allM.TotalWinDurationSeconds += am.TotalWinDurationSeconds;
                    allM.WinDurationCount += am.WinDurationCount;
                    allM.TotalLossDurationSeconds += am.TotalLossDurationSeconds;
                    allM.LossDurationCount += am.LossDurationCount;
                    if (am.WinStreak > allM.WinStreak) allM.WinStreak = am.WinStreak;
                    if (am.LossStreak > allM.LossStreak) allM.LossStreak = am.LossStreak;
                    if (am.HasData) allM.HasData = true;

                    pie.Wins += dm.Pie.Wins;
                    pie.Losses += dm.Pie.Losses;
                    pie.Breakevens += dm.Pie.Breakevens;
                }

                result.Long = longM;
                result.Short = shortM;
                result.All = allM;
                result.Pie = pie;
                result.TotalPnL = longM.TotalPnl + shortM.TotalPnl;
                result.HasData = longM.HasData || shortM.HasData;
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] GetWeekMetrics error: {ex.Message}");
            }

            result.Symbols = symbolSet.ToList();
            _weekMetricsCache[cacheKey] = result;
            return result;
        }

        // --- Trade-list panel queries: individual round trips, grouped by day ---
        // Always shows every trade regardless of the pie chart's symbol filter — the
        // trade list is a separate, always-unfiltered view of the day/week/month.
        private const int MaxTradeListTrades = 300;

        public TradeListResult GetTradesForDay(string date, string symbolFilter = null)
        {
            var dm = GetDayMetrics(date, symbolFilter);
            var days = new List<(string DayKey, List<RoundTripTrade> Trades)>();
            if (dm.Trades.Count > 0)
                days.Add((date, dm.Trades.OrderBy(t => t.ExitTime).ToList()));
            return new TradeListResult { Days = days, TruncatedCount = 0 };
        }

        public TradeListResult GetTradesForWeek(string dateStr, string symbolFilter = null)
        {
            var days = new List<(string DayKey, List<RoundTripTrade> Trades)>();
            if (!DateTime.TryParse(dateStr, out DateTime date))
                return new TradeListResult { Days = days, TruncatedCount = 0 };

            DateTime weekMonday = GetWeekMonday(date);
            for (int d = 0; d < 5; d++) // Mon=0 … Fri=4
            {
                DateTime day = weekMonday.AddDays(d);
                string dayKey = day.ToString("yyyy-MM-dd");
                var dm = GetDayMetrics(dayKey, symbolFilter);
                if (dm.Trades.Count > 0)
                    days.Add((dayKey, dm.Trades.OrderBy(t => t.ExitTime).ToList()));
            }
            return CapTradeListToMostRecent(days, MaxTradeListTrades);
        }

        public TradeListResult GetTradesForMonth(int year, int month, string symbolFilter = null)
        {
            var days = new List<(string DayKey, List<RoundTripTrade> Trades)>();
            int daysInMonth = DateTime.DaysInMonth(year, month);
            for (int day = 1; day <= daysInMonth; day++)
            {
                var date = new DateTime(year, month, day);
                if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
                    continue;

                string dayKey = date.ToString("yyyy-MM-dd");
                var dm = GetDayMetrics(dayKey, symbolFilter);
                if (dm.Trades.Count > 0)
                    days.Add((dayKey, dm.Trades.OrderBy(t => t.ExitTime).ToList()));
            }
            return CapTradeListToMostRecent(days, MaxTradeListTrades);
        }

        // Keeps only the most recent `cap` trades across the whole day-grouped list,
        // dropping from the earliest days first. A day left with zero remaining
        // trades after trimming is omitted entirely (no empty header shown for it).
        private TradeListResult CapTradeListToMostRecent(List<(string DayKey, List<RoundTripTrade> Trades)> days, int cap)
        {
            int total = days.Sum(d => d.Trades.Count);
            if (total <= cap)
                return new TradeListResult { Days = days, TruncatedCount = 0 };

            int toDrop = total - cap;
            int dropped = 0;
            var result = new List<(string DayKey, List<RoundTripTrade> Trades)>();

            foreach (var (dayKey, trades) in days)
            {
                if (dropped >= toDrop)
                {
                    result.Add((dayKey, trades));
                    continue;
                }

                int remainingToDrop = toDrop - dropped;
                if (trades.Count <= remainingToDrop)
                {
                    dropped += trades.Count; // whole day dropped, header omitted
                }
                else
                {
                    dropped += remainingToDrop;
                    result.Add((dayKey, trades.Skip(remainingToDrop).ToList()));
                }
            }

            return new TradeListResult { Days = result, TruncatedCount = toDrop };
        }

        // (long/short breakdowns, win rate, streaks, hold times, pie chart). Operates
        // on FillRecord so live-platform fills and archive-CSV fills are processed
        // through identical math.
        private DayMetrics AggregateDayMetrics(List<FillRecord> dayFills, string symbolFilter = null)
        {
            var metrics = new DayMetrics
            {
                Long = new SideMetrics { LargestLoss = 0 },
                Short = new SideMetrics { LargestLoss = 0 },
                All = new SideMetrics { LargestLoss = 0 },
                Pie = new PieBuckets(),
                Trades = new List<RoundTripTrade>()
            };

            // The traded-symbols list always reflects every symbol seen this day,
            // regardless of any active filter, so the filter buttons never disappear.
            var symbolSet = new SortedSet<string>(
                dayFills.Select(f => GetSymbolRoot(f.Symbol)), StringComparer.OrdinalIgnoreCase);
            metrics.Symbols = symbolSet.ToList();

            var fillsToProcess = string.IsNullOrEmpty(symbolFilter)
                ? dayFills
                : dayFills.Where(f => string.Equals(GetSymbolRoot(f.Symbol), symbolFilter, StringComparison.OrdinalIgnoreCase)).ToList();

            // FIFO state per symbol: running net qty, accumulated value, and open-fill timestamp
            var netQty = new Dictionary<string, double>();
            var netValue = new Dictionary<string, double>();
            var entryQty = new Dictionary<string, double>(); // running round-trip contract count (entry side)
            var openTime = new Dictionary<string, DateTime>(); // timestamp of the first fill that opened the position

            // Quantity-weighted price accumulators, used only to compute the avg entry/exit
            // price shown on the trade-list panel. Kept separate from netValue/entryQty above
            // (which are in FillValue dollar terms) since these need the raw contract price.
            var entryPriceQtySum = new Dictionary<string, double>(); // sum of Price*qty over entry fills
            var exitPriceQtySum = new Dictionary<string, double>();  // sum of Price*qty over closing fills
            var exitQty = new Dictionary<string, double>();          // running closing-side contract count

            var longM = metrics.Long;
            var shortM = metrics.Short;
            var allM = metrics.All;
            var pie = metrics.Pie;
            int longStreakWin = 0, longStreakLoss = 0;
            int shortStreakWin = 0, shortStreakLoss = 0;
            int allStreakWin = 0, allStreakLoss = 0;

            foreach (var fill in fillsToProcess.OrderBy(f => f.DateTime))
            {
                string symbol = fill.Symbol;

                if (!netQty.ContainsKey(symbol))
                {
                    netQty[symbol] = 0;
                    netValue[symbol] = 0;
                    entryQty[symbol] = 0;
                    entryPriceQtySum[symbol] = 0;
                    exitPriceQtySum[symbol] = 0;
                    exitQty[symbol] = 0;
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
                double fillQtyAbs = Math.Abs(signedQty);

                if (isEntryFill)
                {
                    entryQty[symbol] += fillQtyAbs;
                    if (!double.IsNaN(fill.Price))
                        entryPriceQtySum[symbol] += fill.Price * fillQtyAbs;
                }
                else
                {
                    // A closing fill can overshoot (e.g. long 2, sell 5 → closes the 2 long
                    // and opens a 3 short). Only the portion that actually closes the
                    // existing position counts toward this trade's avg exit price; the
                    // overshoot portion belongs to the next position's avg entry price
                    // instead, and is credited to it below once the round trip finalizes.
                    double closingQtyThisFill = Math.Min(fillQtyAbs, Math.Abs(prevQty));
                    exitQty[symbol] += closingQtyThisFill;
                    if (!double.IsNaN(fill.Price))
                        exitPriceQtySum[symbol] += fill.Price * closingQtyThisFill;
                }

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

                    // Tally into the correct side bucket, and always into the combined
                    // "All Trades" bucket (its streaks are tracked in the same chronological
                    // pass so they reflect the true cross-side sequence, not a merge of
                    // the two side streaks after the fact).
                    ref SideMetrics bucket = ref (wasLong ? ref longM : ref shortM);
                    ref int streakWin = ref (wasLong ? ref longStreakWin : ref shortStreakWin);
                    ref int streakLoss = ref (wasLong ? ref longStreakLoss : ref shortStreakLoss);

                    bucket.RoundTrips++;
                    bucket.TotalPnl += pnl;
                    bucket.HasData = true;
                    if (pnl > bucket.LargestWin) bucket.LargestWin = pnl;
                    if (pnl < bucket.LargestLoss) bucket.LargestLoss = pnl;

                    allM.RoundTrips++;
                    allM.TotalPnl += pnl;
                    allM.HasData = true;
                    if (pnl > allM.LargestWin) allM.LargestWin = pnl;
                    if (pnl < allM.LargestLoss) allM.LargestLoss = pnl;

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

                        allM.Wins++;
                        allM.TotalWinPnl += pnl;
                        allM.WinCount++;
                        allStreakWin++;
                        allStreakLoss = 0;
                        if (allStreakWin > allM.WinStreak) allM.WinStreak = allStreakWin;
                        if (durationSecs.HasValue)
                        {
                            allM.TotalWinDurationSeconds += durationSecs.Value;
                            allM.WinDurationCount++;
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

                        allM.TotalLossPnl += pnl;
                        allM.LossCount++;
                        allStreakLoss++;
                        allStreakWin = 0;
                        if (allStreakLoss > allM.LossStreak) allM.LossStreak = allStreakLoss;
                        if (durationSecs.HasValue)
                        {
                            allM.TotalLossDurationSeconds += durationSecs.Value;
                            allM.LossDurationCount++;
                        }
                    }
                    else
                    {
                        streakWin = 0;
                        streakLoss = 0;
                        allStreakWin = 0;
                        allStreakLoss = 0;
                    }

                    if (durationSecs.HasValue)
                    {
                        bucket.TotalDurationSeconds += durationSecs.Value;
                        bucket.DurationSampleCount++;

                        allM.TotalDurationSeconds += durationSecs.Value;
                        allM.DurationSampleCount++;
                    }

                    // Pie bucket: ±$2 breakeven band
                    if (pnl > 2.0) pie.Wins++;
                    else if (pnl < -2.0) pie.Losses++;
                    else pie.Breakevens++;

                    // Record the individual round trip for the trade-list panel, with
                    // quantity-weighted avg entry/exit prices (NaN in either sum stays NaN
                    // via the division below, shown as "—" in the UI rather than a bogus 0).
                    double avgEntryPrice = entryQty[symbol] > 0 ? entryPriceQtySum[symbol] / entryQty[symbol] : double.NaN;
                    double avgExitPrice = exitQty[symbol] > 0 ? exitPriceQtySum[symbol] / exitQty[symbol] : double.NaN;

                    metrics.Trades.Add(new RoundTripTrade
                    {
                        Symbol = symbol,
                        IsLong = wasLong,
                        EntryTime = ot,
                        ExitTime = fill.DateTime,
                        AvgEntryPrice = avgEntryPrice,
                        AvgExitPrice = avgExitPrice,
                        Pnl = pnl,
                        Quantity = entryQty[symbol], // the position's size, read before the reset below
                        DayKey = fill.DateTime.Date.ToString("yyyy-MM-dd")
                    });

                    // Reset FIFO state; if qty overshot zero, remainder starts a new position
                    netQty[symbol] = newQty;
                    netValue[symbol] = newQty != 0 ? fill.FillValue * (Math.Abs(newQty) / Math.Abs(signedQty)) : 0;
                    entryQty[symbol] = newQty != 0 ? Math.Abs(newQty) : 0;

                    // The overshoot portion of this same fill (if any) becomes the new
                    // position's first entry fill — mirror that for the price accumulator too,
                    // rather than losing it or double-counting it against the closed trade.
                    double overshootQty = newQty != 0 ? Math.Abs(newQty) : 0;
                    entryPriceQtySum[symbol] = overshootQty > 0 && !double.IsNaN(fill.Price)
                        ? fill.Price * overshootQty
                        : 0;
                    exitQty[symbol] = 0;
                    exitPriceQtySum[symbol] = 0;

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
            metrics.All = allM;
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

            if (SymbolMatchesList(symbol, _microSymbols))
                return _feePerMicro;
            if (SymbolMatchesList(symbol, _miniSymbols))
                return _feePerMini;

            Core.Instance.Loggers.Log($"[TradeJournal] Unrecognized symbol '{symbol}' — no fee applied.");
            return 0.0;
        }

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

                // Price isn't required for the existing P&L math (that all runs off "Trade
                // value"), but the trade-list panel wants a raw contract price for its avg
                // entry/exit display. Not all archive exports include one, and the exact
                // column name isn't guaranteed, so this tries a few plausible names and
                // simply leaves it as NaN (shown as "—") if none match.
                double price = double.NaN;
                foreach (var priceCol in new[] { "Price", "Fill Price", "Trade Price", "Execution Price" })
                {
                    if (col.TryGetValue(priceCol, out int pIdx) && pIdx < fields.Length &&
                        double.TryParse(fields[pIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
                    {
                        price = p;
                        break;
                    }
                }

                var fill = new FillRecord
                {
                    DateTime = dt,
                    Symbol = symbol,
                    SignedQty = qty,          // export already signs this: + buy, - sell
                    FillValue = -tradeValue,  // Trade value's sign convention is inverted vs GetFillValue
                    Price = price,
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
        private Rectangle _headerRect;
        private bool _showMonthlyMetrics = false;
        private bool _showWeeklyMetrics = false;
        private string _weeklyMetricsDate = null; // the day that was double-clicked
        private DateTime _lastClickTime = DateTime.MinValue;
        private string _lastClickDate = null;
        private const int DoubleClickMs = 400;
        private readonly List<(Rectangle rect, string date)> _dayCells =
            new List<(Rectangle, string)>();

        // Pie-chart toggle: when true, the metrics panel shows a single "All Trades"
        // column instead of the usual Long/Short columns. Clicking the pie chart
        // flips this. Deliberately NOT reset when the day/week/month selection
        // changes (unlike the symbol filter below) — it carries over until the
        // user clicks the pie chart again.
        private bool _showAllTradesView = false;
        private Rectangle _pieRect; // last-drawn pie chart bounds, for click hit-testing

        // Symbol filter, shown as a clickable list under the pie chart. Resets to "all
        // symbols" (null) whenever the day/week/month selection changes; only toggling
        // a symbol label itself changes it without touching the rest of the selection.
        private string _selectedSymbolFilter = null;
        private readonly List<(Rectangle rect, string symbol)> _symbolFilterCells =
            new List<(Rectangle, string)>();

        public event Action<string> OnDaySelected;
        public event Action OnPrevMonth;
        public event Action OnNextMonth;

        // Fired whenever the metrics view mode itself changes (day/week/month), separate
        // from OnDaySelected which fires for every day click regardless of mode. The
        // trade-list panel uses these to know when to reset its expand/note state and
        // reload for the new scope.
        public event Action<string> OnWeekViewSelected;  // anchor date, any day within the week
        public event Action OnMonthViewSelected;

        // Public so the plugin can re-query "what's currently shown" on its own (e.g.
        // after prev/next month), not just react to the events above.
        public bool IsMonthlyView => _showMonthlyMetrics;
        public bool IsWeeklyView => _showWeeklyMetrics;
        public string WeeklyViewDate => _weeklyMetricsDate;
        public string SelectedSymbolFilter => _selectedSymbolFilter;

        // Fired whenever the pie chart's symbol filter itself changes (separately from
        // a day/week/month selection change, which already triggers its own refresh).
        public event Action OnSymbolFilterChanged;

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
            // Symbol filter toggle takes priority and never resets the current
            // day/week/month selection — clicking the already-selected symbol clears
            // the filter back to "all symbols".
            foreach (var (rect, symbol) in _symbolFilterCells)
            {
                if (!rect.Contains(e.Location)) continue;
                _selectedSymbolFilter = string.Equals(_selectedSymbolFilter, symbol, StringComparison.OrdinalIgnoreCase)
                    ? null : symbol;
                OnSymbolFilterChanged?.Invoke();
                Redraw();
                return;
            }

            // Clicking the pie chart toggles between the Long/Short columns and a
            // single combined "All Trades" column. This toggle persists across day,
            // week, and month selection changes — it only changes when the pie
            // itself is clicked again.
            if (_pieRect.Width > 0 && _pieRect.Contains(e.Location))
            {
                _showAllTradesView = !_showAllTradesView;
                Redraw();
                return;
            }

            if (_prevBtnRect.Contains(e.Location)) { _selectedSymbolFilter = null; OnPrevMonth?.Invoke(); return; }
            if (_nextBtnRect.Contains(e.Location)) { _selectedSymbolFilter = null; OnNextMonth?.Invoke(); return; }

            // Clicking the month header switches to Monthly Metrics
            if (_headerRect.Contains(e.Location))
            {
                if (!_showMonthlyMetrics || _showWeeklyMetrics)
                {
                    _selectedSymbolFilter = null;
                    _showMonthlyMetrics = true;
                    _showWeeklyMetrics = false;
                    OnMonthViewSelected?.Invoke();
                    Redraw();
                }
                return;
            }

            foreach (var (rect, date) in _dayCells)
            {
                if (!rect.Contains(e.Location)) continue;

                var now = DateTime.Now;
                bool isDoubleClick = _lastClickDate == date &&
                                     (now - _lastClickTime).TotalMilliseconds <= DoubleClickMs;

                _selectedSymbolFilter = null; // any new day/week selection starts unfiltered

                if (isDoubleClick)
                {
                    // Double-click: show Weekly Metrics for the week this day falls into
                    _showMonthlyMetrics = false;
                    _showWeeklyMetrics = true;
                    _weeklyMetricsDate = date;
                    _lastClickDate = null; // reset so a third click is a fresh single
                    OnDaySelected?.Invoke(date);
                    OnWeekViewSelected?.Invoke(date);
                    Redraw();
                }
                else
                {
                    // Single click: show Daily Metrics
                    _showMonthlyMetrics = false;
                    _showWeeklyMetrics = false;
                    _lastClickTime = now;
                    _lastClickDate = date;
                    OnDaySelected?.Invoke(date);
                    Redraw();
                }
                return;
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

            var metrics = _plugin.GetDayMetrics(selectedDate, _selectedSymbolFilter);

            int panelX = GridStartX;
            int panelW = Math.Max(0, bounds.Width - GridStartX - 8);
            int colW = (int)(panelW / 2 * 0.75); // pull short column 25% closer to long column

            var sfLeft = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near };

            // Header
            var headerRect = new Rectangle(panelX, panelY, panelW, 18);
            gr.DrawString("Daily Metrics", fontHdr, whiteBrush, headerRect, sfLeft);

            if (metrics.HasData)
            {
                double totalPnl = metrics.Long.TotalPnl + metrics.Short.TotalPnl;
                string pnlText = FormatPnl(totalPnl);
                var pnlBrush = totalPnl >= 0 ? greenBrush : redBrush;
                var headerSize = gr.MeasureString("Daily Metrics", fontHdr);
                int pnlX = panelX + (int)headerSize.Width + 6;
                var pnlRect = new Rectangle(pnlX, panelY, panelW - (int)headerSize.Width - 6, 18);
                gr.DrawString(pnlText, fontHdr, pnlBrush, pnlRect, sfLeft);
            }

            int rowY = panelY + 22;
            int rowH = 16;

            if (_showAllTradesView)
            {
                // Single "All Trades" column — same metrics, no Long/Short split.
                var allCol = new Rectangle(panelX, rowY, colW * 2 - 6, rowH);
                gr.DrawString("All Trades", fontNames, lightGray, allCol, sfLeft);
                rowY += rowH;

                void DrawAllRow(string label, string val, SolidBrush brush)
                {
                    var r = new Rectangle(panelX, rowY, colW * 2 - 6, rowH);
                    gr.DrawString($"{label}: {val}", fontCount, brush, r, sfLeft);
                    rowY += rowH;
                }

                var am = metrics.All;

                DrawAllRow("Trades", am.RoundTrips.ToString(), grayBrush);
                DrawAllRow("Win Rate", am.HasData ? $"{am.WinRate:0.#}%" : "—", grayBrush);
                DrawAllRow("Avg P&L", am.HasData ? FormatPnl(am.AvgPnl) : "—", am.HasData && am.AvgPnl >= 0 ? greenBrush : redBrush);
                DrawAllRow("Avg Win", am.WinCount > 0 ? FormatPnl(am.AvgWin) : "—", greenBrush);
                DrawAllRow("Avg Loss", am.LossCount > 0 ? FormatPnl(am.AvgLoss) : "—", redBrush);
                DrawAllRow("Best Trade", am.HasData ? FormatPnl(am.LargestWin) : "—", greenBrush);
                DrawAllRow("Worst Trade", am.HasData ? FormatPnl(am.LargestLoss) : "—", redBrush);
                DrawAllRow("Win Streak", am.HasData ? am.WinStreak.ToString() : "—", greenBrush);
                DrawAllRow("Loss Streak", am.HasData ? am.LossStreak.ToString() : "—", redBrush);
                DrawAllRow("Avg Hold Win", am.WinDurationCount > 0 ? FormatDuration(am.AvgWinDurationSeconds) : "—", greenBrush);
                DrawAllRow("Avg Hold Loss", am.LossDurationCount > 0 ? FormatDuration(am.AvgLossDurationSeconds) : "—", redBrush);
            }
            else
            {
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
            }

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
                DrawSymbolFilterList(gr, pieX, pieY + pieSize + 6, availableW, metrics.Symbols,
                    _selectedSymbolFilter, fontCount, grayBrush, whiteBrush, greenBrush);
            }
            else
            {
                _pieRect = Rectangle.Empty; // pie not drawn this pass; don't hit-test a stale rect
            }

            sfLeft.Dispose();
        }

        // Draws a two-column (Long / Short) metrics breakdown for the Mon–Fri week
        // containing the double-clicked day. The week can span two calendar months.
        private void DrawWeeklyMetricsPanel(Graphics gr, int panelY, string dateStr,
            SolidBrush whiteBrush, SolidBrush grayBrush, SolidBrush lightGray,
            SolidBrush greenBrush, SolidBrush redBrush,
            Font fontHdr, Font fontNames, Font fontCount)
        {
            var bounds = Bounds;
            if (bounds.Width <= 0) return;

            var metrics = _plugin.GetWeekMetrics(dateStr, _selectedSymbolFilter);

            int panelX = GridStartX;
            int panelW = Math.Max(0, bounds.Width - GridStartX - 8);
            int colW = (int)(panelW / 2 * 0.75);

            var sfLeft = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near };

            // Header: "Weekly Metrics  Mon Jun 23 – Fri Jun 27" + P&L
            string[] monthAbbr = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
            string weekLabel = metrics.HasData
                ? $"Weekly Metrics  {monthAbbr[metrics.WeekStart.Month - 1]} {metrics.WeekStart.Day} – {monthAbbr[metrics.WeekEnd.Month - 1]} {metrics.WeekEnd.Day}"
                : "Weekly Metrics";

            var headerRect = new Rectangle(panelX, panelY, panelW, 18);
            gr.DrawString(weekLabel, fontHdr, whiteBrush, headerRect, sfLeft);

            if (metrics.HasData)
            {
                string pnlText = FormatPnl(metrics.TotalPnL);
                var pnlBrush = metrics.TotalPnL >= 0 ? greenBrush : redBrush;
                var headerSize = gr.MeasureString(weekLabel, fontHdr);
                int pnlX = panelX + (int)headerSize.Width + 6;
                var pnlRect = new Rectangle(pnlX, panelY, panelW - (int)headerSize.Width - 6, 18);
                gr.DrawString(pnlText, fontHdr, pnlBrush, pnlRect, sfLeft);
            }

            int rowY = panelY + 22;
            int rowH = 16;

            if (_showAllTradesView)
            {
                gr.DrawString("All Trades", fontNames, lightGray, new Rectangle(panelX, rowY, colW * 2 - 6, rowH), sfLeft);
                rowY += rowH;

                void DrawAllRow(string label, string val, SolidBrush brush)
                {
                    gr.DrawString($"{label}: {val}", fontCount, brush, new Rectangle(panelX, rowY, colW * 2 - 6, rowH), sfLeft);
                    rowY += rowH;
                }

                var am = metrics.All;

                DrawAllRow("Trades", am.RoundTrips.ToString(), grayBrush);
                DrawAllRow("Win Rate", am.HasData ? $"{am.WinRate:0.#}%" : "—", grayBrush);
                DrawAllRow("Avg P&L", am.HasData ? FormatPnl(am.AvgPnl) : "—", am.HasData && am.AvgPnl >= 0 ? greenBrush : redBrush);
                DrawAllRow("Avg Win", am.WinCount > 0 ? FormatPnl(am.AvgWin) : "—", greenBrush);
                DrawAllRow("Avg Loss", am.LossCount > 0 ? FormatPnl(am.AvgLoss) : "—", redBrush);
                DrawAllRow("Best Trade", am.HasData ? FormatPnl(am.LargestWin) : "—", greenBrush);
                DrawAllRow("Worst Trade", am.HasData ? FormatPnl(am.LargestLoss) : "—", redBrush);
                DrawAllRow("Win Streak", am.HasData ? am.WinStreak.ToString() : "—", greenBrush);
                DrawAllRow("Loss Streak", am.HasData ? am.LossStreak.ToString() : "—", redBrush);
                DrawAllRow("Avg Hold Win", am.WinDurationCount > 0 ? FormatDuration(am.AvgWinDurationSeconds) : "—", greenBrush);
                DrawAllRow("Avg Hold Loss", am.LossDurationCount > 0 ? FormatDuration(am.AvgLossDurationSeconds) : "—", redBrush);
            }
            else
            {
                gr.DrawString("Long", fontNames, lightGray, new Rectangle(panelX, rowY, colW - 6, rowH), sfLeft);
                gr.DrawString("Short", fontNames, lightGray, new Rectangle(panelX + colW, rowY, colW - 6, rowH), sfLeft);
                rowY += rowH;

                void DrawMetricRow(string label, string longVal, string shortVal, SolidBrush longBrush, SolidBrush shortBrush)
                {
                    gr.DrawString($"{label}: {longVal}", fontCount, longBrush, new Rectangle(panelX, rowY, colW - 6, rowH), sfLeft);
                    gr.DrawString($"{label}: {shortVal}", fontCount, shortBrush, new Rectangle(panelX + colW, rowY, colW - 6, rowH), sfLeft);
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
            }

            int availableW = Math.Max(0, panelW - (panelX + 2 * colW) - 8);
            int pieSize = Math.Min(200, availableW);
            if (pieSize > 20)
            {
                int pieX = panelX + 2 * colW + 4;
                int pieY = panelY + 14;
                DrawWinLossPie(gr, pieX, pieY, pieSize, metrics.Pie, fontCount, whiteBrush);
                DrawSymbolFilterList(gr, pieX, pieY + pieSize + 6, availableW, metrics.Symbols,
                    _selectedSymbolFilter, fontCount, grayBrush, whiteBrush, greenBrush);
            }
            else
            {
                _pieRect = Rectangle.Empty; // pie not drawn this pass; don't hit-test a stale rect
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

            var metrics = _plugin.GetMonthMetrics(year, month, _selectedSymbolFilter);

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

            if (_showAllTradesView)
            {
                var allCol = new Rectangle(panelX, rowY, colW * 2 - 6, rowH);
                gr.DrawString("All Trades", fontNames, lightGray, allCol, sfLeft);
                rowY += rowH;

                void DrawAllRow(string label, string val, SolidBrush brush)
                {
                    var r = new Rectangle(panelX, rowY, colW * 2 - 6, rowH);
                    gr.DrawString($"{label}: {val}", fontCount, brush, r, sfLeft);
                    rowY += rowH;
                }

                var am = metrics.All;

                DrawAllRow("Trades", am.RoundTrips.ToString(), grayBrush);
                DrawAllRow("Win Rate", am.HasData ? $"{am.WinRate:0.#}%" : "—", grayBrush);
                DrawAllRow("Avg P&L", am.HasData ? FormatPnl(am.AvgPnl) : "—", am.HasData && am.AvgPnl >= 0 ? greenBrush : redBrush);
                DrawAllRow("Avg Win", am.WinCount > 0 ? FormatPnl(am.AvgWin) : "—", greenBrush);
                DrawAllRow("Avg Loss", am.LossCount > 0 ? FormatPnl(am.AvgLoss) : "—", redBrush);
                DrawAllRow("Best Trade", am.HasData ? FormatPnl(am.LargestWin) : "—", greenBrush);
                DrawAllRow("Worst Trade", am.HasData ? FormatPnl(am.LargestLoss) : "—", redBrush);
                DrawAllRow("Win Streak", am.HasData ? am.WinStreak.ToString() : "—", greenBrush);
                DrawAllRow("Loss Streak", am.HasData ? am.LossStreak.ToString() : "—", redBrush);
                DrawAllRow("Avg Hold Win", am.WinDurationCount > 0 ? FormatDuration(am.AvgWinDurationSeconds) : "—", greenBrush);
                DrawAllRow("Avg Hold Loss", am.LossDurationCount > 0 ? FormatDuration(am.AvgLossDurationSeconds) : "—", redBrush);
            }
            else
            {
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
            }

            // Pie chart, same positioning as daily
            int availableW = Math.Max(0, panelW - (panelX + 2 * colW) - 8);
            int pieSize = Math.Min(200, availableW);
            if (pieSize > 20)
            {
                int pieX = panelX + 2 * colW + 4;
                int pieY = panelY + 14;
                DrawWinLossPie(gr, pieX, pieY, pieSize, metrics.Pie, fontCount, whiteBrush);
                DrawSymbolFilterList(gr, pieX, pieY + pieSize + 6, availableW, metrics.Symbols,
                    _selectedSymbolFilter, fontCount, grayBrush, whiteBrush, greenBrush);
            }
            else
            {
                _pieRect = Rectangle.Empty; // pie not drawn this pass; don't hit-test a stale rect
            }

            sfLeft.Dispose();
        }

        // Draws a simple win/loss/breakeven pie chart with in-slice percentage labels, no legend.
        private void DrawWinLossPie(Graphics gr, int x, int y, int size, PieBuckets pie, Font font, SolidBrush textBrush)
        {
            var rect = new Rectangle(x, y, size, size);
            _pieRect = rect; // stored for OnMouseClick hit-testing, even if there's no data to draw

            int total = pie.Total;
            if (total <= 0) return;

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

        // Draws a clickable "Traded Symbols:" list beneath the pie chart. Clicking a
        // symbol filters the panel's metrics and pie chart to that symbol only;
        // clicking the already-selected symbol clears the filter. Wraps to additional
        // rows if the symbol list is wider than maxWidth.
        private void DrawSymbolFilterList(Graphics gr, int x, int y, int maxWidth, List<string> symbols,
            string selectedSymbol, Font font, SolidBrush grayBrush, SolidBrush whiteBrush, SolidBrush accentBrush)
        {
            if (symbols == null || symbols.Count == 0 || maxWidth <= 0) return;

            var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near };

            const int rowH = 16;
            const int padX = 12;

            gr.DrawString("Traded Symbols:", font, grayBrush, new Rectangle(x, y, maxWidth, rowH), sf);

            int curX = x;
            int curY = y + rowH;

            foreach (var sym in symbols)
            {
                var size = gr.MeasureString(sym, font);
                int w = (int)Math.Ceiling(size.Width) + padX;

                if (curX > x && curX + w > x + maxWidth)
                {
                    curX = x;
                    curY += rowH;
                }

                var rect = new Rectangle(curX, curY, w, rowH);
                bool isSelected = string.Equals(sym, selectedSymbol, StringComparison.OrdinalIgnoreCase);

                gr.DrawString(sym, font, isSelected ? accentBrush : whiteBrush, rect, sf);
                _symbolFilterCells.Add((rect, sym));

                curX += w;
            }

            sf.Dispose();
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
            _symbolFilterCells.Clear();

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

            // --- Metrics panel: Monthly, Weekly, or Daily depending on toggle ---
            int panelY = GridStartY + totalWeekRows * cellH + 12;
            if (_showMonthlyMetrics)
                DrawMonthlyMetricsPanel(gr, panelY, month + 1, year, whiteBrush, grayBrush, lightGray,
                    greenBrush, redBrush, fontHdr, fontNames, fontCount);
            else if (_showWeeklyMetrics && _weeklyMetricsDate != null)
                DrawWeeklyMetricsPanel(gr, panelY, _weeklyMetricsDate, whiteBrush, grayBrush, lightGray,
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