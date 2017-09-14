﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Channels;
using Microsoft.AspNetCore.SignalR;

namespace SignalR.StockTicker
{
    public class StockTicker
    {
        private readonly SemaphoreSlim _marketStateLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _updateStockPricesLock = new SemaphoreSlim(1, 1);

        private readonly ConcurrentDictionary<string, Stock> _stocks = new ConcurrentDictionary<string, Stock>();

        // Stock can go up or down by a percentage of this factor on each change
        private readonly double _rangePercent = 0.002;

        private readonly TimeSpan _updateInterval = TimeSpan.FromMilliseconds(250);
        private readonly Random _updateOrNotRandom = new Random();

        private Timer _timer;
        private volatile bool _updatingStockPrices;
        private volatile MarketState _marketState;

        public StockTicker(IHubContext<StockTickerHub> clients)
        {
            Clients = clients;
            LoadDefaultStocks();
        }

        private IHubContext<StockTickerHub> Clients
        {
            get;
            set;
        }

        public MarketState MarketState
        {
            get { return _marketState; }
            private set { _marketState = value; }
        }

        public IEnumerable<Stock> GetAllStocks()
        {
            return _stocks.Values;
        }

        public ReadableChannel<IEnumerable<Stock>> StreamStocks()
        {
            var channel = Channel.CreateUnbounded<IEnumerable<Stock>>();

            Task.Run(async () =>
            {
                while (MarketState == MarketState.Open)
                {
                    await channel.Out.WriteAsync(_stocks.Values);
                    await Task.Delay(_updateInterval);
                }
            });
            return channel.In;
        }

        public async Task OpenMarket()
        {
            await _marketStateLock.WaitAsync();
            try
            {
                if (MarketState != MarketState.Open)
                {
                    _timer = new Timer(UpdateStockPrices, null, _updateInterval, _updateInterval);

                    MarketState = MarketState.Open;

                    await BroadcastMarketStateChange(MarketState.Open);
                }
            }
            finally
            {
                _marketStateLock.Release();
            }
        }

        public async Task CloseMarket()
        {
            await _marketStateLock.WaitAsync();
            try
            {
                if (MarketState == MarketState.Open)
                {
                    if (_timer != null)
                    {
                        _timer.Dispose();
                    }

                    MarketState = MarketState.Closed;

                    await BroadcastMarketStateChange(MarketState.Closed);
                }
            }
            finally
            {
                _marketStateLock.Release();
            }
        }

        public async Task Reset()
        {
            await _marketStateLock.WaitAsync();
            try
            {
                if (MarketState != MarketState.Closed)
                {
                    throw new InvalidOperationException("Market must be closed before it can be reset.");
                }

                LoadDefaultStocks();
                await BroadcastMarketReset();
            }
            finally
            {
                _marketStateLock.Release();
            }
        }

        private void LoadDefaultStocks()
        {
            _stocks.Clear();

            var stocks = new List<Stock>
            {
                new Stock { Symbol = "MSFT", Price = 75.12m },
                new Stock { Symbol = "AAPL", Price = 158.44m },
                new Stock { Symbol = "GOOG", Price = 924.54m }
            };

            stocks.ForEach(stock => _stocks.TryAdd(stock.Symbol, stock));
        }

        private async void UpdateStockPrices(object state)
        {
            // This function must be re-entrant as it's running as a timer interval handler
            await _updateStockPricesLock.WaitAsync();
            try
            {
                if (!_updatingStockPrices)
                {
                    _updatingStockPrices = true;

                    foreach (var stock in _stocks.Values)
                    {
                        TryUpdateStockPrice(stock);
                    }

                    _updatingStockPrices = false;
                }
            }
            finally
            {
                _updateStockPricesLock.Release();
            }
        }

        private bool TryUpdateStockPrice(Stock stock)
        {
            // Randomly choose whether to udpate this stock or not
            var r = _updateOrNotRandom.NextDouble();
            if (r > 0.1)
            {
                return false;
            }

            // Update the stock price by a random factor of the range percent
            var random = new Random((int)Math.Floor(stock.Price));
            var percentChange = random.NextDouble() * _rangePercent;
            var pos = random.NextDouble() > 0.51;
            var change = Math.Round(stock.Price * (decimal)percentChange, 2);
            change = pos ? change : -change;

            stock.Price += change;
            return true;
        }

        private async Task BroadcastMarketStateChange(MarketState marketState)
        {
            switch (marketState)
            {
                case MarketState.Open:
                    await Clients.Clients.All.InvokeAsync("marketOpened");
                    break;
                case MarketState.Closed:
                    await Clients.Clients.All.InvokeAsync("marketClosed");
                    break;
                default:
                    break;
            }
        }

        private async Task BroadcastMarketReset()
        {
            await Clients.Clients.All.InvokeAsync("marketReset");
        }

        private async Task BroadcastStockPrice(Stock stock)
        {
            await Clients.Clients.All.InvokeAsync("updateStockPrice", stock);
        }
    }

    public enum MarketState
    {
        Closed,
        Open
    }
}