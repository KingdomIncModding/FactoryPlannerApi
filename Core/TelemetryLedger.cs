using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace KingdomFactoryApi
{
    // Unity-thread-only cumulative counters. No item references, game mutation,
    // event history, or per-frame samples are retained here.
    public sealed class TelemetryLedger
    {
        public sealed class Row
        {
            public string Source, Target, Item, Producer;
            public int SourcePort, TargetPort;
            public BigInteger Count;
            public readonly Dictionary<string, BigInteger> Earned = new Dictionary<string, BigInteger>();
            public readonly Dictionary<string, BigInteger> Spent = new Dictionary<string, BigInteger>();
        }
        readonly Dictionary<string, Row> transfers = new Dictionary<string, Row>();
        readonly Dictionary<string, Row> deliveries = new Dictionary<string, Row>();
        readonly Dictionary<string, Row> buildings = new Dictionary<string, Row>();
        readonly Stopwatch clock = Stopwatch.StartNew();
        public readonly string Epoch = Guid.NewGuid().ToString("N");
        public static BigInteger Accrued(BigInteger paidOut, BigInteger heldBefore, BigInteger heldAfter) => paidOut + heldAfter - heldBefore;
        static string Key(params object[] values) => Json.Encode(values);
        static Row Get(Dictionary<string, Row> rows, string key, Func<Row> create)
        { if (!rows.TryGetValue(key, out var row)) rows.Add(key, row = create()); return row; }
        static void Add(Dictionary<string, BigInteger> values, string kind, BigInteger amount)
        { values.TryGetValue(kind, out var old); values[kind] = old + amount; }
        Row Delivery(string producer, string target, string item) => Get(deliveries, Key(producer, target, item), () => new Row { Producer = producer, Target = target, Item = item });
        public void Transfer(string source, int sourcePort, string target, int targetPort, string item, string producer, bool objective)
        {
            if (source == null || target == null || item == null) return;
            Get(transfers, Key(source, sourcePort, target, targetPort, item), () => new Row { Source = source, SourcePort = sourcePort, Target = target, TargetPort = targetPort, Item = item }).Count++;
            if (objective) Delivery(producer, target, item).Count++;
        }
        public void Earn(string building, string currency, BigInteger amount, string producer = null, string item = null, bool attributed = false)
        {
            if (building == null || amount <= 0) return;
            Add(Get(buildings, building, () => new Row { Target = building }).Earned, currency, amount);
            if (attributed) Add(Delivery(producer, building, item).Earned, currency, amount);
        }
        public void Spend(string building, string currency, long amount)
        {
            if (building == null || amount <= 0) return;
            Add(Get(buildings, building, () => new Row { Target = building }).Spent, currency, amount);
        }
        public void Retain(ISet<string> alive)
        {
            foreach (var key in buildings.Keys.Where(k => !alive.Contains(k)).ToArray()) buildings.Remove(key);
            foreach (var key in transfers.Where(p => !alive.Contains(p.Value.Source) || !alive.Contains(p.Value.Target)).Select(p => p.Key).ToArray()) transfers.Remove(key);
            foreach (var key in deliveries.Where(p => !alive.Contains(p.Value.Target) || p.Value.Producer != null && !alive.Contains(p.Value.Producer)).Select(p => p.Key).ToArray()) deliveries.Remove(key);
        }
        static object Money(Dictionary<string, BigInteger> source) => source.ToDictionary(p => p.Key, p => (object)p.Value.ToString(CultureInfo.InvariantCulture));
        public object Snapshot() => Json.Obj("available", true, "epoch", Epoch, "clockSeconds", clock.Elapsed.TotalSeconds,
            "transfers", transfers.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Json.Obj("sourceBuildingId", p.Value.Source, "sourcePort", p.Value.SourcePort, "targetBuildingId", p.Value.Target, "targetPort", p.Value.TargetPort, "itemId", p.Value.Item, "count", p.Value.Count.ToString(CultureInfo.InvariantCulture))).ToArray(),
            "deliveries", deliveries.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Json.Obj("producerBuildingId", p.Value.Producer, "targetBuildingId", p.Value.Target, "itemId", p.Value.Item, "count", p.Value.Count.ToString(CultureInfo.InvariantCulture), "earned", Money(p.Value.Earned))).ToArray(),
            "buildings", buildings.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Json.Obj("buildingId", p.Key, "earned", Money(p.Value.Earned), "spent", Money(p.Value.Spent))).ToArray());
    }
}
