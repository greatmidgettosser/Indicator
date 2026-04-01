// MarketStructureTable.cs
// Quantower Indicator

using System;
using System.Drawing;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;

namespace MarketStructureTable
{
    public class MarketStructureTable : Indicator
    {
        [InputParameter("Short Lookback (bars)", 0, 1, 999, 1, 0)]
        public int LookbackShort = 25;

        [InputParameter("Medium Lookback (bars)", 1, 1, 999, 1, 0)]
        public int LookbackMid = 50;

        [InputParameter("Long Lookback (bars)", 2, 1, 999, 1, 0)]
        public int LookbackLong = 100;

        [InputParameter("Table X (px from left)", 3, 0, 5000, 1, 0)]
        public int TableX = 1225;

        [InputParameter("Table Y (px from top)", 4, 0, 5000, 1, 0)]
        public int TableY = 0;

        [InputParameter("Show Table", 5)]
        public bool ShowTable = true;

        [InputParameter("Show Pivot Triangles", 6)]
        public bool ShowPivots = true;

        [InputParameter("Pivot Window (bars each side)", 7, 1, 50, 1, 0)]
        public int PivotWindow = 3;

        private readonly Color BullColor = Color.FromArgb(255, 99, 153, 34);
        private readonly Color BearColor = Color.FromArgb(255, 226, 75, 74);
        private readonly Color HeaderColor = Color.FromArgb(255, 40, 40, 38);
        private readonly Color BgColor = Color.FromArgb(220, 28, 28, 26);
        private readonly Color TextColor = Color.FromArgb(255, 211, 209, 199);
        private readonly Color MutedColor = Color.FromArgb(255, 136, 135, 128);
        private readonly Color BorderColor = Color.FromArgb(255, 46, 46, 43);

        private double vwapCumTP = 0.0;
        private double vwapCumVol = 0.0;
        private DateTime lastDate = DateTime.MinValue;

        private const int BUF = 300;

        private double[] openBuf;
        private double[] closeBuf;
        private double[] vwapBuf;
        private double[] spreadBuf;
        private double[] pivHigh;
        private double[] pivLow;
        private DateTime[] barTime;

        private int head = -1;
        private int barCount = 0;

        public MarketStructureTable()
        {
            Name = "Market Structure Table";
            SeparateWindow = false;
        }

        protected override void OnInit()
        {
            vwapCumTP = 0.0;
            vwapCumVol = 0.0;
            lastDate = DateTime.MinValue;

            openBuf = new double[BUF];
            closeBuf = new double[BUF];
            vwapBuf = new double[BUF];
            spreadBuf = new double[BUF];
            pivHigh = new double[BUF];
            pivLow = new double[BUF];
            barTime = new DateTime[BUF];

            for (int i = 0; i < BUF; i++)
            {
                openBuf[i] = double.NaN;
                closeBuf[i] = double.NaN;
                pivHigh[i] = double.NaN;
                pivLow[i] = double.NaN;
                barTime[i] = DateTime.MinValue;
            }

            head = -1;
            barCount = 0;
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            bool isNewBar = args.Reason == UpdateReason.NewBar
                         || args.Reason == UpdateReason.HistoricalBar;

            DateTime barDate = Time().Date;
            if (barDate != lastDate)
            {
                vwapCumTP = 0.0;
                vwapCumVol = 0.0;
                lastDate = barDate;
            }

            double tp = (High() + Low() + Close()) / 3.0;
            double vol = Volume();
            vwapCumTP += tp * vol;
            vwapCumVol += vol;

            double vwap = vwapCumVol > 0 ? vwapCumTP / vwapCumVol : Close();
            double spread = Close() - vwap;

            if (isNewBar)
            {
                head = (head + 1) % BUF;

                double barClose = args.Reason == UpdateReason.HistoricalBar
                    ? HistoricalData[Count - 1, SeekOriginHistory.Begin][PriceType.Close]
                    : HistoricalData[0, SeekOriginHistory.End][PriceType.Close];

                double barOpen = args.Reason == UpdateReason.HistoricalBar
                    ? HistoricalData[Count - 1, SeekOriginHistory.Begin][PriceType.Open]
                    : HistoricalData[0, SeekOriginHistory.End][PriceType.Open];

                openBuf[head] = barOpen;
                closeBuf[head] = barClose;
                vwapBuf[head] = vwap;
                spreadBuf[head] = spread;
                pivHigh[head] = double.NaN;
                pivLow[head] = double.NaN;
                barTime[head] = Time();

                if (barCount < BUF) barCount++;

                int required = PivotWindow * 2 + 1;
                int center = PivotWindow;
                int windowSize = PivotWindow * 2 + 1;

                if (barCount >= required)
                {
                    double centerVal = RBuf(center);
                    int centerIdx = RIdx(center);

                    bool isHigh = true;
                    bool isLow = true;
                    int equalHighCount = 0;
                    int equalLowCount = 0;
                    int latestEqualHighOffset = -1;
                    int latestEqualLowOffset = -1;

                    for (int offset = 0; offset < windowSize; offset++)
                    {
                        if (offset == center) continue;
                        double val = RBuf(offset);

                        if (val > centerVal)
                            isHigh = false;
                        else if (val == centerVal)
                        {
                            equalHighCount++;
                            if (latestEqualHighOffset < 0 || offset < latestEqualHighOffset)
                                latestEqualHighOffset = offset;
                        }

                        if (val < centerVal)
                            isLow = false;
                        else if (val == centerVal)
                        {
                            equalLowCount++;
                            if (latestEqualLowOffset < 0 || offset < latestEqualLowOffset)
                                latestEqualLowOffset = offset;
                        }
                    }

                    if (isHigh)
                    {
                        if (equalHighCount == 0)
                            pivHigh[centerIdx] = centerVal;
                        else if (equalHighCount == 1)
                        {
                            if (latestEqualHighOffset < center)
                                pivHigh[RIdx(latestEqualHighOffset)] = centerVal;
                            else
                                pivHigh[centerIdx] = centerVal;
                        }
                    }

                    if (isLow)
                    {
                        if (equalLowCount == 0)
                            pivLow[centerIdx] = centerVal;
                        else if (equalLowCount == 1)
                        {
                            if (latestEqualLowOffset < center)
                                pivLow[RIdx(latestEqualLowOffset)] = centerVal;
                            else
                                pivLow[centerIdx] = centerVal;
                        }
                    }
                }
            }
            else
            {
                closeBuf[head] = Close();
                vwapBuf[head] = vwap;
                spreadBuf[head] = spread;
                barTime[head] = Time();
            }
        }

        private int RIdx(int offset) => ((head - offset) % BUF + BUF) % BUF;
        private double RBuf(int offset) => closeBuf[RIdx(offset)];
        private double OBuf(int offset) => openBuf[RIdx(offset)];

        private bool SpreadSlope(int lb)
        {
            if (barCount <= lb) return true;
            double raw = Math.Round(spreadBuf[RIdx(0)] - spreadBuf[RIdx(lb)], 4);
            return raw >= 0;
        }

        private double AvgPointsPerBar(int lb)
        {
            int available = Math.Min(lb, barCount - 1);
            if (available <= 0) return 0.0;

            double sum = 0.0;
            for (int offset = 1; offset <= available; offset++)
            {
                double o = OBuf(offset);
                double c = RBuf(offset);
                if (!double.IsNaN(o) && !double.IsNaN(c))
                    sum += Math.Abs(c - o);
            }
            return Math.Round(sum / available, 4);
        }

        private (bool bullish, string label, double upDisp, double downDisp)
            PivotStructure(int lb)
        {
            int scan = Math.Min(lb, barCount);

            double lastH = double.NaN;
            double lastL = double.NaN;
            double upDisp = 0.0;
            double downDisp = 0.0;
            bool lastWasLow = false;

            for (int offset = scan - 1; offset >= 0; offset--)
            {
                int idx = RIdx(offset);

                double ph = pivHigh[idx];
                double pl = pivLow[idx];

                if (!double.IsNaN(ph))
                {
                    if (!double.IsNaN(lastL))
                        upDisp += Math.Max(0, ph - lastL);

                    lastH = ph;
                    lastWasLow = false;
                }

                if (!double.IsNaN(pl))
                {
                    if (!double.IsNaN(lastH))
                        downDisp += Math.Max(0, lastH - pl);

                    lastL = pl;
                    lastWasLow = true;
                }
            }

            double currentPrice = RBuf(0);

            if (lastWasLow && !double.IsNaN(lastL))
                upDisp += Math.Max(0, currentPrice - lastL);
            else if (!lastWasLow && !double.IsNaN(lastH))
                downDisp += Math.Max(0, lastH - currentPrice);

            bool bull = upDisp >= downDisp;
            string label = $"up {upDisp:F2} down {downDisp:F2}";
            return (bull, label, upDisp, downDisp);
        }

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            if (barCount < PivotWindow * 2 + 1) return;

            if (ShowPivots)
                DrawPivotTriangles(args);

            if (ShowTable)
                DrawTable(args);
        }

        private void DrawPivotTriangles(PaintChartEventArgs args)
        {
            var window = CurrentChart.Windows[args.WindowIndex];
            var conv = window.CoordinatesConverter;
            var g = args.Graphics;

            int maxScan = Math.Min(LookbackLong, barCount);

            for (int offset = maxScan - 1; offset >= 0; offset--)
            {
                int idx = RIdx(offset);
                DateTime t = barTime[idx];
                if (t == DateTime.MinValue) continue;

                int x = (int)conv.GetChartX(t);

                if (!double.IsNaN(pivHigh[idx]))
                {
                    int y = (int)conv.GetChartY(pivHigh[idx]) - 6;
                    g.FillPolygon(new SolidBrush(BearColor), new[]
                    {
                        new Point(x,     y),
                        new Point(x - 4, y - 6),
                        new Point(x + 4, y - 6)
                    });
                }

                if (!double.IsNaN(pivLow[idx]))
                {
                    int y = (int)conv.GetChartY(pivLow[idx]) + 6;
                    g.FillPolygon(new SolidBrush(BullColor), new[]
                    {
                        new Point(x,     y),
                        new Point(x - 4, y + 6),
                        new Point(x + 4, y + 6)
                    });
                }
            }
        }

        private void DrawTable(PaintChartEventArgs args)
        {
            var gr = args.Graphics;

            int colW0 = 55;
            int colW1 = 135;
            int colW2 = 200;
            int rowH = 22;
            int pad = 6;
            int x = TableX;
            int y = TableY;
            int totalW = pad + colW0 + colW1 + colW2 + pad;
            int totalH = rowH * 4 + pad;

            gr.FillRectangle(new SolidBrush(BgColor), x, y, totalW, totalH);
            gr.DrawRectangle(new Pen(BorderColor), x, y, totalW, totalH);

            var fHdr = new Font("Consolas", 8f, FontStyle.Bold);
            var fCell = new Font("Consolas", 9f, FontStyle.Regular);
            var fSml = new Font("Consolas", 9f, FontStyle.Regular);

            gr.FillRectangle(new SolidBrush(HeaderColor), x, y, totalW, rowH);
            int cx = x + pad;
            gr.DrawString("Bars", fHdr, new SolidBrush(MutedColor), cx, y + 5);
            gr.DrawString("Per Bar Expectancy", fHdr, new SolidBrush(MutedColor), cx + colW0, y + 5);
            gr.DrawString("Pivots High vs Pivots Low", fHdr, new SolidBrush(MutedColor), cx + colW0 + colW1, y + 5);

            int[] lbs = { LookbackShort, LookbackMid, LookbackLong };

            for (int i = 0; i < 3; i++)
            {
                int lb = lbs[i];
                int rowY = y + rowH * (i + 1);

                if (i % 2 == 1)
                    gr.FillRectangle(new SolidBrush(Color.FromArgb(12, 255, 255, 255)), x, rowY, totalW, rowH);

                gr.DrawLine(new Pen(BorderColor), x, rowY, x + totalW, rowY);

                cx = x + pad;
                gr.DrawString($"{lb}b", fCell, new SolidBrush(MutedColor), cx, rowY + 4);

                cx += colW0;
                bool sBull = SpreadSlope(lb);
                double avgPts = AvgPointsPerBar(lb);
                double spreadVal = Math.Round(spreadBuf[RIdx(0)] - spreadBuf[RIdx(lb)], 4);
                gr.DrawString(sBull ? "BULL" : "BEAR", fCell,
                    new SolidBrush(sBull ? BullColor : BearColor), cx, rowY + 4);
                gr.DrawString($"{spreadVal:(+0.);(-0.)}|{avgPts:F2}", fSml,
                    new SolidBrush(TextColor), cx + 42, rowY + 5);

                cx += colW1;
                var (pBull, pLabel, _, _) = PivotStructure(lb);
                gr.DrawString(pBull ? "BULL" : "BEAR", fCell,
                    new SolidBrush(pBull ? BullColor : BearColor), cx, rowY + 4);
                gr.DrawString(pLabel, fSml,
                    new SolidBrush(TextColor), cx + 42, rowY + 5);
            }

            int div1 = x + pad + colW0;
            int div2 = div1 + colW1;
            gr.DrawLine(new Pen(BorderColor), div1, y, div1, y + totalH);
            gr.DrawLine(new Pen(BorderColor), div2, y, div2, y + totalH);

            fHdr.Dispose();
            fCell.Dispose();
            fSml.Dispose();
        }
    }
}
