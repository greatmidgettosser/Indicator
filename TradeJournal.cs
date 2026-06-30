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

        public double WinRate => RoundTrips > 0 ? (double)Wins / RoundTrips * 100.0 : 0.0;
        public double AvgPnl => RoundTrips > 0 ? TotalPnl / RoundTrips : 0.0;
    }

    public struct DayMetrics
    {
        public SideMetrics Long;
        public SideMetrics Short;
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
        private int _cellW = 46;        // min 44, max 110 (2.5×)
        private int _cellH = 56;        // min 52, max 130 (2.5×)
        private int _calColumnWidth = 600; // raw pixel width of the calendar column (fits up to ~110px cells)
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

                foreach (var trade in trades)
                {
                    // Filter to selected account when one is configured
                    if (_account != null && !trade.Account.Equals(_account)) continue;

                    DateTime tradeDate = trade.DateTime.ToLocalTime().Date;
                    if (tradeDate.Month != month || tradeDate.Year != year) continue;

                    double pnl = GetTradePnL(trade);
                    if (double.IsNaN(pnl)) continue;

                    string key = tradeDate.ToString("yyyy-MM-dd");

                    if (!_statsCache.TryGetValue(key, out DayStats stats))
                        stats = new DayStats();

                    stats.PnL += pnl;
                    stats.RoundTrips++;
                    stats.HasData = true;

                    _statsCache[key] = stats;
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
                Short = new SideMetrics { LargestLoss = 0 }
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

                var longM = metrics.Long;
                var shortM = metrics.Short;

                foreach (var trade in trades)
                {
                    if (_account != null && !trade.Account.Equals(_account)) continue;

                    DateTime tradeDate = trade.DateTime.ToLocalTime().Date;
                    if (tradeDate != dayDate.Date) continue;

                    double pnl = GetTradePnL(trade);
                    if (double.IsNaN(pnl)) continue;

                    // Closing-fill convention: Sell closes a long, Buy closes a short.
                    string side = trade.Side.ToString();
                    bool isLongClose = side.Equals("Sell", StringComparison.OrdinalIgnoreCase);
                    bool isShortClose = side.Equals("Buy", StringComparison.OrdinalIgnoreCase);

                    if (!isLongClose && !isShortClose) continue;

                    if (isLongClose)
                    {
                        longM.RoundTrips++;
                        longM.TotalPnl += pnl;
                        longM.HasData = true;
                        if (pnl > 0) longM.Wins++;
                        if (pnl > longM.LargestWin) longM.LargestWin = pnl;
                        if (pnl < longM.LargestLoss) longM.LargestLoss = pnl;
                    }
                    else
                    {
                        shortM.RoundTrips++;
                        shortM.TotalPnl += pnl;
                        shortM.HasData = true;
                        if (pnl > 0) shortM.Wins++;
                        if (pnl > shortM.LargestWin) shortM.LargestWin = pnl;
                        if (pnl < shortM.LargestLoss) shortM.LargestLoss = pnl;
                    }
                }

                metrics.Long = longM;
                metrics.Short = shortM;
                metrics.HasData = longM.HasData || shortM.HasData;
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] GetDayMetrics error: {ex.Message}");
            }

            _dayMetricsCache[date] = metrics;
            return metrics;
        }

        private static double GetTradePnL(Trade trade)
        {
            if (trade.NetPnl != null)
                return trade.NetPnl.Value;
            if (trade.GrossPnl != null && trade.Fee != null)
                return trade.GrossPnl.Value - trade.Fee.Value;
            if (trade.GrossPnl != null)
                return trade.GrossPnl.Value;
            return double.NaN;
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
            DrawMetricRow("Best", lm.HasData ? FormatPnl(lm.LargestWin) : "—", sm.HasData ? FormatPnl(sm.LargestWin) : "—", greenBrush, greenBrush);
            DrawMetricRow("Worst", lm.HasData ? FormatPnl(lm.LargestLoss) : "—", sm.HasData ? FormatPnl(sm.LargestLoss) : "—", redBrush, redBrush);

            sfLeft.Dispose();
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