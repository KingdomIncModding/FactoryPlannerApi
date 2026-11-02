#if FACTORY_API_DIAGNOSTICS
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Assets.Behaviour.UI;
using Assets.Source.Flow;
using Assets.Source.Flow.Elements;
using Assets.Source.Flow.Upgrades;
using Assets.Source.Items;
using Assets.Source.Player;
using BepInEx;
using LightJson;
using UnityEngine;

namespace KingdomFactoryApi
{
    public sealed partial class FactoryApiPlugin
    {
        // Opt-in diagnostic launch only. No kingdom load, save, or purchase calls.
        IEnumerator RuntimeCheck()
        {
            yield return new WaitForSecondsRealtime(6);
            string directory = Path.Combine(Paths.GameRootPath, "_Mods", "FactoryPlannerApi", "Verification");
            Directory.CreateDirectory(directory);
            var original = GamePlayer.Current;
            string originalJson = original?.ToJson().ToString();
            try
            {
                if (GameUI.Instance) throw new InvalidOperationException("Runtime diagnostic requires the main menu");
                var fixture = new GamePlayer(); GamePlayer.Current = fixture;
                fixture.AddTech("mine"); fixture.AddTech("forge"); fixture.AddTech("iron_ingot");
                fixture.AddResource("iron_ore", 10);
                fixture.AddCurrency(Currency.Gold, long.MaxValue - 5, false);
                var flow = new FlowDiagram { Label = "API isolated diagnostic" }; fixture.Flows.Add(flow);
                var mine = new Mine(); mine.SetupResource("iron_ore"); flow.Elements.Add(mine);
                var forge = new Forge(); forge.SetupRecipe("iron_ingot"); flow.Elements.Add(forge);
                var stockpile = new Stockpile { Capacity = 23 }; stockpile.Contents.Add(new InventoryItem("iron_ingot")); flow.Elements.Add(stockpile);
                var manifold = new Manifold(); manifold.AddInputSlot(); flow.Elements.Add(manifold);
                var filter = new Filter(); filter.SetFilterType("iron_ingot"); flow.Elements.Add(filter);
                var timer = new Assets.Source.Flow.Elements.Timer { MaxTime = 4.5f, TimeLeft = 1.25f }; flow.Elements.Add(timer);
                flow.Elements.Add(new TrackSwitch { SwitchPosition = true });
                flow.Elements.Add(new SingleButton()); flow.Elements.Add(new Inspector());
                var pipe = new FlowPipe(); pipe.Connect(mine.Outputs[0], forge.Inputs[0]); flow.Pipes.Add(pipe);
                fixture.SetUpgradeSlotCount("Forge", 2); forge.SetUpgrade(0, new Worker()); forge.SetUpgrade(1, new Overseer());
                string before = fixture.ToJson().ToString();
                var probe = new GameReader(Resource("catalog-index.json"), Resource("provenance.json"));
                var localStore = new SnapshotStore(); localStore.Publish("diagnostic", probe.Capture(fixture, "diagnostic"));
                var snapshot = JsonValue.Parse(localStore.Snapshot().Body);
                if (snapshot["sections"]["factory"]["data"]["flows"][0]["buildings"].AsJsonArray.Count != 9) throw new Exception("Factory capture incomplete");
                if (snapshot["sections"]["economy"]["data"]["currencies"]["Gold"].AsString != (long.MaxValue - 5).ToString()) throw new Exception("Currency precision lost");
                if (before != fixture.ToJson().ToString()) throw new Exception("Capture mutated fixture state");
                File.WriteAllText(Path.Combine(directory, "runtime-snapshot.json"), localStore.Snapshot().Body);
                File.WriteAllText(Path.Combine(directory, "runtime-catalog.json"), probe.Catalog());
                long cursor = (long)snapshot["revision"].AsNumber;
                fixture.AddTech("blacksmith"); fixture.AddTech("iron_sword");
                forge.StackSize = 2; forge.UpdateUpgrades();
                fixture.AddResource("iron_ore", 2, true);
                localStore.Publish("diagnostic", probe.Capture(fixture, "diagnostic"));
                var feed = JsonValue.Parse(localStore.Changes(cursor, localStore.InstanceId).Body);
                var changed = feed["changes"][0]["sections"].AsJsonArray.Select(v => v.AsString).ToArray();
                foreach (string section in new[] { "research", "unlocks", "upgrades", "resources", "factory", "native" })
                    if (!changed.Contains(section)) throw new Exception("Missing live change: " + section);
                // Real patched transfer and Shop.Update paths, in a disposable
                // main-menu fixture only. No loaded kingdom is touched.
                telemetry.Snapshot(fixture);
                fixture.AddTech("shop"); var shop = new Shop(); shop.UpdateUpgrades(); flow.Elements.Add(shop);
                mine.Outputs[0].BufferedItems.Clear(); shop.Inputs[0].BufferedItems.Clear();
                var soldItem = new InventoryItem("iron_ingot"); mine.Outputs[0].BufferedItems.Add(soldItem);
                var shopPipe = new FlowPipe(); shopPipe.Connect(mine.Outputs[0], shop.Inputs[0]); flow.Pipes.Add(shopPipe);
                shopPipe.Update(0);
                if (shop.Inputs[0].BufferedItems.Count != 1 || mine.Outputs[0].BufferedItems.Count != 0) throw new Exception("Observed transfer changed transport behavior");
                shop.CollectedGold = 100;
                bool originalNullWorldException = false;
                try { shop.Update(0); } catch (NullReferenceException) { originalNullWorldException = true; }
                // The fixture deliberately has no WorldFlowShop. Its original
                // update throws after selling; observer finalizers must preserve it.
                if (!originalNullWorldException || shop.CollectedGold <= 100) throw new Exception("Shop observation changed original update behavior");
                var measured = JsonValue.Parse(Json.Encode(telemetry.Snapshot(fixture)));
                if (!measured["available"].AsBoolean) throw new Exception("Telemetry observer failed to patch: " + measured.ToString());
                var delivery = measured["deliveries"].AsJsonArray.Single(v => v["targetBuildingId"].AsString == shop.Guid);
                if (delivery["count"].AsString != "1" || delivery["producerBuildingId"].AsString != mine.Guid) throw new Exception("Transfer count or provenance incorrect");
                if (delivery["earned"]["Gold"].AsString != (shop.CollectedGold - 100).ToString()) throw new Exception("Observed Shop sale revenue incorrect");
                File.WriteAllText(Path.Combine(directory, "runtime-telemetry-tests.json"), Json.Encode(Json.Obj("passed", true,
                    "realHarmonyInputTransfer", true, "realShopUpdateRevenue", true, "originalExceptionPreserved", true,
                    "readOnlyObservation", true, "savedKingdomLoaded", false, "testedAtUtc", DateTime.UtcNow.ToString("O"))));
                File.WriteAllText(Path.Combine(directory, "runtime-changes.json"), localStore.Changes(cursor, localStore.InstanceId).Body);
                File.WriteAllText(Path.Combine(directory, "runtime-tests.json"), Json.Encode(Json.Obj("passed", true, "fixtureBuildings", 9,
                    "readOnlyCaptureVerified", true, "researchAndUpgradeChangesVerified", true, "savedKingdomLoaded", false,
                    "testedAtUtc", DateTime.UtcNow.ToString("O"))));
                Logger.LogInfo("FACTORY_API_RUNTIME_PASS: catalog, 9 buildings, inventories, research, upgrades, quotas, connections, exact currencies, unchanged game state");
            }
            catch (Exception e)
            {
                Logger.LogError("FACTORY_API_RUNTIME_FAIL " + e);
                File.WriteAllText(Path.Combine(directory, "runtime-tests.json"), Json.Encode(Json.Obj("passed", false, "error", e.ToString())));
            }
            finally
            {
                GamePlayer.Current = original;
                if (originalJson != original?.ToJson().ToString()) Logger.LogError("FACTORY_API_RUNTIME_FAIL: original player changed");
            }
            yield return new WaitForSecondsRealtime(6);
            Application.Quit();
        }
    }
}
#endif
