// Copyright QUANTOWER LLC. © 2017-2023. All rights reserved.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using System.Threading.Tasks;
using TradingPlatform.BusinessLayer;
using PowerTradesProcessing.Services;
using PowerTradesProcessing.Async;

namespace TimeAndSaleIndi
{
    public enum BaseFilter
    {
        Size,
        Time,
        Last,
        Aggressor
    }

    public class NewTeS : Indicator
    {
        // ===== Async infra (externalized) =====
        private CancellationTokenSource cts;
        private PowerTradesProcessingService _ptService;
        private bool _ptServiceAcquired;

        // ===== State =====
        private readonly List<int> _sellDivergentCandles = new List<int>();
        private readonly List<int> _buyDivergentCandles = new List<int>();
        private readonly List<int> _trappedDivergentCandles = new List<int>();

        private string _toPrint = "Created by Neo-Nix";

        private RingBuffer<double> _sellDivergentBuffer;
        private RingBuffer<double> _buyDivergentBuffer;

        public BaseFilter Filter = BaseFilter.Size;

        // ===== Settings =====
        [InputParameter("Filter Size", 0, 0.0001, double.MaxValue, 0.0001, 4)]
        public double FilterSize = 0.01;

        [InputParameter("Display Buyers", 1)]
        public bool DisplayBuyers = true;

        [InputParameter("Display Sellers", 2)]
        public bool DisplaySellers = true;

        [InputParameter("Continuous Count", 3, 1, int.MaxValue, 1, 0)]
        public int ContinuousCount = 10;

        [InputParameter("Color Buy", 4)]
        public Color ColorBuy = Color.Lime;

        [InputParameter("Color Sell", 5)]
        public Color ColorSell = Color.Orange;

        [InputParameter("Color Trapped", 6)]
        public Color ColorTrapped = Color.DimGray;

        [InputParameter("Log Colors", 7)]
        private bool logColors = false;

        [InputParameter("Print each Trade", 7)]
        private bool printEach = true;

        // ===== Internals =====
        private HistoricalData powerTradesHistoricalData;
        private bool isProcessing = true;

        public NewTeS()
            : base()
        {
            Name = "NewTeS";
            Description = "Time and sale detect on bars";
            SeparateWindow = false;

            // Disegno a barra chiusa (manteniamo la tua scelta originale)
            this.UpdateType = IndicatorUpdateType.OnTick;

            AddLineSeries("count");

            // async infrastructure moved to Services.PowerTradesProcessingService
        }

        protected override void OnInit()
        {
            this.cts = new CancellationTokenSource();
            //_ptService = PowerTradesProcessingService.Instance;
            //_ptService.Acquire();
            //_ptServiceAcquired = true;

            _sellDivergentBuffer = new RingBuffer<double>(ContinuousCount);
            _buyDivergentBuffer = new RingBuffer<double>(ContinuousCount);

            this.HistoricalData.Symbol.NewLast += this.Symbol_NewLast;

            //this.PTRequest();

            this._toPrint = $"Initialized";
        }

        protected override void OnClear()
        {
            this.HistoricalData.Symbol.NewLast -= this.Symbol_NewLast;

            _buyDivergentBuffer?.Clear();
            _sellDivergentBuffer?.Clear();

            _trappedDivergentCandles.Clear();
            _buyDivergentCandles.Clear();
            _sellDivergentCandles.Clear();

            this.cts?.Cancel();

            if (_ptServiceAcquired)
            {
                _ptService?.Release();
                _ptServiceAcquired = false;
            }

            base.Clear();
        }

        public override void Dispose()
        {
            try
            {
                this.HistoricalData.Symbol.NewLast -= this.Symbol_NewLast;
                this.cts?.Cancel();

                if (_ptServiceAcquired)
                {
                    _ptService?.Release();
                    _ptServiceAcquired = false;
                }

                base.Clear();
            }
            catch
            {
                // ignore
            }

            _buyDivergentBuffer?.Clear();
            _sellDivergentBuffer?.Clear();
            _trappedDivergentCandles.Clear();
            _buyDivergentCandles.Clear();
            _sellDivergentCandles.Clear();
        }

        // ===== Live ticks → ring buffers =====
        private void Symbol_NewLast(Symbol symbol, Last last)
        {
            if (this.printEach)
                if (last.AggressorFlag == AggressorFlag.Buy)
                    Core.Instance.Loggers.Log("size: " + last.Size, LoggingLevel.Trading);
                else if (last.AggressorFlag == AggressorFlag.Buy)
                    Core.Instance.Loggers.Log("size: " + last.Size, LoggingLevel.Error);
                else
                    if (last.TickDirection == TickDirection.Up || last.TickDirection == TickDirection.Down)
                    Core.Instance.Loggers.Log("Solved with TIck direction: " + last.Size, LoggingLevel.System);
                else
                    Core.Instance.Loggers.Log("No direction: " + last.Size, LoggingLevel.System);
            else
                RetriveAndUpdateBuffers(last, this.FilterBySize);
        }

        private void RetriveAndUpdateBuffers(Last last, Func<Last, bool> filter)
        {
            if (!filter(last))
                return;

            if (last.AggressorFlag == AggressorFlag.Buy || last.TickDirection == TickDirection.Up)
            {
                if (_buyDivergentBuffer.IsFull)
                {
                    var selected = _buyDivergentBuffer.GetItems().Select(Math.Abs).Min();
                    if (last.Size > selected)
                    {
                        int idx = Array.IndexOf(_buyDivergentBuffer.ToArray(), selected);
                        _buyDivergentBuffer[idx] = last.Size;
                    }
                }
                else
                {
                    _buyDivergentBuffer.Add(last.Size);
                }
            }
            else if (last.AggressorFlag == AggressorFlag.Sell || last.TickDirection == TickDirection.Down)
            {
                if (_sellDivergentBuffer.IsFull)
                {
                    var selected = _sellDivergentBuffer.GetItems().Select(Math.Abs).Min();
                    if (last.Size > selected)
                    {
                        int idx = Array.IndexOf(_sellDivergentBuffer.ToArray(), selected);
                        _sellDivergentBuffer[idx] = last.Size;
                    }
                }
                else
                {
                    _sellDivergentBuffer.Add(last.Size);
                }
            }
        }

        // ===== History T&S → temp ring buffers per barra =====
        private (RingBuffer<double> sellers, RingBuffer<double> buyers)
            RetriveAndUpdateHistoryBuffers(List<IHistoryItem> lasts, Func<HistoryItemLast, bool> filter)
        {
            RingBuffer<double> sellers = new RingBuffer<double>(ContinuousCount);
            RingBuffer<double> buyers = new RingBuffer<double>(ContinuousCount);

            foreach (var lasto in lasts)
            {
                if (lasto is not HistoryItemLast l)
                    continue;

                if (!filter(l))
                    continue;

                if (l.AggressorFlag == AggressorFlag.Buy)
                {
                    if (buyers.IsFull)
                    {
                        var selected = buyers.GetItems().Select(Math.Abs).Min();
                        if (l.Volume > selected)
                        {
                            int idx = Array.IndexOf(buyers.ToArray(), selected);
                            buyers[idx] = l.Volume;
                        }
                    }
                    else
                    {
                        buyers.Add(l.Volume);
                    }
                }
                else if (l.AggressorFlag == AggressorFlag.Sell)
                {
                    if (sellers.IsFull)
                    {
                        var selected = sellers.GetItems().Select(Math.Abs).Min();
                        if (l.Volume > selected)
                        {
                            int idx = Array.IndexOf(sellers.ToArray(), selected);
                            sellers[idx] = l.Volume;
                        }
                    }
                    else
                    {
                        sellers.Add(l.Volume);
                    }
                }
            }

            return (sellers, buyers);
        }

        // ===== Filtri =====
        private bool FilterBySize(Last last) => last.Size >= FilterSize;
        private bool FilterBySize(HistoryItemLast last) => last.Volume >= FilterSize;

        // ===== Validazioni buffer =====
        private bool ValidateArrays(Side side)
        {
            RingBuffer<double> validateFor = side == Side.Buy ? _buyDivergentBuffer : _sellDivergentBuffer;
            if (!validateFor.IsFull)
                return false;

            RingBuffer<double> validateTo = side == Side.Sell ? _buyDivergentBuffer : _sellDivergentBuffer;
            if (validateTo.Count == 0)
                return true;

            var min = validateFor.GetItems().Select(Math.Abs).Min();
            var max = validateTo.GetItems().Select(Math.Abs).Max();
            return min > max;
        }

        private bool ValidateArrays(Side side, RingBuffer<double> buyers, RingBuffer<double> sellers)
        {
            RingBuffer<double> validateFor = side == Side.Buy ? buyers : sellers;

            if (!validateFor.IsFull)
                return false;

            RingBuffer<double> validateTo = side == Side.Sell ? buyers : sellers;
            if (validateTo.Count == 0)
                return true;

            var min = validateFor.GetItems().Select(Math.Abs).Min();
            var max = validateTo.GetItems().Select(Math.Abs).Max();
            return min > max;
        }

        // ===== Update ciclo barra =====
        protected override void OnUpdate(UpdateArgs args)
        {
            if (args.Reason == UpdateReason.NewBar)
            {
                this.ValidateBars();

                Core.Instance.Loggers.Log("ck");

                foreach (var item in this._buyDivergentCandles)
                {
                    Core.Instance.Loggers.Log("buy" + item);
                }

                foreach (var item in this._sellDivergentCandles)
                {
                    Core.Instance.Loggers.Log("sell" + item);
                }
            }

            this.SetValue(this.Count);


        }

        private void ValidateBars()
        {
            if (this.HistoricalData?.Count < 2)
                return;

            // 1) Prendi SEMPRE la "last closed" con indicizzazione da End
            //TODO : cambiare in modo da poter lavorare anche in real-time (non solo a barra chiusa)

            // 2) Mappa alla posizione "da Begin" in modo sicuro (no off-by-one)
            int idx = this.HistoricalData.Count - 2;
            //Core.Instance.Loggers.Log("ondex:" + idx);
            if (idx < 0)  // sicurezza extra
                return;

            double o = this.HistoricalData[idx, SeekOriginHistory.Begin][PriceType.Open];
            double c = this.HistoricalData[idx, SeekOriginHistory.Begin][PriceType.Close];

            bool bull = ValidateArrays(Side.Buy);
            bool bear = ValidateArrays(Side.Sell);

            if (c > o)
            {
                if (bull) _buyDivergentCandles.Add(idx);
                else if (bear) _trappedDivergentCandles.Add(idx);
            }
            else if (c < o)
            {
                if (bear) _sellDivergentCandles.Add(idx);
                else if (bull) _trappedDivergentCandles.Add(idx);
            }

            this.PrintBuffer("Buy buf:", _buyDivergentBuffer);
            this.PrintBuffer("Sell buf:", _sellDivergentBuffer);
            this.printcondition("Bull:", bull);
            this.printcondition("Bear:", bear);
            _sellDivergentBuffer.Clear();
            _buyDivergentBuffer.Clear();
        }

        private void PrintBuffer(string prefix, RingBuffer<double> buffer)
        {
            Core.Instance.Loggers.Log($"{prefix} [{string.Join(", ", buffer.ToArray())}]", LoggingLevel.Trading);
        }

        private void printcondition(string prefix, bool condition)
        {
            Core.Instance.Loggers.Log($"{prefix} {condition}", LoggingLevel.Trading);
        }


        // ===== Pre-elaborazione storica TICK1 (facoltativa) =====
        private void PTRequest()
        {
            //_ptService?.EnqueueProcess(async ct =>
            //{
            //    // keep UI hints as before
            //    this._toPrint = "Loading Tick History ... ";
            //    this.isProcessing = true;

            //    // run sync body within task to cooperate with queue
            //    await Task.Run(() => this.ProcessPowerTrades(), ct);
            //}, TaskPriority.High);

            if (this.isProcessing)
                return;

            Task.Run(() =>
            {
                // keep UI hints as before
                this._toPrint = "Loading Tick History ... ";
                this.isProcessing = true;
                // run sync body within task to cooperate with queue
                this.ProcessPowerTrades();
            }, this.cts.Token);
        }

        private async void ProcessPowerTrades()
        {
            RingBuffer<IHistoryItem> ring = new RingBuffer<IHistoryItem>(this.HistoricalData.Count);

            this._toPrint = $"Creating Dedicated RingBuffer ... ";
            int conto = this.HistoricalData.Count;


            for (int i = 0; i < this.HistoricalData.Count; i++)
            {
                ring.Add(this.HistoricalData[i, SeekOriginHistory.Begin]);
            }


            for (int i = 0; i < conto; i++)
            {
                if (this.cts?.IsCancellationRequested ?? false)
                    break;

                this._toPrint = $"Processing Bar {i}/{conto}";

                try
                {
                    HistoryItem processedItem = (HistoryItem)ring[i];
                    double o = processedItem[PriceType.Open];
                    double c = processedItem[PriceType.Close];
                    if (o == c)
                        continue;

                    Side barSide = c > o ? Side.Buy : Side.Sell;

                    powerTradesHistoricalData = this.Symbol.GetHistory(Period.TICK1, HistoryType.Last, processedItem.TimeLeft,
                        processedItem.TimeLeft.AddTicks(Math.Abs(processedItem.TicksLeft - processedItem.TicksRight)));


                    //var items = this.powerTradesHistoricalData
                    //    .Where(x => x.TicksLeft > ring[i].TicksLeft &&
                    //                x.TicksRight <= ring[i].TicksRight)
                    //    .ToList();

                    var buffers = this.RetriveAndUpdateHistoryBuffers(powerTradesHistoricalData.ToList(), this.FilterBySize);

                    bool bull = this.ValidateArrays(Side.Buy, buffers.buyers, buffers.sellers);
                    bool bear = this.ValidateArrays(Side.Sell, buffers.buyers, buffers.sellers);

                    if (barSide == Side.Buy)
                    {
                        if (bull && this.DisplayBuyers)
                            this._buyDivergentCandles.Add(i);
                        else if (bear && this.DisplaySellers)
                            this._trappedDivergentCandles.Add(i);
                    }
                    else // Sell bar
                    {
                        if (bear && this.DisplaySellers)
                            this._sellDivergentCandles.Add(i);
                        else if (bull && this.DisplayBuyers)
                            this._trappedDivergentCandles.Add(i);
                    }
                }
                catch (Exception ex)
                {
                    Core.Instance.Loggers.Log(ex.Message, LoggingLevel.Error);
                }
            }

            this.isProcessing = false;
        }

        // ===== Render =====
        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);
            if (this.CurrentChart == null)
                return;

            int j = 0;

            try
            {
                Graphics graphics = args.Graphics;
                var mainWindow = this.CurrentChart.MainWindow;

                // finestra visibile
                DateTime leftTime = mainWindow.CoordinatesConverter.GetTime(mainWindow.ClientRectangle.Left);
                DateTime rightTime = mainWindow.CoordinatesConverter.GetTime(mainWindow.ClientRectangle.Right);
                int leftIndex = (int)mainWindow.CoordinatesConverter.GetBarIndex(leftTime);
                int rightIndex = (int)Math.Ceiling(mainWindow.CoordinatesConverter.GetBarIndex(rightTime));

                for (int i = leftIndex; i <= rightIndex; i++)
                {
                    j = i;

                    if (i > 0 && i < this.HistoricalData.Count &&
                        this.HistoricalData[i, SeekOriginHistory.Begin] is HistoryItemBar bar)
                    {
                        // Disegna solo se la barra è marcata (buy/sell/trapped) o è la barra corrente
                        bool shouldDraw =
                            (this.DisplayBuyers && this._buyDivergentCandles.Contains(i)) ||
                            (this.DisplaySellers && this._sellDivergentCandles.Contains(i)) ||
                            this._trappedDivergentCandles.Contains(i) ||
                            i == this.HistoricalData.Count - 1;

                        if (!shouldDraw)
                            continue;

                        // Geometrie
                        double barCenterX = mainWindow.CoordinatesConverter.GetChartX(bar.TimeLeft) + this.CurrentChart.BarsWidth / 2.0;
                        double yHigh = mainWindow.CoordinatesConverter.GetChartY(bar.High);
                        double yLow = mainWindow.CoordinatesConverter.GetChartY(bar.Low);
                        double rectWidth = Math.Max(2, this.CurrentChart.BarsWidth * 0.05);
                        double rectHeight = yLow - yHigh; // Y cresce verso il basso

                        RectangleF rect = new RectangleF(
                            (float)(barCenterX - rectWidth / 2.0),
                            (float)yHigh,
                            (float)rectWidth,
                            (float)rectHeight
                        );

                        // Barra live (ultimo indice)
                        if (i == this.HistoricalData.Count - 1)
                        {
                            double o = this.Open();
                            double c = this.Close();
                            if (c == o)
                                continue;

                            Side internalSide = c > o ? Side.Buy : Side.Sell;
                            bool validateBuy = ValidateArrays(Side.Buy);
                            bool validateSell = ValidateArrays(Side.Sell);
                            Color activeColor = Color.White;

                            switch (internalSide)
                            {
                                case Side.Buy:
                                    if (validateBuy) activeColor = this.ColorBuy;
                                    else if (validateSell) activeColor = this.ColorTrapped;
                                    break;

                                case Side.Sell:
                                    if (validateSell) activeColor = this.ColorSell;
                                    else if (validateBuy) activeColor = this.ColorTrapped;
                                    break;
                            }

                            if (this.isProcessing)
                            {
                                using (var font = new Font("Arial", 12, FontStyle.Bold))
                                using (var brush = new SolidBrush(Color.Yellow))
                                {
                                    graphics.DrawString(this._toPrint, font, brush, new PointF(25f, 75f));
                                }
                            }

                            if (this.logColors)
                                Core.Instance.Loggers.Log(activeColor.Name, LoggingLevel.Trading);

                            using (var brush = new SolidBrush(activeColor))
                                graphics.FillRectangle(brush, rect);
                        }
                        else
                        {
                            // Storico: priorità visiva trapped → buy → sell
                            if (this._trappedDivergentCandles.Contains(i))
                                using (var brush = new SolidBrush(this.ColorTrapped))
                                    graphics.FillRectangle(brush, rect);

                            if (this.DisplayBuyers && this._buyDivergentCandles.Contains(i))
                                using (var brush = new SolidBrush(this.ColorBuy))
                                    graphics.FillRectangle(brush, rect);

                            if (this.DisplaySellers && this._sellDivergentCandles.Contains(i))
                                using (var brush = new SolidBrush(this.ColorSell))
                                    graphics.FillRectangle(brush, rect);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"Source OnPaintChart ex.message {ex.Message} at {j}", LoggingLevel.Error);
            }
        }

        // ===== Worker thread moved into Async.AsyncTaskQueue via Services.PowerTradesProcessingService =====
    }
}
