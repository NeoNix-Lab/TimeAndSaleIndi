using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Threading;
using TradingPlatform.BusinessLayer;

namespace TimeAndSaleIndi
{
    public enum BaseFilter
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
        [InputParameter("Filter Size", 0, 0.0001, double.MaxValue, 0.0001, 4)]
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
        [InputParameter("Color Log", 5)]
        private bool logColors = false;
        private HistoricalData additionalHd = null;
        private bool newBarArrived = false;

        public TimeAndSaleIndi()
            : base()
        {
            Name = "TimeAndSaleIndi";
            Description = "My indicator's annotation";
            // Defines line on demand with particular parameters.
            AddLineSeries("line1", Color.CadetBlue, 1, LineStyle.Solid);

            SeparateWindow = false;
            this.UpdateType = IndicatorUpdateType.OnBarClose;

        }

        protected override void OnInit()
        {
            //try
            //{
            //    DateTime from = this.HistoricalData.FromTime;
            //    DateTime to = this.HistoricalData.ToTime;
            //    HistoryAggregation aggregation = this.HistoricalData.Aggregation;
            //    this.additionalHd = this.Symbol.GetHistory(aggregation,from);

            //    Core.Instance.Loggers.Log($"oN iNIT from {from} to {to} aggregation {aggregation}", LoggingLevel.Trading);
            //    this.additionalHd.NewHistoryItem += (s, e) =>
            //    {
            //        if (this._check())
            //            return;

            //        if (this.HistoricalData == null || this.HistoricalData.Count == 0)
            //            return;

            //        try
            //        {

            //            this.ValidateBars();

            //            //if (args.Reason == UpdateReason.NewTick)
            //            //{
            //            //    this._OnRunColor = (this.Close() > this.Open() && ValidateArrays(Side.Buy)) ? this.ColorBuy
            //            //        : (this.Close() < this.Open() && ValidateArrays(Side.Sell)) ? this.ColorSell : Color.White;
            //            //}

            //        }
            //        catch (Exception ex)
            //        {
            //            Core.Instance.Loggers.Log("Source OnUpdate", LoggingLevel.Error);
            //            Core.Instance.Loggers.Log(ex.Message, LoggingLevel.Error);
            //        }

            //    };
            //    this.additionalHd.HistoryItemUpdated += (s, e) =>
            //    {
            //        //Core.Instance.Loggers.Log($"additionalHd HistoryItemUpdated {e.Item.TimeLeft} {e.Item.Close}", LoggingLevel.Trading);
            //    };
            //}
            //catch (Exception ex)
            //{
            //    Core.Instance.Loggers.Log("oN iNIT" + ex.Message, LoggingLevel.Error);
            //}

            try
            {
                this.newBarArrived = false;
                this.HistoricalData.Symbol.NewLast -= this.Symbol_NewLast;
            }
            catch (Exception)
            {

                throw;
            }
            _SellDivergentBuffer = new RingBuffer<double>(ContinuosCount);
            _BuyDivergentBuffer = new RingBuffer<double>(ContinuosCount);
            this._BuyDivergentCandles = new List<int>();
            this._SellDivergentCandles = new List<int>();
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
            if (this._check())
                return;

            //if (this.newBarArrived)
            //{
            //    this.newBarArrived = false;
            //    _SellDivergentBuffer.Clear();
            //    _BuyDivergentBuffer.Clear();
            //}
            RetriveAndUpdateBuffers(last, this.FilterBySize);
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            this.newBarArrived = true;
            if (this._check())
                return;

            if (this.HistoricalData == null || this.HistoricalData.Count == 0)
                return;

            try
            {
                if (!this.newBarArrived)
                {
                    this.HistoricalData.Symbol.NewLast += this.Symbol_NewLast;
                    this.newBarArrived = true;
                }
            }
            catch (Exception)
            {

                throw;
            }

            try
            {
                if (args.Reason == UpdateReason.NewBar)
                    this.ValidateBars();

                //if (args.Reason == UpdateReason.NewTick)
                //{
                //    this._OnRunColor = (this.Close() > this.Open() && ValidateArrays(Side.Buy)) ? this.ColorBuy
                //        : (this.Close() < this.Open() && ValidateArrays(Side.Sell)) ? this.ColorSell : Color.White;
                //}

            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log("Source OnUpdate", LoggingLevel.Error);
                Core.Instance.Loggers.Log(ex.Message, LoggingLevel.Error);
            }



        }

        public override void Dispose()
        {
            base.Dispose();
            this.Symbol.NewLast -= this.Symbol_NewLast;
            this._BuyDivergentCandles.Clear();
            this._SellDivergentCandles.Clear();
            this._BuyDivergentBuffer.Clear();
            this._SellDivergentBuffer.Clear();
            this.additionalHd?.Dispose();
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
            if(this.HistoricalData?.Count < 10)
                return;
            if (this.HistoricalData[1][PriceType.Close] == this.HistoricalData[1][PriceType.Open])
                return;
            if (this.HistoricalData[1][PriceType.Close] > this.HistoricalData[1][PriceType.Open])
                if (ValidateArrays(Side.Buy))
                    this._BuyDivergentCandles.Add(Array.IndexOf(this.HistoricalData.ToArray(), this.HistoricalData[1]));
            if (this.HistoricalData[1][PriceType.Close] < this.HistoricalData[1][PriceType.Open])
                if (ValidateArrays(Side.Sell))
                    this._SellDivergentCandles.Add(Array.IndexOf(this.HistoricalData.ToArray(), this.HistoricalData[1]));

            for (int i = 0; i < this._SellDivergentBuffer.ToArray().Count(); i++)
            {
                Core.Instance.Loggers.Log($"sellers {this._SellDivergentBuffer[i]}");
            }

            for (int i = 0; i < this._BuyDivergentBuffer.ToArray().Count(); i++)
            {
                Core.Instance.Loggers.Log($"buy {this._BuyDivergentBuffer[i]}");
            }

            Core.Instance.Loggers.Log($"buy condiction {ValidateArrays(Side.Buy)}");
            Core.Instance.Loggers.Log($"sell condiction {ValidateArrays(Side.Sell)}");

            _SellDivergentBuffer.Clear();
            _BuyDivergentBuffer.Clear();
        }

        private bool ValidateArrays(Side side)
        {
            RingBuffer<double> validaTeFor = side == Side.Buy ? _BuyDivergentBuffer : _SellDivergentBuffer;
            if (validaTeFor.IsFull)
            {
                RingBuffer<double> validateTo = side == Side.Sell ? _BuyDivergentBuffer : _SellDivergentBuffer;
                if (validateTo.Count == 0)
                    return true;
                var min = validaTeFor.GetItems().Select(x => Math.Abs(x)).Min();
                var max = validateTo.GetItems().Select(x => Math.Abs(x)).Max();
                if (min > max)
                    return true;
                else
                    return false;

            }
            else
                return false;
        }

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            //base.OnPaintChart(args);

            if (this.CurrentChart == null)
                return;

            //try
            //{
            //    Graphics graphics = args.Graphics;
            //    var mainWindow = this.CurrentChart.MainWindow;

            //    // Get left and right time from visible part
            //    DateTime leftTime = mainWindow.CoordinatesConverter.GetTime(mainWindow.ClientRectangle.Left);
            //    DateTime rightTime = mainWindow.CoordinatesConverter.GetTime(mainWindow.ClientRectangle.Right);

            //    // Convert left and right time to index of bar
            //    int leftIndex = (int)mainWindow.CoordinatesConverter.GetBarIndex(leftTime);
            //    int rightIndex = (int)Math.Ceiling(mainWindow.CoordinatesConverter.GetBarIndex(rightTime));

            //    for (int i = leftIndex; i <= rightIndex; i++)
            //    {

            //        if (i > 0 && i < this.HistoricalData.Count && this.HistoricalData[i, SeekOriginHistory.Begin] is HistoryItemBar bar)
            //        {
            //            if ((this.DisplayBuiers && this._BuyDivergentCandles.Contains(i)) ||
            //                (this.DisplaySellers && this._SellDivergentCandles.Contains(i)) ||
            //                i == this.HistoricalData.Count - 1)
            //            {
            //                // coordinate X del centro barra
            //                double barCenterX = mainWindow.CoordinatesConverter.GetChartX(bar.TimeLeft) + this.CurrentChart.BarsWidth / 2.0;

            //                // coordinate Y per High e Low
            //                double yHigh = mainWindow.CoordinatesConverter.GetChartY(bar.High);
            //                double yLow = mainWindow.CoordinatesConverter.GetChartY(bar.Low);

            //                // larghezza del rettangolo = 10% della larghezza barra
            //                double rectWidth = Math.Max(2, this.CurrentChart.BarsWidth * 0.05);

            //                // altezza del rettangolo = distanza tra High e Low
            //                double rectHeight = yLow - yHigh; // attenzione: Y cresce verso il basso

            //                // rettangolo centrato
            //                RectangleF rect = new RectangleF(
            //                    (float)(barCenterX - rectWidth / 2.0),
            //                    (float)yHigh,
            //                    (float)rectWidth,
            //                    (float)rectHeight
            //                );



            //                if (i == this.HistoricalData.Count - 1)
            //                {
            //                    Color activeColor = (this.Close() > this.Open() && ValidateArrays(Side.Buy)) ? this.ColorBuy
            //                            : (this.Close() < this.Open() && ValidateArrays(Side.Sell)) ? this.ColorSell : Color.White;
            //                    if (this.logColors)
            //                        Core.Instance.Loggers.Log(activeColor.Name, LoggingLevel.Trading);
            //                    using (var brush = new SolidBrush(activeColor))
            //                    {
            //                        graphics.FillRectangle(brush, rect);
            //                    }
            //                }
            //                else if (this.DisplayBuiers && this._BuyDivergentCandles.Contains(i))
            //                    using (var brush = new SolidBrush(this.ColorBuy))
            //                    {
            //                        graphics.FillRectangle(brush, rect);
            //                    }
            //                else if (this.DisplaySellers && this._SellDivergentCandles.Contains(i))
            //                    using (var brush = new SolidBrush(this.ColorSell))
            //                    {
            //                        graphics.FillRectangle(brush, rect);
            //                    }
            //            }

            //        }
            //    }
            //}
            //catch (Exception ex)
            //{

            //    Core.Instance.Loggers.Log($"Source OnPaintChart ex.message {ex.Message}", LoggingLevel.Error);
            //}

            
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
