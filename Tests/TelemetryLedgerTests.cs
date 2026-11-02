using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using KingdomFactoryApi;

static class TelemetryLedgerTests
{
    public static void Run(Action<bool, string> check)
    {
        var ledger = new TelemetryLedger();
        ledger.Transfer("producer", 0, "router", 1, "sword", "producer", false);
        ledger.Transfer("router", 2, "shop", 0, "sword", "producer", true);
        ledger.Transfer("router", 2, "shop", 0, "sword", "producer", true);
        ledger.Earn("shop", "Gold", BigInteger.Parse("9007199254740993"), "producer", "sword", true);
        ledger.Spend("producer", "Gold", 7);
        var root = JsonDocument.Parse(Json.Encode(ledger.Snapshot())).RootElement;
        check(root.GetProperty("transfers").GetArrayLength() == 2, "Transfers aggregate by physical ports/item");
        check(root.GetProperty("deliveries")[0].GetProperty("count").GetString() == "2", "Routing hops do not double-count objective delivery");
        check(root.GetProperty("deliveries")[0].GetProperty("producerBuildingId").GetString() == "producer", "Delivery preserves final producer identity");
        check(root.GetProperty("deliveries")[0].GetProperty("earned").GetProperty("Gold").GetString() == "9007199254740993", "Attributed sale counters retain Int64 precision");
        check(TelemetryLedger.Accrued(100, 100, 0) == 0, "Till collection is not a second sale");
        check(TelemetryLedger.Accrued(120, 100, 0) == 20, "Same-frame sale and collection retain exact revenue");
        check(TelemetryLedger.Accrued(0, 100, 140) == 40, "Uncollected till earnings count as accrued revenue");
        check(TelemetryLedger.Accrued(long.MaxValue, long.MaxValue - 1, 10) == 11, "Accrual avoids intermediate Int64 overflow");
        ledger.Earn("shop", "Gold", 5);
        root = JsonDocument.Parse(Json.Encode(ledger.Snapshot())).RootElement;
        check(root.GetProperty("deliveries")[0].GetProperty("earned").GetProperty("Gold").GetString() == "9007199254740993", "Unattributed rewards are never assigned to an arbitrary producer");
        ledger.Retain(new HashSet<string> { "shop" });
        root = JsonDocument.Parse(Json.Encode(ledger.Snapshot())).RootElement;
        check(root.GetProperty("transfers").GetArrayLength() == 0 && root.GetProperty("deliveries").GetArrayLength() == 0, "Removed buildings release route counters");
        check(root.GetProperty("buildings").GetArrayLength() == 1, "Removed buildings release money counters");
        check(new TelemetryLedger().Epoch != ledger.Epoch, "Counter reset changes the measurement epoch");
    }
}
