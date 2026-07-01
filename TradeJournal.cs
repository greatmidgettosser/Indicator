using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
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

    public class TradeJournalPlugin : Plugin
    {
        private static readonly string JournalFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Quantower", "TradeJournal");

        private string _selectedDate = DateTime.Today.ToString("yyyy-MM-dd");
        private int _currentMonth = DateTime.Today.Month - 1;
        private int _currentYear = DateTime.Today.Year;
        private System.Timers.Timer _saveDebounce;
        private TradeJournalCalendarRenderer _calRenderer;
        private bool _browserReady = false;

        // --- Settings ---
        private Account _account;       // null = all accounts
        private int _cellW = 100;       // default cell width
        private int _cellH = 74;        // default cell height
        private int _calColumnWidth = 550; // raw pixel width of the calendar column
        private const int CalWidthMin = 300;
        private const int CalWidthMax = 1200;

        // Cache so we don't re-query on every redraw
        private Dictionary<string, DayStats> _statsCache = new Dictionary<string, DayStats>();
        private int _statsCacheMonth = -1;
        private int _statsCacheYear = -1;

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

            var startupTimer = new System.Timers.Timer(3500);
            startupTimer.AutoReset = false;
            startupTimer.Elapsed += (s, e) =>
            {
                startupTimer.Dispose();
                LoadNote(_selectedDate);
                _browserReady = true;
                _calRenderer.Redraw();
            };
            startupTimer.Start();
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

                // Only react to trades in the month/day currently being viewed; other
                // months will naturally re-query next time they're navigated to since
                // their cache key (_statsCacheMonth/_statsCacheYear) won't match.
                if (tradeDate.Month == _currentMonth + 1 && tradeDate.Year == _currentYear)
                    InvalidateStatsCache();

                string dateKey = tradeDate.ToString("yyyy-MM-dd");
                _dayMetricsCache.Remove(dateKey);

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
            if (string.IsNullOrWhiteSpace(content))
            {
                if (File.Exists(path)) File.Delete(path);
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

                string escaped = content
                    .Replace("\\", "\\\\")
                    .Replace("'", "\\'")
                    .Replace("\r\n", "\\n")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\n");

                this.Window.Browser.UpdateHtml("selectedDateLabel", HtmlAction.SetTextContent, label);
                this.Window.Browser.UpdateHtml("noteArea", HtmlAction.InvokeJs,
                    $"document.getElementById('noteArea').value = '{escaped}';");
                this.Window.Browser.UpdateHtml("saveIndicator", HtmlAction.SetTextContent, "");
                this.Window.Browser.UpdateHtml("saveIndicator", HtmlAction.SetClass, "save-indicator");
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] Load error: {ex.Message}");
            }
        }

        // --- Trade statistics ---

        private void InvalidateStatsCache()
        {
            _statsCacheMonth = -1;
            _statsCacheYear = -1;
            _statsCache.Clear();
            _dayMetricsCache.Clear();
        }

        public Dictionary<string, DayStats> GetMonthlyTradeStats()
        {
            int month = _currentMonth + 1;
            int year = _currentYear;

            if (_statsCacheMonth == month && _statsCacheYear == year)
                return _statsCache;

            _statsCache.Clear();
            _statsCacheMonth = month;
            _statsCacheYear = year;

            try
            {
                var monthStart = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
                var monthEnd = monthStart.AddMonths(1);

                var trades = Core.Instance.GetTrades(new TradesHistoryRequestParameters
                {
                    From = monthStart,
                    To = monthEnd,
                });

                if (trades == null) return _statsCache;

                // Group fills by day then by symbol, process FIFO to build round trips.
                // Key: "yyyy-MM-dd|SYMBOL"  Value: running net qty and accumulated value
                var daySymbolQty = new Dictionary<string, double>();   // running net position qty
                var daySymbolValue = new Dictionary<string, double>(); // running accumulated trade value

                foreach (var trade in trades.OrderBy(t => t.DateTime))
                {
                    if (_account != null && !trade.Account.Equals(_account)) continue;

                    DateTime tradeDate = trade.DateTime.ToLocalTime().Date;
                    if (tradeDate.Month != month || tradeDate.Year != year) continue;

                    string dayKey = tradeDate.ToString("yyyy-MM-dd");
                    string symbol = trade.Symbol?.Id ?? "UNKNOWN";
                    string posKey = $"{dayKey}|{symbol}";

                    if (!daySymbolQty.ContainsKey(posKey))
                    {
                        daySymbolQty[posKey] = 0;
                        daySymbolValue[posKey] = 0;
                    }

                    double fillValue = GetFillValue(trade);
                    if (double.IsNaN(fillValue)) continue;

                    string side = trade.Side.ToString();
                    bool isBuy = side.Equals("Buy", StringComparison.OrdinalIgnoreCase);
                    double qty = isBuy ? trade.Quantity : -trade.Quantity; // signed qty

                    double prevQty = daySymbolQty[posKey];
                    double newQty = prevQty + qty;

                    daySymbolValue[posKey] += fillValue;

                    // A round trip completes when net qty crosses or returns to zero
                    if ((prevQty > 0 && newQty <= 0) || (prevQty < 0 && newQty >= 0))
                    {
                        double pnl = daySymbolValue[posKey];
                        if (!_statsCache.TryGetValue(dayKey, out DayStats stats))
                            stats = new DayStats();

                        stats.PnL += pnl;
                        stats.RoundTrips++;
                        stats.HasData = true;
                        _statsCache[dayKey] = stats;

                        // Reset accumulator; if qty overshot zero, the remainder starts a new position
                        daySymbolQty[posKey] = newQty;
                        daySymbolValue[posKey] = newQty != 0 ? GetFillValue(trade) * (Math.Abs(newQty) / trade.Quantity) : 0;
                    }
                    else
                    {
                        daySymbolQty[posKey] = newQty;
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] GetMonthlyTradeStats error: {ex.Message}");
            }

            return _statsCache;
        }

        // Per-day long/short breakdown for the metrics panel. Cached per date string
        // since OnDaySelected/Prev/NextMonth already drive when this needs to refresh.
        private Dictionary<string, DayMetrics> _dayMetricsCache = new Dictionary<string, DayMetrics>();

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
                if (!DateTime.TryParse(date, out DateTime dayDate))
                {
                    _dayMetricsCache[date] = metrics;
                    return metrics;
                }

                var dayStart = new DateTime(dayDate.Year, dayDate.Month, dayDate.Day, 0, 0, 0, DateTimeKind.Utc);
                var dayEnd = dayStart.AddDays(1);

                var trades = Core.Instance.GetTrades(new TradesHistoryRequestParameters
                {
                    From = dayStart,
                    To = dayEnd,
                });

                if (trades == null)
                {
                    _dayMetricsCache[date] = metrics;
                    return metrics;
                }

                // Sort chronologically so FIFO pairing is correct
                var dayFills = trades
                    .Where(t => _account == null || t.Account.Equals(_account))
                    .Where(t => t.DateTime.ToLocalTime().Date == dayDate.Date)
                    .OrderBy(t => t.DateTime)
                    .ToList();

                // FIFO state per symbol: running net qty, accumulated value, and open-fill timestamp
                var netQty = new Dictionary<string, double>();
                var netValue = new Dictionary<string, double>();
                var openTime = new Dictionary<string, DateTime>(); // timestamp of the first fill that opened the position

                var longM = metrics.Long;
                var shortM = metrics.Short;
                var pie = metrics.Pie;
                int longStreakWin = 0, longStreakLoss = 0;
                int shortStreakWin = 0, shortStreakLoss = 0;

                foreach (var trade in dayFills)
                {
                    string symbol = trade.Symbol?.Id ?? "UNKNOWN";
                    string side = trade.Side.ToString();
                    bool isBuy = side.Equals("Buy", StringComparison.OrdinalIgnoreCase);

                    if (!netQty.ContainsKey(symbol))
                    {
                        netQty[symbol] = 0;
                        netValue[symbol] = 0;
                    }

                    double signedQty = isBuy ? trade.Quantity : -trade.Quantity;
                    double prevQty = netQty[symbol];
                    double newQty = prevQty + signedQty;
                    double fillValue = GetFillValue(trade);
                    if (double.IsNaN(fillValue)) { netQty[symbol] = newQty; continue; }

                    // If this fill opens a new position from flat, record the open timestamp
                    if (prevQty == 0)
                        openTime[symbol] = trade.DateTime;

                    netValue[symbol] += fillValue;

                    // Check if a round trip closed (net qty crossed zero)
                    bool closedRoundTrip = (prevQty > 0 && newQty <= 0) || (prevQty < 0 && newQty >= 0);

                    if (closedRoundTrip)
                    {
                        double pnl = netValue[symbol];
                        bool wasLong = prevQty > 0; // long position = opened with Buy fills

                        // Duration: time from open fill to this close fill
                        double? durationSecs = null;
                        if (openTime.TryGetValue(symbol, out DateTime ot))
                        {
                            var span = trade.DateTime - ot;
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
                        netValue[symbol] = newQty != 0 ? fillValue * (Math.Abs(newQty) / trade.Quantity) : 0;
                        if (newQty != 0)
                            openTime[symbol] = trade.DateTime; // new position opened by this same fill
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
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] GetDayMetrics error: {ex.Message}");
            }

            _dayMetricsCache[date] = metrics;
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
                dates.Add(Path.GetFileNameWithoutExtension(file));
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

            foreach (var (rect, date) in _dayCells)
            {
                if (rect.Contains(e.Location))
                {
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
        private void DrawDayMetricsPanel(Graphics gr, int panelY, string selectedDate,
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
            gr.DrawString("Day Metrics", fontHdr, whiteBrush, headerRect, sfLeft);

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
            var tradeStats = _plugin.GetMonthlyTradeStats();

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
            gr.DrawString($"{monthNames[month]} {year}", fontHdr, whiteBrush, headerRect, sfCenter);

            // --- Day-name row ---
            string[] dayNames = { "Mo", "Tu", "We", "Th", "Fr" };
            for (int i = 0; i < 5; i++)
            {
                var r = new Rectangle(GridStartX + i * cellW, DayNamesY, cellW, DayNamesH);
                gr.DrawString(dayNames[i], fontNames, grayBrush, r, sfCenter);
            }

            // --- Day cells (Mon–Fri only) ---
            int daysInMonth = DateTime.DaysInMonth(year, month + 1);
            var today = DateTime.Today;
            int weekRow = 0;

            // Pre-compute how many week rows this month needs so we know where the
            // grid ends and can place the metrics panel directly below it.
            int totalWeekRows = 1;
            {
                int wr = 0;
                for (int dd = 1; dd <= daysInMonth; dd++)
                {
                    var dow2 = new DateTime(year, month + 1, dd).DayOfWeek;
                    if (dow2 == DayOfWeek.Monday && dd > 1) wr++;
                }
                totalWeekRows = wr + 1;
            }

            // Row offsets scale with cellH so content stays proportionally placed
            int pnlOffsetY = (int)(cellH * 0.35f);  // ~18px at cellH=52
            int countOffsetY = (int)(cellH * 0.63f);  // ~33px at cellH=52
            int dayNumH = (int)(cellH * 0.27f);  // ~14px at cellH=52

            for (int d = 1; d <= daysInMonth; d++)
            {
                var date = new DateTime(year, month + 1, d);
                var dow = date.DayOfWeek;

                if (dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday)
                    continue;

                if (dow == DayOfWeek.Monday)
                {
                    if (d > 1) weekRow++;
                }

                int col = (int)dow - 1; // Mon=0 … Fri=4

                var cellRect = new Rectangle(
                    GridStartX + col * cellW,
                    GridStartY + weekRow * cellH,
                    cellW - 2,
                    cellH - 2);

                string dateStr = $"{year}-{(month + 1):D2}-{d:D2}";
                bool isToday = today.Year == year && today.Month == month + 1 && today.Day == d;
                bool isSelected = dateStr == selected;
                bool hasNote = noteDates.Contains(dateStr);

                if (isSelected)
                    gr.FillRectangle(selectedBrush, cellRect);
                else if (isToday)
                    gr.FillRectangle(todayBrush, cellRect);

                // Day number
                var dayNumRect = new Rectangle(cellRect.X, cellRect.Y + 2, cellRect.Width, dayNumH);
                gr.DrawString(d.ToString(),
                    isSelected || isToday ? fontDay : fontNames,
                    isSelected || isToday ? whiteBrush : lightGray,
                    dayNumRect, sfTopCenter);

                // Trade stats
                if (tradeStats.TryGetValue(dateStr, out DayStats stats) && stats.HasData)
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

            // --- Day metrics panel (Long / Short breakdown for the selected day) ---
            int panelY = GridStartY + totalWeekRows * cellH + 12;
            DrawDayMetricsPanel(gr, panelY, selected, whiteBrush, grayBrush, lightGray,
                greenBrush, redBrush, fontHdr, fontNames, fontCount);

            // --- Dispose ---
            fontDay.Dispose(); fontPnl.Dispose(); fontCount.Dispose();
            fontHdr.Dispose(); fontNames.Dispose(); fontArrow.Dispose();
            whiteBrush.Dispose(); grayBrush.Dispose(); lightGray.Dispose();
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
