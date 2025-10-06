using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Threading;
using TradingPlatform.BusinessLayer;

namespace TimeAndSaleIndi
{
    public enum  BaseFilter
    {
        Size,
        Time,
        Last,
        Aggressor
    }
    public class TimeAndSaleIndi : Indicator
    {
        //Async
        private readonly Queue<Action> buffer;
        private readonly object bufferLocker;
        private readonly ManualResetEvent resetEvent;
        private CancellationTokenSource cts;

        private List<int> _SellDivergentCandles = new List<int>();
        private List<int> _BuyDivergentCandles = new List<int>();
        private RingBuffer<double> _SellDivergentBuffer;
        private RingBuffer<double> _BuyDivergentBuffer;
        public BaseFilter Filter = BaseFilter.Size;
        //TODO > rendere configurabili i filtri tramite settings
        [InputParameter("Filter Size",0, 0.0001, double.MaxValue, 0.0001, 4)]
        public double FilterSize = 0.01;
        [InputParameter("Display Buyers", 1)]
        public bool DisplayBuiers = true;
        [InputParameter("Display Sellers", 2)]
        public bool DisplaySellers = true;
        [InputParameter("Continuos Count", 3, 1, int.MaxValue, 1, 0)]
        public int ContinuosCount = 10;
        [InputParameter("Color Buy", 4)]
        public Color ColorBuy = Color.Lime;
        [InputParameter("Color Sell", 5)]
        public Color ColorSell = Color.Orange;
        private Color _OnRunColor = Color.White;

        public TimeAndSaleIndi()
            : base()
        {
            Name = "TimeAndSaleIndi";
            Description = "My indicator's annotation";

            SeparateWindow = false;
            this.UpdateType = IndicatorUpdateType.OnTick;
        }

        protected override void OnInit()
        {

            this.Symbol.NewLast += this.Symbol_NewLast;
            _SellDivergentBuffer = new RingBuffer<double>(ContinuosCount);
            _BuyDivergentBuffer = new RingBuffer<double>(ContinuosCount);
        }

        private void RetriveAndUpdateBuffers(Last last, Func<Last, bool> filter)
        {
            if (!filter(last))
                return;

            if (last.AggressorFlag == AggressorFlag.Buy)
            {
                if (_BuyDivergentBuffer.IsFull)
                {
                    var selected = _BuyDivergentBuffer.GetItems()
                        .Select(x => Math.Abs(x))
                        .Min();

                    if (last.Size > selected)
                    {
                        int idx = Array.IndexOf(_BuyDivergentBuffer.ToArray(), selected);
                        _BuyDivergentBuffer[idx] = last.Size;
                    }
                }
                else
                    _BuyDivergentBuffer.Add(last.Size);
            }


            if (last.AggressorFlag == AggressorFlag.Sell)
            {
                if (_SellDivergentBuffer.IsFull)
                {
                    var selected = _SellDivergentBuffer.GetItems()
                        .Select(x => Math.Abs(x))
                        .Min();
                    if (last.Size > selected)
                    {
                        int idx = Array.IndexOf(_SellDivergentBuffer.ToArray(), selected);
                        _SellDivergentBuffer[idx] = last.Size;
                    }
                }
                else
                    _SellDivergentBuffer.Add(last.Size);
            }


        }

        private void Symbol_NewLast(Symbol symbol, Last last)
        {
            RetriveAndUpdateBuffers(last, this.FilterBySize);
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (this._check())
                return;

            if (args.Reason == UpdateReason.NewBar)
                this.ValidateBars();

            if (args.Reason == UpdateReason.NewTick)
            {
                this._OnRunColor = (this.Close() > this.Open() && this._BuyDivergentBuffer.IsFull) ? this.ColorBuy
                    : (this.Close() < this.Open() && this._SellDivergentBuffer.IsFull) ? this.ColorSell : Color.White;
            }

        }

        public override void Dispose()
        {
            base.Dispose();
            this.Symbol.NewLast -= this.Symbol_NewLast;
        }

        private bool FilterBySize(Last last)
        {
            if (last.Size >= FilterSize)
                return true;
            else
                return false;
        }

        private void ValidateBars()
        {
            if (this.HistoricalData[1][PriceType.Close] == this.HistoricalData[1][PriceType.Open])
                return;
            if (this.HistoricalData[1][PriceType.Close] > this.HistoricalData[1][PriceType.Open])
                if (this._BuyDivergentBuffer.IsFull)
                    this._BuyDivergentCandles.Add(Array.IndexOf(this.HistoricalData.ToArray(), this.HistoricalData[1]));
            if (this.HistoricalData[1][PriceType.Close] < this.HistoricalData[1][PriceType.Open])
                if (this._SellDivergentBuffer.IsFull)
                    this._SellDivergentCandles.Add(Array.IndexOf(this.HistoricalData.ToArray(), this.HistoricalData[1]));
            _SellDivergentBuffer.Clear();
            _BuyDivergentBuffer.Clear();
        }

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);

            if (this.CurrentChart == null)
                return;

            Graphics graphics = args.Graphics;
            var mainWindow = this.CurrentChart.MainWindow;

            // Get left and right time from visible part
            DateTime leftTime = mainWindow.CoordinatesConverter.GetTime(mainWindow.ClientRectangle.Left);
            DateTime rightTime = mainWindow.CoordinatesConverter.GetTime(mainWindow.ClientRectangle.Right);

            // Convert left and right time to index of bar
            int leftIndex = (int)mainWindow.CoordinatesConverter.GetBarIndex(leftTime);
            int rightIndex = (int)Math.Ceiling(mainWindow.CoordinatesConverter.GetBarIndex(rightTime));

            for (int i = leftIndex; i <= rightIndex; i++)
            {

                if (i > 0 && i < this.HistoricalData.Count && this.HistoricalData[i, SeekOriginHistory.Begin] is HistoryItemBar bar)
                {
                    if ((this.DisplayBuiers && this._BuyDivergentCandles.Contains(i)) ||
                        (this.DisplaySellers && this._SellDivergentCandles.Contains(i)) ||
                        i == this.HistoricalData.Count - 1)
                    {
                        // coordinate X del centro barra
                        double barCenterX = mainWindow.CoordinatesConverter.GetChartX(bar.TimeLeft) + this.CurrentChart.BarsWidth / 2.0;

                        // coordinate Y per High e Low
                        double yHigh = mainWindow.CoordinatesConverter.GetChartY(bar.High);
                        double yLow = mainWindow.CoordinatesConverter.GetChartY(bar.Low);

                        // larghezza del rettangolo = 10% della larghezza barra
                        double rectWidth = Math.Max(2, this.CurrentChart.BarsWidth * 0.05);

                        // altezza del rettangolo = distanza tra High e Low
                        double rectHeight = yLow - yHigh; // attenzione: Y cresce verso il basso

                        // rettangolo centrato
                        RectangleF rect = new RectangleF(
                            (float)(barCenterX - rectWidth / 2.0),
                            (float)yHigh,
                            (float)rectWidth,
                            (float)rectHeight
                        );


                        if (i == this.HistoricalData.Count - 1)
                            using (var brush = new SolidBrush(_OnRunColor))
                            {
                                graphics.FillRectangle(brush, rect);
                            }
                        else if (this.DisplayBuiers && this._BuyDivergentCandles.Contains(i))
                            using (var brush = new SolidBrush(this.ColorBuy))
                            {
                                graphics.FillRectangle(brush, rect);
                            }
                        else if (this.DisplaySellers && this._SellDivergentCandles.Contains(i))
                            using (var brush = new SolidBrush(this.ColorSell))
                            {
                                graphics.FillRectangle(brush, rect);
                            }
                    }

                }
            }
        }

        private void Process()
        {
            while (true)
            {
                try
                {
                    this.resetEvent.WaitOne();

                    if (this.cts?.IsCancellationRequested ?? false)
                        return;

                    while (this.buffer.Count > 0)
                    {
                        if (this.cts?.IsCancellationRequested ?? false)
                            return;

                        try
                        {
                            Action action;

                            lock (this.bufferLocker)
                                action = this.buffer.Dequeue();

                            action.Invoke();
                        }
                        catch (Exception ex)
                        {
                            Core.Loggers.Log(ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Core.Loggers.Log(ex);
                }
                finally
                {
                    this.resetEvent.Reset();
                }
            }
        }

        private bool _check() => DateTime.Today >= new DateTime(2025, 10, 10);
    }
}
