using System;
using System.Drawing;
using TradingPlatform.BusinessLayer;

namespace VolumeWeightedTrend
{
    /// <summary>
    /// Volume Weighted Trend — overlay indicator.
    /// VWMA baseline with ATR volatility bands; trend flips when price
    /// closes beyond a band. Ported from the QuantAlgo Pine Script version
    /// (ribbons, bar-coloring, and alerts intentionally omitted).
    /// </summary>
    public class VolumeWeightedTrend : Indicator
    {
        // ── CALCULATION SETTINGS ─────────────────────────────────
        [InputParameter("VWMA Length", 10, 5, 500, 1, 0)]
        public int VwmaLength = 34;

        [InputParameter("ATR Multiplier", 20, 0.5, 10.0, 0.1, 1)]
        public double AtrMultiplier = 1.5;

        [InputParameter("Preset Configuration", 30,
            variants: new object[] {
                "Default",       0,
                "Fast Response", 1,
                "Smooth Trend",  2 })]
        public int PresetConfig = 0;

        // ── VISUAL SETTINGS ───────────────────────────────────────
        [InputParameter("Color Preset", 40,
            variants: new object[] {
                "Classic", 0,
                "Aqua",    1,
                "Cosmic",  2,
                "Cyber",   3,
                "Neon",    4,
                "Custom",  5 })]
        public int ColorPreset = 5;

        [InputParameter("Bullish Trend Color", 50)]
        public Color BullishInput = Color.FromArgb(255, 0, 255, 170);

        [InputParameter("Bearish Trend Color", 60)]
        public Color BearishInput = Color.FromArgb(255, 255, 0, 0);

        [InputParameter("Enable Neon Glow Effect", 70)]
        public bool EnableGlow = true;

        [InputParameter("Band Transparency (0-100)", 80, 0, 100, 1, 0)]
        public int BandTransparency = 90;

        // ── LINE SERIES INDEX MAP ─────────────────────────────────
        // 0 — Glow Outer
        // 1 — Glow Inner
        // 2 — VWMA Line
        // 3 — Upper Band
        // 4 — Lower Band

        // ── INTERNAL STATE ────────────────────────────────────────
        private int _trendDirection = 0;
        private double _atrRma = double.NaN; // Wilder RMA state for ATR

        public VolumeWeightedTrend()
            : base()
        {
            Name = "Volume Weighted Trend";
            Description = "VWMA baseline with ATR volatility bands";
            SeparateWindow = false; // overlay on price pane

            AddLineSeries("VWMA Glow Outer", Color.FromArgb(80, 0, 255, 170), lineWidth: 8, LineStyle.Solid);
            AddLineSeries("VWMA Glow Inner", Color.FromArgb(130, 0, 255, 170), lineWidth: 4, LineStyle.Solid);
            AddLineSeries("VWMA Line", Color.FromArgb(255, 0, 255, 170), lineWidth: 2, LineStyle.Solid);
            AddLineSeries("Upper Band", Color.FromArgb(60, 0, 255, 170), lineWidth: 1, LineStyle.Solid);
            AddLineSeries("Lower Band", Color.FromArgb(60, 0, 255, 170), lineWidth: 1, LineStyle.Solid);
        }

        protected override void OnInit()
        {
            _trendDirection = 0;
            _atrRma = double.NaN;
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            // Apply preset overrides (mirrors the Pine if/else block)
            int vwmaLength = VwmaLength;
            double atrMultiplier = AtrMultiplier;
            if (PresetConfig == 1) { vwmaLength = 21; atrMultiplier = 1.2; }
            else if (PresetConfig == 2) { vwmaLength = 55; atrMultiplier = 2.0; }

            bool isClosingBar = args.Reason == UpdateReason.NewBar
                             || args.Reason == UpdateReason.HistoricalBar;

            int minBars = vwmaLength + 5;
            if (Count - 1 < minBars) return;

            double vwma = CalcVwma(vwmaLength);
            double atr = CalcAtrRma(vwmaLength, isClosingBar);

            double upperBand = vwma + atr * atrMultiplier;
            double lowerBand = vwma - atr * atrMultiplier;

            // Trend direction persists until price closes beyond a band
            if (Close(0) > upperBand)
                _trendDirection = 1;
            else if (Close(0) < lowerBand)
                _trendDirection = -1;
            // else: unchanged, matches Pine's var-persisted trend_direction

            (Color bullish, Color bearish) = GetColorPreset();
            Color trendColor = _trendDirection == 1 ? bullish : bearish;

            // ── Plot VWMA + glow ──────────────────────────────────
            if (EnableGlow)
            {
                SetValue(vwma, 0);
                SetValue(vwma, 1);
                LinesSeries[0].SetMarker(0, new IndicatorLineMarker(Color.FromArgb(60, trendColor.R, trendColor.G, trendColor.B)));
                LinesSeries[1].SetMarker(0, new IndicatorLineMarker(Color.FromArgb(110, trendColor.R, trendColor.G, trendColor.B)));
            }
            else
            {
                SetValue(double.NaN, 0);
                SetValue(double.NaN, 1);
            }

            SetValue(vwma, 2);
            LinesSeries[2].SetMarker(0, new IndicatorLineMarker(trendColor));

            // ── Plot bands ────────────────────────────────────────
            int bandAlpha = (int)Math.Round((100 - BandTransparency) / 100.0 * 255.0);
            Color bandColor = Color.FromArgb(bandAlpha, trendColor.R, trendColor.G, trendColor.B);

            SetValue(upperBand, 3);
            SetValue(lowerBand, 4);
            LinesSeries[3].SetMarker(0, new IndicatorLineMarker(bandColor));
            LinesSeries[4].SetMarker(0, new IndicatorLineMarker(bandColor));
        }

        // ═══════════════════════════════════════════════════════
        //  SUB-CALCULATIONS
        // ═══════════════════════════════════════════════════════

        // Volume-weighted moving average of Close over `length` bars
        private double CalcVwma(int length)
        {
            double sumPV = 0, sumV = 0;
            for (int i = 0; i < length; i++)
            {
                sumPV += Close(i) * Volume(i);
                sumV += Volume(i);
            }
            if (sumV < 1e-10) return Close(0);
            return sumPV / sumV;
        }

        // Wilder RMA-smoothed ATR (matches Pine's ta.atr), commits state only on closed bars
        private double CalcAtrRma(int period, bool commit)
        {
            double tr = Math.Max(High(0) - Low(0),
                        Math.Max(Math.Abs(High(0) - Close(1)),
                                 Math.Abs(Low(0) - Close(1))));

            if (double.IsNaN(_atrRma))
            {
                // Seed with a simple average over the first `period` bars
                double sum = 0;
                for (int i = 0; i < period; i++)
                {
                    double t = Math.Max(High(i) - Low(i),
                               Math.Max(Math.Abs(High(i) - Close(i + 1)),
                                        Math.Abs(Low(i) - Close(i + 1))));
                    sum += t;
                }
                double seed = sum / period;
                if (commit) _atrRma = seed;
                return seed;
            }

            double next = (_atrRma * (period - 1) + tr) / period;
            if (commit) _atrRma = next;
            return next;
        }

        private (Color bullish, Color bearish) GetColorPreset()
        {
            switch (ColorPreset)
            {
                case 0: return (Color.FromArgb(255, 0, 255, 0), Color.FromArgb(255, 255, 0, 0));       // Classic
                case 1: return (Color.FromArgb(255, 0, 212, 255), Color.FromArgb(255, 255, 140, 0));   // Aqua
                case 2: return (Color.FromArgb(255, 73, 255, 206), Color.FromArgb(255, 153, 50, 204));  // Cosmic
                case 3: return (Color.FromArgb(255, 0, 204, 204), Color.FromArgb(255, 255, 102, 0));    // Cyber
                case 4: return (Color.FromArgb(255, 255, 255, 0), Color.FromArgb(255, 255, 0, 255));    // Neon
                default: return (BullishInput, BearishInput);                                            // Custom
            }
        }
    }
}
