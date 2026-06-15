using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;

namespace MicahsOscillator
{
    /// <summary>
    /// Micah's Oscillator — a multi-layer momentum & confluence system.
    /// Plots in its own separate pane below price.
    /// </summary>
    public class MicahsOscillator : Indicator
    {
        // ── WAVE SETTINGS ────────────────────────────────────────
        [InputParameter("Calculation Engine", 10,
            variants: new object[] {
                "Stochastic (default)", 0,
                "Rate of Change",       1,
                "Original (Regression)",2,
                "Hybrid",               3 })]
        public int Engine = 0;

        [InputParameter("Momentum Period", 20, 2, 200, 1, 0)]
        public int MomPeriod = 14;

        [InputParameter("Signal Period", 30, 1, 50, 1, 0)]
        public int SignalPeriod = 5;

        [InputParameter("Smoothing Period", 40, 1, 20, 1, 0)]
        public int SmoothPeriod = 3;

        // ── MFI / CONFLUENCE SETTINGS ────────────────────────────
        [InputParameter("MFI Period", 50, 2, 100, 1, 0)]
        public int MfiPeriod = 14;

        [InputParameter("Confluence Threshold", 60, 1, 40, 1, 0)]
        public int ConfluenceThreshold = 20;

        // ── REVERSAL SIGNAL SETTINGS ─────────────────────────────
        [InputParameter("Volume MA Period", 70, 2, 100, 1, 0)]
        public int VolMaPeriod = 20;

        [InputParameter("Volume Spike Multiplier", 80, 1.0, 5.0, 0.1, 1)]
        public double VolMultiplier = 1.5;

        [InputParameter("Overbought Level", 90, 50, 90, 1, 0)]
        public int OBLevel = 70;

        [InputParameter("Oversold Level", 100, 10, 50, 1, 0)]
        public int OSLevel = 30;

        [InputParameter("Signal Cooldown (bars)", 110, 1, 30, 1, 0)]
        public int CooldownBars = 5;

        // ── CAPITULATION FILTER ───────────────────────────────────
        [InputParameter("Enable Capitulation Filter", 120)]
        public bool UseCapFilter = true;

        [InputParameter("WVF Lookback Period", 130, 5, 100, 1, 0)]
        public int WvfPeriod = 22;

        [InputParameter("WVF BB Period", 140, 5, 50, 1, 0)]
        public int WvfBbPeriod = 20;

        [InputParameter("WVF BB Multiplier", 150, 0.5, 4.0, 0.1, 1)]
        public double WvfBbMult = 2.0;

        // ── TTM SQUEEZE SETTINGS ──────────────────────────────────
        [InputParameter("Squeeze BB Period", 160, 5, 50, 1, 0)]
        public int SqBbPeriod = 20;

        [InputParameter("Squeeze BB Multiplier", 170, 0.5, 4.0, 0.1, 1)]
        public double SqBbMult = 2.0;

        [InputParameter("Squeeze KC Period", 180, 5, 50, 1, 0)]
        public int SqKcPeriod = 20;

        [InputParameter("Squeeze KC Multiplier", 190, 0.5, 4.0, 0.1, 1)]
        public double SqKcMult = 1.5;

        // ── DIVERGENCE SETTINGS ───────────────────────────────────
        [InputParameter("Show Divergences", 200)]
        public bool ShowDivergences = true;

        [InputParameter("Divergence Pivot Lookback", 210, 2, 20, 1, 0)]
        public int DivPivotBars = 5;

        // ── LINE SERIES INDEX MAP ─────────────────────────────────
        // 0 — Momentum Wave
        // 1 — Signal Line
        // 2 — Zero line
        // 3 — OB reference
        // 4 — OS reference
        // 5 — TTM Squeeze dot
        // 6 — Band Top High edge
        // 7 — Band Top Low edge
        // 8 — Band Bot High edge
        // 9 — Band Bot Low edge

        // ── INTERNAL STATE ────────────────────────────────────────
        private int _barsSinceSignal = 999;
        private double _bandTop;
        private double _bandBottom;
        private double _emaVal = double.NaN;

        // Divergence state
        private double _lastPricePivotHigh = double.NaN;
        private double _lastOscPivotHigh = double.NaN;
        private double _lastPricePivotLow = double.NaN;
        private double _lastOscPivotLow = double.NaN;
        private int _lastPivotHighBar = -1;
        private int _lastPivotLowBar = -1;

        // Divergence line storage for OnPaintChart
        private struct DivLine
        {
            public int Bar1, Bar2;
            public double Val1, Val2;
            public Color Color;
            public bool Dashed;
        }
        private readonly List<DivLine> _divLines = new List<DivLine>();

        // ── CONSTRUCTOR ───────────────────────────────────────────
        public MicahsOscillator()
            : base()
        {
            Name = "Micah's Oscillator";
            Description = "Multi-layer momentum & confluence system";
            SeparateWindow = true;

            // 0 — Momentum Wave — yellow
            AddLineSeries("Momentum", Color.FromArgb(255, 230, 180, 0), lineWidth: 2, LineStyle.Solid);
            // 1 — Signal Line — orange
            AddLineSeries("Signal", Color.FromArgb(255, 220, 100, 0), lineWidth: 1, LineStyle.Solid);
            // 2 — Zero line
            AddLineSeries("Zero", Color.FromArgb(80, 255, 255, 255), lineWidth: 1, LineStyle.Dot);
            // 3 — OB reference
            AddLineSeries("OB Ref", Color.FromArgb(60, 255, 80, 80), lineWidth: 1, LineStyle.Dash);
            // 4 — OS reference
            AddLineSeries("OS Ref", Color.FromArgb(60, 80, 255, 120), lineWidth: 1, LineStyle.Dash);
            // 5 — TTM Squeeze dot on zero line
            AddLineSeries("Squeeze", Color.FromArgb(255, 255, 165, 0), lineWidth: 4, LineStyle.Points);
            // 6-9 — Confluence band edges
            AddLineSeries("Band Top High", Color.FromArgb(0, 0, 0, 0), lineWidth: 1, LineStyle.Solid);
            AddLineSeries("Band Top Low", Color.FromArgb(0, 0, 0, 0), lineWidth: 1, LineStyle.Solid);
            AddLineSeries("Band Bot High", Color.FromArgb(0, 0, 0, 0), lineWidth: 1, LineStyle.Solid);
            AddLineSeries("Band Bot Low", Color.FromArgb(0, 0, 0, 0), lineWidth: 1, LineStyle.Solid);
        }

        // ── INIT ─────────────────────────────────────────────────
        protected override void OnInit()
        {
            _bandTop = 75.0;
            _bandBottom = -75.0;
            _emaVal = double.NaN;
            _barsSinceSignal = 999;
            _divLines.Clear();
            _lastPricePivotHigh = double.NaN;
            _lastOscPivotHigh = double.NaN;
            _lastPricePivotLow = double.NaN;
            _lastOscPivotLow = double.NaN;
            _lastPivotHighBar = -1;
            _lastPivotLowBar = -1;
        }

        protected override void OnClear()
        {
            _divLines.Clear();
        }

        // ── MAIN CALCULATION ─────────────────────────────────────
        protected override void OnUpdate(UpdateArgs args)
        {
            int bar = Count - 1;

            // True only on a fully closed bar — used to gate markers and EMA commits
            bool isClosingBar = args.Reason == UpdateReason.NewBar
                             || args.Reason == UpdateReason.HistoricalBar;

            int minBars = Math.Max(Math.Max(MomPeriod, MfiPeriod),
                          Math.Max(WvfPeriod, SqBbPeriod)) + SignalPeriod + 5;
            if (bar < minBars) return;

            // ── 1. MOMENTUM WAVE ─────────────────────────────────
            // Lines update every tick for smooth visual; EMA state only commits on bar close
            double mom = CalcMomentum();
            double sig = CalcSignalEma(mom, isClosingBar);
            double momNorm = Clamp(mom, -80, 80);
            double sigNorm = Clamp(sig, -80, 80);

            SetValue(momNorm, 0);
            SetValue(sigNorm, 1);
            SetValue(0.0, 2);
            SetValue(OBLevel * 0.8, 3);
            SetValue(-OSLevel * 0.8, 4);

            // Ribbon color between momentum and signal (updates every tick)
            Color ribbonColor = momNorm >= sigNorm
                ? Color.FromArgb(80, 230, 180, 0)
                : Color.FromArgb(80, 220, 100, 0);
            LinesSeries[0].SetMarker(0, new IndicatorLineMarker(ribbonColor));

            // Crossover arrows — only on confirmed bar close to avoid tick noise
            if (isClosingBar)
            {
                bool crossUp = momNorm > sigNorm && GetValue(1, 1) >= GetValue(0, 1);
                bool crossDown = momNorm < sigNorm && GetValue(1, 1) <= GetValue(0, 1);
                if (crossUp)
                    LinesSeries[0].SetMarker(0, new IndicatorLineMarker(Color.Yellow,
                        upperIcon: IndicatorLineMarkerIconType.UpArrow));
                else if (crossDown)
                    LinesSeries[0].SetMarker(0, new IndicatorLineMarker(Color.OrangeRed,
                        bottomIcon: IndicatorLineMarkerIconType.DownArrow));
            }

            // ── 2. MFI & CONFLUENCE BANDS ─────────────────────────
            double mfi = CalcMfi();
            double mfiNorm = (mfi - 50.0) * 1.6;

            bool bullConfluence = momNorm < -10 && mfiNorm < -10
                                  && Math.Abs(momNorm - mfiNorm) < ConfluenceThreshold;
            bool bearConfluence = momNorm > 10 && mfiNorm > 10
                                  && Math.Abs(momNorm - mfiNorm) < ConfluenceThreshold;

            Color upperBandColor = bearConfluence
                ? Color.FromArgb(220, 230, 120, 0)
                : Color.FromArgb(60, 180, 80, 0);
            Color lowerBandColor = bullConfluence
                ? Color.FromArgb(220, 30, 200, 80)
                : Color.FromArgb(60, 20, 120, 50);

            double bandHeight = 8.0;
            SetValue(_bandTop, 6);
            SetValue(_bandTop - bandHeight, 7);
            SetValue(_bandBottom + bandHeight, 8);
            SetValue(_bandBottom, 9);

            LinesSeries[6].SetMarker(0, new IndicatorLineMarker(upperBandColor));
            LinesSeries[7].SetMarker(0, new IndicatorLineMarker(upperBandColor));
            LinesSeries[8].SetMarker(0, new IndicatorLineMarker(lowerBandColor));
            LinesSeries[9].SetMarker(0, new IndicatorLineMarker(lowerBandColor));

            // ── 3. REVERSAL SIGNALS — only on bar close ───────────
            if (isClosingBar)
            {
                _barsSinceSignal++;
                if (_barsSinceSignal >= CooldownBars)
                {
                    bool volSpike = IsVolumeSpike();
                    bool atOB = momNorm > (OBLevel * 0.8);
                    bool atOS = momNorm < -(OSLevel * 0.8);
                    bool mfiOB = mfi > OBLevel;
                    bool mfiOS = mfi < OSLevel;

                    double wvf = CalcWvf();
                    double wvfBand = CalcWvfUpperBand();
                    bool capLong = !UseCapFilter || wvf > wvfBand;
                    bool capShort = !UseCapFilter || CalcInvWvf() > wvfBand;

                    // BEARISH reversal
                    if (volSpike && atOB && mfiOB && capShort)
                    {
                        bool isMajor = wvf > wvfBand * 1.2;
                        string lbl = isMajor ? "X" : "O";
                        Color clr = isMajor ? Color.Red : Color.OrangeRed;
                        LinesSeries[0].SetMarker(0, new IndicatorLineMarker(clr)
                        {
                            UpperIcon = IndicatorLineMarkerIconType.Text,
                            TextSettings = new IndicatorLineMarkerTextSettings { Text = lbl }
                        });
                        _barsSinceSignal = 0;
                    }
                    // BULLISH reversal
                    else if (volSpike && atOS && mfiOS && capLong)
                    {
                        bool isMajor = wvf > wvfBand * 1.2;
                        string lbl = isMajor ? "X" : "O";
                        Color clr = isMajor ? Color.Lime : Color.LimeGreen;
                        LinesSeries[0].SetMarker(0, new IndicatorLineMarker(clr)
                        {
                            BottomIcon = IndicatorLineMarkerIconType.Text,
                            TextSettings = new IndicatorLineMarkerTextSettings { Text = lbl }
                        });
                        _barsSinceSignal = 0;
                    }
                }
            }

            // ── 4. TTM SQUEEZE ───────────────────────────────────
            bool squeezeOn = IsSqueezeOn();
            Color sqColor = squeezeOn
                ? Color.FromArgb(255, 255, 140, 0)
                : Color.FromArgb(255, 0, 220, 220);
            LinesSeries[5].SetMarker(0, new IndicatorLineMarker(sqColor));
            SetValue(0.0, 5);

            // ── 5. DIVERGENCES — only on bar close ────────────────
            if (isClosingBar && ShowDivergences && bar >= DivPivotBars * 2)
                CheckDivergences(bar, momNorm);
        }

        // ═══════════════════════════════════════════════════════
        //  SUB-CALCULATIONS
        //  All use offset-from-current: 0 = current bar, 1 = 1 bar ago
        // ═══════════════════════════════════════════════════════

        private double CalcMomentum()
        {
            switch (Engine)
            {
                case 1: return CalcROC();
                case 2: return CalcOriginal();
                case 3: return (CalcStochastic() + CalcROC() + CalcOriginal()) / 3.0;
                default: return CalcStochastic();
            }
        }

        private double CalcStochastic()
        {
            double hi = double.MinValue, lo = double.MaxValue;
            for (int i = 0; i < MomPeriod; i++)
            {
                hi = Math.Max(hi, High(i));
                lo = Math.Min(lo, Low(i));
            }
            double range = hi - lo;
            if (range < 1e-10) return 0;
            double k = (Close(0) - lo) / range * 100.0 - 50.0;
            return k * 1.6;
        }

        private double CalcROC()
        {
            double prev = Close(MomPeriod);
            if (Math.Abs(prev) < 1e-10) return 0;
            return (Close(0) - prev) / prev * 100.0 * 4.0;
        }

        private double CalcOriginal()
        {
            double sumY = 0, sumX = 0, sumXY = 0, sumX2 = 0;
            int n = MomPeriod;
            for (int i = 0; i < n; i++)
            {
                double mid = (High(i) + Low(i)) / 2.0;
                double diff = Close(i) - mid;
                sumY += diff;
                sumX += i;
                sumXY += i * diff;
                sumX2 += i * i;
            }
            double denom = n * sumX2 - sumX * sumX;
            if (Math.Abs(denom) < 1e-10) return 0;
            double slope = (n * sumXY - sumX * sumY) / denom;
            return Clamp(slope * 100.0, -80, 80);
        }

        // commit=true: update stored EMA (bar close)
        // commit=false: return projected value without mutating state (tick)
        private double CalcSignalEma(double newVal, bool commit)
        {
            double k = 2.0 / (SignalPeriod + 1.0);
            if (double.IsNaN(_emaVal))
            {
                if (commit) _emaVal = newVal;
                return newVal;
            }
            double next = newVal * k + _emaVal * (1.0 - k);
            if (commit) _emaVal = next;
            return next;
        }

        private double CalcMfi()
        {
            double posFlow = 0, negFlow = 0;
            double prevTp = (High(1) + Low(1) + Close(1)) / 3.0;
            for (int i = 0; i < MfiPeriod; i++)
            {
                double tp = (High(i) + Low(i) + Close(i)) / 3.0;
                double rmf = tp * Volume(i);
                if (tp >= prevTp) posFlow += rmf;
                else negFlow += rmf;
                prevTp = tp;
            }
            if (negFlow < 1e-10) return 100;
            double ratio = posFlow / negFlow;
            return 100.0 - 100.0 / (1.0 + ratio);
        }

        private bool IsVolumeSpike()
        {
            double avgVol = 0;
            int count = 0;
            for (int i = 1; i <= VolMaPeriod; i++, count++)
                avgVol += Volume(i);
            if (count == 0) return false;
            avgVol /= count;
            return Volume(0) > avgVol * VolMultiplier;
        }

        private double CalcWvf()
        {
            double highest = double.MinValue;
            for (int i = 0; i < WvfPeriod; i++)
                highest = Math.Max(highest, Close(i));
            if (highest < 1e-10) return 0;
            return (highest - Low(0)) / highest * 100.0;
        }

        private double CalcInvWvf()
        {
            double lowest = double.MaxValue;
            for (int i = 0; i < WvfPeriod; i++)
                lowest = Math.Min(lowest, Close(i));
            if (Math.Abs(lowest) < 1e-10) return 0;
            return Math.Abs((lowest - High(0)) / lowest * 100.0);
        }

        private double CalcWvfUpperBand()
        {
            double sum = 0, sum2 = 0;
            int cnt = 0;
            for (int i = 0; i < WvfBbPeriod; i++, cnt++)
            {
                double highest = double.MinValue;
                for (int j = i; j < i + WvfPeriod; j++)
                    highest = Math.Max(highest, Close(j));
                double wvfI = (highest < 1e-10) ? 0 : (highest - Low(i)) / highest * 100.0;
                sum += wvfI;
                sum2 += wvfI * wvfI;
            }
            if (cnt == 0) return 999;
            double mean = sum / cnt;
            double std = Math.Sqrt(Math.Max(0, sum2 / cnt - mean * mean));
            return mean + WvfBbMult * std;
        }

        private bool IsSqueezeOn()
        {
            double sma = 0;
            for (int i = 0; i < SqBbPeriod; i++)
                sma += Close(i);
            sma /= SqBbPeriod;

            double sum2 = 0;
            for (int i = 0; i < SqBbPeriod; i++)
            {
                double diff = Close(i) - sma;
                sum2 += diff * diff;
            }
            double std = Math.Sqrt(sum2 / SqBbPeriod);
            double bbUpper = sma + SqBbMult * std;
            double bbLower = sma - SqBbMult * std;

            double atr = CalcAtr(SqKcPeriod);
            double kcUpper = sma + SqKcMult * atr;
            double kcLower = sma - SqKcMult * atr;

            return bbUpper < kcUpper && bbLower > kcLower;
        }

        private double CalcAtr(int period)
        {
            double sum = 0;
            int cnt = 0;
            for (int i = 0; i < period; i++, cnt++)
            {
                double tr = Math.Max(High(i) - Low(i),
                            Math.Max(Math.Abs(High(i) - Close(i + 1)),
                                     Math.Abs(Low(i) - Close(i + 1))));
                sum += tr;
            }
            return cnt > 0 ? sum / cnt : 0;
        }

        // ── Divergences ──────────────────────────────────────────
        private void CheckDivergences(int bar, double oscVal)
        {
            int lb = DivPivotBars;
            int pivot = bar - lb;
            if (pivot < lb) return;

            bool isSwingHigh = true, isSwingLow = true;
            for (int i = 1; i <= lb; i++)
            {
                double pivClose = Close(lb);
                double olderClose = Close(lb + i);
                double newerClose = Close(lb - i);

                if (pivClose <= olderClose || pivClose <= newerClose) isSwingHigh = false;
                if (pivClose >= olderClose || pivClose >= newerClose) isSwingLow = false;
            }

            double oscAtPivot = GetValue(lb, 0);
            double curPrice = Close(lb);

            if (isSwingHigh && !double.IsNaN(_lastPricePivotHigh) && _lastPivotHighBar >= 0)
            {
                if (curPrice > _lastPricePivotHigh && oscAtPivot < _lastOscPivotHigh)
                    StoreDivLine(_lastPivotHighBar, pivot, _lastOscPivotHigh, oscAtPivot, Color.Red, false);
                else if (curPrice < _lastPricePivotHigh && oscAtPivot > _lastOscPivotHigh)
                    StoreDivLine(_lastPivotHighBar, pivot, _lastOscPivotHigh, oscAtPivot, Color.Orange, true);

                _lastPricePivotHigh = curPrice;
                _lastOscPivotHigh = oscAtPivot;
                _lastPivotHighBar = pivot;
            }
            else if (isSwingHigh)
            {
                _lastPricePivotHigh = curPrice;
                _lastOscPivotHigh = oscAtPivot;
                _lastPivotHighBar = pivot;
            }

            if (isSwingLow && !double.IsNaN(_lastPricePivotLow) && _lastPivotLowBar >= 0)
            {
                if (curPrice < _lastPricePivotLow && oscAtPivot > _lastOscPivotLow)
                    StoreDivLine(_lastPivotLowBar, pivot, _lastOscPivotLow, oscAtPivot, Color.Lime, false);
                else if (curPrice > _lastPricePivotLow && oscAtPivot < _lastOscPivotLow)
                    StoreDivLine(_lastPivotLowBar, pivot, _lastOscPivotLow, oscAtPivot, Color.Cyan, true);

                _lastPricePivotLow = curPrice;
                _lastOscPivotLow = oscAtPivot;
                _lastPivotLowBar = pivot;
            }
            else if (isSwingLow)
            {
                _lastPricePivotLow = curPrice;
                _lastOscPivotLow = oscAtPivot;
                _lastPivotLowBar = pivot;
            }
        }

        private void StoreDivLine(int bar1, int bar2,
                                  double val1, double val2,
                                  Color color, bool dashed)
        {
            _divLines.Add(new DivLine
            {
                Bar1 = bar1,
                Bar2 = bar2,
                Val1 = val1,
                Val2 = val2,
                Color = color,
                Dashed = dashed
            });
        }

        // ── UTILITY ─────────────────────────────────────────────
        private static double Clamp(double val, double min, double max)
            => val < min ? min : val > max ? max : val;

        // ── PAINT ────────────────────────────────────────────────
        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);

            if (!ShowDivergences || _divLines.Count == 0) return;
            if (CurrentChart == null) return;

            var window = CurrentChart.Windows[args.WindowIndex];
            var conv = window.CoordinatesConverter;
            var g = args.Graphics;

            foreach (var dl in _divLines)
            {
                int offset1 = (Count - 1) - dl.Bar1;
                int offset2 = (Count - 1) - dl.Bar2;
                if (offset1 < 0 || offset2 < 0) continue;

                DateTime t1 = Time(offset1);
                DateTime t2 = Time(offset2);

                int x1 = (int)conv.GetChartX(t1);
                int x2 = (int)conv.GetChartX(t2);
                int y1 = (int)conv.GetChartY(dl.Val1);
                int y2 = (int)conv.GetChartY(dl.Val2);

                using var pen = new Pen(dl.Color, 2)
                {
                    DashStyle = dl.Dashed ? DashStyle.Dash : DashStyle.Solid
                };
                g.DrawLine(pen, x1, y1, x2, y2);
            }
        }
    }
}
