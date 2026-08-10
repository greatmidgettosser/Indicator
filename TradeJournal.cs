using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Native;
using TradingPlatform.PresentationLayer.Plugins;
using TradingPlatform.PresentationLayer.Renderers;

namespace TradeJournal
{
    // Global color theme, shared by every renderer regardless of which plugin instance
    // they belong to (mirrors how "one setting affects everything on screen" behaves for
    // a single-window plugin like this). Toggled from the plugin's Settings panel
    // ("Light Mode" checkbox). Dark values are the original palette, unchanged.
    public static class Theme
    {
        public static bool LightMode = false;

        // Backgrounds
        public static Color ChartBg => LightMode ? Color.FromArgb(235, 242, 250) : Color.FromArgb(37, 37, 37);
        public static Color PanelBg => LightMode ? Color.FromArgb(232, 240, 250) : Color.FromArgb(30, 30, 30);
        public static Color BoxBg => LightMode ? Color.FromArgb(218, 231, 245) : Color.FromArgb(42, 42, 42);
        public static Color HeaderBg => LightMode ? Color.FromArgb(212, 227, 244) : Color.FromArgb(30, 30, 30);
        public static Color SeparatorBg => LightMode ? Color.FromArgb(224, 236, 248) : Color.FromArgb(32, 32, 32);
        public static Color HoverBg => LightMode ? Color.FromArgb(202, 221, 242) : Color.FromArgb(42, 42, 42);
        public static Color TodayBg => LightMode ? Color.FromArgb(175, 208, 238) : Color.FromArgb(42, 74, 107);
        public static Color SelectedBg => LightMode ? Color.FromArgb(182, 222, 200) : Color.FromArgb(26, 107, 58);

        // Text
        public static Color TextPrimary => LightMode ? Color.FromArgb(20, 32, 48) : Color.White;
        public static Color TextNearPrimary => LightMode ? Color.FromArgb(30, 42, 58) : Color.FromArgb(220, 220, 220);
        public static Color TextGray => LightMode ? Color.FromArgb(95, 112, 132) : Color.FromArgb(120, 120, 120);
        public static Color TextLightGray => LightMode ? Color.FromArgb(75, 92, 112) : Color.FromArgb(170, 170, 170);
        public static Color TextDim => LightMode ? Color.FromArgb(140, 158, 178) : Color.FromArgb(75, 75, 75);
        public static Color TextDim2 => LightMode ? Color.FromArgb(140, 158, 178) : Color.FromArgb(80, 80, 80);
        public static Color IconGray => LightMode ? Color.FromArgb(90, 108, 128) : Color.FromArgb(180, 180, 180);

        // Gridlines / borders
        public static Color GridLine => LightMode ? Color.FromArgb(198, 214, 232) : Color.FromArgb(55, 55, 55);
        public static Color GridLineFaint => LightMode ? Color.FromArgb(210, 224, 240) : Color.FromArgb(45, 45, 45);
        public static Color RowSep => LightMode ? Color.FromArgb(212, 226, 242) : Color.FromArgb(38, 38, 38);
        public static Color HeaderSep => LightMode ? Color.FromArgb(180, 200, 222) : Color.FromArgb(60, 60, 60);
        public static Color Border => LightMode ? Color.FromArgb(160, 182, 205) : Color.FromArgb(80, 80, 80);

        // Win / loss / neutral — kept close to the original hues but darkened slightly in
        // light mode so they stay legible against a light background.
        public static Color Win => LightMode ? Color.FromArgb(15, 150, 85) : Color.FromArgb(0, 200, 100);
        public static Color WinAlt => LightMode ? Color.FromArgb(30, 165, 100) : Color.FromArgb(46, 204, 113);
        public static Color Loss => LightMode ? Color.FromArgb(195, 55, 55) : Color.FromArgb(220, 60, 60);
        public static Color LossAlt => LightMode ? Color.FromArgb(200, 60, 55) : Color.FromArgb(224, 72, 60);
        public static Color LossAlt2 => LightMode ? Color.FromArgb(205, 65, 55) : Color.FromArgb(231, 76, 60);
        public static Color BreakEven => LightMode ? Color.FromArgb(180, 140, 20) : Color.FromArgb(230, 200, 60);
        public static Color Yellow => LightMode ? Color.FromArgb(180, 140, 15) : Color.FromArgb(255, 220, 50);
    }

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

    // Extra metrics computed only for the "All Trades" combined view (not per-side).
    // These require the full ordered trade list (equity curve, time slots) so they
    // cannot be merged incrementally the way SideMetrics fields are.
    public struct TimeSlotStats
    {
        public double TotalPnl;
        public int Wins;
        public int Losses;
        public bool HasData => Wins + Losses > 0;
    }

    public struct AllExtraMetrics
    {
        public double MaxRunUp;        // largest single-trade run of cumulative equity gain from a trough
        public double MaxDrawdown;     // largest peak-to-trough drop in cumulative equity
        // AvgWin / AvgLoss ratio — already computable from SideMetrics but stored here for convenience
        public double AvgWinAvgLossRatio; // 0 if denominator is 0
        public double ProfitFactor;        // gross profit / |gross loss|; double.NaN if no losses
        // NY session time slots: index 0 = 7:30–8am, 1 = 8–9am, … 6 = 12–1pm
        public TimeSlotStats[] TimeSlots; // length 7
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
        public double BrokerFee;    // per-fill fee sourced from either the live platform (trade.Fee)
                                    // or the archive CSV's "Fee" column. 0 when the broker doesn't
                                    // report a fee per-fill (e.g. AMP, which posts fees on the
                                    // end-of-day statement rather than per-fill). When the accumulated
                                    // BrokerFee across all fills in a round trip is non-zero, it is
                                    // used directly instead of the manual fee-per-contract settings,
                                    // mirroring the Risk Manager's GetTradeFee() fallback logic.
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
        private static readonly string TradeNotesFolder = Path.Combine(RootFolder, "Notes", "Trades");
        private static readonly string ArchiveFolder = Path.Combine(RootFolder, "TradeArchive");
        private static readonly string ScreenshotsFolder = Path.Combine(RootFolder, "Screenshots");

        // --- Screenshot capture ---
        // Tracks net position quantity per symbol root so we can label each fill as
        // entry or exit. Seeded from Core.Instance.Positions on plugin load.
        private readonly Dictionary<string, double> _livePositions = new Dictionary<string, double>();

        // Deduplication: if multiple fills for the same symbol fire within 500ms we
        // take one screenshot and reuse it for all fills in that window.
        // Key = symbol root, Value = (captureTime, savedPath, isEntry)
        private readonly Dictionary<string, (DateTime WindowStart, string SavedPath, bool IsEntry)> _pendingScreenshots
            = new Dictionary<string, (DateTime, string, bool)>();
        private readonly object _screenshotLock = new object();

        // Registered screenshot button element ids → file paths for the currently
        // displayed trade note header, so click events can open the right file.
        private readonly Dictionary<string, string> _screenshotButtonPaths = new Dictionary<string, string>();

        private string _selectedDate = DateTime.Today.ToString("yyyy-MM-dd");
        private int _currentMonth = DateTime.Today.Month - 1;
        private int _currentYear = DateTime.Today.Year;
        private System.Timers.Timer _saveDebounce;
        private TradeJournalCalendarRenderer _calRenderer;
        private TradeJournalTradeListRenderer _tlRenderer;
        private TradeJournalScreenshotStripRenderer _ssRenderer;
        private TradeJournalTitleBarRenderer _tbRenderer;
        private bool _browserReady = false;
        private readonly HashSet<string> _loadedDates = new HashSet<string>();
        private string _lastLoadedHtml = null; // the htmlContent string last pushed into the div

        // Trade-list panel (bottom half of the notes column): a flat table rebuilt from
        // scratch on every view change or new fill. Rows are clickable via per-row
        // hidden button elements whose AddEventHandler calls are refreshed each render.
        private bool _tradeListReady = false;

        // Trade note: a small note area below the trade list, assigned to the clicked row.
        // Keyed by RoundTripTrade.TradeKey (Symbol|ExitTime); files live in Notes\Trades\.
        private string _selectedTradeKey = null;
        private readonly HashSet<string> _tradeNoteLoadedKeys = new HashSet<string>();
        private string _lastLoadedTradeNoteHtml = null;
        private System.Timers.Timer _tradeNoteSaveDebounce;

        // --- Settings ---
        private Account _account;       // null = all accounts
        private int _cellW = 100;       // default cell width
        private int _cellH = 74;        // default cell height
        private double _fontScale = 1.0; // default font scale (independent of cell size)
        private int _calColumnWidth = 550; // raw pixel width of the calendar column
        private const int CalWidthMin = 300;
        private const int CalWidthMax = 1200;
        private bool _calculateFees = false;
        private double _feePerMicro = 0.0;
        private double _feePerMini = 0.0;
        private string _microSymbols = "MES, MNQ, M2K";
        private string _miniSymbols = "ES, NQ, RTY";

        // Additional metrics chart drawn beneath the metrics panel (Daily/Weekly/Monthly/Yearly),
        // filling the empty space that's otherwise left below the panel in full screen. Which
        // chart style (line vs histogram) is used is a runtime, click-to-toggle choice living
        // on the renderer, not a persisted setting — see _additionalMetricsUseLineChart there.
        private bool _showAdditionalMetrics = false;

        // Daily reset boundary setting.
        // Midnight        = calendar day boundary (default). Each day runs from 12:00am–11:59pm local time.
        // SessionBoundary = futures session boundary (5:00pm Eastern). Any fill from 5:00pm ET
        //                   onward belongs to the *next* calendar day. Sunday evening fills
        //                   (5:00pm ET onward) are attributed to Monday, not Sunday.
        public enum DailyResetMode { Midnight, SessionBoundary }
        private DailyResetMode _dailyReset = DailyResetMode.Midnight;

        // Cached Eastern time zone — looked up once to avoid repeated FindSystemTimeZoneById calls.
        private static readonly TimeZoneInfo _easternTz = GetEasternTimeZone();
        private static TimeZoneInfo GetEasternTimeZone()
        {
            // Windows TZDB id; Quantower runs on Windows so this is always available.
            try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { return TimeZoneInfo.Local; } // defensive fallback
        }

        // Time slot boundaries — user-configurable, stored as "H:mm" strings.
        // Defaults match the original NY session schedule.
        public TimeSpan SlotStart0 = new TimeSpan(7, 30, 0);
        public TimeSpan SlotStart1 = new TimeSpan(8, 0, 0);
        public TimeSpan SlotStart2 = new TimeSpan(9, 0, 0);
        public TimeSpan SlotStart3 = new TimeSpan(10, 0, 0);
        public TimeSpan SlotStart4 = new TimeSpan(11, 0, 0);
        public TimeSpan SlotStart5 = new TimeSpan(12, 0, 0);
        public TimeSpan SlotStart6 = new TimeSpan(13, 0, 0);
        public TimeSpan SlotEnd6 = new TimeSpan(14, 0, 0);

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


        private const bool AllowMultiplePanels = true;
        private bool _inertPanel;

        private static readonly object _instanceLock = new object();
        private static readonly List<TradeJournalPlugin> _instances = new List<TradeJournalPlugin>();
        private static TradeJournalPlugin _primaryInstance;

        // Only the primary instance takes screenshots. Every instance still tracks
        // trades for its own display; this flag only gates the global side effect.
        private bool _isPrimaryInstance;
        private bool _tradeAddedSubscribed;

        // Set the moment Dispose starts. Every timer callback and every browser
        // touch checks this first, so nothing can reach a torn-down window.
        private volatile bool _disposed;

        // Initial-load pump state.
        private System.Timers.Timer _initPump;
        private readonly object _initLock = new object();
        private int _initPumpAttempts;
        private int _initPumpBusy;              // Interlocked re-entrancy guard
        private bool _initialLoadCompleted;
        private bool _screenshotHandlerAttached;
        private bool _browserLoadedSubscribed;
        private const int InitPumpIntervalMs = 500;
        private const int InitPumpMaxAttempts = 40;   // 40 x 500ms = 20s, same budget as before

        // Content re-sync after a forced (unconfirmed) load.
        private System.Timers.Timer _resyncTimer;
        private int _resyncCount;
        private const int ResyncMaxPasses = 3;

        private void RegisterInstance()
        {
            lock (_instanceLock)
            {
                if (!_instances.Contains(this))
                    _instances.Add(this);

                if (_primaryInstance == null || _primaryInstance._disposed)
                {
                    _primaryInstance = this;
                    _isPrimaryInstance = true;
                }
                else
                {
                    _isPrimaryInstance = false;
                    Core.Instance.Loggers.Log(
                        "[TradeJournal] Additional panel opened — screenshot capture stays with the first panel.");
                }
            }
        }

        private void UnregisterInstance()
        {
            lock (_instanceLock)
            {
                _instances.Remove(this);

                if (_primaryInstance == this)
                {
                    _primaryInstance = null;
                    _isPrimaryInstance = false;

                    // Hand screenshot ownership to whichever panel is still open.
                    foreach (var candidate in _instances)
                    {
                        if (candidate._disposed) continue;
                        _primaryInstance = candidate;
                        candidate._isPrimaryInstance = true;
                        candidate.SeedLivePositions();
                        break;
                    }
                }
            }
        }

        // Seeds the live position tracker from any currently open positions so the
        // first fill after load is correctly labeled entry vs exit.
        private void SeedLivePositions()
        {
            try
            {
                var openPositions = Core.Instance.Positions;
                if (openPositions == null) return;

                lock (_screenshotLock)
                {
                    _livePositions.Clear();
                    foreach (var pos in openPositions)
                    {
                        if (_account != null && !pos.Account.Equals(_account)) continue;
                        string root = GetSymbolRoot(pos.Symbol.Name);
                        _livePositions[root] = pos.Quantity * (pos.Side == Side.Buy ? 1 : -1);
                    }
                }
            }
            catch { /* non-fatal — tracker starts empty */ }
        }

        // Subscribes to BrowserLoaded exactly once, whenever the browser first
        // becomes reachable. Populate may run before the browser object exists, and
        // it may also be called more than once — neither can double-subscribe here.
        private void EnsureBrowserLoadedSubscription()
        {
            if (_disposed || _browserLoadedSubscribed) return;

            var browser = this.Window?.Browser;
            if (browser == null) return;

            try
            {
                browser.BrowserLoaded += OnBrowserLoaded;
                _browserLoadedSubscribed = true;
            }
            catch { }
        }

        // --- Safe browser access -------------------------------------------------
        // Every write to the DOM goes through here. Returns silently if the plugin
        // is being torn down or the browser is gone, which is what used to throw
        // from stray timer callbacks after the panel was closed.
        private void BrowserUpdate(string elementId, HtmlAction action, string content)
        {
            if (_disposed) return;
            var browser = this.Window?.Browser;
            if (browser == null) return;

            try
            {
                browser.UpdateHtml(elementId, action, content);
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] BrowserUpdate('{elementId}') failed: {ex.Message}");
            }
        }

        // Pushes the current Theme.LightMode state onto the note panel's <body id="pageBody">
        // as a CSS class — layout.html's stylesheet keys off body.light-mode to swap its color
        // variables. Called on initial load and whenever the "Light Mode" setting changes.
        private void ApplyThemeToBrowser()
        {
            BrowserUpdate("pageBody", HtmlAction.SetClass, Theme.LightMode ? "light-mode" : "");
        }

        // Blocking browser reads are the deadlock hazard. The actual round-trip is
        // pushed onto a worker thread and waited on with a timeout, so a caller on
        // the UI thread can stall for at most timeoutMs instead of forever. Returns
        // null on timeout/failure; callers treat null as "skip this save".
        private string ReadBrowserHtml(string elementId, int timeoutMs = 1500)
        {
            if (_disposed) return null;
            var browser = this.Window?.Browser;
            if (browser == null) return null;

            try
            {
                var task = Task.Run(() =>
                {
                    try
                    {
                        var r = browser.GetHtmlValue(elementId, HtmlGetValueAction.GetProperty, "innerHTML");
                        return r?.Result as string;
                    }
                    catch
                    {
                        return null;
                    }
                });

                if (!task.Wait(timeoutMs))
                {
                    Core.Instance.Loggers.Log($"[TradeJournal] Browser read of '{elementId}' timed out after {timeoutMs}ms.");
                    return null;
                }

                return task.Result;
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] ReadBrowserHtml('{elementId}') failed: {ex.Message}");
                return null;
            }
        }

        // Two panels open on the same note file can collide on a write. Retry a
        // couple of times rather than throwing away the user's text.
        private static void WriteTextWithRetry(string path, string content)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.WriteAllText(path, content);
                    return;
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(40);
                }
                catch (UnauthorizedAccessException) when (attempt < 3)
                {
                    Thread.Sleep(40);
                }
            }
        }

        private static void DeleteFileWithRetry(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(40);
                }
                catch (UnauthorizedAccessException) when (attempt < 3)
                {
                    Thread.Sleep(40);
                }
            }
        }
        // ===================================================================

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

            // Claim primary/secondary status before anything with a global side
            // effect is wired up (see BUGFIX REGION note (b)).
            RegisterInstance();

            if (!AllowMultiplePanels && !_isPrimaryInstance)
            {
                // Inert placeholder: no timers, no renderers, no event handlers,
                // nothing that could collide with the panel that is already open.
                _inertPanel = true;
                Core.Instance.Loggers.Log(
                    "[TradeJournal] A Trade Journal panel is already open; this one stays inert.");
                return;
            }

            Directory.CreateDirectory(JournalFolder);
            Directory.CreateDirectory(TradeNotesFolder);
            Directory.CreateDirectory(ArchiveFolder);
            Directory.CreateDirectory(ScreenshotsFolder);

            SeedLivePositions();

            _saveDebounce = new System.Timers.Timer(1000);
            _saveDebounce.AutoReset = false;
            _saveDebounce.Elapsed += OnSaveDebounceElapsed;

            _tradeNoteSaveDebounce = new System.Timers.Timer(1000);
            _tradeNoteSaveDebounce.AutoReset = false;
            _tradeNoteSaveDebounce.Elapsed += OnTradeNoteSaveDebounceElapsed;

            _verseRotateTimer = new System.Timers.Timer(2 * 60 * 1000);
            _verseRotateTimer.AutoReset = true;
            _verseRotateTimer.Elapsed += (s, e) => { if (!_disposed) PushRandomVerse(); };
            _verseRotateTimer.Start();

            ApplyGridLayout();

            // Title bar renderer: placed in col 0, row 0 (same cell as calendar),
            // spanning both columns, with NO margin. The calendar and all other
            // renderers have NonClientMargin applied which shifts them down by
            // TitleBarH pixels, leaving exactly that strip for this renderer.
            var tbControl = this.Window.CreateRenderingControl("TradeJournalTitleBar");
            tbControl.Layout.Column = 0;
            tbControl.Layout.Row = 0;
            tbControl.Layout.ColumnSpan = 2;
            tbControl.Layout.RowSpan = 1;
            _tbRenderer = new TradeJournalTitleBarRenderer(tbControl, this);

            var calControl = this.Window.CreateRenderingControl("TradeJournalCalendar");
            calControl.Layout.Column = 0;
            calControl.Layout.Row = 0;
            calControl.Layout.RowSpan = 3;
            _calRenderer = new TradeJournalCalendarRenderer(calControl, this);
            _calRenderer.OnDaySelected += OnDaySelected;
            _calRenderer.OnPrevMonth += OnPrevMonth;
            _calRenderer.OnNextMonth += OnNextMonth;
            _calRenderer.OnWeekViewSelected += OnWeekViewSelected;
            _calRenderer.OnMonthViewSelected += OnMonthViewSelected;
            _calRenderer.OnSymbolFilterChanged += () => RenderTradeList();

            var tlControl = this.Window.CreateRenderingControl("TradeJournalTradeList");
            tlControl.Layout.Column = 1;
            tlControl.Layout.Row = 0;
            _tlRenderer = new TradeJournalTradeListRenderer(tlControl, this);
            _tlRenderer.OnTradeSelected += OnTradeRowClicked;

            var ssControl = this.Window.CreateRenderingControl("TradeJournalScreenshotStrip");
            ssControl.Layout.Column = 1;
            ssControl.Layout.Row = 1;
            _ssRenderer = new TradeJournalScreenshotStripRenderer(ssControl);

            this.Window.Browser.AddEventHandler("noteArea", "oninput", OnNoteInput);
            this.Window.Browser.AddEventHandler("tradeNoteArea", "oninput", OnTradeNoteInput);
            this.Window.Browser.Layout.Column = 1;
            this.Window.Browser.Layout.Row = 2;

            // Every panel listens so its own view stays live; only the primary
            // instance actually captures screenshots (see OnTradeAdded).
            Core.Instance.TradeAdded += OnTradeAdded;
            _tradeAddedSubscribed = true;
        }

        /// <summary>
        /// (Re)applies the two-column grid layout using a star ratio derived from _calWidthPct.
        /// Column 0 (calendar) gets _calWidthPct stars; column 1 (notes) gets the remainder.
        /// This keeps the split proportional to the panel so the calendar can never overflow.
        /// </summary>
        private void ApplyGridLayout()
        {
            if (_disposed || this.Window == null) return;

            this.Window.ReinitializeGridStructure(new NativeGridDefinition
            {
                Columns = new List<NativeGridItemDefinitionDefinition>
                {
                    new NativeGridItemDefinitionDefinition(false, _calColumnWidth) { SizeType = NativeGridItemDefinitionSizeType.Pixel },
                    new NativeGridItemDefinitionDefinition(false, 1) { SizeType = NativeGridItemDefinitionSizeType.Star }
                },
                Rows = new List<NativeGridItemDefinitionDefinition>
                {
                    // Row 0: trade list (large)
                    new NativeGridItemDefinitionDefinition(false, 10) { SizeType = NativeGridItemDefinitionSizeType.Star },
                    // Row 1: screenshot strip (small)
                    new NativeGridItemDefinitionDefinition(false, 1) { SizeType = NativeGridItemDefinitionSizeType.Star },
                    // Row 2: browser panel (large)
                    new NativeGridItemDefinitionDefinition(false, 10) { SizeType = NativeGridItemDefinitionSizeType.Star }
                }
            });

            // Calendar spans all three rows on the left.
            if (_calRenderer != null)
            {
                _calRenderer.Layout.Column = 0;
                _calRenderer.Layout.Row = 0;
                _calRenderer.Layout.RowSpan = 3;
            }

            // Trade list: right column, row 0.
            if (_tlRenderer != null)
            {
                _tlRenderer.Layout.Column = 1;
                _tlRenderer.Layout.Row = 0;
                _tlRenderer.Layout.RowSpan = 1;
            }

            // Screenshot strip: right column, row 1.
            if (_ssRenderer != null)
            {
                _ssRenderer.Layout.Column = 1;
                _ssRenderer.Layout.Row = 1;
                _ssRenderer.Layout.RowSpan = 1;
            }

            // Browser: right column, row 2.
            if (this.Window?.Browser != null)
            {
                this.Window.Browser.Layout.Column = 1;
                this.Window.Browser.Layout.Row = 2;
                this.Window.Browser.Layout.RowSpan = 1;
            }
        }

        public override void Populate(PluginParameters args = null)
        {
            base.Populate(args);
            if (_account == null)
                _account = Core.Instance.Accounts.FirstOrDefault();

            // Stay subscribed for the lifetime of the panel: if the page is ever
            // reloaded we want to re-push the note/verse content into the fresh DOM.
            EnsureBrowserLoadedSubscription();

            // BUG 1 FIX: do not wait for BrowserLoaded. On a first manual creation
            // the browser is usually already loaded by now and the event will never
            // arrive, which is what left the trade list and the notes blank until a
            // full Quantower restart. The pump probes the DOM itself and completes
            // as soon as it answers, whether or not the event ever fires.
            StartInitialLoadPump();
        }

        private void OnBrowserLoaded(NativeWebBrowserEventArgs args)
        {
            if (_disposed) return;

            // A (re)load means the DOM we pushed content into is gone. Re-arm the
            // pump so the content gets pushed again.
            bool wasLoadedBefore;
            lock (_initLock)
            {
                wasLoadedBefore = _initialLoadCompleted;
                _initialLoadCompleted = false;
            }

            // Only a genuine reload (one that follows a completed load) throws away
            // the element handlers, so only then do we allow them to be re-attached.
            if (wasLoadedBefore)
                _screenshotHandlerAttached = false;

            _browserReady = false;
            StartInitialLoadPump();
        }

        private const string DomReadySentinel = "__tj_ready__";

        // ── Bible Verses ─────────────────────────────────────────────────────
        // Add or remove verses here. Format: ("verse text", "Reference")
        private static readonly (string Text, string Ref)[] BibleVerses =
                {
            ("I can do all things through Christ who strengthens me.", "Philippians 4:13"),
            ("The Lord is my shepherd; I shall not want.", "Psalm 23:1"),
            ("Be still, and know that I am God.", "Psalm 46:10"),
            ("For God so loved the world that he gave his one and only Son.", "John 3:16"),
            ("Cast all your anxiety on him because he cares for you.", "1 Peter 5:7"),
            ("But those who hope in the Lord will renew their strength.", "Isaiah 40:31"),
            ("May the God of hope fill you with all joy and peace as you trust in him.", "Romans 15:13"),
            ("He who began a good work in you will carry it on to completion.", "Philippians 1:6"),
            ("For the Spirit God gave us does not make us timid, but gives us power, love and self-discipline.", "2 Timothy 1:7"),
            ("And let us consider how we may spur one another on toward love and good deeds.", "Hebrews 10:24"),
            ("The Lord gives strength to his people; the Lord blesses his people with peace.", "Psalm 29:11"),
            ("Great peace have those who love your law, and nothing can make them stumble.", "Psalm 119:165"),
            ("You will keep in perfect peace those whose minds are steadfast, because they trust in you.", "Isaiah 26:3"),
            ("The Lord is trustworthy in all he promises and faithful in all he does.", "Psalm 145:13"),
            ("He gives strength to the weary and increases the power of the weak.", "Isaiah 40:29"),
            ("And we know that in all things God works for the good of those who love him.", "Romans 8:28"),
            ("He will cover you with his feathers, and under his wings you will find refuge.", "Psalm 91:4"),
            ("The righteous will live by faith.", "Romans 1:17"),
            ("If you believe, you will receive whatever you ask for in prayer.", "Matthew 21:22"),
            ("Because you know that the testing of your faith produces perseverance.", "James 1:3"),
            ("Trust in the Lord with all your heart and lean not on your own understanding.", "Proverbs 3:5"),
            ("I have fought the good fight, I have finished the race, I have kept the faith.", "2 Timothy 4:7"),
            ("My flesh and my heart may fail, but God is the strength of my heart.", "Psalm 73:26"),
            ("In quietness and trust is your strength.", "Isaiah 30:15"),
            ("The Lord is my light and my salvation\u2014whom shall I fear?", "Psalm 27:1"),
            ("Do not be anxious about anything, but in every situation present your requests to God.", "Philippians 4:6"),
            ("The Lord is close to the brokenhearted and saves those who are crushed in spirit.", "Psalm 34:18"),
            ("I lift up my eyes to the mountains\u2014where does my help come from? My help comes from the Lord.", "Psalm 121:1-2"),
            ("Even youths grow tired and weary, but those who hope in the Lord will soar on wings like eagles.", "Isaiah 40:30-31"),
            ("For I know the plans I have for you, plans to prosper you and not to harm you.", "Jeremiah 29:11"),
            ("The Lord your God is with you, the Mighty Warrior who saves; he will rejoice over you with singing.", "Zephaniah 3:17"),
            ("Let us not become weary in doing good, for at the proper time we will reap a harvest.", "Galatians 6:9"),
            ("Be strong and courageous. Do not be afraid; do not be discouraged, for the Lord your God is with you.", "Joshua 1:9"),
            ("Commit to the Lord whatever you do, and he will establish your plans.", "Proverbs 16:3"),
            ("The name of the Lord is a fortified tower; the righteous run to it and are safe.", "Proverbs 18:10"),
            ("No weapon forged against you will prevail, and you will refute every tongue that accuses you.", "Isaiah 54:17"),
            ("Come to me, all you who are weary and burdened, and I will give you rest.", "Matthew 11:28"),
            ("With God all things are possible.", "Matthew 19:26"),
            ("I have told you these things so that in me you may have peace. In this world you will have trouble. But take heart! I have overcome the world.", "John 16:33"),
            ("If God is for us, who can be against us?", "Romans 8:31"),
            ("Neither death nor life, nor angels nor demons, nor the present nor the future, shall be able to separate us from the love of God.", "Romans 8:38-39"),
            ("Now to him who is able to do immeasurably more than all we ask or imagine, according to his power that is at work within us.", "Ephesians 3:20"),
            ("I can do all this through him who gives me strength, and my God will meet all your needs.", "Philippians 4:13-19"),
            ("For our struggle is not against flesh and blood, but against the spiritual forces of evil in the heavenly realms.", "Ephesians 6:12"),
            ("Above all else, guard your heart, for everything you do flows from it.", "Proverbs 4:23"),
            ("The fear of the Lord is the beginning of wisdom; all who follow his precepts have good understanding.", "Psalm 111:10"),
            ("Blessed is the one who trusts in the Lord, whose confidence is in him.", "Jeremiah 17:7"),
            ("I sought the Lord, and he answered me; he delivered me from all my fears.", "Psalm 34:4"),
            ("The Lord is my rock, my fortress and my deliverer; my God is my rock, in whom I take refuge.", "Psalm 18:2"),
            ("This is the day the Lord has made; let us rejoice and be glad in it.", "Psalm 118:24"),
            ("The joy of the Lord is your strength.", "Nehemiah 8:10"),
            ("Taste and see that the Lord is good; blessed is the one who takes refuge in him.", "Psalm 34:8"),
            ("The Lord will fight for you; you need only to be still.", "Exodus 14:14"),
            ("You are the light of the world.", "Matthew 5:14"),
            ("Ask and it will be given to you; seek and you will find; knock and the door will be opened to you.", "Matthew 7:7"),
            ("Peace I leave with you; my peace I give you. Do not let your hearts be troubled and do not be afraid.", "John 14:27"),
            ("Your word is a lamp for my feet, a light on my path.", "Psalm 119:105"),
            ("Delight yourself in the Lord, and he will give you the desires of your heart.", "Psalm 37:4"),
            ("The Lord is good, a refuge in times of trouble. He cares for those who trust in him.", "Nahum 1:7"),
            ("My grace is sufficient for you, for my power is made perfect in weakness.", "2 Corinthians 12:9"),
            ("The Lord is compassionate and gracious, slow to anger, abounding in love.", "Psalm 103:8"),
            ("Rejoice in the Lord always. I will say it again: Rejoice!", "Philippians 4:4"),
            ("The Lord is my strength and my shield; my heart trusts in him, and he helps me.", "Psalm 28:7"),
            ("Let all that you do be done in love.", "1 Corinthians 16:14"),
            ("Be joyful in hope, patient in affliction, faithful in prayer.", "Romans 12:12"),
            ("Give thanks to the Lord, for he is good; his love endures forever.", "Psalm 107:1"),
            ("The Lord is gracious and righteous; our God is full of compassion.", "Psalm 116:5"),
            ("The unfolding of your words gives light; it gives understanding to the simple.", "Psalm 119:130"),
            ("Those who look to him are radiant; their faces are never covered with shame.", "Psalm 34:5"),
            ("Let the peace of Christ rule in your hearts.", "Colossians 3:15"),
            ("Whatever you do, work at it with all your heart, as working for the Lord.", "Colossians 3:23"),
            ("God is our refuge and strength, an ever-present help in trouble.", "Psalm 46:1"),
            ("Wait for the Lord; be strong and take heart and wait for the Lord.", "Psalm 27:14"),
            ("The Lord makes firm the steps of the one who delights in him.", "Psalm 37:23"),
            ("Be completely humble and gentle; be patient, bearing with one another in love.", "Ephesians 4:2"),
            ("Do not be afraid or terrified because of them, for the Lord your God goes with you; he will never leave you nor forsake you.", "Deuteronomy 31:6"),
            ("Look to the Lord and his strength; seek his face always.", "1 Chronicles 16:11"),
            ("I keep my eyes always on the Lord. With him at my right hand, I will not be shaken.", "Psalm 16:8"),
            ("Be strong and take heart, all you who hope in the Lord.", "Psalm 31:24"),
            ("Cast your cares on the Lord and he will sustain you; he will never let the righteous be shaken.", "Psalm 55:22"),
            ("Yes, my soul, find rest in God; my hope comes from him. Truly he is my rock and my salvation; he is my fortress, I will not be shaken.", "Psalm 62:5-6"),
            ("Blessed are those whose strength is in you, whose hearts are set on pilgrimage.", "Psalm 84:5"),
            ("When anxiety was great within me, your consolation brought me joy.", "Psalm 94:19"),
            ("When I called, you answered me; you greatly emboldened me.", "Psalm 138:3"),
            ("He heals the brokenhearted and binds up their wounds.", "Psalm 147:3"),
            ("Surely God is my salvation; I will trust and not be afraid. The Lord, the Lord himself, is my strength and my defense.", "Isaiah 12:2"),
            ("When you pass through the waters, I will be with you; and when you pass through the rivers, they will not sweep over you.", "Isaiah 43:2"),
            ("I will refresh the weary and satisfy the faint.", "Jeremiah 31:25"),
            ("Because of the Lord's great love we are not consumed, for his compassions never fail. They are new every morning; great is your faithfulness.", "Lamentations 3:22-23"),
            ("Not by might nor by power, but by my Spirit, says the Lord Almighty.", "Zechariah 4:6"),
            ("Therefore do not worry about tomorrow, for tomorrow will worry about itself. Each day has enough trouble of its own.", "Matthew 6:34"),
            ("Be on your guard; stand firm in the faith; be courageous; be strong.", "1 Corinthians 16:13"),
            };
        private static readonly Random _verseRng = new Random();
        private System.Timers.Timer _verseRotateTimer;
        // ─────────────────────────────────────────────────────────────────────

        // --- Initial load pump ---------------------------------------------------
        // Repeatedly probes the DOM on a background thread until it answers, then
        // completes initialization exactly once. Runs off the UI thread so the
        // blocking browser round-trip can never stall Quantower's message pump.

        private void StartInitialLoadPump()
        {
            if (_disposed) return;

            lock (_initLock)
            {
                if (_initialLoadCompleted) return;
                if (_initPump != null) return;   // already running

                _initPumpAttempts = 0;
                _initPump = new System.Timers.Timer(InitPumpIntervalMs) { AutoReset = true };
                _initPump.Elapsed += OnInitPumpElapsed;
                _initPump.Start();
            }
        }

        private void StopInitialLoadPump()
        {
            System.Timers.Timer pump;
            lock (_initLock)
            {
                pump = _initPump;
                _initPump = null;
            }

            if (pump == null) return;
            try
            {
                pump.Stop();
                pump.Elapsed -= OnInitPumpElapsed;
                pump.Dispose();
            }
            catch { }
        }

        private void OnInitPumpElapsed(object sender, ElapsedEventArgs e)
        {
            if (_disposed)
            {
                StopInitialLoadPump();
                return;
            }

            // A slow probe must not stack up behind itself.
            if (Interlocked.Exchange(ref _initPumpBusy, 1) == 1) return;

            try
            {
                EnsureBrowserLoadedSubscription();

                int attempt = ++_initPumpAttempts;
                bool domConfirmed = ProbeDomReady(attempt);

                if (!domConfirmed && attempt < InitPumpMaxAttempts) return;

                if (!domConfirmed)
                    Core.Instance.Loggers.Log(
                        "[TradeJournal] DOM readiness could not be confirmed; loading anyway and re-syncing afterwards.");

                StopInitialLoadPump();
                CompleteInitialLoad(_selectedDate, domConfirmed);
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] Initial load pump error: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _initPumpBusy, 0);
            }
        }

        // Writes a sentinel into the contenteditable div with SetInnerHtml (the same
        // path LoadNote uses) and reads it back (the same path SaveNoteFromBrowser
        // uses), so a success here means both directions of the bridge really work.
        private bool ProbeDomReady(int attempt)
        {
            if (_disposed) return false;
            var browser = this.Window?.Browser;
            if (browser == null) return false;

            try
            {
                browser.UpdateHtml("noteArea", HtmlAction.SetInnerHtml, DomReadySentinel);
                var actual = ReadBrowserHtml("noteArea", timeoutMs: 1200);
                return actual != null && actual.Trim() == DomReadySentinel;
            }
            catch (Exception ex)
            {
                if (attempt == 1 || attempt % 10 == 0)
                    Core.Instance.Loggers.Log(
                        $"[TradeJournal] DOM readiness check failed (attempt {attempt}/{InitPumpMaxAttempts}): {ex.Message}");
                return false;
            }
        }

        private void CompleteInitialLoad(string date, bool domConfirmed)
        {
            if (_disposed) return;

            lock (_initLock)
            {
                if (_initialLoadCompleted) return;
                _initialLoadCompleted = true;
            }

            try
            {
                _browserReady = true;

                if (_inertPanel)
                {
                    BrowserUpdate("selectedDateLabel", HtmlAction.SetTextContent, "Trade Journal");
                    BrowserUpdate("noteArea", HtmlAction.SetInnerHtml,
                        "Trade Journal is already open in another panel. Close this one and use the existing panel.");
                    return;
                }

                // Attach exactly once, even if the page reloads and we come back here.
                if (!_screenshotHandlerAttached)
                {
                    var browser = this.Window?.Browser;
                    if (browser != null)
                    {
                        browser.AddEventHandler("screenshotStrip", "onclick",
                            (elementId, args) => OnScreenshotThumbnailClicked(args?.ToString()),
                            stopPropogation: false, valueSource: "value");
                        _screenshotHandlerAttached = true;
                    }
                }

                LoadNote(date);
                PushRandomVerse();   // show a verse on startup; the timer rotates it afterwards
                InitializeTradeList();
                ApplyThemeToBrowser();
                _calRenderer?.Redraw();

                if (!domConfirmed)
                    StartContentResync();
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] CompleteInitialLoad error: {ex.Message}");
            }
        }

        // Only used when the DOM never confirmed: push the content a few more times
        // in case the div came alive right after we gave up waiting for it. Every
        // call here is idempotent, so a redundant pass costs nothing.
        private void StartContentResync()
        {
            if (_disposed) return;
            if (_resyncTimer != null) return;

            _resyncCount = 0;
            _resyncTimer = new System.Timers.Timer(2500) { AutoReset = true };
            _resyncTimer.Elapsed += OnResyncElapsed;
            _resyncTimer.Start();
        }

        private void OnResyncElapsed(object sender, ElapsedEventArgs e)
        {
            if (_disposed || ++_resyncCount > ResyncMaxPasses)
            {
                StopContentResync();
                return;
            }

            try
            {
                LoadNote(_selectedDate);
                PushRandomVerse();
                RenderTradeList(preserveScroll: true);
                ApplyThemeToBrowser();
                _calRenderer?.Redraw();
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] Content re-sync error: {ex.Message}");
            }
        }

        private void StopContentResync()
        {
            var t = _resyncTimer;
            _resyncTimer = null;
            if (t == null) return;
            try
            {
                t.Stop();
                t.Elapsed -= OnResyncElapsed;
                t.Dispose();
            }
            catch { }
        }

        public override void Dispose()
        {
            // Set first: every timer callback and every browser touch checks this,
            // so nothing can reach the window while it is being torn down.
            _disposed = true;

            try
            {
                if (_tradeAddedSubscribed)
                {
                    Core.Instance.TradeAdded -= OnTradeAdded;
                    _tradeAddedSubscribed = false;
                }
            }
            catch { }

            try
            {
                if (_browserLoadedSubscribed && this.Window?.Browser != null)
                    this.Window.Browser.BrowserLoaded -= OnBrowserLoaded;
                _browserLoadedSubscribed = false;
            }
            catch { }

            StopInitialLoadPump();
            StopContentResync();

            try { _saveDebounce?.Stop(); _saveDebounce?.Dispose(); } catch { }
            try { _tradeNoteSaveDebounce?.Stop(); _tradeNoteSaveDebounce?.Dispose(); } catch { }
            try { _verseRotateTimer?.Stop(); _verseRotateTimer?.Dispose(); } catch { }

            try { _calRenderer?.Dispose(); } catch { }
            try { _tlRenderer?.Dispose(); } catch { }
            try { _ssRenderer?.Dispose(); } catch { }
            try { _tbRenderer?.Dispose(); } catch { }
            _calRenderer = null;
            _tlRenderer = null;
            _ssRenderer = null;
            _tbRenderer = null;

            UnregisterInstance();

            base.Dispose();
        }

        protected override void OnLayoutUpdated()
        {
            base.OnLayoutUpdated();
            if (_disposed) return;

            // Reserve space for Quantower's native caption on every panel in the
            // right column, not just the browser, so nothing paints over the
            // title bar / caption buttons.
            //
            // Only the top-row renderers (calendar and trade list, both in row 0)
            // need the full NonClientMargin including its Top offset.  The screenshot
            // strip (row 1) and browser (row 2) sit below those renderers inside the
            // grid; applying the full top margin to them too pushes them down a second
            // time, which creates the visible blue gap above and below the strip in
            // standalone (floating) mode.  Build a zero-top copy via dynamic so we
            // read the Left/Right/Bottom values without a compile-time ctor signature
            // dependency, then construct the typed NativeThickness directly.
            var fullMargin = this.NonClientMargin;
            dynamic dm = fullMargin;
            var noTopMargin = new NativeThickness(dm.Left, 0, dm.Right, dm.Bottom);

            if (_calRenderer != null)
                _calRenderer.Layout.Margin = fullMargin;
            if (_tlRenderer != null)
                _tlRenderer.Layout.Margin = fullMargin;
            if (_ssRenderer != null)
                _ssRenderer.Layout.Margin = noTopMargin;
            if (this.Window?.Browser != null)
                this.Window.Browser.Layout.Margin = noTopMargin;

            // Title bar renderer has no margin — it occupies the strip above
            // the other renderers. Redraw so it can react to float/tab changes.
            _tbRenderer?.Redraw();
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
                result.Add(new SettingItemDouble("FontScale", _fontScale)
                {
                    Text = "Font Size",
                    SortIndex = 3,
                    Minimum = 0.5,
                    Maximum = 2.0,
                    Increment = 0.05,
                    DecimalPlaces = 2
                });
                result.Add(new SettingItemInteger("CalendarColumnWidth", _calColumnWidth)
                {
                    Text = "Calendar Width (px)",
                    SortIndex = 4,
                    Minimum = CalWidthMin,
                    Maximum = CalWidthMax
                });
                result.Add(new SettingItemBoolean("CalculateFees", _calculateFees)
                { Text = "Calculate Commissions & Fees", SortIndex = 5 });
                result.Add(new SettingItemDouble("FeePerMicro", _feePerMicro)
                { Text = "Fee Per Micro Contract (Round Trip)", SortIndex = 6, Increment = 0.01, DecimalPlaces = 2 });
                result.Add(new SettingItemDouble("FeePerMini", _feePerMini)
                { Text = "Fee Per Mini Contract (Round Trip)", SortIndex = 7, Increment = 0.01, DecimalPlaces = 2 });
                result.Add(new SettingItemString("MicroSymbols", _microSymbols)
                { Text = "Micro Symbols (comma-separated)", SortIndex = 8 });
                result.Add(new SettingItemString("MiniSymbols", _miniSymbols)
                { Text = "Mini Symbols (comma-separated)", SortIndex = 9 });
                result.Add(new SettingItemString("Slot0Start", SlotStart0.ToString(@"h\:mm"))
                { Text = "Time Slot 1 Start (H:mm)", SortIndex = 10 });
                result.Add(new SettingItemString("Slot1Start", SlotStart1.ToString(@"h\:mm"))
                { Text = "Time Slot 2 Start (H:mm)", SortIndex = 11 });
                result.Add(new SettingItemString("Slot2Start", SlotStart2.ToString(@"h\:mm"))
                { Text = "Time Slot 3 Start (H:mm)", SortIndex = 12 });
                result.Add(new SettingItemString("Slot3Start", SlotStart3.ToString(@"h\:mm"))
                { Text = "Time Slot 4 Start (H:mm)", SortIndex = 13 });
                result.Add(new SettingItemString("Slot4Start", SlotStart4.ToString(@"h\:mm"))
                { Text = "Time Slot 5 Start (H:mm)", SortIndex = 14 });
                result.Add(new SettingItemString("Slot5Start", SlotStart5.ToString(@"h\:mm"))
                { Text = "Time Slot 6 Start (H:mm)", SortIndex = 15 });
                result.Add(new SettingItemString("Slot6Start", SlotStart6.ToString(@"h\:mm"))
                { Text = "Time Slot 7 Start (H:mm)", SortIndex = 16 });
                result.Add(new SettingItemString("Slot6End", SlotEnd6.ToString(@"h\:mm"))
                { Text = "Time Slot 7 End (H:mm)", SortIndex = 17 });
                result.Add(new SettingItemBoolean("DailyReset",
                    _dailyReset == DailyResetMode.SessionBoundary)
                { Text = "Session Reset (5pm ET, includes Sun night)", SortIndex = 18 });
                result.Add(new SettingItemBoolean("ShowAdditionalMetrics", _showAdditionalMetrics)
                { Text = "Show Additional Metrics Chart", SortIndex = 19 });
                result.Add(new SettingItemBoolean("LightMode", Theme.LightMode)
                { Text = "Light Mode", SortIndex = 20 });
                return result;
            }
            set
            {
                base.Settings = value;
                if (_disposed) return;

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
                        case "FontScale":
                            double newFontScale = Convert.ToDouble(item.Value);
                            if (Math.Abs(newFontScale - _fontScale) > 0.0001)
                            {
                                _fontScale = newFontScale;
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
                        case "ShowAdditionalMetrics":
                            bool newShowAdditional = (bool)item.Value;
                            if (newShowAdditional != _showAdditionalMetrics)
                            {
                                _showAdditionalMetrics = newShowAdditional;
                                layoutChanged = true;
                            }
                            break;
                        case "LightMode":
                            bool newLightMode = (bool)item.Value;
                            if (newLightMode != Theme.LightMode)
                            {
                                Theme.LightMode = newLightMode;
                                ApplyThemeToBrowser();
                                _tbRenderer?.Redraw();          // ← ADD THIS LINE
                                layoutChanged = true; // force a redraw so the new palette shows immediately
                            }
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
                        case "Slot0Start": if (TryParseSlot((string)item.Value, out var s0)) { SlotStart0 = s0; InvalidateStatsCache(); } break;
                        case "Slot1Start": if (TryParseSlot((string)item.Value, out var s1)) { SlotStart1 = s1; InvalidateStatsCache(); } break;
                        case "Slot2Start": if (TryParseSlot((string)item.Value, out var s2)) { SlotStart2 = s2; InvalidateStatsCache(); } break;
                        case "Slot3Start": if (TryParseSlot((string)item.Value, out var s3)) { SlotStart3 = s3; InvalidateStatsCache(); } break;
                        case "Slot4Start": if (TryParseSlot((string)item.Value, out var s4)) { SlotStart4 = s4; InvalidateStatsCache(); } break;
                        case "Slot5Start": if (TryParseSlot((string)item.Value, out var s5)) { SlotStart5 = s5; InvalidateStatsCache(); } break;
                        case "Slot6Start": if (TryParseSlot((string)item.Value, out var s6)) { SlotStart6 = s6; InvalidateStatsCache(); } break;
                        case "Slot6End": if (TryParseSlot((string)item.Value, out var s7)) { SlotEnd6 = s7; InvalidateStatsCache(); } break;
                        case "DailyReset":
                            {
                                var newReset = (bool)item.Value
                                    ? DailyResetMode.SessionBoundary
                                    : DailyResetMode.Midnight;
                                if (newReset != _dailyReset)
                                {
                                    _dailyReset = newReset;
                                    InvalidateStatsCache();
                                }
                                break;
                            }
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
                if (_disposed) return;
                if (_account != null && !trade.Account.Equals(_account)) return;

                // Screenshot capture — must happen before cache invalidation so the
                // position tracker update and screen capture fire on the actual fill event.
                //
                // BUG 2 FIX: only one panel captures. With two panels open this used to
                // fire two full-screen BitBlts per fill, both writing the same file path
                // at the same time.
                if (_isPrimaryInstance)
                    CaptureScreenshotForFill(trade);

                DateTime localTime = trade.DateTime.ToLocalTime();
                string dateKey = GetDayKey(localTime);
                // Parse dateKey back to a DateTime for month/week lookup
                DateTime tradeDate = DateTime.Parse(dateKey);

                // Invalidate just the specific month this trade falls in — adjacent
                // months shown as dimmed fill days are cached independently and will
                // pick this up next time they're queried.
                _monthStatsCache.Remove((tradeDate.Year, tradeDate.Month));

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
                RenderTradeList(preserveScroll: true); // keep the trade list panel in sync with new fills, without
                                                       // resetting expand/open-note state the way a view change would
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] OnTradeAdded error: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // Screenshot capture
        // -----------------------------------------------------------------------

        // Called from OnTradeAdded. Updates the live position tracker, decides
        // entry vs exit, deduplicates within a 500ms window, then fires capture.
        private void CaptureScreenshotForFill(Trade trade)
        {
            try
            {
                string root = GetSymbolRoot(trade.Symbol.Name);
                double signedQty = trade.Quantity * (trade.Side == Side.Buy ? 1.0 : -1.0);

                double prevQty, newQty;
                lock (_screenshotLock)
                {
                    _livePositions.TryGetValue(root, out prevQty);
                    newQty = prevQty + signedQty;
                    _livePositions[root] = newQty;
                }

                // Determine label: if absolute position grew → entry; if shrank → exit.
                bool isEntry = Math.Abs(newQty) >= Math.Abs(prevQty);
                DateTime localTime = trade.DateTime.ToLocalTime();
                string dayKey = GetDayKey(localTime);
                string dayFolder = Path.Combine(ScreenshotsFolder, dayKey);
                Directory.CreateDirectory(dayFolder);

                // Deduplicate: if a screenshot for this symbol was already taken within
                // the last 500ms and has the same entry/exit label, reuse that file.
                lock (_screenshotLock)
                {
                    if (_pendingScreenshots.TryGetValue(root, out var pending))
                    {
                        if ((localTime - pending.WindowStart).TotalMilliseconds <= 500
                            && pending.IsEntry == isEntry)
                        {
                            // Already captured within the deduplication window — nothing to do.
                            return;
                        }
                    }

                    // Build filename: YYYYMMDD_HHmmss_SYMBOL_entry/exit.bmp
                    string label = isEntry ? "entry" : "exit";
                    string fileName = $"{localTime:yyyyMMdd_HHmmss}_{root}_{label}.bmp";
                    string filePath = Path.Combine(dayFolder, fileName);

                    // Register this as the active deduplication window.
                    _pendingScreenshots[root] = (localTime, filePath, isEntry);

                    // Entry screenshots fire after a 1-second delay so chart markers paint.
                    // Exit screenshots fire immediately (position is already flat).
                    int delayMs = isEntry ? 1000 : 0;
                    string captureTarget = filePath; // capture for lambda closure
                    Task.Delay(delayMs).ContinueWith(_ => DoCapture(captureTarget));
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] CaptureScreenshotForFill error: {ex.Message}");
            }
        }

        // Captures the primary monitor and saves it as a BMP file.
        // BMP is chosen deliberately: it is a trivial header + raw pixels with
        // zero compression or checksum logic, so it cannot produce a corrupt file.
        // Every Windows application can open BMP natively.
        private static void DoCapture(string filePath)
        {
            IntPtr hdcScreen = IntPtr.Zero;
            IntPtr hdcMem = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            try
            {
                int width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
                int height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);

                hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
                hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
                hBitmap = NativeMethods.CreateCompatibleBitmap(hdcScreen, width, height);

                IntPtr hOld = NativeMethods.SelectObject(hdcMem, hBitmap);
                NativeMethods.BitBlt(hdcMem, 0, 0, width, height,
                                     hdcScreen, 0, 0, NativeMethods.SRCCOPY);
                NativeMethods.SelectObject(hdcMem, hOld);

                // GetDIBits with negative biHeight gives top-down BGRA rows.
                var bmi = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = (uint)width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 24,        // 24-bit BGR — no alpha, smaller file
                    biCompression = 0,         // BI_RGB
                };
                // BMP rows must be padded to a 4-byte boundary.
                int rowBytes = ((width * 3 + 3) / 4) * 4;
                byte[] pixels = new byte[rowBytes * height];
                NativeMethods.GetDIBits(hdcMem, hBitmap, 0, (uint)height,
                                        pixels, ref bmi, 0);

                WriteBmp(filePath, pixels, width, height, rowBytes);
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] DoCapture error: {ex.Message}");
            }
            finally
            {
                if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
                if (hdcMem != IntPtr.Zero) NativeMethods.DeleteDC(hdcMem);
                if (hdcScreen != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }

        // Writes a valid 24-bit BMP file from raw BGR pixel rows.
        // BMP format: BITMAPFILEHEADER (14 bytes) + BITMAPINFOHEADER (40 bytes) + pixels.
        private static void WriteBmp(string filePath, byte[] pixels,
                                     int width, int height, int rowBytes)
        {
            int headerSize = 14 + 40; // BITMAPFILEHEADER + BITMAPINFOHEADER
            int fileSize = headerSize + pixels.Length;

            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                // BITMAPFILEHEADER
                w.Write((ushort)0x4D42); // 'BM'
                w.Write((uint)fileSize);
                w.Write((ushort)0);      // reserved
                w.Write((ushort)0);      // reserved
                w.Write((uint)headerSize); // offset to pixel data

                // BITMAPINFOHEADER
                w.Write((uint)40);       // header size
                w.Write((int)width);
                // Positive height = bottom-up (standard BMP). GetDIBits with negative
                // biHeight returned top-down rows, so we write them reversed here so
                // the BMP displays right-side up.
                w.Write((int)height);
                w.Write((ushort)1);      // planes
                w.Write((ushort)24);     // bits per pixel
                w.Write((uint)0);        // compression: BI_RGB
                w.Write((uint)pixels.Length);
                w.Write((int)2835);      // X pels per metre (~72 dpi)
                w.Write((int)2835);      // Y pels per metre
                w.Write((uint)0);        // colours used
                w.Write((uint)0);        // colours important

                // Pixel data — write rows bottom-up (last row of the top-down buffer first)
                // so the BMP is correctly oriented without needing a separate flip pass.
                for (int y = height - 1; y >= 0; y--)
                    w.Write(pixels, y * rowBytes, rowBytes);
            }
        }

        // Win32 imports for screen capture — no System.Windows.Forms dependency.
        private static class NativeMethods
        {
            public const int SM_CXSCREEN = 0;
            public const int SM_CYSCREEN = 1;
            public const uint SRCCOPY = 0x00CC0020;

            [System.Runtime.InteropServices.DllImport("gdi32.dll")]
            public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest,
                int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

            [System.Runtime.InteropServices.DllImport("gdi32.dll")]
            public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

            [System.Runtime.InteropServices.DllImport("gdi32.dll")]
            public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);

            [System.Runtime.InteropServices.DllImport("gdi32.dll")]
            public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

            [System.Runtime.InteropServices.DllImport("gdi32.dll")]
            public static extern bool DeleteObject(IntPtr h);

            [System.Runtime.InteropServices.DllImport("gdi32.dll")]
            public static extern bool DeleteDC(IntPtr hdc);

            [System.Runtime.InteropServices.DllImport("gdi32.dll")]
            public static extern int GetDIBits(IntPtr hdc, IntPtr hBitmap,
                uint startScan, uint scanLines, byte[] lpvBits,
                ref BITMAPINFOHEADER lpbi, uint uUsage);

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern IntPtr GetDC(IntPtr hwnd);

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern int GetSystemMetrics(int nIndex);

            [System.Runtime.InteropServices.StructLayout(
                System.Runtime.InteropServices.LayoutKind.Sequential)]
            public struct BITMAPINFOHEADER
            {
                public uint biSize, biWidth;
                public int biHeight;
                public ushort biPlanes, biBitCount;
                public uint biCompression, biSizeImage;
                public int biXPelsPerMeter, biYPelsPerMeter;
                public uint biClrUsed, biClrImportant;
            }
        }

        // Returns all screenshot paths for a specific round-trip trade, split into entry
        // and exit lists sorted chronologically. Screenshots are matched to this trade by
        // time window: entry shots within 2 minutes of the trade's entry time, exit shots
        // within 2 minutes of the trade's exit time. This ensures that clicking one trade
        // in the list only shows icons for that trade, not every trade of the same symbol
        // on the same day.
        private (List<string> Entries, List<string> Exits) GetScreenshotsForTrade(
    string dayKey, string symbolRoot,
    DateTime entryTime = default, DateTime exitTime = default)
        {
            var entries = new List<string>();
            var exits = new List<string>();
            string dayFolder = Path.Combine(ScreenshotsFolder, dayKey);
            if (!Directory.Exists(dayFolder)) return (entries, exits);

            // Scan for ALL screenshots taken between the trade's entry and exit times,
            // inclusive of a small buffer on each end. This correctly handles scaled-in
            // and scaled-out positions where multiple entry/exit fills fire mid-trade.
            const double BufferSeconds = 3.0;
            DateTime windowStart = entryTime == default ? DateTime.MinValue : entryTime.AddSeconds(-BufferSeconds);
            DateTime windowEnd = exitTime == default ? DateTime.MaxValue : exitTime.AddSeconds(+BufferSeconds);

            var extensions = new[] { "*.bmp", "*.png", "*.jpg", "*.jpeg" };
            var allFiles = extensions
                .SelectMany(ext => Directory.GetFiles(dayFolder, ext))
                .OrderBy(f => f);

            foreach (var file in allFiles)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                var parts = name.Split('_');
                if (parts.Length < 4) continue;
                string sym = parts[2];
                string label = parts[3];
                if (!sym.Equals(symbolRoot, StringComparison.OrdinalIgnoreCase)) continue;

                bool hasCaptureTime = DateTime.TryParseExact(
                    parts[0] + "_" + parts[1], "yyyyMMdd_HHmmss",
                    CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None,
                    out DateTime captureTime);

                // If we can't parse the timestamp, include the file — better than silently dropping it.
                if (!hasCaptureTime || (captureTime >= windowStart && captureTime <= windowEnd))
                {
                    if (label == "entry") entries.Add(file);
                    else if (label == "exit") exits.Add(file);
                }
            }

            return (entries, exits);
        }

        // Rebuilds the trade note header line with label + thumbnail strip.
        // Called whenever a trade row is clicked or the note panel is refreshed.
        private void UpdateTradeNoteHeader(string tradeKey)
        {
            if (_disposed) return;
            if (!_browserReady) return;

            string label = FormatTradeKeyLabel(tradeKey);

            // Parse dayKey and symbol from tradeKey (format: SYMBOL|ExitTime)
            var keyParts = tradeKey.Split('|');
            string symRoot = keyParts.Length > 0 ? GetSymbolRoot(keyParts[0]) : "";
            string dayKey = "";
            if (keyParts.Length > 1 && DateTime.TryParse(keyParts[1], out DateTime exitDt))
                dayKey = GetDayKey(exitDt.Kind == DateTimeKind.Utc ? exitDt.ToLocalTime() : exitDt);
            if (string.IsNullOrEmpty(dayKey))
                dayKey = GetDayKey(DateTime.Now);

            // Resolve entry and exit times from the trade list renderer for time-window filtering.
            DateTime tradeEntryTime = default;
            DateTime tradeExitTime = default;
            if (_tlRenderer != null)
            {
                var et = _tlRenderer.GetEntryTimeForKey(tradeKey);
                if (et.HasValue) tradeEntryTime = et.Value;
            }
            if (keyParts.Length > 1 && DateTime.TryParse(keyParts[1], out DateTime parsedExit))
                tradeExitTime = parsedExit.Kind == DateTimeKind.Utc ? parsedExit.ToLocalTime() : parsedExit;

            var (entryPaths, exitPaths) = GetScreenshotsForTrade(dayKey, symRoot, tradeEntryTime, tradeExitTime);

            // Clear previous button registrations.
            _screenshotButtonPaths.Clear();

            if (entryPaths.Count == 0 && exitPaths.Count == 0)
            {
                BrowserUpdate("tradeNoteLabel", HtmlAction.SetInnerHtml,
                    $"<span class=\"trade-note-title\">{System.Net.WebUtility.HtmlEncode(label)}</span>");
                _ssRenderer?.Clear();
                return;
            }

            BrowserUpdate("tradeNoteLabel", HtmlAction.SetInnerHtml,
                $"<span class=\"trade-note-title\">{System.Net.WebUtility.HtmlEncode(label)}</span>");
            _ssRenderer?.SetScreenshots(entryPaths, exitPaths);
        }

        // Opens the full-resolution screenshot in the system default image viewer.
        // path arrives directly from the button's value attribute via valueSource="value" —
        // no dictionary lookup needed.
        private void OnScreenshotThumbnailClicked(string path)
        {
            Core.Instance.Loggers.Log($"[TradeJournal] OnScreenshotThumbnailClicked fired, path='{path}'");
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    Core.Instance.Loggers.Log("[TradeJournal] Screenshot click: empty path received");
                    return;
                }
                // HtmlEncode was applied when writing the value attr; decode it back.
                path = System.Net.WebUtility.HtmlDecode(path);
                if (!File.Exists(path))
                {
                    Core.Instance.Loggers.Log($"[TradeJournal] Screenshot click: file not found at path={path}");
                    return;
                }
                Core.Instance.Loggers.Log($"[TradeJournal] Opening screenshot: {path}");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] OnScreenshotThumbnailClicked error: {ex.Message}");
            }
        }

        // Thumbnail decode requires System.Drawing.Image which is unavailable in this
        // build environment (.NET 9, no System.Private.Windows.Core reference).
        // Returns null so UpdateTradeNoteHeader falls back to the 📷 emoji placeholder.
        // Clicking the placeholder still opens the full-resolution PNG via Process.Start.
        private static string LoadThumbnailBase64(string path, int thumbSize) => null;

        private void OnDaySelected(string date)
        {
            SaveNoteFromBrowser(_selectedDate);
            _selectedDate = date;
            _currentYear = int.Parse(date.Split('-')[0]);
            _currentMonth = int.Parse(date.Split('-')[1]) - 1;
            foreach (var key in _dayMetricsCache.Keys.Where(k => k.Date == date).ToList())
                _dayMetricsCache.Remove(key);
            LoadNote(_selectedDate);
            ClearTradeNoteSelection();
            RenderTradeList();
        }

        private void OnWeekViewSelected(string date)
        {
            ClearTradeNoteSelection();
            RenderTradeList();
        }

        private void OnMonthViewSelected()
        {
            ClearTradeNoteSelection();
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
            ClearTradeNoteSelection();
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
            ClearTradeNoteSelection();
            RenderTradeList();
            _calRenderer.Redraw();
        }

        private void OnNoteInput(string elementId, object args)
        {
            if (_disposed) return;
            _saveDebounce?.Stop();
            _saveDebounce?.Start();
            BrowserUpdate("saveIndicator", HtmlAction.SetTextContent, "typing...");
            BrowserUpdate("saveIndicator", HtmlAction.SetClass, "save-indicator");
        }

        private void OnSaveDebounceElapsed(object sender, ElapsedEventArgs e)
        {
            if (_disposed) return;
            SaveNoteFromBrowser(_selectedDate);
        }

        // --- Trade note (per-trade, small note area below the trade list) ---

        private void OnTradeNoteInput(string elementId, object args)
        {
            if (_disposed) return;
            _tradeNoteSaveDebounce?.Stop();
            _tradeNoteSaveDebounce?.Start();
            BrowserUpdate("tradeNoteSaveIndicator", HtmlAction.SetTextContent, "typing...");
            BrowserUpdate("tradeNoteSaveIndicator", HtmlAction.SetClass, "trade-note-save-indicator");
        }

        private void OnTradeNoteSaveDebounceElapsed(object sender, ElapsedEventArgs e)
        {
            if (_disposed) return;
            SaveTradeNoteFromBrowser(_selectedTradeKey);
        }

        private void SaveTradeNoteFromBrowser(string tradeKey)
        {
            if (_disposed) return;
            if (!_browserReady) return;
            if (string.IsNullOrEmpty(tradeKey)) return;
            if (!_tradeNoteLoadedKeys.Contains(tradeKey)) return;

            try
            {
                string raw = ReadBrowserHtml("tradeNoteArea");

                if (raw != null)
                {
                    if (raw.Length == 0 && _lastLoadedTradeNoteHtml != null && _lastLoadedTradeNoteHtml.Length > 0)
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

                    WriteTradeNoteToDisk(tradeKey, content);
                    BrowserUpdate("tradeNoteSaveIndicator", HtmlAction.SetTextContent, "saved");
                    BrowserUpdate("tradeNoteSaveIndicator", HtmlAction.SetClass, "trade-note-save-indicator saved");
                    // Refresh note-dot indicators without resetting scroll position
                    _tlRenderer?.RefreshNoteDots(GetTradeNoteKeys());
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] SaveTradeNoteFromBrowser error: {ex.Message}");
            }
        }

        private void WriteTradeNoteToDisk(string tradeKey, string content)
        {
            string path = Path.Combine(TradeNotesFolder, SanitizeTradeKey(tradeKey) + ".txt");
            if (string.IsNullOrWhiteSpace(content))
            {
                DeleteFileWithRetry(path);
            }
            else
            {
                WriteTextWithRetry(path, content);
            }
        }

        private void LoadTradeNote(string tradeKey)
        {
            try
            {
                string path = Path.Combine(TradeNotesFolder, SanitizeTradeKey(tradeKey) + ".txt");
                string content = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

                string htmlContent = content
                    .Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;");

                BrowserUpdate("tradeNoteArea", HtmlAction.SetInnerHtml, htmlContent);
                BrowserUpdate("tradeNoteSaveIndicator", HtmlAction.SetTextContent, "");
                BrowserUpdate("tradeNoteSaveIndicator", HtmlAction.SetClass, "trade-note-save-indicator");

                _lastLoadedTradeNoteHtml = htmlContent;
                _tradeNoteLoadedKeys.Add(tradeKey);
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] LoadTradeNote error: {ex.Message}");
            }
        }

        // Converts a TradeKey like "MES|2026-07-08T14:32:01.500" into a safe filename.
        // Milliseconds are stripped so that notes survive re-imports where the sub-second
        // precision may differ from the original server data.
        // Exposed publicly so the trade list renderer can check note existence without
        // duplicating the sanitization logic.
        public static string SanitizeTradeKeyPublic(string tradeKey) => SanitizeTradeKey(tradeKey);

        private static string SanitizeTradeKey(string tradeKey)
        {
            // Strip milliseconds: "SYMBOL|2026-07-08T14:32:01.500" → "SYMBOL|2026-07-08T14:32:01"
            int dotIndex = tradeKey.LastIndexOf('.');
            if (dotIndex > 0)
                tradeKey = tradeKey.Substring(0, dotIndex);

            return tradeKey
                .Replace("|", "_")
                .Replace(":", "-")
                .Replace("/", "-")
                .Replace("\\", "-");
        }

        // Called by the trade list renderer when a row is clicked.
        // tradeKey is the original RoundTripTrade.TradeKey ("SYMBOL|yyyy-MM-ddTHH:mm:ss.fff").
        private void OnTradeRowClicked(string tradeKey)
        {
            try
            {
                if (string.IsNullOrEmpty(tradeKey)) return;
                if (tradeKey == _selectedTradeKey) return; // already selected

                SaveTradeNoteFromBrowser(_selectedTradeKey);
                _tradeNoteSaveDebounce?.Stop();

                _selectedTradeKey = tradeKey;

                // Tell the renderer to highlight this row
                _tlRenderer?.SelectTradeKey(tradeKey);

                // Update the trade note header with label + screenshot strip (if any)
                UpdateTradeNoteHeader(tradeKey);

                LoadTradeNote(tradeKey);
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] OnTradeRowClicked error: {ex.Message}");
            }
        }



        // Produces a short label for the trade note header from a TradeKey.
        // Uses entry time when available (looked up from the trade list), falling back
        // to the exit time embedded in the key if the trade isn't in the current list.
        private string FormatTradeKeyLabel(string tradeKey)
        {
            // Try to find the matching trade in the current trade list so we can show entry time
            if (_tlRenderer != null)
            {
                var entryTime = _tlRenderer.GetEntryTimeForKey(tradeKey);
                if (entryTime.HasValue)
                {
                    var parts2 = tradeKey.Split('|');
                    string sym2 = parts2.Length > 0 ? parts2[0] : tradeKey;
                    return $"{sym2}  {entryTime.Value:MMM d  HH:mm:ss}";
                }
            }

            var parts = tradeKey.Split('|');
            if (parts.Length != 2) return tradeKey;
            string symbol = parts[0];
            if (DateTime.TryParse(parts[1], out DateTime dt))
                return $"{symbol}  {dt:MMM d  HH:mm:ss}";
            return tradeKey;
        }

        // Resets the trade note panel to the "nothing selected" state (called when the
        // day/week/month selection changes and the trade list is rebuilt).
        private void ClearTradeNoteSelection()
        {
            if (_disposed) return;
            if (!_browserReady) return;
            SaveTradeNoteFromBrowser(_selectedTradeKey);
            _tradeNoteSaveDebounce?.Stop();
            _selectedTradeKey = null;
            _lastLoadedTradeNoteHtml = null;
            _tlRenderer?.SelectTradeKey(null);
            BrowserUpdate("tradeNoteLabel", HtmlAction.SetInnerHtml, "<span class=\"trade-note-prefix\">Trade Note</span>");
            _ssRenderer?.Clear();
            BrowserUpdate("tradeNoteArea", HtmlAction.SetInnerHtml, "");
            BrowserUpdate("tradeNoteSaveIndicator", HtmlAction.SetTextContent, "");
            BrowserUpdate("tradeNoteSaveIndicator", HtmlAction.SetClass, "trade-note-save-indicator");
        }

        private void SaveNoteFromBrowser(string date)
        {
            if (_disposed) return;
            if (!_browserReady) return;
            if (string.IsNullOrEmpty(date)) return;
            if (!_loadedDates.Contains(date)) return;

            try
            {
                // Off-thread, timeout-bounded read. Callers on the UI thread
                // (day click, prev/next month) used to block here indefinitely.
                // null means the read didn't come back in time — skip the save
                // rather than risk writing a half-initialized DOM over the file.
                string raw = ReadBrowserHtml("noteArea");

                if (raw != null)
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
                    BrowserUpdate("saveIndicator", HtmlAction.SetTextContent, "saved");
                    BrowserUpdate("saveIndicator", HtmlAction.SetClass, "save-indicator saved");
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
                DeleteFileWithRetry(path);
            }
            else
            {
                // Retry-wrapped: two open panels can land on the same file.
                WriteTextWithRetry(path, content);
            }
        }

        private void PushRandomVerse()
        {
            try
            {
                if (_disposed) return;
                if (!_browserReady) return;
                var v = BibleVerses[_verseRng.Next(BibleVerses.Length)];
                string html = $"\"{v.Text}\" <span class=\"daily-verse-ref\">\u2014 {v.Ref}</span>";
                BrowserUpdate("dailyVerseText", HtmlAction.SetInnerHtml, html);
            }
            catch { }
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

                BrowserUpdate("selectedDateLabel", HtmlAction.SetTextContent, label);
                BrowserUpdate("noteArea", HtmlAction.SetInnerHtml, htmlContent);
                BrowserUpdate("saveIndicator", HtmlAction.SetTextContent, "");
                BrowserUpdate("saveIndicator", HtmlAction.SetClass, "save-indicator");

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

        // Passes the current trade list data to the GDI+ renderer for drawing.
        private void RenderTradeList(bool preserveScroll = false)
        {
            if (_disposed) return;
            if (!_tradeListReady || _tlRenderer == null) return;

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

                bool showDaySeparators = isMonthly || isWeekly;
                var noteKeys = GetTradeNoteKeys();
                _tlRenderer.SetTrades(listResult, showDaySeparators, _selectedTradeKey, noteKeys, resetScroll: !preserveScroll);
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
            _yearlyMetricsCache.Clear();
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

                        DateTime localTime = trade.DateTime.ToLocalTime();
                        string fillDayKey = GetDayKey(localTime);
                        var fillParts = fillDayKey.Split('-');
                        int fillYear = int.Parse(fillParts[0], CultureInfo.InvariantCulture);
                        int fillMonth = int.Parse(fillParts[1], CultureInfo.InvariantCulture);
                        if (fillYear != year || fillMonth != month) continue;

                        // Archive already has real data for this day — don't mix in
                        // platform data too, or the day would be double-counted.
                        if (archiveDaysThisMonth.Contains(fillDayKey)) continue;

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

        // Returns every fill for a single trading day, preferring the archive if it covers
        // that day and falling back to the live platform otherwise.
        //
        // When DailyReset = Midnight  : "day" is the calendar date; query UTC midnight ± generous
        //                                slack to avoid clipping any timezone's session fills.
        // When DailyReset = SessionBoundary: "day" is the session day (Mon–Fri) whose session
        //                                runs from 5pm ET the prior calendar day through 4:59pm ET
        //                                that calendar day. Sunday evening from 5pm ET counts as
        //                                Monday's session so Sunday fills roll to Monday.
        //
        // In both modes the UTC query window is over-inclusive; filtering is done by GetDayKey
        // after converting to local time, so no fills are dropped by the window.
        private List<FillRecord> GetFillsForDay(DateTime day)
        {
            EnsureArchiveLoaded();
            string targetDayKey = day.ToString("yyyy-MM-dd");

            if (_archiveCoveredDays.Contains(targetDayKey))
                return _archiveFillsCache.Where(f => GetDayKey(f.DateTime) == targetDayKey).ToList();

            var result = new List<FillRecord>();
            try
            {
                // Build a query window that unconditionally covers the full local calendar day
                // regardless of UTC offset or reset mode:
                //   queryStart = 18h before local midnight in UTC  (covers UTC-14 edge)
                //   queryEnd   = 30h after  local midnight in UTC  (covers UTC+14 AND full evening
                //                session until 11:59pm local, e.g. MDT fill at 11pm = UTC+17h)
                // GetDayKey filtering after the query is the authoritative cut — the window is
                // intentionally over-inclusive so nothing is dropped by the UTC boundary.
                DateTime localMidnight = new DateTime(day.Year, day.Month, day.Day, 0, 0, 0, DateTimeKind.Local);
                DateTime queryStart = localMidnight.ToUniversalTime().AddHours(-18);
                DateTime queryEnd = localMidnight.ToUniversalTime().AddHours(+30);

                var trades = Core.Instance.GetTrades(new TradesHistoryRequestParameters
                {
                    From = queryStart,
                    To = queryEnd,
                });

                if (trades != null)
                {
                    foreach (var trade in trades)
                    {
                        if (_account != null && !trade.Account.Equals(_account)) continue;

                        DateTime localTime = trade.DateTime.ToLocalTime();
                        if (GetDayKey(localTime) != targetDayKey) continue;

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

        // Returns the "yyyy-MM-dd" trading-day key for a fill timestamp under the current reset mode.
        //
        // Midnight mode:
        //   Returns the calendar date of the local timestamp (unchanged from original behaviour).
        //
        // SessionBoundary mode:
        //   CME/CBOT futures reset at 5:00pm Eastern each weekday. Any fill at or after
        //   5:00pm ET is part of the *next* calendar day's session:
        //     • Mon–Sat fills at or after 5pm ET  → next calendar day
        //     • Sunday fills at or after 5pm ET    → Monday (Sunday night open belongs to Monday)
        //     • Sunday fills before 5pm ET         → Sunday (no market, but we don't drop them)
        //   Fills from midnight through 4:59:59pm ET stay on the same calendar day.
        private string GetDayKey(DateTime localTime)
        {
            // Sunday evening fills always belong to Monday regardless of reset mode —
            // the futures market opens Sunday night and that session is Monday's session.
            if (localTime.DayOfWeek == DayOfWeek.Sunday)
            {
                // Any fill on Sunday at all goes to Monday. The market is closed during
                // daytime Sunday, so any fill that exists is from the evening open.
                return localTime.Date.AddDays(1).ToString("yyyy-MM-dd");
            }

            if (_dailyReset == DailyResetMode.Midnight)
                return localTime.Date.ToString("yyyy-MM-dd");

            // Convert local fill time → Eastern time for the 5pm boundary comparison.
            DateTime et = TimeZoneInfo.ConvertTime(localTime, TimeZoneInfo.Local, _easternTz);
            var resetBoundary = new TimeSpan(17, 0, 0); // 5:00pm ET

            DateTime tradingDay;
            if (et.TimeOfDay >= resetBoundary)
            {
                // Fill is in the evening session — it belongs to the next calendar day.
                // AddDays(1) handles Sunday→Monday correctly; any other Saturday/Sunday edge
                // cases are also handled naturally because the calendar grid already skips weekends.
                tradingDay = et.Date.AddDays(1);
            }
            else
            {
                tradingDay = et.Date;
            }

            return tradingDay.ToString("yyyy-MM-dd");
        }

        // Converts a live platform Trade into the common FillRecord shape.
        // BrokerFee mirrors the Risk Manager's GetTradeFee() logic:
        //   - trade.Fee non-zero → real per-fill fee; store it so it's used at round-trip close
        //     instead of the manual settings (avoids double-counting).
        //   - trade.Fee null or zero (e.g. AMP) → store 0; the manual fallback fires at close.
        private static FillRecord? ToFillRecord(Trade trade)
        {
            double fillValue = GetFillValue(trade);
            if (double.IsNaN(fillValue)) return null;

            string side = trade.Side.ToString();
            bool isBuy = side.Equals("Buy", StringComparison.OrdinalIgnoreCase);
            double signedQty = isBuy ? trade.Quantity : -trade.Quantity;
            string symbol = trade.Symbol?.Name ?? trade.Symbol?.Id ?? "UNKNOWN";

            // Capture the broker-reported per-fill fee when the platform supplies one.
            // A zero or null fee means the broker doesn't post fees per-fill (AMP style),
            // so BrokerFee stays 0 and the manual fee-per-contract rate is used instead.
            double brokerFee = (trade.Fee != null && trade.Fee.Value != 0.0) ? trade.Fee.Value : 0.0;

            return new FillRecord
            {
                DateTime = trade.DateTime.ToLocalTime(),
                Symbol = symbol,
                SignedQty = signedQty,
                FillValue = fillValue,
                Price = trade.Price,
                BrokerFee = brokerFee,
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
                string dayKey = GetDayKey(fill.DateTime);
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

        // All-time aggregate metrics, keyed by symbol filter (null/"" = all symbols).
        // Invalidated whenever new fills arrive, same as month/week caches.
        private Dictionary<string, MonthMetrics> _yearlyMetricsCache
            = new Dictionary<string, MonthMetrics>();

        // Computes AllExtraMetrics from an ordered list of round-trip trades.
        // timeSlotIndex: 0=7:30-8am, 1=8-9am, 2=9-10am, 3=10-11am, 4=11am-12pm,
        //                5=12-1pm, 6=1-2pm  (NY session only, entry time determines slot)
        public static AllExtraMetrics ComputeAllExtraMetrics(List<RoundTripTrade> trades, SideMetrics allMetrics, TimeSpan[] slotStarts, TimeSpan slotEnd)
        {
            var result = new AllExtraMetrics
            {
                TimeSlots = new TimeSlotStats[7]
            };

            if (trades == null || trades.Count == 0)
                return result;

            // Equity curve peak-to-trough drawdown and run-up, scoped to this timeframe only.
            // Seed peak and trough from the first trade so no prior-session P&L bleeds in.
            var orderedTrades = trades.OrderBy(x => x.ExitTime).ToList();
            double equity = 0;
            double peak = 0;
            double trough = 0;
            double maxRunUp = 0;
            double maxDrawdown = 0;
            bool first = true;

            foreach (var t in orderedTrades)
            {
                equity += t.Pnl;

                if (first)
                {
                    // Seed peak/trough at 0 (the pre-trade equity baseline) so the
                    // first trade is measured as a run-up or drawdown from zero,
                    // then fall through into the normal evaluation below.
                    peak = 0;
                    trough = 0;
                    first = false;
                }

                // Track running peak and running trough independently every step,
                // so run-up and drawdown are measured symmetrically regardless of
                // whether equity is currently making a new all-time high/low.
                if (equity > peak) peak = equity;
                if (equity < trough) trough = equity;

                double runUp = equity - trough;
                if (runUp > maxRunUp) maxRunUp = runUp;

                double dd = peak - equity;
                if (dd > maxDrawdown) maxDrawdown = dd;
            }

            result.MaxRunUp = maxRunUp;
            result.MaxDrawdown = maxDrawdown;

            // Avg Win / Avg Loss ratio
            double avgWin = allMetrics.WinCount > 0 ? allMetrics.TotalWinPnl / allMetrics.WinCount : 0;
            double avgLoss = allMetrics.LossCount > 0 ? Math.Abs(allMetrics.TotalLossPnl / allMetrics.LossCount) : 0;
            result.AvgWinAvgLossRatio = avgLoss > 0 ? avgWin / avgLoss : 0;

            // Profit factor
            double grossProfit = allMetrics.TotalWinPnl;
            double grossLoss = Math.Abs(allMetrics.TotalLossPnl);
            // All wins → ∞, all losses → 0, mixed → ratio
            if (grossLoss > 0 && grossProfit > 0)
                result.ProfitFactor = grossProfit / grossLoss;
            else if (grossLoss == 0 && grossProfit > 0)
                result.ProfitFactor = double.PositiveInfinity;
            else if (grossProfit == 0 && grossLoss > 0)
                result.ProfitFactor = 0;
            else
                result.ProfitFactor = double.NaN;

            // NY session time slots (entry time, local time)
            // Slot 0: 7:30 – 8:00am
            // Slot 1: 8:00 – 9:00am  ... Slot 6: 1:00 – 2:00pm
            foreach (var t in trades)
            {
                var tod2 = t.EntryTime.TimeOfDay;
                int slot = -1;
                for (int si = slotStarts.Length - 1; si >= 0; si--)
                {
                    if (tod2 >= slotStarts[si])
                    {
                        if (si == slotStarts.Length - 1 && tod2 >= slotEnd) break;
                        slot = si;
                        break;
                    }
                }
                if (slot < 0) continue; // outside NY session hours
                result.TimeSlots[slot].TotalPnl += t.Pnl;
                if (t.Pnl > 0) result.TimeSlots[slot].Wins++;
                else if (t.Pnl < 0) result.TimeSlots[slot].Losses++;
            }

            return result;
        }

        // Returns the NY session slot index based on local entry time, or -1 if outside session.
        // Slot 0: 7:30–8:00am, Slot 1: 8–9am, ..., Slot 6: 1–2pm
        // Parse "H:mm" or "HH:mm" into a TimeSpan. Returns false on bad input.
        private static bool TryParseSlot(string s, out TimeSpan result)
        {
            result = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var parts = s.Trim().Split(':');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], out int h) || !int.TryParse(parts[1], out int m)) return false;
            if (h < 0 || h > 23 || m < 0 || m > 59) return false;
            result = new TimeSpan(h, m, 0);
            return true;
        }

        // Maps a raw trade-history time (as-is, no timezone conversion) to a slot index
        // using the user-configured slot boundaries. Returns -1 if outside all slots.
        public int GetNYSessionSlot(DateTime tradeTime)
        {
            var tod = tradeTime.TimeOfDay;
            var starts = new[] { SlotStart0, SlotStart1, SlotStart2, SlotStart3, SlotStart4, SlotStart5, SlotStart6 };
            for (int i = starts.Length - 1; i >= 0; i--)
            {
                if (tod >= starts[i])
                {
                    // For the last slot, also enforce the end boundary
                    if (i == starts.Length - 1 && tod >= SlotEnd6) return -1;
                    return i;
                }
            }
            return -1; // before slot 0 start
        }

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

        // Returns a flat ordered list of all trades for the week/month, used to compute
        // AllExtraMetrics which requires the full equity curve (not just aggregated stats).
        public List<RoundTripTrade> GetAllTradesForWeek(string dateStr, string symbolFilter = null)
        {
            var all = new List<RoundTripTrade>();
            if (!DateTime.TryParse(dateStr, out DateTime date)) return all;
            DateTime weekMonday = GetWeekMonday(date);
            for (int d = 0; d < 5; d++)
            {
                var dm = GetDayMetrics(weekMonday.AddDays(d).ToString("yyyy-MM-dd"), symbolFilter);
                all.AddRange(dm.Trades);
            }
            return all.OrderBy(t => t.ExitTime).ToList();
        }

        public List<RoundTripTrade> GetAllTradesForMonth(int year, int month, string symbolFilter = null)
        {
            var all = new List<RoundTripTrade>();
            int daysInMonth = DateTime.DaysInMonth(year, month);
            for (int day = 1; day <= daysInMonth; day++)
            {
                var date = new DateTime(year, month, day);
                if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday) continue;
                var dm = GetDayMetrics(date.ToString("yyyy-MM-dd"), symbolFilter);
                all.AddRange(dm.Trades);
            }
            return all.OrderBy(t => t.ExitTime).ToList();
        }

        // Aggregates metrics across every day that has fills — both live platform fills
        // and any loaded archive CSV rows. The result is the same MonthMetrics shape used
        // by monthly/weekly views so DrawYearlyMetricsPanel can share the same rendering code.
        public MonthMetrics GetYearlyMetrics(string symbolFilter = null)
        {
            string cacheKey = symbolFilter ?? string.Empty;
            if (_yearlyMetricsCache.TryGetValue(cacheKey, out MonthMetrics cached))
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
                // Scope to the current calendar year. Archive fills and live platform
                // fills are both filtered to Jan 1 – Dec 31 of this year.
                int thisYear = DateTime.Now.Year;
                var yearStart = new DateTime(thisYear, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var yearEnd = new DateTime(thisYear + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);

                var allDates = new SortedSet<DateTime>();

                foreach (var f in _archiveFillsCache)
                {
                    string dk = GetDayKey(f.DateTime);
                    if (dk.StartsWith(thisYear.ToString()))
                        allDates.Add(DateTime.Parse(dk));
                }

                var liveTrades = Core.Instance.GetTrades(new TradesHistoryRequestParameters
                {
                    From = yearStart,
                    To = yearEnd,
                });
                if (liveTrades != null)
                    foreach (var t in liveTrades)
                    {
                        string dk = GetDayKey(t.DateTime.ToLocalTime());
                        if (dk.StartsWith(thisYear.ToString()))
                            allDates.Add(DateTime.Parse(dk));
                    }

                var longM = result.Long;
                var shortM = result.Short;
                var allM = result.All;
                var pie = result.Pie;

                foreach (var date in allDates)
                {
                    if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
                        continue;

                    var dayFills = GetFillsForDay(date);
                    if (dayFills.Count == 0) continue;

                    var dm = AggregateDayMetrics(dayFills, symbolFilter);
                    foreach (var sym in dm.Symbols) symbolSet.Add(sym);
                    if (!dm.HasData) continue;

                    // Merge Long
                    var lm = dm.Long;
                    longM.RoundTrips += lm.RoundTrips; longM.Wins += lm.Wins;
                    longM.WinCount += lm.WinCount; longM.LossCount += lm.LossCount;
                    longM.TotalPnl += lm.TotalPnl; longM.TotalWinPnl += lm.TotalWinPnl; longM.TotalLossPnl += lm.TotalLossPnl;
                    if (lm.LargestWin > longM.LargestWin) longM.LargestWin = lm.LargestWin;
                    if (lm.LargestLoss < longM.LargestLoss) longM.LargestLoss = lm.LargestLoss;
                    longM.TotalDurationSeconds += lm.TotalDurationSeconds; longM.DurationSampleCount += lm.DurationSampleCount;
                    longM.TotalWinDurationSeconds += lm.TotalWinDurationSeconds; longM.WinDurationCount += lm.WinDurationCount;
                    longM.TotalLossDurationSeconds += lm.TotalLossDurationSeconds; longM.LossDurationCount += lm.LossDurationCount;
                    if (lm.WinStreak > longM.WinStreak) longM.WinStreak = lm.WinStreak;
                    if (lm.LossStreak > longM.LossStreak) longM.LossStreak = lm.LossStreak;
                    if (lm.HasData) longM.HasData = true;

                    // Merge Short
                    var sm = dm.Short;
                    shortM.RoundTrips += sm.RoundTrips; shortM.Wins += sm.Wins;
                    shortM.WinCount += sm.WinCount; shortM.LossCount += sm.LossCount;
                    shortM.TotalPnl += sm.TotalPnl; shortM.TotalWinPnl += sm.TotalWinPnl; shortM.TotalLossPnl += sm.TotalLossPnl;
                    if (sm.LargestWin > shortM.LargestWin) shortM.LargestWin = sm.LargestWin;
                    if (sm.LargestLoss < shortM.LargestLoss) shortM.LargestLoss = sm.LargestLoss;
                    shortM.TotalDurationSeconds += sm.TotalDurationSeconds; shortM.DurationSampleCount += sm.DurationSampleCount;
                    shortM.TotalWinDurationSeconds += sm.TotalWinDurationSeconds; shortM.WinDurationCount += sm.WinDurationCount;
                    shortM.TotalLossDurationSeconds += sm.TotalLossDurationSeconds; shortM.LossDurationCount += sm.LossDurationCount;
                    if (sm.WinStreak > shortM.WinStreak) shortM.WinStreak = sm.WinStreak;
                    if (sm.LossStreak > shortM.LossStreak) shortM.LossStreak = sm.LossStreak;
                    if (sm.HasData) shortM.HasData = true;

                    // Merge All
                    var am = dm.All;
                    allM.RoundTrips += am.RoundTrips; allM.Wins += am.Wins;
                    allM.WinCount += am.WinCount; allM.LossCount += am.LossCount;
                    allM.TotalPnl += am.TotalPnl; allM.TotalWinPnl += am.TotalWinPnl; allM.TotalLossPnl += am.TotalLossPnl;
                    if (am.LargestWin > allM.LargestWin) allM.LargestWin = am.LargestWin;
                    if (am.LargestLoss < allM.LargestLoss) allM.LargestLoss = am.LargestLoss;
                    allM.TotalDurationSeconds += am.TotalDurationSeconds; allM.DurationSampleCount += am.DurationSampleCount;
                    allM.TotalWinDurationSeconds += am.TotalWinDurationSeconds; allM.WinDurationCount += am.WinDurationCount;
                    allM.TotalLossDurationSeconds += am.TotalLossDurationSeconds; allM.LossDurationCount += am.LossDurationCount;
                    if (am.WinStreak > allM.WinStreak) allM.WinStreak = am.WinStreak;
                    if (am.LossStreak > allM.LossStreak) allM.LossStreak = am.LossStreak;
                    if (am.HasData) allM.HasData = true;

                    pie.Wins += dm.Pie.Wins;
                    pie.Losses += dm.Pie.Losses;
                    pie.Breakevens += dm.Pie.Breakevens;
                }

                result.Long = longM; result.Short = shortM; result.All = allM; result.Pie = pie;
                result.TotalPnL = longM.TotalPnl + shortM.TotalPnl;
                result.HasData = longM.HasData || shortM.HasData;
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[TradeJournal] GetYearlyMetrics error: {ex.Message}");
            }

            result.Symbols = symbolSet.ToList();
            _yearlyMetricsCache[cacheKey] = result;
            return result;
        }

        public List<RoundTripTrade> GetAllTradesForYear(string symbolFilter = null)
        {
            var all = new List<RoundTripTrade>();
            var allDates = new SortedSet<DateTime>();

            int thisYear = DateTime.Now.Year;
            var yearStart = new DateTime(thisYear, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var yearEnd = new DateTime(thisYear + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            foreach (var f in _archiveFillsCache)
            {
                string dk = GetDayKey(f.DateTime);
                if (dk.StartsWith(thisYear.ToString()))
                    allDates.Add(DateTime.Parse(dk));
            }

            var liveTrades = Core.Instance.GetTrades(new TradesHistoryRequestParameters
            {
                From = yearStart,
                To = yearEnd,
            });
            if (liveTrades != null)
                foreach (var t in liveTrades)
                {
                    string dk = GetDayKey(t.DateTime.ToLocalTime());
                    if (dk.StartsWith(thisYear.ToString()))
                        allDates.Add(DateTime.Parse(dk));
                }

            foreach (var date in allDates)
            {
                if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday) continue;
                var dm = GetDayMetrics(date.ToString("yyyy-MM-dd"), symbolFilter);
                all.AddRange(dm.Trades);
            }
            return all.OrderBy(t => t.ExitTime).ToList();
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
                days.Add((date, dm.Trades.OrderBy(t => t.EntryTime).ToList()));
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
                    days.Add((dayKey, dm.Trades.OrderBy(t => t.EntryTime).ToList()));
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
                    days.Add((dayKey, dm.Trades.OrderBy(t => t.EntryTime).ToList()));
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
            var brokerFeeAccum = new Dictionary<string, double>(); // sum of BrokerFee values across all fills in this round trip

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
                    brokerFeeAccum[symbol] = 0;
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
                brokerFeeAccum[symbol] += fill.BrokerFee; // accumulate per-fill broker fees across this round trip

                // Check if a round trip closed (net qty crossed zero)
                bool closedRoundTrip = (prevQty > 0 && newQty <= 0) || (prevQty < 0 && newQty >= 0);

                if (closedRoundTrip)
                {
                    // Fee priority — mirrors Risk Manager's GetTradeFee() logic exactly:
                    // If any fill in this round trip carried a non-zero broker-reported fee
                    // (from trade.Fee on live fills, or the "Fee" CSV column on archive fills),
                    // use the accumulated total directly. The broker already netted or reported
                    // the real commission, so the manual setting must not be applied on top.
                    // If all fills had zero broker fee (e.g. AMP, where fees post on the
                    // end-of-day statement and trade.Fee is always null/0), fall back to the
                    // manual per-contract rate from the plugin settings as before.
                    double totalBrokerFee = brokerFeeAccum[symbol];
                    double fee = totalBrokerFee > 0.0
                        ? totalBrokerFee                            // use broker-reported fees as-is
                        : GetFeePerContract(symbol) * entryQty[symbol]; // fall back to manual settings
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
                        DayKey = GetDayKey(fill.DateTime)
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
                    brokerFeeAccum[symbol] = 0; // reset for the next round trip on this symbol

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

                // Read the per-fill fee from the export's "Fee" column if present.
                // AMP/CQG and similar brokers that already net fees into Gross P/L export
                // Fee = 0; brokers that report real per-fill fees will have a positive value.
                double csvFee = 0.0;
                if (col.TryGetValue("Fee", out int feeIdx) && feeIdx < fields.Length)
                    double.TryParse(fields[feeIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out csvFee);

                var fill = new FillRecord
                {
                    DateTime = dt,
                    Symbol = symbol,
                    SignedQty = qty,          // export already signs this: + buy, - sell
                    FillValue = -tradeValue,  // Trade value's sign convention is inverted vs GetFillValue
                    Price = price,
                    BrokerFee = csvFee,       // 0 for brokers with fees already in P/L (e.g. AMP)
                };

                yield return (fill, tradeId, GetDayKey(dt));
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
        public double FontScale => _fontScale;
        public bool ShowAdditionalMetrics => _showAdditionalMetrics;

        // Height of Quantower's native title bar in pixels.
        // Returns 0 when the panel is docked in a tab (no title bar shown).
        // Read via dynamic to avoid a compile-time reference to the SDK margin type.
        public int NonClientMarginTop
        {
            get { try { return (int)((dynamic)this.NonClientMargin).Top; } catch { return 0; } }
        }

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

        // Returns the set of trade keys (sanitized filenames, without extension) that
        // have a non-empty trade note on disk. Used by the trade list renderer to draw
        // a note indicator dot on rows that have an associated note.
        public HashSet<string> GetTradeNoteKeys()
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(TradeNotesFolder)) return keys;
            foreach (var file in Directory.GetFiles(TradeNotesFolder, "*.txt"))
            {
                if (new FileInfo(file).Length == 0) continue;
                keys.Add(Path.GetFileNameWithoutExtension(file));
            }
            return keys;
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
        private bool _showYearlyMetrics = false;
        private string _weeklyMetricsDate = null; // the day that was double-clicked
        private DateTime _lastClickTime = DateTime.MinValue;
        private string _lastClickDate = null;
        private DateTime _lastHeaderClickTime = DateTime.MinValue; // for header double-click detection
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
        private Rectangle _additionalChartRect; // last-drawn additional-metrics chart bounds, for click hit-testing
        private bool _additionalMetricsUseLineChart = true; // true = cumulative line, false = per-trade histogram; toggled by clicking the chart

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
        public event Action OnYearlyViewSelected;

        // Public so the plugin can re-query "what's currently shown" on its own (e.g.
        // after prev/next month), not just react to the events above.
        public bool IsMonthlyView => _showMonthlyMetrics;
        public bool IsWeeklyView => _showWeeklyMetrics;
        public bool IsYearlyView => _showYearlyMetrics;
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

            // Clicking the additional-metrics chart toggles between the cumulative
            // line chart and the per-trade histogram.
            if (_additionalChartRect.Width > 0 && _additionalChartRect.Contains(e.Location))
            {
                _additionalMetricsUseLineChart = !_additionalMetricsUseLineChart;
                Redraw();
                return;
            }

            if (_prevBtnRect.Contains(e.Location)) { _selectedSymbolFilter = null; OnPrevMonth?.Invoke(); return; }
            if (_nextBtnRect.Contains(e.Location)) { _selectedSymbolFilter = null; OnNextMonth?.Invoke(); return; }

            // Clicking the month header: single-click → Monthly Metrics, double-click → Yearly Metrics
            if (_headerRect.Contains(e.Location))
            {
                var now = DateTime.Now;
                bool isHeaderDoubleClick = (now - _lastHeaderClickTime).TotalMilliseconds <= DoubleClickMs;
                _lastHeaderClickTime = now;
                _selectedSymbolFilter = null;

                if (isHeaderDoubleClick)
                {
                    // Double-click: Yearly Metrics
                    _showYearlyMetrics = true;
                    _showMonthlyMetrics = false;
                    _showWeeklyMetrics = false;
                    _lastHeaderClickTime = DateTime.MinValue; // reset so a third click is a fresh single
                    OnYearlyViewSelected?.Invoke();
                    Redraw();
                }
                else if (!_showMonthlyMetrics || _showWeeklyMetrics || _showYearlyMetrics)
                {
                    // Single click: Monthly Metrics
                    _showMonthlyMetrics = true;
                    _showWeeklyMetrics = false;
                    _showYearlyMetrics = false;
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
                    _showYearlyMetrics = false;
                    _weeklyMetricsDate = date;
                    _lastClickDate = null; // reset so a third click is a fresh single
                    OnDaySelected?.Invoke(date);
                    OnWeekViewSelected?.Invoke(date);
                    Redraw();
                }
                else
                {
                    // Single click: show Daily Metrics, defaulting to the "All Trades" combined view
                    _showMonthlyMetrics = false;
                    _showWeeklyMetrics = false;
                    _showYearlyMetrics = false;
                    _showAllTradesView = true; // always start on All Trades; user can click the pie to toggle
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
        // Returns the Y coordinate just past the bottom of everything drawn (text rows and/or
        // pie+symbol list, whichever extends further), so the caller can place additional
        // content — like the optional trades chart — right beneath it without overlap.
        private int DrawDailyMetricsPanel(Graphics gr, int panelY, string selectedDate,
            SolidBrush whiteBrush, SolidBrush grayBrush, SolidBrush lightGray,
            SolidBrush greenBrush, SolidBrush redBrush,
            Font fontHdr, Font fontNames, Font fontCount)
        {
            var bounds = Bounds;
            if (bounds.Width <= 0) return panelY;

            var metrics = _plugin.GetDayMetrics(selectedDate, _selectedSymbolFilter);

            int panelX = GridStartX;
            int panelW = Math.Max(0, bounds.Width - GridStartX - 8);
            int colW = (int)(panelW / 2 * 0.75); // pull short column 25% closer to long column

            var sfLeft = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near };

            // Header
            var headerRect = new Rectangle(panelX, panelY, panelW, fontHdr.Height + 4);
            gr.DrawString("Daily Metrics", fontHdr, whiteBrush, headerRect, sfLeft);

            if (metrics.HasData)
            {
                double totalPnl = metrics.Long.TotalPnl + metrics.Short.TotalPnl;
                string pnlText = FormatPnl(totalPnl);
                var pnlBrush = totalPnl >= 0 ? greenBrush : redBrush;
                var headerSize = gr.MeasureString("Daily Metrics", fontHdr);
                int pnlX = panelX + (int)headerSize.Width + 6;
                var pnlRect = new Rectangle(pnlX, panelY, panelW - (int)headerSize.Width - 6, fontHdr.Height + 4);
                gr.DrawString(pnlText, fontHdr, pnlBrush, pnlRect, sfLeft);
            }

            int rowY = panelY + fontHdr.Height + 8;
            int rowH = fontCount.Height + 2;
            int rowYRight = rowY; // right column tracks independently; also doubles as the
                                  // All-Trades-view bottom marker once the block below runs

            if (_showAllTradesView)
            {
                // Two sub-columns under "All Trades": left = existing metrics, right = extra metrics
                gr.DrawString("All Trades", fontNames, lightGray,
                    new Rectangle(panelX, rowY, colW * 2 - 6, rowH), sfLeft);
                rowY += rowH;

                var am = metrics.All;
                var slotArr = new[] { _plugin.SlotStart0, _plugin.SlotStart1, _plugin.SlotStart2, _plugin.SlotStart3, _plugin.SlotStart4, _plugin.SlotStart5, _plugin.SlotStart6 };
                var extra = TradeJournalPlugin.ComputeAllExtraMetrics(metrics.Trades, am, slotArr, _plugin.SlotEnd6);

                int leftX = panelX;
                int rightX = panelX + colW;
                rowYRight = rowY; // right column tracks independently so we can reset if needed

                void DrawAllLeft(string label, string val, SolidBrush brush)
                {
                    gr.DrawString($"{label}: {val}", fontCount, brush,
                        new Rectangle(leftX, rowY, colW - 6, rowH), sfLeft);
                    rowY += rowH;
                }

                void DrawAllRight(string label, string val, SolidBrush brush)
                {
                    gr.DrawString($"{label}: {val}", fontCount, brush,
                        new Rectangle(rightX, rowYRight, colW - 6, rowH), sfLeft);
                    rowYRight += rowH;
                }

                DrawAllLeft("Trades", am.RoundTrips > 0 ? $"{am.RoundTrips} ({am.WinCount} W : {am.LossCount} L)" : am.RoundTrips.ToString(), grayBrush);
                DrawAllLeft("Win Rate", am.HasData ? $"{am.WinRate:0.#}%" : "—", grayBrush);
                DrawAllLeft("Avg P&L", am.HasData ? FormatPnl(am.AvgPnl) : "—", am.HasData && am.AvgPnl >= 0 ? greenBrush : redBrush);
                DrawAllLeft("Avg Win", am.WinCount > 0 ? FormatPnl(am.AvgWin) : "—", greenBrush);
                DrawAllLeft("Avg Loss", am.LossCount > 0 ? FormatPnl(am.AvgLoss) : "—", redBrush);
                DrawAllLeft("Best Trade", am.HasData ? FormatPnl(am.LargestWin) : "—", greenBrush);
                DrawAllLeft("Worst Trade", am.HasData ? FormatPnl(am.LargestLoss) : "—", redBrush);
                DrawAllLeft("Win Streak", am.HasData ? am.WinStreak.ToString() : "—", greenBrush);
                DrawAllLeft("Loss Streak", am.HasData ? am.LossStreak.ToString() : "—", redBrush);
                DrawAllLeft("Avg Hold Win", am.WinDurationCount > 0 ? FormatDuration(am.AvgWinDurationSeconds) : "—", greenBrush);
                DrawAllLeft("Avg Hold Loss", am.LossDurationCount > 0 ? FormatDuration(am.AvgLossDurationSeconds) : "—", redBrush);

                // Right column: extra All-Trades-only metrics
                DrawAllRight("Max Run Up", am.HasData ? FormatPnl(extra.MaxRunUp) : "—", greenBrush);
                DrawAllRight("Max Drawdown", am.HasData ? FormatPnl(-extra.MaxDrawdown) : "—", redBrush);
                DrawAllRight("Avg Win/Avg Loss", extra.AvgWinAvgLossRatio > 0 ? $"{extra.AvgWinAvgLossRatio:0.##}" : "—", grayBrush);
                {
                    string pfText = double.IsNaN(extra.ProfitFactor) ? "—"
                        : double.IsPositiveInfinity(extra.ProfitFactor) ? "\u221e"
                        : $"{extra.ProfitFactor:0.##}";
                    SolidBrush pfBrush = double.IsNaN(extra.ProfitFactor) ? grayBrush
                        : extra.ProfitFactor >= 1 ? greenBrush : redBrush;
                    DrawAllRight("Profit Factor", pfText, pfBrush);
                }

                // Time slots — find most profitable (only profitable slots eligible) for yellow text
                var slotStarts = new[] { _plugin.SlotStart0, _plugin.SlotStart1, _plugin.SlotStart2, _plugin.SlotStart3, _plugin.SlotStart4, _plugin.SlotStart5, _plugin.SlotStart6 };
                var slotEnds = new[] { _plugin.SlotStart1, _plugin.SlotStart2, _plugin.SlotStart3, _plugin.SlotStart4, _plugin.SlotStart5, _plugin.SlotStart6, _plugin.SlotEnd6 };
                string FmtT(TimeSpan t) => t.Minutes == 0 ? $"{(t.Hours % 12 == 0 ? 12 : t.Hours % 12)}{(t.Hours < 12 ? "am" : "pm")}"
                                                          : $"{(t.Hours % 12 == 0 ? 12 : t.Hours % 12)}:{t.Minutes:00}{(t.Hours < 12 ? "am" : "pm")}";
                string[] slotLabels = slotStarts.Select((s, i) => $"{FmtT(s)} - {FmtT(slotEnds[i])}").ToArray();
                double bestPnl = 0; // must be strictly positive to qualify
                bool anySlot = false;
                for (int si = 0; si < extra.TimeSlots.Length; si++)
                    if (extra.TimeSlots[si].HasData) { anySlot = true; if (extra.TimeSlots[si].TotalPnl > bestPnl) bestPnl = extra.TimeSlots[si].TotalPnl; }
                var yellowBrush = new SolidBrush(Theme.Yellow);
                for (int si = 0; si < extra.TimeSlots.Length; si++)
                {
                    var slot = extra.TimeSlots[si];
                    string slotVal = slot.HasData
                        ? $"{(slot.TotalPnl >= 0 ? "+" : "-")}${Math.Abs(slot.TotalPnl):0.##} ({slot.Wins}W : {slot.Losses}L)"
                        : "—";
                    bool isBest = anySlot && slot.HasData && bestPnl > 0 && slot.TotalPnl == bestPnl;
                    SolidBrush slotBrush = isBest ? yellowBrush : (slot.HasData ? (slot.TotalPnl >= 0 ? greenBrush : redBrush) : grayBrush);
                    DrawAllRight(slotLabels[si], slotVal, slotBrush);
                }
                yellowBrush.Dispose();
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

                DrawMetricRow("Trades",
                    lm.RoundTrips > 0 ? $"{lm.RoundTrips} ({lm.WinCount} W : {lm.LossCount} L)" : lm.RoundTrips.ToString(),
                    sm.RoundTrips > 0 ? $"{sm.RoundTrips} ({sm.WinCount} W : {sm.LossCount} L)" : sm.RoundTrips.ToString(),
                    grayBrush, grayBrush);
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

            int textBottom = Math.Max(rowY, rowYRight);

            // --- Pie chart (Win / Loss / Breakeven), placed to the right of the columns ---
            // Sized larger to fill more of the available right-hand space, and nudged
            // left/down slightly so it sits closer to the metric columns and centers
            // better against the taller row list above.
            int availableW = Math.Max(0, panelW - (panelX + 2 * colW) - 8);
            int pieSize = Math.Min(200, availableW);
            int pieBottom = panelY;
            if (pieSize > 20)
            {
                int pieX = panelX + 2 * colW + 4;
                int pieY = panelY + 14;
                DrawWinLossPie(gr, pieX, pieY, pieSize, metrics.Pie, fontCount, whiteBrush);
                pieBottom = DrawSymbolFilterList(gr, pieX, pieY + pieSize + 6, availableW, metrics.Symbols,
                    _selectedSymbolFilter, fontCount, grayBrush, whiteBrush, greenBrush);
            }
            else
            {
                _pieRect = Rectangle.Empty; // pie not drawn this pass; don't hit-test a stale rect
            }

            sfLeft.Dispose();
            return Math.Max(textBottom, pieBottom);
        }

        // Draws a two-column (Long / Short) metrics breakdown for the Mon–Fri week
        // containing the double-clicked day. The week can span two calendar months.
        private int DrawWeeklyMetricsPanel(Graphics gr, int panelY, string dateStr,
            SolidBrush whiteBrush, SolidBrush grayBrush, SolidBrush lightGray,
            SolidBrush greenBrush, SolidBrush redBrush,
            Font fontHdr, Font fontNames, Font fontCount)
        {
            var bounds = Bounds;
            if (bounds.Width <= 0) return panelY;

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

            var headerRect = new Rectangle(panelX, panelY, panelW, fontHdr.Height + 4);
            gr.DrawString(weekLabel, fontHdr, whiteBrush, headerRect, sfLeft);

            if (metrics.HasData)
            {
                string pnlText = FormatPnl(metrics.TotalPnL);
                var pnlBrush = metrics.TotalPnL >= 0 ? greenBrush : redBrush;
                var headerSize = gr.MeasureString(weekLabel, fontHdr);
                int pnlX = panelX + (int)headerSize.Width + 6;
                var pnlRect = new Rectangle(pnlX, panelY, panelW - (int)headerSize.Width - 6, fontHdr.Height + 4);
                gr.DrawString(pnlText, fontHdr, pnlBrush, pnlRect, sfLeft);
            }

            int rowY = panelY + fontHdr.Height + 8;
            int rowH = fontCount.Height + 2;
            int rowYRight = rowY;

            if (_showAllTradesView)
            {
                gr.DrawString("All Trades", fontNames, lightGray, new Rectangle(panelX, rowY, colW * 2 - 6, rowH), sfLeft);
                rowY += rowH;

                var am = metrics.All;
                var weekTrades = _plugin.GetAllTradesForWeek(dateStr, _selectedSymbolFilter);
                var slotArr = new[] { _plugin.SlotStart0, _plugin.SlotStart1, _plugin.SlotStart2, _plugin.SlotStart3, _plugin.SlotStart4, _plugin.SlotStart5, _plugin.SlotStart6 };
                var extra = TradeJournalPlugin.ComputeAllExtraMetrics(weekTrades, am, slotArr, _plugin.SlotEnd6);

                int leftX = panelX;
                int rightX = panelX + colW;
                rowYRight = rowY;

                void DrawAllLeft(string label, string val, SolidBrush brush)
                {
                    gr.DrawString($"{label}: {val}", fontCount, brush, new Rectangle(leftX, rowY, colW - 6, rowH), sfLeft);
                    rowY += rowH;
                }

                void DrawAllRight(string label, string val, SolidBrush brush)
                {
                    gr.DrawString($"{label}: {val}", fontCount, brush, new Rectangle(rightX, rowYRight, colW - 6, rowH), sfLeft);
                    rowYRight += rowH;
                }

                DrawAllLeft("Trades", am.RoundTrips > 0 ? $"{am.RoundTrips} ({am.WinCount} W : {am.LossCount} L)" : am.RoundTrips.ToString(), grayBrush);
                DrawAllLeft("Win Rate", am.HasData ? $"{am.WinRate:0.#}%" : "—", grayBrush);
                DrawAllLeft("Avg P&L", am.HasData ? FormatPnl(am.AvgPnl) : "—", am.HasData && am.AvgPnl >= 0 ? greenBrush : redBrush);
                DrawAllLeft("Avg Win", am.WinCount > 0 ? FormatPnl(am.AvgWin) : "—", greenBrush);
                DrawAllLeft("Avg Loss", am.LossCount > 0 ? FormatPnl(am.AvgLoss) : "—", redBrush);
                DrawAllLeft("Best Trade", am.HasData ? FormatPnl(am.LargestWin) : "—", greenBrush);
                DrawAllLeft("Worst Trade", am.HasData ? FormatPnl(am.LargestLoss) : "—", redBrush);
                DrawAllLeft("Win Streak", am.HasData ? am.WinStreak.ToString() : "—", greenBrush);
                DrawAllLeft("Loss Streak", am.HasData ? am.LossStreak.ToString() : "—", redBrush);
                DrawAllLeft("Avg Hold Win", am.WinDurationCount > 0 ? FormatDuration(am.AvgWinDurationSeconds) : "—", greenBrush);
                DrawAllLeft("Avg Hold Loss", am.LossDurationCount > 0 ? FormatDuration(am.AvgLossDurationSeconds) : "—", redBrush);

                DrawAllRight("Max Run Up", am.HasData ? FormatPnl(extra.MaxRunUp) : "—", greenBrush);
                DrawAllRight("Max Drawdown", am.HasData ? FormatPnl(-extra.MaxDrawdown) : "—", redBrush);
                DrawAllRight("Avg Win/Avg Loss", extra.AvgWinAvgLossRatio > 0 ? $"{extra.AvgWinAvgLossRatio:0.##}" : "—", grayBrush);
                {
                    string pfText = double.IsNaN(extra.ProfitFactor) ? "—"
                        : double.IsPositiveInfinity(extra.ProfitFactor) ? "\u221e"
                        : $"{extra.ProfitFactor:0.##}";
                    SolidBrush pfBrush = double.IsNaN(extra.ProfitFactor) ? grayBrush
                        : extra.ProfitFactor >= 1 ? greenBrush : redBrush;
                    DrawAllRight("Profit Factor", pfText, pfBrush);
                }

                var slotStarts = new[] { _plugin.SlotStart0, _plugin.SlotStart1, _plugin.SlotStart2, _plugin.SlotStart3, _plugin.SlotStart4, _plugin.SlotStart5, _plugin.SlotStart6 };
                var slotEnds = new[] { _plugin.SlotStart1, _plugin.SlotStart2, _plugin.SlotStart3, _plugin.SlotStart4, _plugin.SlotStart5, _plugin.SlotStart6, _plugin.SlotEnd6 };
                string FmtT(TimeSpan t) => t.Minutes == 0 ? $"{(t.Hours % 12 == 0 ? 12 : t.Hours % 12)}{(t.Hours < 12 ? "am" : "pm")}"
                                                          : $"{(t.Hours % 12 == 0 ? 12 : t.Hours % 12)}:{t.Minutes:00}{(t.Hours < 12 ? "am" : "pm")}";
                string[] slotLabels = slotStarts.Select((s, i) => $"{FmtT(s)} - {FmtT(slotEnds[i])}").ToArray();
                double bestPnl = 0; // must be strictly positive to qualify
                bool anySlot = false;
                for (int si = 0; si < extra.TimeSlots.Length; si++)
                    if (extra.TimeSlots[si].HasData) { anySlot = true; if (extra.TimeSlots[si].TotalPnl > bestPnl) bestPnl = extra.TimeSlots[si].TotalPnl; }
                var yellowBrush = new SolidBrush(Theme.Yellow);
                for (int si = 0; si < extra.TimeSlots.Length; si++)
                {
                    var slot = extra.TimeSlots[si];
                    string slotVal = slot.HasData
                        ? $"{(slot.TotalPnl >= 0 ? "+" : "-")}${Math.Abs(slot.TotalPnl):0.##} ({slot.Wins}W : {slot.Losses}L)"
                        : "—";
                    bool isBest = anySlot && slot.HasData && bestPnl > 0 && slot.TotalPnl == bestPnl;
                    SolidBrush slotBrush = isBest ? yellowBrush : (slot.HasData ? (slot.TotalPnl >= 0 ? greenBrush : redBrush) : grayBrush);
                    DrawAllRight(slotLabels[si], slotVal, slotBrush);
                }
                yellowBrush.Dispose();
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

                DrawMetricRow("Trades",
                    lm.RoundTrips > 0 ? $"{lm.RoundTrips} ({lm.WinCount} W : {lm.LossCount} L)" : lm.RoundTrips.ToString(),
                    sm.RoundTrips > 0 ? $"{sm.RoundTrips} ({sm.WinCount} W : {sm.LossCount} L)" : sm.RoundTrips.ToString(),
                    grayBrush, grayBrush);
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

            int textBottom = Math.Max(rowY, rowYRight);

            int availableW = Math.Max(0, panelW - (panelX + 2 * colW) - 8);
            int pieSize = Math.Min(200, availableW);
            int pieBottom = panelY;
            if (pieSize > 20)
            {
                int pieX = panelX + 2 * colW + 4;
                int pieY = panelY + 14;
                DrawWinLossPie(gr, pieX, pieY, pieSize, metrics.Pie, fontCount, whiteBrush);
                pieBottom = DrawSymbolFilterList(gr, pieX, pieY + pieSize + 6, availableW, metrics.Symbols,
                    _selectedSymbolFilter, fontCount, grayBrush, whiteBrush, greenBrush);
            }
            else
            {
                _pieRect = Rectangle.Empty; // pie not drawn this pass; don't hit-test a stale rect
            }

            sfLeft.Dispose();
            return Math.Max(textBottom, pieBottom);
        }

        // Draws a two-column (Long / Short) metrics breakdown for the entire displayed month.
        // Activated by double-clicking the month header. Aggregates every fill across all
        // loaded archive data and live platform trades. Shares the exact same layout as
        // DrawMonthlyMetricsPanel — only the data source and header label differ.
        private int DrawYearlyMetricsPanel(Graphics gr, int panelY,
            SolidBrush whiteBrush, SolidBrush grayBrush, SolidBrush lightGray,
            SolidBrush greenBrush, SolidBrush redBrush,
            Font fontHdr, Font fontNames, Font fontCount)
        {
            var bounds = Bounds;
            if (bounds.Width <= 0) return panelY;

            var metrics = _plugin.GetYearlyMetrics(_selectedSymbolFilter);

            int panelX = GridStartX;
            int panelW = Math.Max(0, bounds.Width - GridStartX - 8);
            int colW = (int)(panelW / 2 * 0.75);

            var sfLeft = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near };

            // Header: "Yearly Metrics" label + total P&L on the same line
            var headerRect = new Rectangle(panelX, panelY, panelW, fontHdr.Height + 4);
            gr.DrawString("Yearly Metrics", fontHdr, whiteBrush, headerRect, sfLeft);

            if (metrics.HasData)
            {
                string pnlText = FormatPnl(metrics.TotalPnL);
                var pnlBrush = metrics.TotalPnL >= 0 ? greenBrush : redBrush;
                var headerSize = gr.MeasureString("Yearly Metrics", fontHdr);
                int pnlX = panelX + (int)headerSize.Width + 6;
                var pnlRect = new Rectangle(pnlX, panelY, panelW - (int)headerSize.Width - 6, fontHdr.Height + 4);
                gr.DrawString(pnlText, fontHdr, pnlBrush, pnlRect, sfLeft);
            }

            int rowY = panelY + fontHdr.Height + 8;
            int rowH = fontCount.Height + 2;
            int rowYRight = rowY;

            if (_showAllTradesView)
            {
                gr.DrawString("All Trades", fontNames, lightGray,
                    new Rectangle(panelX, rowY, colW * 2 - 6, rowH), sfLeft);
                rowY += rowH;

                var am = metrics.All;
                var allTrades = _plugin.GetAllTradesForYear(_selectedSymbolFilter);
                var slotArr = new[] { _plugin.SlotStart0, _plugin.SlotStart1, _plugin.SlotStart2, _plugin.SlotStart3, _plugin.SlotStart4, _plugin.SlotStart5, _plugin.SlotStart6 };
                var extra = TradeJournalPlugin.ComputeAllExtraMetrics(allTrades, am, slotArr, _plugin.SlotEnd6);

                int leftX = panelX;
                int rightX = panelX + colW;
                rowYRight = rowY;

                void DrawAllLeft(string label, string val, SolidBrush brush)
                {
                    gr.DrawString($"{label}: {val}", fontCount, brush, new Rectangle(leftX, rowY, colW - 6, rowH), sfLeft);
                    rowY += rowH;
                }
                void DrawAllRight(string label, string val, SolidBrush brush)
                {
                    gr.DrawString($"{label}: {val}", fontCount, brush, new Rectangle(rightX, rowYRight, colW - 6, rowH), sfLeft);
                    rowYRight += rowH;
                }

                DrawAllLeft("Trades", am.RoundTrips > 0 ? $"{am.RoundTrips} ({am.WinCount} W : {am.LossCount} L)" : am.RoundTrips.ToString(), grayBrush);
                DrawAllLeft("Win Rate", am.HasData ? $"{am.WinRate:0.#}%" : "—", grayBrush);
                DrawAllLeft("Avg P&L", am.HasData ? FormatPnl(am.AvgPnl) : "—", am.HasData && am.AvgPnl >= 0 ? greenBrush : redBrush);
                DrawAllLeft("Avg Win", am.WinCount > 0 ? FormatPnl(am.AvgWin) : "—", greenBrush);
                DrawAllLeft("Avg Loss", am.LossCount > 0 ? FormatPnl(am.AvgLoss) : "—", redBrush);
                DrawAllLeft("Best Trade", am.HasData ? FormatPnl(am.LargestWin) : "—", greenBrush);
                DrawAllLeft("Worst Trade", am.HasData ? FormatPnl(am.LargestLoss) : "—", redBrush);
                DrawAllLeft("Win Streak", am.HasData ? am.WinStreak.ToString() : "—", greenBrush);
                DrawAllLeft("Loss Streak", am.HasData ? am.LossStreak.ToString() : "—", redBrush);
                DrawAllLeft("Avg Hold Win", am.WinDurationCount > 0 ? FormatDuration(am.AvgWinDurationSeconds) : "—", greenBrush);
                DrawAllLeft("Avg Hold Loss", am.LossDurationCount > 0 ? FormatDuration(am.AvgLossDurationSeconds) : "—", redBrush);

                DrawAllRight("Max Run Up", am.HasData ? FormatPnl(extra.MaxRunUp) : "—", greenBrush);
                DrawAllRight("Max Drawdown", am.HasData ? FormatPnl(-extra.MaxDrawdown) : "—", redBrush);
                DrawAllRight("Avg Win/Avg Loss", extra.AvgWinAvgLossRatio > 0 ? $"{extra.AvgWinAvgLossRatio:0.##}" : "—", grayBrush);
                {
                    string pfText = double.IsNaN(extra.ProfitFactor) ? "—"
                        : double.IsPositiveInfinity(extra.ProfitFactor) ? "\u221e"
                        : $"{extra.ProfitFactor:0.##}";
                    SolidBrush pfBrush = double.IsNaN(extra.ProfitFactor) ? grayBrush
                        : extra.ProfitFactor >= 1 ? greenBrush : redBrush;
                    DrawAllRight("Profit Factor", pfText, pfBrush);
                }

                var slotStarts = new[] { _plugin.SlotStart0, _plugin.SlotStart1, _plugin.SlotStart2, _plugin.SlotStart3, _plugin.SlotStart4, _plugin.SlotStart5, _plugin.SlotStart6 };
                var slotEnds = new[] { _plugin.SlotStart1, _plugin.SlotStart2, _plugin.SlotStart3, _plugin.SlotStart4, _plugin.SlotStart5, _plugin.SlotStart6, _plugin.SlotEnd6 };
                string FmtT(TimeSpan t) => t.Minutes == 0 ? $"{(t.Hours % 12 == 0 ? 12 : t.Hours % 12)}{(t.Hours < 12 ? "am" : "pm")}"
                                                           : $"{(t.Hours % 12 == 0 ? 12 : t.Hours % 12)}:{t.Minutes:00}{(t.Hours < 12 ? "am" : "pm")}";
                string[] slotLabels = slotStarts.Select((s, i) => $"{FmtT(s)} - {FmtT(slotEnds[i])}").ToArray();
                double bestPnl = 0;
                bool anySlot = false;
                for (int si = 0; si < extra.TimeSlots.Length; si++)
                    if (extra.TimeSlots[si].HasData) { anySlot = true; if (extra.TimeSlots[si].TotalPnl > bestPnl) bestPnl = extra.TimeSlots[si].TotalPnl; }
                var yellowBrush = new SolidBrush(Theme.Yellow);
                for (int si = 0; si < extra.TimeSlots.Length; si++)
                {
                    var slot = extra.TimeSlots[si];
                    string slotVal = slot.HasData
                        ? $"{(slot.TotalPnl >= 0 ? "+" : "-")}${Math.Abs(slot.TotalPnl):0.##} ({slot.Wins}W : {slot.Losses}L)"
                        : "—";
                    bool isBest = anySlot && slot.HasData && bestPnl > 0 && slot.TotalPnl == bestPnl;
                    SolidBrush slotBrush = isBest ? yellowBrush : (slot.HasData ? (slot.TotalPnl >= 0 ? greenBrush : redBrush) : grayBrush);
                    DrawAllRight(slotLabels[si], slotVal, slotBrush);
                }
                yellowBrush.Dispose();
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
                    gr.DrawString($"{label}: {longVal}", fontCount, longBrush, new Rectangle(panelX, rowY, colW - 6, rowH), sfLeft);
                    gr.DrawString($"{label}: {shortVal}", fontCount, shortBrush, new Rectangle(panelX + colW, rowY, colW - 6, rowH), sfLeft);
                    rowY += rowH;
                }

                var lm = metrics.Long;
                var sm = metrics.Short;

                DrawMetricRow("Trades",
                    lm.RoundTrips > 0 ? $"{lm.RoundTrips} ({lm.WinCount} W : {lm.LossCount} L)" : lm.RoundTrips.ToString(),
                    sm.RoundTrips > 0 ? $"{sm.RoundTrips} ({sm.WinCount} W : {sm.LossCount} L)" : sm.RoundTrips.ToString(),
                    grayBrush, grayBrush);
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

            int textBottom = Math.Max(rowY, rowYRight);

            // Pie chart — same position as monthly/daily
            int availableW = Math.Max(0, panelW - (panelX + 2 * colW) - 8);
            int pieSize = Math.Min(200, availableW);
            int pieBottom = panelY;
            if (pieSize > 20)
            {
                int pieX = panelX + 2 * colW + 4;
                int pieY = panelY + 14;
                DrawWinLossPie(gr, pieX, pieY, pieSize, metrics.Pie, fontCount, whiteBrush);
                pieBottom = DrawSymbolFilterList(gr, pieX, pieY + pieSize + 6, availableW, metrics.Symbols,
                    _selectedSymbolFilter, fontCount, grayBrush, whiteBrush, greenBrush);
            }
            else
            {
                _pieRect = Rectangle.Empty;
            }

            sfLeft.Dispose();
            return Math.Max(textBottom, pieBottom);
        }

        // Activated by clicking the month header; mirrors DrawDailyMetricsPanel's layout.
        private int DrawMonthlyMetricsPanel(Graphics gr, int panelY, int month, int year,
            SolidBrush whiteBrush, SolidBrush grayBrush, SolidBrush lightGray,
            SolidBrush greenBrush, SolidBrush redBrush,
            Font fontHdr, Font fontNames, Font fontCount)
        {
            var bounds = Bounds;
            if (bounds.Width <= 0) return panelY;

            var metrics = _plugin.GetMonthMetrics(year, month, _selectedSymbolFilter);

            int panelX = GridStartX;
            int panelW = Math.Max(0, bounds.Width - GridStartX - 8);
            int colW = (int)(panelW / 2 * 0.75);

            var sfLeft = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near };

            // Header: "Monthly Metrics" label + total P&L on the same line
            var headerRect = new Rectangle(panelX, panelY, panelW, fontHdr.Height + 4);
            gr.DrawString("Monthly Metrics", fontHdr, whiteBrush, headerRect, sfLeft);

            if (metrics.HasData)
            {
                string pnlText = FormatPnl(metrics.TotalPnL);
                var pnlBrush = metrics.TotalPnL >= 0 ? greenBrush : redBrush;
                // Measure the header label width so the P&L sits right after it with a small gap
                var headerSize = gr.MeasureString("Monthly Metrics", fontHdr);
                int pnlX = panelX + (int)headerSize.Width + 6;
                var pnlRect = new Rectangle(pnlX, panelY, panelW - (int)headerSize.Width - 6, fontHdr.Height + 4);
                gr.DrawString(pnlText, fontHdr, pnlBrush, pnlRect, sfLeft);
            }

            int rowY = panelY + fontHdr.Height + 8;
            int rowH = fontCount.Height + 2;
            int rowYRight = rowY;

            if (_showAllTradesView)
            {
                gr.DrawString("All Trades", fontNames, lightGray,
                    new Rectangle(panelX, rowY, colW * 2 - 6, rowH), sfLeft);
                rowY += rowH;

                var am = metrics.All;
                var monthTrades = _plugin.GetAllTradesForMonth(year, month, _selectedSymbolFilter);
                var slotArr = new[] { _plugin.SlotStart0, _plugin.SlotStart1, _plugin.SlotStart2, _plugin.SlotStart3, _plugin.SlotStart4, _plugin.SlotStart5, _plugin.SlotStart6 };
                var extra = TradeJournalPlugin.ComputeAllExtraMetrics(monthTrades, am, slotArr, _plugin.SlotEnd6);

                int leftX = panelX;
                int rightX = panelX + colW;
                rowYRight = rowY;

                void DrawAllLeft(string label, string val, SolidBrush brush)
                {
                    gr.DrawString($"{label}: {val}", fontCount, brush, new Rectangle(leftX, rowY, colW - 6, rowH), sfLeft);
                    rowY += rowH;
                }

                void DrawAllRight(string label, string val, SolidBrush brush)
                {
                    gr.DrawString($"{label}: {val}", fontCount, brush, new Rectangle(rightX, rowYRight, colW - 6, rowH), sfLeft);
                    rowYRight += rowH;
                }

                DrawAllLeft("Trades", am.RoundTrips > 0 ? $"{am.RoundTrips} ({am.WinCount} W : {am.LossCount} L)" : am.RoundTrips.ToString(), grayBrush);
                DrawAllLeft("Win Rate", am.HasData ? $"{am.WinRate:0.#}%" : "—", grayBrush);
                DrawAllLeft("Avg P&L", am.HasData ? FormatPnl(am.AvgPnl) : "—", am.HasData && am.AvgPnl >= 0 ? greenBrush : redBrush);
                DrawAllLeft("Avg Win", am.WinCount > 0 ? FormatPnl(am.AvgWin) : "—", greenBrush);
                DrawAllLeft("Avg Loss", am.LossCount > 0 ? FormatPnl(am.AvgLoss) : "—", redBrush);
                DrawAllLeft("Best Trade", am.HasData ? FormatPnl(am.LargestWin) : "—", greenBrush);
                DrawAllLeft("Worst Trade", am.HasData ? FormatPnl(am.LargestLoss) : "—", redBrush);
                DrawAllLeft("Win Streak", am.HasData ? am.WinStreak.ToString() : "—", greenBrush);
                DrawAllLeft("Loss Streak", am.HasData ? am.LossStreak.ToString() : "—", redBrush);
                DrawAllLeft("Avg Hold Win", am.WinDurationCount > 0 ? FormatDuration(am.AvgWinDurationSeconds) : "—", greenBrush);
                DrawAllLeft("Avg Hold Loss", am.LossDurationCount > 0 ? FormatDuration(am.AvgLossDurationSeconds) : "—", redBrush);

                DrawAllRight("Max Run Up", am.HasData ? FormatPnl(extra.MaxRunUp) : "—", greenBrush);
                DrawAllRight("Max Drawdown", am.HasData ? FormatPnl(-extra.MaxDrawdown) : "—", redBrush);
                DrawAllRight("Avg Win/Avg Loss", extra.AvgWinAvgLossRatio > 0 ? $"{extra.AvgWinAvgLossRatio:0.##}" : "—", grayBrush);
                {
                    string pfText = double.IsNaN(extra.ProfitFactor) ? "—"
                        : double.IsPositiveInfinity(extra.ProfitFactor) ? "\u221e"
                        : $"{extra.ProfitFactor:0.##}";
                    SolidBrush pfBrush = double.IsNaN(extra.ProfitFactor) ? grayBrush
                        : extra.ProfitFactor >= 1 ? greenBrush : redBrush;
                    DrawAllRight("Profit Factor", pfText, pfBrush);
                }

                var slotStarts = new[] { _plugin.SlotStart0, _plugin.SlotStart1, _plugin.SlotStart2, _plugin.SlotStart3, _plugin.SlotStart4, _plugin.SlotStart5, _plugin.SlotStart6 };
                var slotEnds = new[] { _plugin.SlotStart1, _plugin.SlotStart2, _plugin.SlotStart3, _plugin.SlotStart4, _plugin.SlotStart5, _plugin.SlotStart6, _plugin.SlotEnd6 };
                string FmtT(TimeSpan t) => t.Minutes == 0 ? $"{(t.Hours % 12 == 0 ? 12 : t.Hours % 12)}{(t.Hours < 12 ? "am" : "pm")}"
                                                          : $"{(t.Hours % 12 == 0 ? 12 : t.Hours % 12)}:{t.Minutes:00}{(t.Hours < 12 ? "am" : "pm")}";
                string[] slotLabels = slotStarts.Select((s, i) => $"{FmtT(s)} - {FmtT(slotEnds[i])}").ToArray();
                double bestPnl = 0; // must be strictly positive to qualify
                bool anySlot = false;
                for (int si = 0; si < extra.TimeSlots.Length; si++)
                    if (extra.TimeSlots[si].HasData) { anySlot = true; if (extra.TimeSlots[si].TotalPnl > bestPnl) bestPnl = extra.TimeSlots[si].TotalPnl; }
                var yellowBrush = new SolidBrush(Theme.Yellow);
                for (int si = 0; si < extra.TimeSlots.Length; si++)
                {
                    var slot = extra.TimeSlots[si];
                    string slotVal = slot.HasData
                        ? $"{(slot.TotalPnl >= 0 ? "+" : "-")}${Math.Abs(slot.TotalPnl):0.##} ({slot.Wins}W : {slot.Losses}L)"
                        : "—";
                    bool isBest = anySlot && slot.HasData && bestPnl > 0 && slot.TotalPnl == bestPnl;
                    SolidBrush slotBrush = isBest ? yellowBrush : (slot.HasData ? (slot.TotalPnl >= 0 ? greenBrush : redBrush) : grayBrush);
                    DrawAllRight(slotLabels[si], slotVal, slotBrush);
                }
                yellowBrush.Dispose();
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

                DrawMetricRow("Trades",
                    lm.RoundTrips > 0 ? $"{lm.RoundTrips} ({lm.WinCount} W : {lm.LossCount} L)" : lm.RoundTrips.ToString(),
                    sm.RoundTrips > 0 ? $"{sm.RoundTrips} ({sm.WinCount} W : {sm.LossCount} L)" : sm.RoundTrips.ToString(),
                    grayBrush, grayBrush);
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

            int textBottom = Math.Max(rowY, rowYRight);

            // Pie chart, same positioning as daily
            int availableW = Math.Max(0, panelW - (panelX + 2 * colW) - 8);
            int pieSize = Math.Min(200, availableW);
            int pieBottom = panelY;
            if (pieSize > 20)
            {
                int pieX = panelX + 2 * colW + 4;
                int pieY = panelY + 14;
                DrawWinLossPie(gr, pieX, pieY, pieSize, metrics.Pie, fontCount, whiteBrush);
                pieBottom = DrawSymbolFilterList(gr, pieX, pieY + pieSize + 6, availableW, metrics.Symbols,
                    _selectedSymbolFilter, fontCount, grayBrush, whiteBrush, greenBrush);
            }
            else
            {
                _pieRect = Rectangle.Empty; // pie not drawn this pass; don't hit-test a stale rect
            }

            sfLeft.Dispose();
            return Math.Max(textBottom, pieBottom);
        }

        // Draws a simple win/loss/breakeven pie chart with in-slice percentage labels, no legend.
        private void DrawWinLossPie(Graphics gr, int x, int y, int size, PieBuckets pie, Font font, SolidBrush textBrush)
        {
            var rect = new Rectangle(x, y, size, size);
            _pieRect = rect; // stored for OnMouseClick hit-testing, even if there's no data to draw

            int total = pie.Total;
            if (total <= 0) return;

            var winColor = Theme.Win;
            var lossColor = Theme.Loss;
            var beColor = Theme.BreakEven;

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
        // Returns the Y coordinate just past the bottom of the drawn list, so callers can
        // know how much vertical space this took up (it can wrap to multiple rows).
        private int DrawSymbolFilterList(Graphics gr, int x, int y, int maxWidth, List<string> symbols,
            string selectedSymbol, Font font, SolidBrush grayBrush, SolidBrush whiteBrush, SolidBrush accentBrush)
        {
            if (symbols == null || symbols.Count == 0 || maxWidth <= 0) return y;

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
            return curY + rowH;
        }

        // Draws the "additional metrics" chart beneath the metrics panel: either a cumulative
        // equity-curve line (with gradient fill under it) or a per-trade histogram, for
        // whatever trades belong to the currently selected timeframe. Returns the rect that
        // was actually drawn into (Rectangle.Empty if nothing was drawn), so the caller can
        // store it for click-to-toggle hit-testing.
        private Rectangle DrawTradesChart(Graphics gr, Rectangle area, List<RoundTripTrade> trades, bool useLineChart,
            Font fontAxis, SolidBrush grayBrush, SolidBrush whiteBrush, SolidBrush greenBrush, SolidBrush redBrush)
        {
            if (area.Height < 40 || area.Width < 60) return Rectangle.Empty;

            var sfCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            if (trades == null || trades.Count == 0)
            {
                gr.DrawString("No trades in this period", fontAxis, grayBrush, area, sfCenter);
                sfCenter.Dispose();
                return area;
            }

            var ordered = trades.OrderBy(t => t.ExitTime).ToList();
            int n = ordered.Count;

            // --- Determine the value range first (before laying out the chart rect), so the
            // left-axis gutter can be sized to whatever the actual labels need — never clipped,
            // no matter how large the numbers get.
            List<double> cum = null;
            double minV, maxV;
            const int targetGridlines = 5; // aim for ~5 (top, 3 in-between, bottom)

            if (useLineChart)
            {
                double running = 0;
                cum = new List<double>(ordered.Count);
                foreach (var t in ordered) { running += t.Pnl; cum.Add(running); }
                minV = Math.Min(0, cum.Min());
                maxV = Math.Max(0, cum.Max());
                if (maxV - minV < 1e-6) maxV = minV + 1;
            }
            else
            {
                // Histogram stays symmetric around zero — both extremities anchored to the
                // single largest-magnitude trade, same as before, just with more labels in between.
                double maxAbs = ordered.Max(t => Math.Abs(t.Pnl));
                if (maxAbs < 1e-6) maxAbs = 1;
                minV = -maxAbs;
                maxV = maxAbs;
            }

            // Snap the range to a "nice" axis: a clean, constant dollar step (1/2/5 x a power of
            // ten) so every gridline is evenly spaced in both value AND pixels. Since minV <= 0 <=
            // maxV always and the snapped bounds are exact multiples of that step, $0 lands exactly
            // on a gridline automatically — no special-casing needed, and no uneven-looking jumps
            // in the price scale.
            double rawRange = maxV - minV;
            double rawStep = rawRange / (targetGridlines - 1);
            double mag = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
            double norm = rawStep / mag;
            double niceNorm = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10;
            double step = niceNorm * mag;
            minV = Math.Floor(minV / step) * step;
            maxV = Math.Ceiling(maxV / step) * step;
            if (maxV - minV < 1e-9) maxV = minV + step;
            int gridlineCount = (int)Math.Round((maxV - minV) / step) + 1;
            double GridV(int gi) => minV + gi * step;

            // Measure the widest gridline label so the left gutter always fits it fully.
            float widestLabel = 0f;
            for (int gi = 0; gi < gridlineCount; gi++)
            {
                double v = GridV(gi);
                float w = gr.MeasureString(FormatPnl(v), fontAxis).Width;
                if (w > widestLabel) widestLabel = w;
            }

            int axisH = fontAxis.Height + 6;
            int leftPad = (int)Math.Ceiling(widestLabel) + 10; // labels + small gap before the plot area
            var chartRect = new Rectangle(area.X + leftPad, area.Y, area.Width - leftPad - 4, area.Height - axisH);
            if (chartRect.Width < 10 || chartRect.Height < 10)
            {
                sfCenter.Dispose();
                return area;
            }

            var sfNear = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
            var sfFar = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };

            // Draw all horizontal (price) gridlines + their value labels now that the plot area is final.
            for (int gi = 0; gi < gridlineCount; gi++)
            {
                double v = GridV(gi);
                float y = chartRect.Bottom - (float)((v - minV) / (maxV - minV) * chartRect.Height);
                using (var gridPen = new Pen(Theme.GridLine, 1f))
                    gr.DrawLine(gridPen, chartRect.X, y, chartRect.Right, y);
                var labelRect = new Rectangle(area.X, (int)y - fontAxis.Height / 2, leftPad - 6, fontAxis.Height + 2);
                gr.DrawString(FormatPnl(v), fontAxis, grayBrush, labelRect, sfFar);
            }

            // X position for the i-th trade, evenly spaced across the plot width by order —
            // shared by the vertical time gridlines below and by the line/histogram drawing.
            // For the line chart, trade i is shifted one slot right of the raw i/(n-1) spacing
            // so slot 0 (the chart's left edge) is left free for a synthetic $0 anchor point —
            // equity starts flat at zero before the first trade, matching reality.
            float PtX(int i) => chartRect.X + (useLineChart
                ? (float)(i + 1) / n * chartRect.Width
                : (n == 1 ? chartRect.Width / 2f : (float)i / (n - 1) * chartRect.Width));

            // --- Vertical (time) gridlines + labels ---
            // Same trading day → time-of-day labels ("9:30am"); spans multiple days → date labels ("8/6").
            bool sameDay = ordered[0].ExitTime.Date == ordered[n - 1].ExitTime.Date;
            string FmtX(DateTime t) => sameDay
                ? $"{(t.Hour % 12 == 0 ? 12 : t.Hour % 12)}:{t.Minute:00}{(t.Hour < 12 ? "am" : "pm")}"
                : t.ToString("M/d");

            int xLabelCount = Math.Min(5, n);
            var xLabelIdx = new List<int>();
            if (xLabelCount <= 1)
            {
                xLabelIdx.Add(0);
            }
            else
            {
                for (int li = 0; li < xLabelCount; li++)
                    xLabelIdx.Add((int)Math.Round((double)li * (n - 1) / (xLabelCount - 1)));
                xLabelIdx = xLabelIdx.Distinct().ToList(); // rounding can collapse indices when n is small
            }

            foreach (int idx in xLabelIdx)
            {
                float x = PtX(idx);
                using (var vGridPen = new Pen(Theme.GridLineFaint, 1f))
                    gr.DrawLine(vGridPen, x, chartRect.Y, x, chartRect.Bottom);
            }

            if (useLineChart)
            {
                float PtY(double v) => chartRect.Bottom - (float)((v - minV) / (maxV - minV) * chartRect.Height);
                float zeroY = PtY(0);

                // n+1 points: a synthetic $0 anchor at the chart's left edge (pre-trade equity),
                // followed by the cumulative value after each trade. Kept alongside the raw
                // values (not just pixel Y) so we can detect sign changes below.
                var vals = new double[n + 1];
                var points = new PointF[n + 1];
                vals[0] = 0;
                points[0] = new PointF(chartRect.X, zeroY);
                for (int i = 0; i < n; i++)
                {
                    vals[i + 1] = cum[i];
                    points[i + 1] = new PointF(PtX(i), PtY(cum[i]));
                }

                // Split the polyline into runs that stay on one side of zero. Whenever
                // consecutive points straddle zero, interpolate the exact crossing point
                // (in x) so the color change lands precisely on the zero line, not at
                // whichever data point happens to be nearest it.
                var segments = new List<(List<PointF> Pts, bool Positive)>();
                var current = new List<PointF> { points[0] };
                bool curPositive = vals[0] >= 0;
                for (int i = 1; i <= n; i++)
                {
                    bool prevPositive = vals[i - 1] >= 0;
                    bool nowPositive = vals[i] >= 0;
                    if (prevPositive == nowPositive)
                    {
                        current.Add(points[i]);
                    }
                    else
                    {
                        double t = (0 - vals[i - 1]) / (vals[i] - vals[i - 1]);
                        float zx = points[i - 1].X + (float)t * (points[i].X - points[i - 1].X);
                        var zeroPt = new PointF(zx, zeroY);
                        current.Add(zeroPt);
                        segments.Add((current, curPositive));
                        current = new List<PointF> { zeroPt, points[i] };
                        curPositive = nowPositive;
                    }
                }
                segments.Add((current, curPositive));

                foreach (var seg in segments)
                {
                    if (seg.Pts.Count < 2) continue;
                    var segColor = seg.Positive ? Theme.WinAlt : Theme.LossAlt2;
                    var ptsArr = seg.Pts.ToArray();

                    using (var fillPath = new GraphicsPath())
                    {
                        fillPath.AddLines(ptsArr);
                        fillPath.AddLine(ptsArr[ptsArr.Length - 1].X, zeroY, ptsArr[0].X, zeroY);
                        fillPath.CloseFigure();

                        // Fade from opaque at the line down/up to fully transparent right at
                        // the zero baseline, rather than at the chart's bottom edge, so each
                        // above/below-zero run tapers off exactly where it crosses zero.
                        float top = Math.Min(ptsArr.Min(p => p.Y), zeroY);
                        float bottom = Math.Max(ptsArr.Max(p => p.Y), zeroY);
                        if (bottom - top < 1f) bottom = top + 1f;
                        var gradRect = new RectangleF(chartRect.X, top, Math.Max(1, chartRect.Width), bottom - top);
                        var nearLine = Color.FromArgb(70, segColor);
                        var nearZero = Color.FromArgb(0, segColor);
                        var startColor = seg.Positive ? nearLine : nearZero; // top of rect
                        var endColor = seg.Positive ? nearZero : nearLine;   // bottom of rect
                        using (var fillBrush = new LinearGradientBrush(gradRect, startColor, endColor, LinearGradientMode.Vertical))
                            gr.FillPath(fillBrush, fillPath);
                    }
                    using (var linePen = new Pen(segColor, 2f) { LineJoin = LineJoin.Round })
                        gr.DrawLines(linePen, ptsArr);
                }
            }
            else
            {
                float zeroY = chartRect.Bottom - (float)((0 - minV) / (maxV - minV) * chartRect.Height);
                float slotW = (float)chartRect.Width / n;
                float barW = Math.Max(1f, Math.Min(slotW * 0.7f, 24f));

                for (int i = 0; i < n; i++)
                {
                    double pnl = ordered[i].Pnl;
                    float barEdgeVal = chartRect.Bottom - (float)((pnl - minV) / (maxV - minV) * chartRect.Height);
                    float x = chartRect.X + i * slotW + (slotW - barW) / 2f;
                    var brush = pnl >= 0 ? greenBrush : redBrush;
                    if (pnl >= 0)
                        gr.FillRectangle(brush, x, barEdgeVal, barW, zeroY - barEdgeVal);
                    else
                        gr.FillRectangle(brush, x, zeroY, barW, barEdgeVal - zeroY);
                }
            }

            // X-axis time labels — one under each vertical gridline, first left-anchored and
            // last right-anchored so they never run outside the plot area.
            for (int li = 0; li < xLabelIdx.Count; li++)
            {
                int idx = xLabelIdx[li];
                float x = PtX(idx);
                string label = FmtX(ordered[idx].ExitTime);

                if (li == 0)
                    gr.DrawString(label, fontAxis, grayBrush, new Rectangle((int)x, chartRect.Bottom + 2, 80, axisH), sfNear);
                else if (li == xLabelIdx.Count - 1)
                    gr.DrawString(label, fontAxis, grayBrush, new Rectangle((int)x - 80, chartRect.Bottom + 2, 80, axisH), sfFar);
                else
                    gr.DrawString(label, fontAxis, grayBrush, new Rectangle((int)x - 40, chartRect.Bottom + 2, 80, axisH), sfCenter);
            }

            sfCenter.Dispose();
            sfNear.Dispose();
            sfFar.Dispose();
            return area;
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
            gr.Clear(Theme.ChartBg);
            _dayCells.Clear();
            _symbolFilterCells.Clear();

            // Read live settings from plugin each frame so changes take effect immediately
            int cellW = _plugin.CellW;
            int cellH = _plugin.CellH;
            int nextX = NextBtnX;

            // Font size is fully independent of cell size now, driven by its own
            // "Font Size" setting (FontScale, default 1.0 = original baseline sizes).
            // CellWidth/CellHeight only affect cell/grid geometry, never text size.
            float scale = (float)_plugin.FontScale;
            // The metrics panels below the grid (Daily/Weekly/Monthly/Yearly) size their
            // header/row rectangles from the actual font height now, so they can never clip.
            // Still cap how far their fonts scale so a large FontScale doesn't blow the
            // panel up into something silly-looking; the calendar cell fonts (fDay/fPnl/fArrow)
            // are unaffected and keep scaling freely with FontScale.
            float panelScale = Math.Min(scale, 1.6f);
            float fDay = Math.Max(6f, 8f * scale);
            float fPnl = Math.Max(5f, 7f * scale);
            float fCount = Math.Max(5f, 6f * panelScale);
            float fHdr = Math.Max(7f, 9f * panelScale);
            float fNames = Math.Max(5f, 7f * panelScale);
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
            var whiteBrush = new SolidBrush(Theme.TextPrimary);
            var grayBrush = new SolidBrush(Theme.TextGray);
            var lightGray = new SolidBrush(Theme.TextLightGray);
            var dimBrush = new SolidBrush(Theme.TextDim);
            var todayBrush = new SolidBrush(Theme.TodayBg);
            var selectedBrush = new SolidBrush(Theme.SelectedBg);
            var greenBrush = new SolidBrush(Theme.Win);
            var redBrush = new SolidBrush(Theme.Loss);
            var notePen = new Pen(Theme.Win, 2);
            var arrowBrush = new SolidBrush(Theme.IconGray);

            var sfCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var sfTopCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };

            string[] monthNames = { "January","February","March","April","May","June",
                "July","August","September","October","November","December" };

            // --- Nav arrows ---
            _prevBtnRect = new Rectangle(PrevBtnX, HeaderY, 24, HeaderH);
            _nextBtnRect = new Rectangle(nextX, HeaderY, 24, HeaderH);

            gr.DrawString("\u2039", fontArrow, arrowBrush, _prevBtnRect, sfCenter);
            gr.DrawString("\u203a", fontArrow, arrowBrush, _nextBtnRect, sfCenter);

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

            // --- Metrics panel: All Time, Monthly, Weekly, or Daily depending on toggle ---
            int panelY = GridStartY + totalWeekRows * cellH + 12;
            int metricsPanelBottom;
            List<RoundTripTrade> chartTrades = null;

            if (_showYearlyMetrics)
            {
                metricsPanelBottom = DrawYearlyMetricsPanel(gr, panelY, whiteBrush, grayBrush, lightGray,
                    greenBrush, redBrush, fontHdr, fontNames, fontCount);
                if (_plugin.ShowAdditionalMetrics)
                    chartTrades = _plugin.GetAllTradesForYear(_selectedSymbolFilter);
            }
            else if (_showMonthlyMetrics)
            {
                metricsPanelBottom = DrawMonthlyMetricsPanel(gr, panelY, month + 1, year, whiteBrush, grayBrush, lightGray,
                    greenBrush, redBrush, fontHdr, fontNames, fontCount);
                if (_plugin.ShowAdditionalMetrics)
                    chartTrades = _plugin.GetAllTradesForMonth(year, month + 1, _selectedSymbolFilter);
            }
            else if (_showWeeklyMetrics && _weeklyMetricsDate != null)
            {
                metricsPanelBottom = DrawWeeklyMetricsPanel(gr, panelY, _weeklyMetricsDate, whiteBrush, grayBrush, lightGray,
                    greenBrush, redBrush, fontHdr, fontNames, fontCount);
                if (_plugin.ShowAdditionalMetrics)
                    chartTrades = _plugin.GetAllTradesForWeek(_weeklyMetricsDate, _selectedSymbolFilter);
            }
            else
            {
                metricsPanelBottom = DrawDailyMetricsPanel(gr, panelY, selected, whiteBrush, grayBrush, lightGray,
                    greenBrush, redBrush, fontHdr, fontNames, fontCount);
                if (_plugin.ShowAdditionalMetrics)
                    chartTrades = _plugin.GetDayMetrics(selected, _selectedSymbolFilter).Trades;
            }

            // --- Additional metrics chart: fills the leftover space below the panel.
            // Click it to toggle between the cumulative line view and the per-trade histogram.
            if (_plugin.ShowAdditionalMetrics)
            {
                int chartPanelX = GridStartX;
                int chartPanelW = Math.Max(0, Bounds.Width - GridStartX - 8);
                int chartTop = metricsPanelBottom + 18;
                int chartBottom = Bounds.Height - 8;
                var chartArea = new Rectangle(chartPanelX, chartTop, chartPanelW, chartBottom - chartTop);
                _additionalChartRect = DrawTradesChart(gr, chartArea, chartTrades, _additionalMetricsUseLineChart,
                    fontCount, grayBrush, whiteBrush, greenBrush, redBrush);
            }
            else
            {
                _additionalChartRect = Rectangle.Empty;
            }

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

    // GDI+ renderer for the trade list panel — identical architecture to
    // TradeJournalCalendarRenderer: BufferedGraphic + NativeControl.MouseClickNative
    // + a _tradeCells hit-list so clicks are handled natively without any browser bridge.
    public class TradeJournalTradeListRenderer : Renderer
    {
        private BufferedGraphic _bufferedGraphic;

        // Data set by the plugin on each view change
        private List<RoundTripTrade> _trades = new List<RoundTripTrade>();
        private bool _showDaySeparators = false;
        private int _truncatedCount = 0;
        private string _selectedTradeKey = null;
        private HashSet<string> _tradeNoteKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Hit-test list: one entry per data row (separators are not clickable)
        private readonly List<(Rectangle Rect, string TradeKey)> _tradeCells =
            new List<(Rectangle, string)>();

        // Scroll state
        private int _scrollOffsetY = 0;
        private int _contentHeight = 0; // total pixel height of all drawn rows

        // Fired when the user clicks a trade row; payload is RoundTripTrade.TradeKey
        public event Action<string> OnTradeSelected;

        // Column layout (proportional widths assigned during Draw)
        private static readonly string[] ColHeaders =
            { "Date", "Entry Time", "Exit Time", "Symbol", "Qty", "Side", "Entry Price", "Exit Price", "Hold Time", "P&L", "P&L Points" };

        public TradeJournalTradeListRenderer(IRenderingNativeControl native, TradeJournalPlugin plugin)
            : base(native)
        {
            _bufferedGraphic = new BufferedGraphic(Draw, Refresh, native.DisposeImage,
                native.IsDisplayed, BufferedGraphicRequiredThreadType.LowPriority);

            NativeControl.MouseClickNative += OnMouseClick;
            NativeControl.MouseWheelNative += OnMouseWheel;
        }

        // Called by the plugin to push new data; triggers a redraw.
        public void SetTrades(TradeListResult result, bool showDaySeparators, string selectedTradeKey, HashSet<string> tradeNoteKeys, bool resetScroll = true)
        {
            _trades = result.Days.SelectMany(d => d.Trades).OrderByDescending(t => t.ExitTime).ToList();
            _showDaySeparators = showDaySeparators;
            _truncatedCount = result.TruncatedCount;
            _selectedTradeKey = selectedTradeKey;
            _tradeNoteKeys = tradeNoteKeys ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (resetScroll) _scrollOffsetY = 0;
            _bufferedGraphic.IsDirty = true;
        }

        // Called by the plugin when a row is selected externally (e.g. ClearTradeNoteSelection)
        public void SelectTradeKey(string tradeKey)
        {
            _selectedTradeKey = tradeKey;
            _bufferedGraphic.IsDirty = true;
        }

        // Looks up the entry time for a trade by its TradeKey, or returns null if not found.
        // Used by the plugin to show entry time in the trade note label.
        public DateTime? GetEntryTimeForKey(string tradeKey)
        {
            foreach (var t in _trades)
                if (t.TradeKey == tradeKey) return t.EntryTime;
            return null;
        }

        // Updates only the note-dot state and redraws — does NOT reset scroll position.
        // Called after saving a trade note so the dot appears immediately.
        public void RefreshNoteDots(HashSet<string> tradeNoteKeys)
        {
            _tradeNoteKeys = tradeNoteKeys ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _bufferedGraphic.IsDirty = true;
        }

        private void OnMouseWheel(NativeMouseEventArgs e)
        {
            int delta = e.Delta > 0 ? -40 : 40;
            int maxScroll = Math.Max(0, _contentHeight - (Bounds.Height - HeaderRowH));
            _scrollOffsetY = Math.Max(0, Math.Min(_scrollOffsetY + delta, maxScroll));
            _bufferedGraphic.IsDirty = true;
        }

        private void OnMouseClick(NativeMouseEventArgs e)
        {
            // Adjust for scroll: hit-test rects are stored in content-space
            int clickY = e.Location.Y - HeaderRowH + _scrollOffsetY;
            int clickX = e.Location.X;

            foreach (var (rect, key) in _tradeCells)
            {
                var scrolledRect = new Rectangle(rect.X, rect.Y, rect.Width, rect.Height);
                // rect.Y is in content-space; translate to screen-space for the hit test
                int screenY = rect.Y - _scrollOffsetY + HeaderRowH;
                var screenRect = new Rectangle(rect.X, screenY, rect.Width, rect.Height);
                if (!screenRect.Contains(e.Location)) continue;

                if (key == _selectedTradeKey) return; // already selected
                _selectedTradeKey = key;
                _bufferedGraphic.IsDirty = true;
                OnTradeSelected?.Invoke(key);
                return;
            }
        }

        // Height of the sticky header row
        private const int HeaderRowH = 28;
        private const int RowH = 24;
        private const int SepH = 22;
        private const int PadX = 8;
        private const int PadY = 4;

        private void Draw(Graphics gr)
        {
            _tradeCells.Clear();

            var bounds = Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            gr.Clear(Theme.PanelBg);

            var fontHeader = new Font("Arial", 9.6f, FontStyle.Regular);
            var fontRow = new Font("Segoe UI", 10.8f, FontStyle.Regular);
            var fontSep = new Font("Arial", 9.6f, FontStyle.Regular);

            var whiteBrush = new SolidBrush(Theme.TextNearPrimary);
            var grayBrush = new SolidBrush(Theme.TextGray);
            var dimBrush = new SolidBrush(Theme.TextDim2);
            var greenBrush = new SolidBrush(Theme.Win);
            var redBrush = new SolidBrush(Theme.LossAlt);
            var selBrush = new SolidBrush(Theme.SelectedBg);
            var hoverBrush = new SolidBrush(Theme.HoverBg);
            var sepBgBrush = new SolidBrush(Theme.SeparatorBg);
            var headerBgBrush = new SolidBrush(Theme.HeaderBg);
            var noteDotBrush = new SolidBrush(Theme.Win);

            var sfLeft = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
            var sfRight = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
            var sfCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };

            // --- Column widths (fixed pixel amounts that sum to roughly the panel width) ---
            int w = bounds.Width - PadX * 2;
            // Date EntryT ExitT Symbol Qty Side EntryP ExitP Hold PnL PnlPts
            int[] colW = {
                (int)(w * 0.09f),  // Date
                (int)(w * 0.09f),  // Entry Time
                (int)(w * 0.09f),  // Exit Time
                (int)(w * 0.09f),  // Symbol
                (int)(w * 0.05f),  // Qty
                (int)(w * 0.07f),  // Side
                (int)(w * 0.12f),  // Entry $
                (int)(w * 0.12f),  // Exit $
                (int)(w * 0.09f),  // Hold
                (int)(w * 0.10f),  // P&L
                (int)(w * 0.09f),  // P&L Points
            };

            // Compute column X positions
            int[] colX = new int[colW.Length];
            colX[0] = PadX;
            for (int i = 1; i < colW.Length; i++)
                colX[i] = colX[i - 1] + colW[i - 1];

            // --- Sticky header row ---
            gr.FillRectangle(headerBgBrush, 0, 0, bounds.Width, HeaderRowH);
            gr.DrawLine(new Pen(Theme.HeaderSep), 0, HeaderRowH - 1, bounds.Width, HeaderRowH - 1);
            for (int i = 0; i < ColHeaders.Length; i++)
            {
                var r = new Rectangle(colX[i], 0, colW[i], HeaderRowH);
                gr.DrawString(ColHeaders[i].ToUpper(), fontHeader, grayBrush, r, sfLeft);
            }

            // --- Clip to content area below header ---
            gr.SetClip(new Rectangle(0, HeaderRowH, bounds.Width, bounds.Height - HeaderRowH));

            // --- Draw rows (content-space Y, then translate to screen by subtracting scroll) ---
            int contentY = 0; // running Y in content-space

            if (_trades.Count == 0)
            {
                string emptyMsg = "No trades for this period";
                var r = new Rectangle(PadX, 20 - _scrollOffsetY + HeaderRowH, bounds.Width - PadX * 2, RowH);
                gr.DrawString(emptyMsg, fontSep, dimBrush, r, sfLeft);
            }
            else
            {
                if (_truncatedCount > 0)
                {
                    int screenY = contentY - _scrollOffsetY + HeaderRowH;
                    var r = new Rectangle(PadX, screenY, bounds.Width - PadX * 2, SepH);
                    gr.DrawString($"+{_truncatedCount} earlier trade{(_truncatedCount == 1 ? "" : "s")} not shown",
                        fontSep, dimBrush, r, sfLeft);
                    contentY += SepH;
                }

                string lastDayKey = null;

                foreach (var t in _trades)
                {
                    // Day separator row
                    if (_showDaySeparators && t.DayKey != lastDayKey)
                    {
                        int screenY = contentY - _scrollOffsetY + HeaderRowH;
                        if (screenY + SepH > HeaderRowH && screenY < bounds.Height)
                        {
                            gr.FillRectangle(sepBgBrush, 0, screenY, bounds.Width, SepH);
                            DateTime.TryParse(t.DayKey, out DateTime sepDate);
                            string sepLabel = sepDate != default ? sepDate.ToString("dddd, MMM d") : t.DayKey;
                            gr.DrawString(sepLabel.ToUpper(), fontSep, grayBrush,
                                new Rectangle(PadX, screenY, bounds.Width - PadX * 2, SepH), sfLeft);
                        }
                        contentY += SepH;
                        lastDayKey = t.DayKey;
                    }

                    // Data row — only draw if visible
                    int rowScreenY = contentY - _scrollOffsetY + HeaderRowH;
                    bool visible = rowScreenY + RowH > HeaderRowH && rowScreenY < bounds.Height;

                    bool isSelected = t.TradeKey == _selectedTradeKey;
                    var rowRect = new Rectangle(0, rowScreenY, bounds.Width, RowH);

                    if (visible)
                    {
                        if (isSelected)
                            gr.FillRectangle(selBrush, rowRect);

                        gr.DrawLine(new Pen(Theme.RowSep), 0, rowScreenY + RowH - 1, bounds.Width, rowScreenY + RowH - 1);

                        string entryPrice = double.IsNaN(t.AvgEntryPrice) ? "\u2014" : t.AvgEntryPrice.ToString("#,##0.##");
                        string exitPrice = double.IsNaN(t.AvgExitPrice) ? "\u2014" : t.AvgExitPrice.ToString("#,##0.##");
                        string holdTime = FormatHoldTime(t.ExitTime - t.EntryTime);
                        string pnlText = FormatPnl(t.Pnl);
                        string sideText = t.IsLong ? "Long" : "Short";

                        // P&L Points: difference in price (exit - entry for long, entry - exit for short)
                        double pnlPoints = double.NaN;
                        if (!double.IsNaN(t.AvgEntryPrice) && !double.IsNaN(t.AvgExitPrice))
                            pnlPoints = t.IsLong ? t.AvgExitPrice - t.AvgEntryPrice : t.AvgEntryPrice - t.AvgExitPrice;
                        string pnlPointsText = double.IsNaN(pnlPoints) ? "\u2014"
                            : (pnlPoints >= 0 ? $"+{pnlPoints:0.##}" : $"{pnlPoints:0.##}");

                        var sideColor = t.IsLong ? greenBrush : redBrush;
                        var pnlColor = t.Pnl > 0 ? greenBrush : t.Pnl < 0 ? redBrush : grayBrush;
                        var pnlPointsColor = !double.IsNaN(pnlPoints) && pnlPoints > 0 ? greenBrush
                            : !double.IsNaN(pnlPoints) && pnlPoints < 0 ? redBrush : grayBrush;
                        var textColor = isSelected ? whiteBrush : whiteBrush;

                        void Cell(int col, string text, StringFormat sf, SolidBrush brush)
                        {
                            var r = new Rectangle(colX[col], rowScreenY, colW[col] - 2, RowH);
                            gr.DrawString(text, fontRow, brush, r, sf);
                        }

                        Cell(0, t.ExitTime.ToString("MMM d"), sfLeft, textColor);
                        Cell(1, t.EntryTime.ToString("HH:mm:ss"), sfLeft, textColor);
                        Cell(2, t.ExitTime.ToString("HH:mm:ss"), sfLeft, textColor);
                        Cell(3, TradeJournalPlugin.GetSymbolRoot(t.Symbol), sfLeft, textColor);
                        Cell(4, t.Quantity.ToString("0.#"), sfLeft, textColor);
                        Cell(5, sideText, sfLeft, sideColor);
                        Cell(6, entryPrice, sfLeft, textColor);
                        Cell(7, exitPrice, sfLeft, textColor);
                        Cell(8, holdTime, sfLeft, textColor);
                        Cell(9, pnlText, sfLeft, pnlColor);
                        Cell(10, pnlPointsText, sfLeft, pnlPointsColor);

                        // Note indicator: small filled circle in the right margin for
                        // trades that have a saved note, mirroring the calendar's underline.
                        string sanitizedKey = TradeJournalPlugin.SanitizeTradeKeyPublic(t.TradeKey);
                        if (_tradeNoteKeys.Contains(sanitizedKey))
                        {
                            const int dotSize = 6;
                            int dotX = bounds.Width - PadX - dotSize;
                            int dotY = rowScreenY + (RowH - dotSize) / 2;
                            gr.FillEllipse(noteDotBrush, dotX, dotY, dotSize, dotSize);
                        }
                    }

                    // Always register hit-test rect (in content-space Y)
                    _tradeCells.Add((new Rectangle(0, contentY, bounds.Width, RowH), t.TradeKey));
                    contentY += RowH;
                }
            }

            _contentHeight = contentY;

            // --- Scrollbar ---
            int visibleH = bounds.Height - HeaderRowH;
            if (_contentHeight > visibleH)
            {
                int sbW = 4;
                int sbX = bounds.Width - sbW - 2;
                float thumbRatio = (float)visibleH / _contentHeight;
                int thumbH = Math.Max(20, (int)(visibleH * thumbRatio));
                float scrollRatio = _contentHeight > visibleH ? (float)_scrollOffsetY / (_contentHeight - visibleH) : 0f;
                int thumbY = HeaderRowH + (int)((visibleH - thumbH) * scrollRatio);
                gr.FillRectangle(dimBrush, sbX, thumbY, sbW, thumbH);
            }

            gr.ResetClip();

            // Dispose
            fontHeader.Dispose(); fontRow.Dispose(); fontSep.Dispose();
            whiteBrush.Dispose(); grayBrush.Dispose(); dimBrush.Dispose();
            greenBrush.Dispose(); redBrush.Dispose(); selBrush.Dispose();
            hoverBrush.Dispose(); sepBgBrush.Dispose(); headerBgBrush.Dispose();
            noteDotBrush.Dispose();
            sfLeft.Dispose(); sfRight.Dispose(); sfCenter.Dispose();
        }

        private static string FormatHoldTime(TimeSpan ts)
        {
            if (ts.TotalSeconds < 0) ts = TimeSpan.Zero;
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes:00}m";
            if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds:00}s";
            return $"{(int)ts.TotalSeconds}s";
        }

        private static string FormatPnl(double pnl)
        {
            string sign = pnl < 0 ? "-" : "+";
            double abs = Math.Abs(pnl);
            return abs >= 1_000 ? $"{sign}${abs / 1000.0:0.##}k" : $"{sign}${abs:0.##}";
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
            NativeControl.MouseWheelNative -= OnMouseWheel;
            if (_bufferedGraphic != null)
            {
                _bufferedGraphic.Dispose();
                _bufferedGraphic = null;
            }
            base.Dispose();
        }
    }

    public class TradeJournalScreenshotStripRenderer : Renderer
    {
        private BufferedGraphic _bufferedGraphic;
        private readonly List<(Rectangle Rect, string Path)> _cells = new List<(Rectangle, string)>();
        private List<string> _entryPaths = new List<string>();
        private List<string> _exitPaths = new List<string>();

        public TradeJournalScreenshotStripRenderer(IRenderingNativeControl native)
            : base(native)
        {
            _bufferedGraphic = new BufferedGraphic(
                Draw, base.Refresh, native.DisposeImage,
                native.IsDisplayed, BufferedGraphicRequiredThreadType.LowPriority);
            NativeControl.MouseClickNative += OnMouseClick;
        }

        public void SetScreenshots(List<string> entryPaths, List<string> exitPaths)
        {
            _entryPaths = entryPaths ?? new List<string>();
            _exitPaths = exitPaths ?? new List<string>();
            _bufferedGraphic.IsDirty = true;
        }

        public void Clear()
        {
            _entryPaths = new List<string>();
            _exitPaths = new List<string>();
            _bufferedGraphic.IsDirty = true;
        }

        private void OnMouseClick(NativeMouseEventArgs e)
        {
            foreach (var cell in _cells)
            {
                if (cell.Rect.Contains(e.Location))
                {
                    try
                    {
                        if (File.Exists(cell.Path))
                            System.Diagnostics.Process.Start(
                                new System.Diagnostics.ProcessStartInfo(cell.Path)
                                { UseShellExecute = true });
                    }
                    catch { }
                    break;
                }
            }
        }

        private void Draw(Graphics gr)
        {
            _cells.Clear();
            Rectangle bounds = base.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            gr.Clear(Theme.PanelBg);
            if (_entryPaths.Count == 0 && _exitPaths.Count == 0) return;

            using var font = new Font("Arial", 8f, FontStyle.Regular);
            using var labelBrush = new SolidBrush(Theme.TextLightGray);
            using var numBrush = new SolidBrush(Theme.Win);
            using var boxBrush = new SolidBrush(Theme.BoxBg);
            using var borderPen = new Pen(Theme.Border);
            var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };

            int x = 8;
            int h = bounds.Height;
            int numW = 18;
            int gap = 8;

            void DrawGroup(string groupLabel, List<string> paths)
            {
                if (paths.Count == 0) return;
                SizeF sz = gr.MeasureString(groupLabel, font);
                int lw = (int)sz.Width + 4;
                gr.DrawString(groupLabel, font, labelBrush, new Rectangle(x, 0, lw, h), sf);
                x += lw + 2;
                for (int i = 0; i < paths.Count; i++)
                {
                    var rect = new Rectangle(x, 2, numW, h - 4);
                    gr.FillRectangle(boxBrush, rect);
                    gr.DrawRectangle(borderPen, rect);
                    gr.DrawString((i + 1).ToString(), font, numBrush, rect, sf);
                    _cells.Add((rect, paths[i]));
                    x += numW + 2;
                }
                x += gap;
            }

            DrawGroup("Entries", _entryPaths);
            DrawGroup("Exits", _exitPaths);
            sf.Dispose();
        }

        public override nint Render() => _bufferedGraphic.CurrentImage;

        public override void OnResize()
        {
            base.OnResize();
            var b = base.Bounds;
            if (b.Width == 0 || b.Height == 0) return;
            try { _bufferedGraphic.Resize(b.Width, b.Height); _bufferedGraphic.IsDirty = true; }
            catch { }
        }

        public override void Dispose()
        {
            NativeControl.MouseClickNative -= OnMouseClick;
            if (_bufferedGraphic != null) { _bufferedGraphic.Dispose(); _bufferedGraphic = null; }
            base.Dispose();
        }
    }

    // =========================================================================
    // Title-bar chrome renderer
    // =========================================================================
    // Occupies the same grid cell as the calendar (col 0, row 0, rowspan 3)
    // but spans both columns and has NO Layout.Margin. It only paints within
    // y=0..NonClientMarginTop, leaving the rest transparent so the calendar
    // and trade list (which have NonClientMargin pushed down) show through.
    // When tabbed NonClientMarginTop==0 so nothing is painted.
    public class TradeJournalTitleBarRenderer : Renderer
    {
        private readonly TradeJournalPlugin _plugin;
        private BufferedGraphic _bufferedGraphic;

        public TradeJournalTitleBarRenderer(IRenderingNativeControl native, TradeJournalPlugin plugin)
            : base(native)
        {
            _plugin = plugin;
            _bufferedGraphic = new BufferedGraphic(
                Draw, base.Refresh, native.DisposeImage,
                native.IsDisplayed, BufferedGraphicRequiredThreadType.LowPriority);
        }

        public void Redraw() => _bufferedGraphic.IsDirty = true;

        private void Draw(Graphics gr)
        {
            var bounds = Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            int tbH = _plugin.NonClientMarginTop;
            int w = bounds.Width;

            // When tabbed NonClientMarginTop is 0 — paint nothing (match bg color).
            if (tbH <= 0) { gr.Clear(Theme.PanelBg); return; }

            gr.Clear(Theme.PanelBg);

            int cy = tbH / 2;
            int btnW = tbH;
            int iconSz = 10;
            int ix, iy;

            // Separator at bottom of strip
            using var sepPen = new Pen(Theme.GridLine);
            gr.DrawLine(sepPen, 0, tbH - 1, w, tbH - 1);

            var iconColor = Theme.IconGray;

            // ── Close (×) — far right ─────────────────────────────────
            int closeX = w - btnW;
            using (var p = new Pen(iconColor, 1.5f))
            {
                ix = closeX + (btnW - iconSz) / 2;
                iy = cy - iconSz / 2;
                gr.DrawLine(p, ix, iy, ix + iconSz, iy + iconSz);
                gr.DrawLine(p, ix + iconSz, iy, ix, iy + iconSz);
            }

            // ── Maximize (□ with top bar) — next left ─────────────────
            int maxX = closeX - btnW;
            using (var p = new Pen(iconColor, 1.5f))
            {
                ix = maxX + (btnW - iconSz) / 2;
                iy = cy - iconSz / 2;
                gr.DrawRectangle(p, ix, iy, iconSz, iconSz);
                gr.DrawLine(p, ix, iy + 2, ix + iconSz, iy + 2);
            }

            // ── Minimize (—) — next left ──────────────────────────────
            int minX = maxX - btnW;
            using (var p = new Pen(iconColor, 1.5f))
            {
                int lx = minX + (btnW - iconSz) / 2;
                gr.DrawLine(p, lx, cy + 1, lx + iconSz, cy + 1);
            }

            // ── Fullscreen (two overlapping squares) — next left ──────
            int fsX = minX - btnW;
            using (var p = new Pen(iconColor, 1.5f))
            {
                ix = fsX + (btnW - iconSz) / 2;
                iy = cy - iconSz / 2;
                int half = iconSz / 2 - 1;
                gr.DrawRectangle(p, ix + half, iy, iconSz - half, iconSz - half);
                gr.DrawRectangle(p, ix, iy + half, iconSz - half, iconSz - half);
            }

            // ── Hamburger (≡) — far left ──────────────────────────────
            int hamLineW = iconSz;
            int hamX = (btnW - hamLineW) / 2;
            int hamSpacing = 3;
            int hamTopY = cy - hamSpacing;
            using var hamPen = new Pen(iconColor, 1.5f);
            for (int l = 0; l < 3; l++)
                gr.DrawLine(hamPen, hamX, hamTopY + l * hamSpacing, hamX + hamLineW, hamTopY + l * hamSpacing);
        }

        public override IntPtr Render() => _bufferedGraphic.CurrentImage;

        public override void OnResize()
        {
            base.OnResize();
            var b = Bounds;
            if (b.Width == 0 || b.Height == 0) return;
            try { _bufferedGraphic.Resize(b.Width, b.Height); _bufferedGraphic.IsDirty = true; }
            catch { }
        }

        public override void Dispose()
        {
            if (_bufferedGraphic != null) { _bufferedGraphic.Dispose(); _bufferedGraphic = null; }
            base.Dispose();
        }
    }
}