using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Timers;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Native;
using TradingPlatform.PresentationLayer.Plugins;
using TradingPlatform.PresentationLayer.Renderers;

namespace TradeJournal
{
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
                AllowSettings = false,
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

            this.Window.ReinitializeGridStructure(new NativeGridDefinition
            {
                Columns = new List<NativeGridItemDefinitionDefinition>
                {
                    new NativeGridItemDefinitionDefinition(false, 240) { SizeType = NativeGridItemDefinitionSizeType.Pixel },
                    new NativeGridItemDefinitionDefinition(false, 1)   { SizeType = NativeGridItemDefinitionSizeType.Star }
                },
                Rows = new List<NativeGridItemDefinitionDefinition>
                {
                    new NativeGridItemDefinitionDefinition(false, 1) { SizeType = NativeGridItemDefinitionSizeType.Star }
                }
            });

            var calControl = this.Window.CreateRenderingControl("TradeJournalCalendar");
            calControl.Layout.Column = 0;
            _calRenderer = new TradeJournalCalendarRenderer(calControl, this);
            _calRenderer.OnDaySelected += OnDaySelected;
            _calRenderer.OnPrevMonth += OnPrevMonth;
            _calRenderer.OnNextMonth += OnNextMonth;

            this.Window.Browser.AddEventHandler("noteArea", "oninput", OnNoteInput);
            this.Window.Browser.Layout.Column = 1;
        }

        public override void Populate(PluginParameters args = null)
        {
            base.Populate(args);
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

        // --- Events ---

        private void OnDaySelected(string date)
        {
            SaveNoteFromBrowser(_selectedDate);
            _selectedDate = date;
            _currentYear = int.Parse(date.Split('-')[0]);
            _currentMonth = int.Parse(date.Split('-')[1]) - 1;
            LoadNote(_selectedDate);
        }

        private void OnPrevMonth()
        {
            SaveNoteFromBrowser(_selectedDate);
            _currentMonth--;
            if (_currentMonth < 0) { _currentMonth = 11; _currentYear--; }
            _selectedDate = $"{_currentYear}-{(_currentMonth + 1):D2}-01";
            LoadNote(_selectedDate);
            _calRenderer.Redraw();
        }

        private void OnNextMonth()
        {
            SaveNoteFromBrowser(_selectedDate);
            _currentMonth++;
            if (_currentMonth > 11) { _currentMonth = 0; _currentYear++; }
            _selectedDate = $"{_currentYear}-{(_currentMonth + 1):D2}-01";
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
            // Block saves until the browser has fully loaded and the note has been populated —
            // otherwise an empty textarea would overwrite and delete the file on disk
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

        // Expose state to renderer
        public int CurrentMonth => _currentMonth;
        public int CurrentYear => _currentYear;
        public string SelectedDate => _selectedDate;

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

        private const int PrevBtnX = 8;
        private const int NextBtnX = 208;
        private const int HeaderY = 8;
        private const int HeaderH = 24;
        private const int DayNamesY = 40;
        private const int DayNamesH = 16;
        private const int GridStartY = 60;
        private const int CellW = 32;
        private const int CellH = 28;
        private const int GridStartX = 8;

        private Rectangle _prevBtnRect;
        private Rectangle _nextBtnRect;
        private readonly List<(Rectangle rect, string date)> _dayCells = new List<(Rectangle, string)>();

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

        public void Redraw()
        {
            _bufferedGraphic.IsDirty = true;
        }

        private void OnMouseClick(NativeMouseEventArgs e)
        {
            if (_prevBtnRect.Contains(e.Location))
            {
                OnPrevMonth?.Invoke();
                return;
            }
            if (_nextBtnRect.Contains(e.Location))
            {
                OnNextMonth?.Invoke();
                return;
            }
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

        private void Draw(Graphics gr)
        {
            gr.Clear(Color.FromArgb(37, 37, 37));

            _dayCells.Clear();

            var noteDates = _plugin.GetNoteDates();
            int month = _plugin.CurrentMonth;
            int year = _plugin.CurrentYear;
            string selected = _plugin.SelectedDate;

            var fontSmall = new Font("Arial", 9, FontStyle.Regular);
            var fontBold = new Font("Arial", 9, FontStyle.Bold);
            var whiteBrush = new SolidBrush(Color.White);
            var grayBrush = new SolidBrush(Color.FromArgb(120, 120, 120));
            var todayBrush = new SolidBrush(Color.FromArgb(42, 74, 107));
            var selectedBrush = new SolidBrush(Color.FromArgb(26, 107, 58));
            var hoverBrush = new SolidBrush(Color.FromArgb(60, 60, 60));
            var notePen = new Pen(Color.FromArgb(0, 200, 100), 2);
            var arrowBrush = new SolidBrush(Color.FromArgb(180, 180, 180));
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            string[] monthNames = { "January","February","March","April","May","June",
                "July","August","September","October","November","December" };
            string[] dayNames = { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" };

            _prevBtnRect = new Rectangle(PrevBtnX, HeaderY, 24, HeaderH);
            _nextBtnRect = new Rectangle(NextBtnX, HeaderY, 24, HeaderH);

            gr.DrawString("‹", new Font("Arial", 14, FontStyle.Bold), arrowBrush, _prevBtnRect, sf);
            gr.DrawString("›", new Font("Arial", 14, FontStyle.Bold), arrowBrush, _nextBtnRect, sf);

            var headerRect = new Rectangle(36, HeaderY, 168, HeaderH);
            gr.DrawString($"{monthNames[month]} {year}", fontBold, whiteBrush, headerRect, sf);

            for (int i = 0; i < 7; i++)
            {
                var r = new Rectangle(GridStartX + i * CellW, DayNamesY, CellW, DayNamesH);
                gr.DrawString(dayNames[i], fontSmall, grayBrush, r, sf);
            }

            int firstDay = (int)new DateTime(year, month + 1, 1).DayOfWeek;
            int daysInMonth = DateTime.DaysInMonth(year, month + 1);
            var today = DateTime.Today;

            for (int d = 1; d <= daysInMonth; d++)
            {
                int slot = firstDay + d - 1;
                int col = slot % 7;
                int row = slot / 7;

                var cellRect = new Rectangle(
                    GridStartX + col * CellW,
                    GridStartY + row * CellH,
                    CellW - 2,
                    CellH - 2);

                string dateStr = $"{year}-{(month + 1):D2}-{d:D2}";

                bool isToday = today.Year == year && today.Month == month + 1 && today.Day == d;
                bool isSelected = dateStr == selected;
                bool hasNote = noteDates.Contains(dateStr);

                if (isSelected)
                    gr.FillRectangle(selectedBrush, cellRect);
                else if (isToday)
                    gr.FillRectangle(todayBrush, cellRect);

                gr.DrawString(d.ToString(), isSelected || isToday ? fontBold : fontSmall,
                    isSelected || isToday ? whiteBrush : grayBrush, cellRect, sf);

                if (hasNote)
                    gr.DrawLine(notePen, cellRect.X + 4, cellRect.Bottom,
                        cellRect.Right - 4, cellRect.Bottom);

                _dayCells.Add((cellRect, dateStr));
            }

            fontSmall.Dispose();
            fontBold.Dispose();
            whiteBrush.Dispose();
            grayBrush.Dispose();
            todayBrush.Dispose();
            selectedBrush.Dispose();
            hoverBrush.Dispose();
            notePen.Dispose();
            arrowBrush.Dispose();
            sf.Dispose();
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