// ZmqMarketDataBridge.cs
//
// Headless Quantower Strategy that republishes over a ZeroMQ PUB socket:
//   1. L1 quotes + last-trade prints for up to 3 symbols (one topic per symbol)
//   2. YOUR account activity, for ALL accounts/symbols on every connected
//      connection: orders, positions, and executed trades (fills)
//
// Python (or anything else) SUBs to tcp://<this-machine>:5556 and filters by
// topic.
//
// ------------------------------------------------------------------------
// SETUP (unchanged)
// ------------------------------------------------------------------------
// 1. Class Library referencing TradingPlatform.BusinessLayer.dll
// 2. Install-Package NetMQ
// 3. Copy DLL (+ NetMQ.dll + AsyncIO.dll) to
//        %AppData%\Quantower\Settings\Scripts\Strategies\
// 4. Strategy Runner -> "Zmq Market Data Bridge" -> set inputs -> Run.
//
// ------------------------------------------------------------------------
// TOPICS
// ------------------------------------------------------------------------
//   <SYMBOL NAME>   market data (quotes + last prints), e.g. "ES"
//   orders          order lifecycle events + periodic snapshot
//   positions       position lifecycle events + periodic snapshot
//   trades          executions (fills)
//   screenshots     Trade Journal screenshots, converted BMP -> PNG
//
// Each message is 2 ZMQ frames: [topic][json].
// EXCEPTION: "screenshots" is 3 frames: [topic][json metadata][raw PNG bytes].
//   metadata = {"type":"screenshot","kind":"entry|exit","positionId":..,"account":..,
//               "accountName":..,"symbol":..,"root":..,"file":..,"fileTime":..,
//               "flat":true|false,"width":..,"height":..,"bytes":..,"ts":..}
//   "flat" (exit shots only) = the position was fully closed after that fill,
//   i.e. this is the FINAL exit. positionId/symbol are null if the fill could not
//   be matched (root + kind are always present, taken from the file name).
//
// ------------------------------------------------------------------------
// WIRE FORMAT - market data (unchanged)
// ------------------------------------------------------------------------
//   {"type":"quote","symbol":"ES","bid":..,"ask":..,"bidSize":..,"askSize":..,"ts":..}
//   {"type":"trade","symbol":"ES","price":..,"size":..,"ts":..}
//
//   NOTE: market-data prints use type "trade". Your own fills use
//   type "execution" (topic "trades") so the two never get confused.
//
// ------------------------------------------------------------------------
// WIRE FORMAT - account activity
// ------------------------------------------------------------------------
// topic "orders":
//   {"type":"order","event":"added|updated|removed","ts":..,"order":{...}}
//   {"type":"orders_snapshot","ts":..,"orders":[{...},{...}]}
//   order = id, account, accountName, symbol, side, orderType, orderTypeId,
//           status, originalStatus, tif, qty, filled, remaining, price,
//           triggerPrice, trailOffset, avgFillPrice, positionId, groupId,
//           remarks, lastUpdate
//
// topic "positions":
//   {"type":"position","event":"opened|updated|pnl|closed","ts":..,"position":{...}}
//   {"type":"positions_snapshot","ts":..,"positions":[{...}]}   (empty list = flat)
//   position = id, account, accountName, symbol, side, qty, signedQty,
//              openPrice, currentPrice, openTime, netPnl, grossPnl, fee,
//              pnlTicks, slOrderId, slPrice, tpOrderId, tpPrice
//   "updated" = qty / open price / SL / TP changed
//   "pnl"     = PnL-only change, throttled (see "PnL Throttle" input)
//
// topic "trades":
//   {"type":"execution","ts":..,"trade":{...}}
//   trade = id, orderId, positionId, account, accountName, symbol, side,
//           price, qty, fee, grossPnl, netPnl, impact, orderTypeId, time
//
// All timestamps are Unix epoch milliseconds (UTC). Numbers that are unset
// (NaN/Infinity) are sent as JSON null.
//
// Because PUB/SUB is fire-and-forget (a late-joining or briefly-disconnected
// subscriber misses events), the strategy re-publishes a full snapshot of
// open orders and open positions every N seconds. Python should treat the
// snapshots as the source of truth and use the events for low-latency updates.
// ------------------------------------------------------------------------

using NetMQ;
using NetMQ.Sockets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Threading;
using TradingPlatform.BusinessLayer;

namespace ZmqMarketDataBridge
{
    public class ZmqMarketDataBridge : Strategy
    {
        // ---- Input parameters, set these from the Strategy Runner UI ----

        [InputParameter("Symbol 1", 10)]
        public Symbol Symbol1;

        [InputParameter("Symbol 2", 20)]
        public Symbol Symbol2;

        [InputParameter("Symbol 3", 30)]
        public Symbol Symbol3;

        [InputParameter("ZMQ Publish Port", 40)]
        public int PublishPort = 5556;

        [InputParameter("Publish Orders/Positions/Trades", 50)]
        public bool PublishTradingFeed = true;

        [InputParameter("Snapshot Interval (sec, 0 = off)", 60)]
        public int SnapshotIntervalSec = 5;

        [InputParameter("PnL Throttle (ms, 0 = no PnL-only updates)", 70)]
        public int PnlThrottleMs = 500;

        [InputParameter("Include Trades Older Than Start", 80)]
        public bool IncludeOldTrades = false;

        [InputParameter("Publish Trade Journal Screenshots", 90)]
        public bool PublishScreenshots = true;

        // -------------------------------------------------------------

        private const string TopicOrders = "orders";
        private const string TopicPositions = "positions";
        private const string TopicTrades = "trades";

        private PublisherSocket _pubSocket;
        private Symbol[] _symbols;
        private readonly object _sendLock = new object();

        private bool _feedActive;
        private long _startMs;
        private Timer _snapshotTimer;

        // Last-published state per position, used to decide whether an
        // Updated event is a real change or just a PnL tick.
        private sealed class PosState
        {
            public double Qty, OpenPrice, Sl, Tp;
            public long LastMs;
        }
        private readonly ConcurrentDictionary<string, PosState> _posState =
            new ConcurrentDictionary<string, PosState>();

        // ---- Screenshots (Trade Journal writes BMPs; we convert + publish them) ----
        private const string TopicScreenshots = "screenshots";

        // Same root the Trade Journal plugin uses: <Documents>\Quantower\Trade Journal\Screenshots\<day>\*.bmp
        private static readonly string ScreenshotsRoot = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Quantower", "Trade Journal", "Screenshots");

        private System.IO.FileSystemWatcher _ssWatcher;
        private readonly ConcurrentDictionary<string, byte> _ssSeen = new ConcurrentDictionary<string, byte>();

        // Recent fills, used to find the position id for a screenshot.
        private sealed class TradeRef
        {
            public string PositionId, AccountId, AccountName, Symbol, Root;
            public DateTime Local;
        }
        private readonly ConcurrentQueue<TradeRef> _recentTrades = new ConcurrentQueue<TradeRef>();

        public ZmqMarketDataBridge()
        {
            this.Name = "Zmq Market Data Bridge";
            this.Description = "Publishes L1 quotes + trades for up to 3 symbols, plus live orders/positions/executions for all accounts, over ZMQ PUB.";
        }

        protected override void OnCreated()
        {
        }

        protected override void OnRun()
        {
            _symbols = new[] { Symbol1, Symbol2, Symbol3 };

            bool anySymbol = false;
            foreach (var sym in _symbols)
            {
                if (sym != null)
                    anySymbol = true;
            }

            // Symbols are optional now: the trading feed works without them.
            if (!anySymbol && !PublishTradingFeed)
            {
                this.Log("ZmqMarketDataBridge: no symbols configured and trading feed disabled, nothing to do.", StrategyLoggingLevel.Error);
                return;
            }

            try
            {
                _pubSocket = new PublisherSocket();
                _pubSocket.Options.SendHighWatermark = 1000;
                _pubSocket.Options.Linger = TimeSpan.FromMilliseconds(200); // don't hang on Dispose
                _pubSocket.Bind($"tcp://*:{PublishPort}");
                this.Log($"ZmqMarketDataBridge: ZMQ PUB bound on tcp://*:{PublishPort}", StrategyLoggingLevel.Info);
            }
            catch (Exception ex)
            {
                this.Log($"ZmqMarketDataBridge: failed to bind ZMQ socket - {ex.Message}", StrategyLoggingLevel.Error);
                return;
            }

            _startMs = NowMs();

            foreach (var sym in _symbols)
            {
                if (sym == null)
                    continue;

                sym.NewQuote += OnNewQuote;
                sym.NewLast += OnNewLast;
                this.Log($"ZmqMarketDataBridge: subscribed to {sym.Name}", StrategyLoggingLevel.Info);
            }

            if (PublishTradingFeed)
                StartTradingFeed();

            if (PublishScreenshots)
                StartScreenshotWatcher();
        }

        protected override void OnStop()
        {
            StopScreenshotWatcher();
            StopTradingFeed();

            if (_symbols != null)
            {
                foreach (var sym in _symbols)
                {
                    if (sym == null)
                        continue;

                    sym.NewQuote -= OnNewQuote;
                    sym.NewLast -= OnNewLast;
                }
            }

            CloseSocket();

            this.Log("ZmqMarketDataBridge: stopped, ZMQ socket closed.", StrategyLoggingLevel.Info);
        }

        protected override void OnRemove()
        {
            StopScreenshotWatcher();
            StopTradingFeed();
            CloseSocket();
        }

        private void CloseSocket()
        {
            lock (_sendLock)
            {
                _pubSocket?.Dispose();
                _pubSocket = null;
            }
        }

        // -------------------------------------------------------------
        // Trading feed: start / stop
        // -------------------------------------------------------------

        private void StartTradingFeed()
        {
            if (_feedActive)
                return;

            var core = Core.Instance;

            core.OrderAdded += OnOrderAdded;
            core.OrderRemoved += OnOrderRemoved;
            core.PositionAdded += OnPositionAdded;
            core.PositionRemoved += OnPositionRemoved;
            core.TradeAdded += OnTradeAdded;

            // Hook anything that already exists (opened before Run was pressed),
            // since Core.*Added only fires for NEW objects.
            foreach (var o in core.Orders)
                o.Updated += OnOrderUpdated;

            foreach (var p in core.Positions)
            {
                _posState[Key(p)] = CaptureState(p);
                p.Updated += OnPositionUpdated;
            }

            _feedActive = true;

            // Initial state so Python doesn't have to wait for the first timer tick.
            PublishOrdersSnapshot();
            PublishPositionsSnapshot();

            if (SnapshotIntervalSec > 0)
            {
                int ms = SnapshotIntervalSec * 1000;
                _snapshotTimer = new Timer(_ =>
                {
                    Guard("snapshot", () =>
                    {
                        PublishOrdersSnapshot();
                        PublishPositionsSnapshot();
                    });
                }, null, ms, ms);
            }

            this.Log("ZmqMarketDataBridge: trading feed active (orders/positions/trades).", StrategyLoggingLevel.Info);
        }

        private void StopTradingFeed()
        {
            if (!_feedActive)
                return;

            _feedActive = false;

            _snapshotTimer?.Dispose();
            _snapshotTimer = null;

            var core = Core.Instance;

            core.OrderAdded -= OnOrderAdded;
            core.OrderRemoved -= OnOrderRemoved;
            core.PositionAdded -= OnPositionAdded;
            core.PositionRemoved -= OnPositionRemoved;
            core.TradeAdded -= OnTradeAdded;

            foreach (var o in core.Orders)
                o.Updated -= OnOrderUpdated;

            foreach (var p in core.Positions)
                p.Updated -= OnPositionUpdated;

            _posState.Clear();
        }

        // -------------------------------------------------------------
        // Orders
        // -------------------------------------------------------------

        private void OnOrderAdded(Order order)
        {
            Guard("order added", () =>
            {
                order.Updated -= OnOrderUpdated; // avoid double-hook
                order.Updated += OnOrderUpdated;
                PublishOrder("added", order);
            });
        }

        private void OnOrderUpdated(IOrder iOrder)
        {
            Guard("order updated", () =>
            {
                var order = iOrder as Order;
                if (order != null)
                    PublishOrder("updated", order);
            });
        }

        private void OnOrderRemoved(Order order)
        {
            Guard("order removed", () =>
            {
                order.Updated -= OnOrderUpdated;
                // Final status (Filled / Cancelled / Refused ...) is in order.status
                PublishOrder("removed", order);
            });
        }

        private void PublishOrder(string evt, Order o)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"type\":\"order\",\"event\":").Append(Q(evt))
              .Append(",\"ts\":").Append(NowMs())
              .Append(",\"order\":");
            WriteOrder(sb, o);
            sb.Append('}');
            Publish(TopicOrders, sb.ToString());
        }

        private void PublishOrdersSnapshot()
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"type\":\"orders_snapshot\",\"ts\":").Append(NowMs()).Append(",\"orders\":[");
            bool first = true;
            foreach (var o in Core.Instance.Orders)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteOrder(sb, o);
            }
            sb.Append("]}");
            Publish(TopicOrders, sb.ToString());
        }

        private static void WriteOrder(StringBuilder sb, Order o)
        {
            sb.Append('{');
            AddStr(sb, "id", o.Id);
            AddStr(sb, "account", o.Account?.Id);
            AddStr(sb, "accountName", o.Account?.Name);
            AddStr(sb, "symbol", o.Symbol?.Name);
            AddStr(sb, "side", o.Side.ToString());
            AddStr(sb, "orderType", o.OrderType?.Name);
            AddStr(sb, "orderTypeId", o.OrderTypeId);
            AddStr(sb, "status", o.Status.ToString());
            AddStr(sb, "originalStatus", o.OriginalStatus);
            AddStr(sb, "tif", o.TimeInForce.ToString());
            AddNum(sb, "qty", o.TotalQuantity);
            AddNum(sb, "filled", o.FilledQuantity);
            AddNum(sb, "remaining", o.RemainingQuantity);
            AddNum(sb, "price", o.Price);
            AddNum(sb, "triggerPrice", o.TriggerPrice);
            AddNum(sb, "trailOffset", o.TrailOffset);
            AddNum(sb, "avgFillPrice", o.AverageFillPrice);
            AddStr(sb, "positionId", o.PositionId);
            AddStr(sb, "groupId", o.GroupId);
            AddStr(sb, "remarks", o.Comment);
            AddLong(sb, "lastUpdate", ToMs(o.LastUpdateTime));
            sb.Append('}');
        }

        // -------------------------------------------------------------
        // Positions
        // -------------------------------------------------------------

        private void OnPositionAdded(Position pos)
        {
            Guard("position added", () =>
            {
                _posState[Key(pos)] = CaptureState(pos);
                pos.Updated -= OnPositionUpdated;
                pos.Updated += OnPositionUpdated;
                PublishPosition("opened", pos);
            });
        }

        private void OnPositionUpdated(Position pos)
        {
            Guard("position updated", () =>
            {
                string key = Key(pos);
                long now = NowMs();
                double sl = SlPrice(pos);
                double tp = TpPrice(pos);

                var st = _posState.GetOrAdd(key, _ => new PosState { LastMs = 0 });

                bool structural, pnlDue;
                lock (st)
                {
                    structural = !st.Qty.Equals(pos.Quantity)
                              || !st.OpenPrice.Equals(pos.OpenPrice)
                              || !st.Sl.Equals(sl)
                              || !st.Tp.Equals(tp);

                    pnlDue = PnlThrottleMs > 0 && (now - st.LastMs) >= PnlThrottleMs;

                    if (!structural && !pnlDue)
                        return;

                    st.Qty = pos.Quantity;
                    st.OpenPrice = pos.OpenPrice;
                    st.Sl = sl;
                    st.Tp = tp;
                    st.LastMs = now;
                }

                PublishPosition(structural ? "updated" : "pnl", pos);
            });
        }

        private void OnPositionRemoved(Position pos)
        {
            Guard("position removed", () =>
            {
                pos.Updated -= OnPositionUpdated;
                PublishPosition("closed", pos);
                PosState ignored;
                _posState.TryRemove(Key(pos), out ignored);
            });
        }

        private void PublishPosition(string evt, Position p)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"type\":\"position\",\"event\":").Append(Q(evt))
              .Append(",\"ts\":").Append(NowMs())
              .Append(",\"position\":");
            WritePosition(sb, p);
            sb.Append('}');
            Publish(TopicPositions, sb.ToString());
        }

        private void PublishPositionsSnapshot()
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"type\":\"positions_snapshot\",\"ts\":").Append(NowMs()).Append(",\"positions\":[");
            bool first = true;
            foreach (var p in Core.Instance.Positions)
            {
                if (!first) sb.Append(',');
                first = false;
                WritePosition(sb, p);
            }
            sb.Append("]}");
            Publish(TopicPositions, sb.ToString());
        }

        private static void WritePosition(StringBuilder sb, Position p)
        {
            double qty = p.Quantity;
            double signed = p.Side == Side.Sell ? -qty : qty;

            sb.Append('{');
            AddStr(sb, "id", p.Id);
            AddStr(sb, "account", p.Account?.Id);
            AddStr(sb, "accountName", p.Account?.Name);
            AddStr(sb, "symbol", p.Symbol?.Name);
            AddStr(sb, "side", p.Side.ToString());
            AddNum(sb, "qty", qty);
            AddNum(sb, "signedQty", signed);
            AddNum(sb, "openPrice", p.OpenPrice);
            AddNum(sb, "currentPrice", p.CurrentPrice);
            AddLong(sb, "openTime", ToMs(p.OpenTime));
            AddNum(sb, "netPnl", PnlVal(p.NetPnL));
            AddNum(sb, "grossPnl", PnlVal(p.GrossPnL));
            AddNum(sb, "fee", PnlVal(p.Fee));
            AddNum(sb, "pnlTicks", p.GrossPnLTicks);
            AddStr(sb, "slOrderId", FindOrder(p, true)?.Id);
            AddNum(sb, "slPrice", SlPrice(p));
            AddStr(sb, "tpOrderId", FindOrder(p, false)?.Id);
            AddNum(sb, "tpPrice", TpPrice(p));
            sb.Append('}');
        }

        private static PosState CaptureState(Position p)
        {
            return new PosState
            {
                Qty = p.Quantity,
                OpenPrice = p.OpenPrice,
                Sl = SlPrice(p),
                Tp = TpPrice(p),
                LastMs = NowMs()
            };
        }

        // Finds the position's stop / limit by scanning the working orders
        // (account + symbol + order type, opposite side), the same way the
        // trading bots do, instead of using Position.StopLoss/TakeProfit.
        private static Order FindOrder(Position p, bool stop)
        {
            foreach (var o in Core.Instance.Orders)
            {
                if (o.Account != p.Account || o.Symbol != p.Symbol)
                    continue;
                if (o.Side == p.Side)
                    continue;

                bool match = stop
                    ? (o.OrderTypeId == OrderType.Stop
                       || o.OrderTypeId == OrderType.StopLimit
                       || o.OrderTypeId == OrderType.TrailingStop)
                    : o.OrderTypeId == OrderType.Limit;

                if (match)
                    return o;
            }
            return null;
        }

        private static double SlPrice(Position p)
        {
            var o = FindOrder(p, true);
            return o == null ? double.NaN : o.TriggerPrice;
        }

        private static double TpPrice(Position p)
        {
            var o = FindOrder(p, false);
            return o == null ? double.NaN : o.Price;
        }

        private static double PnlVal(PnLItem item)
        {
            return item == null ? double.NaN : item.Value;
        }

        // IDs are only unique per connection, so key on both.
        private static string Key(Position p)
        {
            return p.ConnectionId + ":" + p.Id;
        }

        // -------------------------------------------------------------
        // Trades (executions / fills)
        // -------------------------------------------------------------

        private void OnTradeAdded(Trade trade)
        {
            Guard("trade added", () =>
            {
                long tradeMs = ToMs(trade.DateTime);

                // Skip history that the platform may load on connect.
                // 5s tolerance for clock skew vs. the broker's timestamps.
                if (!IncludeOldTrades && tradeMs > 0 && tradeMs < _startMs - 5000)
                    return;

                RememberTrade(trade);

                var sb = new StringBuilder(384);
                sb.Append("{\"type\":\"execution\",\"ts\":").Append(NowMs())
                  .Append(",\"trade\":{");
                AddStr(sb, "id", trade.Id);
                AddStr(sb, "orderId", trade.OrderId);
                AddStr(sb, "positionId", trade.PositionId);
                AddStr(sb, "account", trade.Account?.Id);
                AddStr(sb, "accountName", trade.Account?.Name);
                AddStr(sb, "symbol", trade.Symbol?.Name);
                AddStr(sb, "side", trade.Side.ToString());
                AddNum(sb, "price", trade.Price);
                AddNum(sb, "qty", trade.Quantity);
                AddNum(sb, "fee", PnlVal(trade.Fee));
                AddNum(sb, "grossPnl", PnlVal(trade.GrossPnl));
                AddNum(sb, "netPnl", PnlVal(trade.NetPnl));
                AddStr(sb, "impact", trade.PositionImpactType.ToString());
                AddStr(sb, "orderTypeId", trade.OrderTypeId);
                AddLong(sb, "time", tradeMs);
                sb.Append("}}");

                Publish(TopicTrades, sb.ToString());
            });
        }

        // -------------------------------------------------------------
        // Screenshots: Trade Journal BMP -> PNG -> ZMQ "screenshots" topic
        // -------------------------------------------------------------
        // Trade Journal saves a ~6 MB 24-bit BMP per fill, named
        //   yyyyMMdd_HHmmss_<ROOT>_<entry|exit>.bmp
        // inside a per-trading-day folder. We watch the whole tree, wait for the
        // file to be fully written, convert it to PNG (so ~6 MB becomes a fraction
        // of that before it crosses the wire), find the matching fill to get the
        // position id, and publish 3 frames: [topic][json][png bytes].

        private void StartScreenshotWatcher()
        {
            try
            {
                System.IO.Directory.CreateDirectory(ScreenshotsRoot);

                var w = new System.IO.FileSystemWatcher(ScreenshotsRoot, "*.bmp");
                w.IncludeSubdirectories = true; // day folders are created on demand
                w.Created += OnScreenshotCreated;
                w.EnableRaisingEvents = true;
                _ssWatcher = w;

                this.Log($"ZmqMarketDataBridge: watching {ScreenshotsRoot} for screenshots.", StrategyLoggingLevel.Info);
            }
            catch (Exception ex)
            {
                this.Log($"ZmqMarketDataBridge: screenshot watcher failed to start - {ex.Message}", StrategyLoggingLevel.Error);
            }
        }

        private void StopScreenshotWatcher()
        {
            var w = _ssWatcher;
            _ssWatcher = null;
            if (w == null)
                return;

            try
            {
                w.EnableRaisingEvents = false;
                w.Created -= OnScreenshotCreated;
                w.Dispose();
            }
            catch { }

            _ssSeen.Clear();
        }

        private void OnScreenshotCreated(object sender, System.IO.FileSystemEventArgs e)
        {
            if (_ssSeen.Count > 2000)
                _ssSeen.Clear();

            if (!_ssSeen.TryAdd(e.FullPath, 0))
                return;

            string path = e.FullPath;
            System.Threading.Tasks.Task.Run(() => Guard("screenshot", () => ProcessScreenshot(path)));
        }

        private void RememberTrade(Trade trade)
        {
            if (!PublishScreenshots)
                return;

            _recentTrades.Enqueue(new TradeRef
            {
                PositionId = trade.PositionId,
                AccountId = trade.Account?.Id,
                AccountName = trade.Account?.Name,
                Symbol = trade.Symbol?.Name,
                Root = SymbolRoot(trade.Symbol?.Name),
                Local = trade.DateTime.ToLocalTime() // same value Trade Journal puts in the file name
            });

            TradeRef dropped;
            while (_recentTrades.Count > 200 && _recentTrades.TryDequeue(out dropped)) { }
        }

        private TradeRef FindTradeFor(string root, DateTime fileLocal)
        {
            TradeRef best = null;
            double bestDiff = 2.0; // seconds; file name is truncated to the second

            foreach (var t in _recentTrades)
            {
                if (!string.Equals(t.Root, root, StringComparison.OrdinalIgnoreCase))
                    continue;

                double diff = Math.Abs((t.Local - fileLocal).TotalSeconds);
                if (diff <= bestDiff)
                {
                    best = t;
                    bestDiff = diff;
                }
            }
            return best;
        }

        // True when no open position remains for this account + symbol root,
        // i.e. the fill that produced the screenshot closed the position out.
        private static bool IsFlat(string positionId, string accountId, string root)
        {
            foreach (var p in Core.Instance.Positions)
            {
                if (p.Quantity <= 0)
                    continue;

                if (!string.IsNullOrEmpty(positionId) && p.Id == positionId)
                    return false;

                bool sameAccount = accountId == null || p.Account?.Id == accountId;
                if (sameAccount && string.Equals(SymbolRoot(p.Symbol?.Name), root, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        // Same rule as TradeJournalPlugin.GetSymbolRoot: "MNQU26" -> "MNQ".
        private static readonly char[] ContractMonthCodes =
            { 'F', 'G', 'H', 'J', 'K', 'M', 'N', 'Q', 'U', 'V', 'X', 'Z' };

        private static string SymbolRoot(string symbol)
        {
            if (string.IsNullOrEmpty(symbol))
                return symbol ?? string.Empty;

            if (symbol.Length >= 3)
            {
                char monthChar = symbol[symbol.Length - 3];
                bool yearOk = char.IsDigit(symbol[symbol.Length - 2]) && char.IsDigit(symbol[symbol.Length - 1]);
                if (yearOk && Array.IndexOf(ContractMonthCodes, monthChar) >= 0)
                    return symbol.Substring(0, symbol.Length - 3);
            }
            return symbol;
        }

        private void ProcessScreenshot(string path)
        {
            // 1) Wait for Trade Journal to finish writing. The BMP header stores the
            //    total file size, so "length on disk == length in header" means done.
            byte[] bmp = null;
            for (int i = 0; i < 50 && bmp == null; i++)
            {
                try
                {
                    using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open,
                        System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                    {
                        var buf = new byte[fs.Length];
                        int off = 0;
                        while (off < buf.Length)
                        {
                            int n = fs.Read(buf, off, buf.Length - off);
                            if (n <= 0)
                                break;
                            off += n;
                        }

                        if (off == buf.Length && buf.Length >= 54 && buf[0] == 0x42 && buf[1] == 0x4D
                            && BitConverter.ToUInt32(buf, 2) == (uint)buf.Length)
                            bmp = buf;
                    }
                }
                catch (System.IO.IOException) { }

                if (bmp == null)
                    Thread.Sleep(100);
            }

            if (bmp == null)
            {
                this.Log($"ZmqMarketDataBridge: screenshot never finished writing: {path}", StrategyLoggingLevel.Error);
                return;
            }

            // 2) Convert BMP -> PNG before sending.
            int width, height;
            byte[] png = BmpToPng(bmp, out width, out height);
            if (png == null)
            {
                this.Log($"ZmqMarketDataBridge: unsupported BMP format (expected 24-bit): {path}", StrategyLoggingLevel.Error);
                return;
            }

            // 3) Parse yyyyMMdd_HHmmss_<ROOT>_<entry|exit>.bmp
            string stem = System.IO.Path.GetFileNameWithoutExtension(path);
            string[] parts = stem.Split('_');
            if (parts.Length < 4)
            {
                this.Log($"ZmqMarketDataBridge: unexpected screenshot file name: {stem}", StrategyLoggingLevel.Error);
                return;
            }

            string kind = parts[parts.Length - 1].ToLowerInvariant();
            string root = string.Join("_", parts, 2, parts.Length - 3);
            if (kind != "entry" && kind != "exit")
            {
                this.Log($"ZmqMarketDataBridge: screenshot name has no entry/exit label: {stem}", StrategyLoggingLevel.Error);
                return;
            }

            DateTime fileLocal;
            DateTime.TryParseExact(parts[0] + parts[1], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out fileLocal);

            // 4) Find the fill this screenshot belongs to (gives us the position id).
            //    Exit shots are taken at the fill itself, so the fill may land a moment later.
            TradeRef match = null;
            for (int i = 0; i < 30 && match == null; i++)
            {
                match = FindTradeFor(root, fileLocal);
                if (match == null)
                    Thread.Sleep(100);
            }

            // Evaluated last, after the conversion time has passed, so a closing fill's
            // position removal has been processed.
            bool flat = kind == "exit" && IsFlat(match?.PositionId, match?.AccountId, root);

            DateTime shotLocal = match != null ? match.Local : DateTime.SpecifyKind(fileLocal, DateTimeKind.Local);

            var sb = new StringBuilder(384);
            sb.Append('{');
            AddStr(sb, "type", "screenshot");
            AddStr(sb, "kind", kind);
            AddStr(sb, "positionId", match?.PositionId);
            AddStr(sb, "account", match?.AccountId);
            AddStr(sb, "accountName", match?.AccountName);
            AddStr(sb, "symbol", match?.Symbol);
            AddStr(sb, "root", root);
            AddStr(sb, "file", stem + ".png");
            AddLong(sb, "fileTime", ToMs(shotLocal));
            Sep(sb);
            sb.Append("\"flat\":").Append(flat ? "true" : "false");
            AddNum(sb, "width", width);
            AddNum(sb, "height", height);
            AddNum(sb, "bytes", png.Length);
            AddLong(sb, "ts", NowMs());
            sb.Append('}');

            PublishFrames(TopicScreenshots, sb.ToString(), png);

            this.Log($"ZmqMarketDataBridge: published {stem}.png ({bmp.Length / 1048576.0:0.00} MB BMP -> {png.Length / 1048576.0:0.00} MB PNG, position {(match?.PositionId ?? "unmatched")}, flat={flat}).",
                StrategyLoggingLevel.Info);
        }

        // 3-frame publish: [topic][json][binary payload].
        private void PublishFrames(string topic, string json, byte[] payload)
        {
            try
            {
                lock (_sendLock)
                {
                    if (_pubSocket == null)
                        return;

                    _pubSocket.SendMoreFrame(topic).SendMoreFrame(json).SendFrame(payload);
                }
            }
            catch (Exception ex)
            {
                this.Log($"ZmqMarketDataBridge: publish (binary) failed - {ex.Message}", StrategyLoggingLevel.Error);
            }
        }

        // -------------------------------------------------------------
        // Minimal BMP (24-bit, uncompressed) -> PNG (RGB 8-bit) encoder.
        // No System.Drawing / extra DLLs: only DeflateStream from the BCL.
        // Handles exactly what Trade Journal's WriteBmp produces (and any
        // other 24-bit BI_RGB bottom-up or top-down BMP).
        // -------------------------------------------------------------

        private static byte[] BmpToPng(byte[] bmp, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (bmp == null || bmp.Length < 54 || bmp[0] != 0x42 || bmp[1] != 0x4D)
                return null;

            int dataOffset = BitConverter.ToInt32(bmp, 10);
            int w = BitConverter.ToInt32(bmp, 18);
            int h = BitConverter.ToInt32(bmp, 22);
            int bpp = BitConverter.ToUInt16(bmp, 28);
            uint compression = BitConverter.ToUInt32(bmp, 30);

            if (bpp != 24 || compression != 0 || w <= 0 || h == 0)
                return null;

            bool bottomUp = h > 0;
            int absH = Math.Abs(h);
            int srcRow = ((w * 3 + 3) / 4) * 4;
            int rowLen = w * 3;

            if (dataOffset < 54 || (long)dataOffset + (long)srcRow * absH > bmp.Length)
                return null;

            // Build the PNG scanlines: 1 filter byte + RGB bytes per row.
            // Per row, pick Sub or Up filter by smallest sum of absolute values
            // (standard PNG heuristic) - this is what makes charts compress well.
            byte[] raw = new byte[(rowLen + 1) * absH];
            byte[] cur = new byte[rowLen];
            byte[] prev = new byte[rowLen];
            byte[] fSub = new byte[rowLen];
            byte[] fUp = new byte[rowLen];

            for (int y = 0; y < absH; y++)
            {
                int srcIdx = bottomUp ? (absH - 1 - y) : y;
                int so = dataOffset + srcIdx * srcRow;

                for (int x = 0; x < rowLen; x += 3)
                {
                    cur[x] = bmp[so + x + 2];     // R
                    cur[x + 1] = bmp[so + x + 1]; // G
                    cur[x + 2] = bmp[so + x];     // B
                }

                long sumSub = 0, sumUp = 0;
                for (int i = 0; i < rowLen; i++)
                {
                    byte sv = unchecked((byte)(cur[i] - (i >= 3 ? cur[i - 3] : 0)));
                    byte uv = unchecked((byte)(cur[i] - prev[i]));
                    fSub[i] = sv;
                    fUp[i] = uv;
                    sumSub += Math.Abs((int)unchecked((sbyte)sv));
                    sumUp += Math.Abs((int)unchecked((sbyte)uv));
                }

                int ro = y * (rowLen + 1);
                if (sumUp <= sumSub)
                {
                    raw[ro] = 2; // Up
                    Buffer.BlockCopy(fUp, 0, raw, ro + 1, rowLen);
                }
                else
                {
                    raw[ro] = 1; // Sub
                    Buffer.BlockCopy(fSub, 0, raw, ro + 1, rowLen);
                }

                byte[] swap = prev;
                prev = cur;
                cur = swap;
            }

            // zlib stream = 2-byte header + raw deflate + big-endian Adler-32.
            byte[] zlib;
            using (var ms = new System.IO.MemoryStream(raw.Length / 4 + 64))
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
                {
                    ds.Write(raw, 0, raw.Length);
                }

                uint adler = Adler32(raw);
                ms.WriteByte((byte)(adler >> 24));
                ms.WriteByte((byte)(adler >> 16));
                ms.WriteByte((byte)(adler >> 8));
                ms.WriteByte((byte)adler);
                zlib = ms.ToArray();
            }

            using (var png = new System.IO.MemoryStream(zlib.Length + 128))
            {
                png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

                byte[] ihdr = new byte[13];
                PutBE(ihdr, 0, w);
                PutBE(ihdr, 4, absH);
                ihdr[8] = 8;  // bit depth
                ihdr[9] = 2;  // colour type: truecolour RGB
                ihdr[10] = 0; // compression
                ihdr[11] = 0; // filter method
                ihdr[12] = 0; // no interlace

                WritePngChunk(png, "IHDR", ihdr);
                WritePngChunk(png, "IDAT", zlib);
                WritePngChunk(png, "IEND", new byte[0]);

                width = w;
                height = absH;
                return png.ToArray();
            }
        }

        private static void PutBE(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        private static void WritePngChunk(System.IO.Stream s, string type, byte[] data)
        {
            byte[] len = new byte[4];
            PutBE(len, 0, data.Length);
            s.Write(len, 0, 4);

            byte[] t = Encoding.ASCII.GetBytes(type);
            s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);

            uint crc = 0xFFFFFFFFu;
            crc = Crc32Update(crc, t);
            crc = Crc32Update(crc, data);
            crc ^= 0xFFFFFFFFu;

            byte[] c = new byte[4];
            PutBE(c, 0, unchecked((int)crc));
            s.Write(c, 0, 4);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32Update(uint crc, byte[] buf)
        {
            for (int i = 0; i < buf.Length; i++)
                crc = CrcTable[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint Adler32(byte[] data)
        {
            const uint Mod = 65521;
            uint a = 1, b = 0;
            int idx = 0, remaining = data.Length;

            while (remaining > 0)
            {
                int n = Math.Min(remaining, 5552);
                for (int i = 0; i < n; i++)
                {
                    a += data[idx++];
                    b += a;
                }
                a %= Mod;
                b %= Mod;
                remaining -= n;
            }
            return (b << 16) | a;
        }

        // -------------------------------------------------------------
        // Market data handlers (unchanged)
        // -------------------------------------------------------------

        private void OnNewQuote(Symbol symbol, Quote quote)
        {
            long tsMs = NowMs();

            string json = string.Format(CultureInfo.InvariantCulture,
                "{{\"type\":\"quote\",\"symbol\":\"{0}\",\"bid\":{1},\"ask\":{2},\"bidSize\":{3},\"askSize\":{4},\"ts\":{5}}}",
                EscapeJson(symbol.Name),
                quote.Bid.ToString(CultureInfo.InvariantCulture),
                quote.Ask.ToString(CultureInfo.InvariantCulture),
                quote.BidSize.ToString(CultureInfo.InvariantCulture),
                quote.AskSize.ToString(CultureInfo.InvariantCulture),
                tsMs);

            Publish(symbol.Name, json);
        }

        private void OnNewLast(Symbol symbol, Last last)
        {
            long tsMs = NowMs();

            string json = string.Format(CultureInfo.InvariantCulture,
                "{{\"type\":\"trade\",\"symbol\":\"{0}\",\"price\":{1},\"size\":{2},\"ts\":{3}}}",
                EscapeJson(symbol.Name),
                last.Price.ToString(CultureInfo.InvariantCulture),
                last.Size.ToString(CultureInfo.InvariantCulture),
                tsMs);

            Publish(symbol.Name, json);
        }

        // -------------------------------------------------------------
        // Publishing + JSON helpers
        // -------------------------------------------------------------

        private void Publish(string topic, string json)
        {
            try
            {
                lock (_sendLock)
                {
                    if (_pubSocket == null)
                        return;

                    _pubSocket.SendMoreFrame(topic).SendFrame(json);
                }
            }
            catch (Exception ex)
            {
                this.Log($"ZmqMarketDataBridge: publish failed - {ex.Message}", StrategyLoggingLevel.Error);
            }
        }

        // Quantower raises these events on its own threads; an unhandled
        // exception there could take down the strategy, so never let one escape.
        private void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                this.Log($"ZmqMarketDataBridge: {what} failed - {ex.Message}", StrategyLoggingLevel.Error);
            }
        }

        private static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private static long ToMs(DateTime dt)
        {
            if (dt == DateTime.MinValue)
                return 0;

            dt = dt.Kind == DateTimeKind.Local
                ? dt.ToUniversalTime()
                : DateTime.SpecifyKind(dt, DateTimeKind.Utc);

            return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
        }

        // Object-body helpers: each appends  "key":value  with a leading comma
        // unless it is the first member right after '{'.
        private static void Sep(StringBuilder sb)
        {
            if (sb.Length > 0 && sb[sb.Length - 1] != '{')
                sb.Append(',');
        }

        private static void AddStr(StringBuilder sb, string key, string value)
        {
            Sep(sb);
            sb.Append('"').Append(key).Append("\":").Append(value == null ? "null" : Q(value));
        }

        private static void AddNum(StringBuilder sb, string key, double value)
        {
            Sep(sb);
            sb.Append('"').Append(key).Append("\":");
            if (double.IsNaN(value) || double.IsInfinity(value))
                sb.Append("null");
            else
                sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void AddLong(StringBuilder sb, string key, long value)
        {
            Sep(sb);
            sb.Append('"').Append(key).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static string Q(string s)
        {
            return "\"" + EscapeJson(s) + "\"";
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;

            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}