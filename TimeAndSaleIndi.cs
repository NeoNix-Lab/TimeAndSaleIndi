using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingPlatform.BusinessLayer;

namespace TimeAndSaleIndi
{
    public class NewTeSHistorical : Indicator
    {
        private CancellationTokenSource _cts;
        private Task _backgroundTask;
        private bool _isProcessing;
        private double _progress;
        public double ProgressPercent => _progress;

        private readonly List<int> _buyCandles = new();
        private readonly List<int> _sellCandles = new();
        private readonly List<int> _trappedCandles = new();

        [InputParameter("Filter Size", 0, 0.0001, double.MaxValue, 0.0001, 4)]
        public double FilterSize = 0.01;

        [InputParameter("Continuous Count", 1, 1, int.MaxValue, 1, 0)]
        public int ContinuousCount = 10;

        [InputParameter("Display Buyers", 2)]
        public bool DisplayBuyers = true;

        [InputParameter("Display Sellers", 3)]
        public bool DisplaySellers = true;

        [InputParameter("Color Buy", 4)]
        public Color ColorBuy = Color.Lime;

        [InputParameter("Color Sell", 5)]
        public Color ColorSell = Color.Orange;

        [InputParameter("Color Trapped", 6)]
        public Color ColorTrapped = Color.DimGray;

        public NewTeSHistorical()
        {
            Name = "NewTeS (Historical)";
            Description = "Historical time & sales divergence detector";
            SeparateWindow = false;
            UpdateType = IndicatorUpdateType.OnBarClose;
        }

        protected override void OnInit()
        {
            // Cancella eventuale task precedente
            try
            {
                _cts?.Cancel();
                _backgroundTask?.Wait(200);
            }
            catch { /* ignore */ }

            _cts = new CancellationTokenSource();
            _isProcessing = false;
            _progress = 0;

            _buyCandles.Clear();
            _sellCandles.Clear();
            _trappedCandles.Clear();

            // Avvio del caricamento storico
            _backgroundTask = Task.Run(() => ProcessPowerTradesAsync(_cts.Token));
        }

        protected override void OnClear()
        {
            try
            {
                _cts?.Cancel();
                _backgroundTask?.Wait(200);
            }
            catch { /* ignore */ }

            _buyCandles.Clear();
            _sellCandles.Clear();
            _trappedCandles.Clear();
        }

        public override void Dispose()
        {
            OnClear();
            base.Dispose();
        }

        // ===== OFFLINE PROCESSING =====

        private async Task ProcessPowerTradesAsync(CancellationToken ct)
        {
            if (HistoricalData == null || HistoricalData.Count < 2)
                return;

            try
            {
                _isProcessing = true;
                _progress = 0;

                // ====== Snapshot immutabile ======
                DateTime start = HistoricalData.FromTime;
                DateTime end = HistoricalData.ToTime;
                var barsSnapshot = HistoricalData.ToArray(); // copia statica delle barre

                // copia statica dei tick
                var allTicks = Symbol.GetHistory(Period.TICK1, HistoryType.Last, start, end)
                    .OfType<HistoryItemLast>()
                    .ToList();

                // buffer locali per barra
                var buyers = new RingBuffer<double>(ContinuousCount);
                var sellers = new RingBuffer<double>(ContinuousCount);

                int totalBars = barsSnapshot.Length;
                int step = Math.Max(1, totalBars / 200);

                for (int i = 0; i < totalBars - 1; i++)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    var bar = barsSnapshot[i];
                    buyers.Clear();
                    sellers.Clear();

                    // snapshot locale per tick della barra
                    var ticks = allTicks
                        .Where(t => t.TimeLeft >= bar.TimeLeft && t.TimeLeft < bar.TimeLeft.AddTicks(Math.Abs(bar.TicksRight - bar.TicksLeft)))
                        .ToList();

                    foreach (var t in ticks)
                    {
                        if (t.Volume < FilterSize)
                            continue;

                        if ((t.TickDirection == TickDirection.NotSet)
                            && (t.AggressorFlag == AggressorFlag.None || t.AggressorFlag == AggressorFlag.NotSet))
                            continue;

                        var buffer = t.AggressorFlag == AggressorFlag.Buy ? buyers : sellers;

                        if (buffer.IsFull)
                        {
                            double min = buffer.GetItems().Select(Math.Abs).Min();
                            if (t.Volume > min)
                            {
                                int idx = Array.IndexOf(buffer.ToArray(), min);
                                buffer[idx] = t.Volume;
                            }
                        }
                        else
                        {
                            buffer.Add(t.Volume);
                        }
                    }

                    bool bull = ValidationHelper.ValidateArrays(Side.Buy, buyers, sellers);
                    bool bear = ValidationHelper.ValidateArrays(Side.Sell, buyers, sellers);

                    double o = bar[PriceType.Open];
                    double c = bar[PriceType.Close];

                    if (c > o)
                    {
                        if (bull && DisplayBuyers)
                            _buyCandles.Add(i);
                        else if (bear)
                            _trappedCandles.Add(i);
                    }
                    else if (c < o)
                    {
                        if (bear && DisplaySellers)
                            _sellCandles.Add(i);
                        else if (bull)
                            _trappedCandles.Add(i);
                    }

                    if (i % step == 0)
                    {
                        _progress = Math.Round((double)i / totalBars * 100.0, 1);
                        await Task.Delay(1, ct);
                    }
                }

                _progress = 100;
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[NewTeS] Error: {ex.Message}", LoggingLevel.Error);
            }
            finally
            {
                _isProcessing = false;
            }
        }



        // ===== VISUAL =====
        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);
            if (CurrentChart == null)
                return;

            Graphics g = args.Graphics;
            var window = CurrentChart.MainWindow;

            DateTime left = window.CoordinatesConverter.GetTime(window.ClientRectangle.Left);
            DateTime right = window.CoordinatesConverter.GetTime(window.ClientRectangle.Right);
            int leftIdx = (int)window.CoordinatesConverter.GetBarIndex(left);
            int rightIdx = (int)Math.Ceiling(window.CoordinatesConverter.GetBarIndex(right));

            for (int i = leftIdx; i <= rightIdx; i++)
            {
                if (i <= 0 || i >= HistoricalData.Count)
                    continue;

                if (HistoricalData[i, SeekOriginHistory.Begin] is not HistoryItemBar bar)
                    continue;

                bool marked =
                    (_buyCandles.Contains(i) && DisplayBuyers) ||
                    (_sellCandles.Contains(i) && DisplaySellers) ||
                    _trappedCandles.Contains(i);

                if (!marked)
                    continue;

                double centerX = window.CoordinatesConverter.GetChartX(bar.TimeLeft) + CurrentChart.BarsWidth / 2.0;
                double yHigh = window.CoordinatesConverter.GetChartY(bar.High);
                double yLow = window.CoordinatesConverter.GetChartY(bar.Low);
                double w = Math.Max(2, CurrentChart.BarsWidth * 0.05);
                double h = yLow - yHigh;

                RectangleF rect = new((float)(centerX - w / 2.0), (float)yHigh, (float)w, (float)h);

                Color color = Color.Transparent;
                if (_trappedCandles.Contains(i)) color = ColorTrapped;
                else if (_buyCandles.Contains(i)) color = ColorBuy;
                else if (_sellCandles.Contains(i)) color = ColorSell;

                using var brush = new SolidBrush(color);
                g.FillRectangle(brush, rect);
            }

            if (_isProcessing)
            {
                using var font = new Font("Arial", 11, FontStyle.Bold);
                using var brush = new SolidBrush(Color.Yellow);
                g.DrawString($"Processing tick history... {_progress:0.0}%", font, brush, new PointF(25, 75));
            }
        }
    }

    public static class ValidationHelper
    {
        /// <summary>
        /// Confronta due buffer (buyers/sellers) e determina se il lato richiesto è dominante.
        /// </summary>
        public static bool ValidateArrays(Side side, RingBuffer<double> buyers, RingBuffer<double> sellers)
        {
            RingBuffer<double> validateFor = side == Side.Buy ? buyers : sellers;
            RingBuffer<double> validateTo = side == Side.Buy ? sellers : buyers;

            if (!validateFor.IsFull)
                return false;

            if (validateTo.Count == 0)
                return true;

            double min = validateFor.GetItems().Select(Math.Abs).Min();
            double max = validateTo.GetItems().Select(Math.Abs).Max();

            return min > max;
        }
    }
}
