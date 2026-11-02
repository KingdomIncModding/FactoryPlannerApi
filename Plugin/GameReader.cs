using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Assets.Source.Flow;
using Assets.Source.Flow.Elements;
using Assets.Source.Items;
using Assets.Source.Items.Type;
using Assets.Source.Player;
using LightJson;
using UnityEngine;

namespace KingdomFactoryApi
{
    // Called only by the Unity thread. Serialized strings are the boundary to HTTP.
    internal sealed class GameReader
    {
        readonly Dictionary<FlowDiagram, string> flowIds = new Dictionary<FlowDiagram, string>();
        readonly string index, provenance;
        readonly int ownerThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        public Func<GamePlayer, object> Telemetry;
        public GameReader(string index, string provenance) { this.index = index; this.provenance = provenance; }
        void AssertThread()
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != ownerThread) throw new InvalidOperationException("Game capture requires Unity's main thread");
        }
        public void Reset() { AssertThread(); flowIds.Clear(); }
        static object Native(JsonValue value) => new RawJson(value.ToString());
        static object Currency(CurrencyCollection currency)
        {
            var output = new Dictionary<string, object>();
            if (currency != null) foreach (var amount in currency.GetCurrency()) output[amount.c.ToString()] = amount.amt.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return output;
        }
        public string Catalog()
        {
            AssertThread();
            var items = ItemType.All.Select(item => Json.Obj("id", item.Identifier, "name", item.GetDisplayName(), "icon", item.IconName,
                "kind", item.GetType().Name, "canSell", item.CanSell, "sourceQuest", item.SourceQuest, "sourceDungeon", item.SourceDungeon,
                "sourceArena", item.SourceArena, "sourceTraining", item.SourceTraining,
                "resource", item is ResourceItemType resource ? Json.Obj("tag", resource.Tag.ToString(), "quotaPerUpgrade", resource.QuotaAmount,
                    "craftCost", Currency(resource.ResourceCost), "baseUpgradeCost", Currency(resource.QuotaBaseCost), "upgradeCostMultiplier", resource.QuotaCostMultiplier) : null,
                "forest", item is ForestItemType forest ? Json.Obj("workerType", forest.WorkerType.ToString(), "workersPerItem", forest.WorkersPerItem, "harvestCost", Currency(forest.HarvestCost)) : null)).ToArray();
            var recipes = CraftingRecipe.All.Select(recipe => Json.Obj("id", recipe.Identifier, "name", recipe.DisplayName, "icon", recipe.IconName,
                "stationTag", recipe.Tag.ToString(), "requiredTechId", recipe.RequiredTech?.Identifier, "craftingTimeSeconds", recipe.CraftingTime,
                "baseProductivity", recipe.BaseProductivity, "cost", Currency(recipe.CraftingCost), "reward", Currency(recipe.CraftingReward),
                "inputs", recipe.Reagents.Select((r, i) => Json.Obj("port", i, "itemId", r.Item?.Identifier, "count", r.Count,
                    "filtered", r.PreviewFilter, "description", r.ReagentText, "affectsQuality", r.AffectsQuality)).ToArray(),
                "outputs", recipe.Results.Select((r, i) => Json.Obj("resultIndex", i, "itemId", r.Item?.Identifier, "transformToItemId", r.TransformTo?.Identifier,
                    "enhancementId", r.Enhancement?.Identifier, "count", r.Count, "probability", r.Probability > 0 ? (object)r.Probability : 1,
                    "hasOutput", r.HasOutput, "customResult", r.Item == null && r.TransformTo == null && r.Enhancement == null)).ToArray())).ToArray();
            var research = TechNode.Nodes.Select(t => Json.Obj("id", t.Identifier, "name", t.Name, "description", t.Description,
                "kind", t.NodeType.ToString(), "icon", t.IconName, "rootId", t.Root?.Identifier, "previousId", t.Previous?.Identifier,
                "requirements", (t.AdditionalRequirements ?? new List<string>()).ToArray(), "customRequirement", t.CustomRequirementDesc,
                "tier", t.CostTier, "developerOnly", t.DevMode)).ToArray();
            string runtimeHash;
            using (var algorithm = System.Security.Cryptography.SHA256.Create())
            using (var file = File.OpenRead(Path.Combine(BepInEx.Paths.GameRootPath, "KingdomInc_Data", "Managed", "Assembly-CSharp.dll")))
                runtimeHash = string.Concat(algorithm.ComputeHash(file).Select(b => b.ToString("x2")));
            return Json.Encode(Json.Obj("apiVersion", "1", "gameVersion", Application.version, "unityVersion", Application.unityVersion,
                "runtimeAssemblySha256", runtimeHash, "referenceMatchesRuntime", runtimeHash == JsonValue.Parse(provenance)["files"]["KingdomInc_Data/Managed/Assembly-CSharp.dll"].AsString,
                "items", items, "recipes", recipes, "research", research, "iconBasePath", "/api/v1/icons/",
                "buildingTypes", typeof(FlowElement).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(FlowElement).IsAssignableFrom(t))
                    .Select(t => t.Name).OrderBy(t => t, StringComparer.Ordinal).ToArray(),
                "upgradeTypes", typeof(FlowUpgrade).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(FlowUpgrade).IsAssignableFrom(t))
                    .Select(t => t.Name).OrderBy(t => t, StringComparer.Ordinal).ToArray(),
                "reference", new RawJson(index), "provenance", new RawJson(provenance),
                "notes", new[] { "Runtime definitions are authoritative. Extracted reference rules are version-pinned.",
                    "Filtered/custom recipe inputs and results cannot always be reduced to fixed item rates.",
                    "The API reports sampled state and theoretical capacity; it does not measure realized throughput." }));
        }
        public Dictionary<string, string> Capture(GamePlayer player, string sessionId)
        {
            AssertThread();
            var sections = SnapshotStore.SectionNames.ToDictionary(k => k, k => "null");
            sections["session"] = Json.Encode(Json.Obj("loaded", player != null, "id", sessionId, "gameVersion", Application.version,
                "gameTimeSeconds", player == null ? null : (object)player.GameTime, "activeFlowId", player != null && player.CurrentFlow >= 0 && player.CurrentFlow < player.Flows.Count ? FlowId(player.ActiveFlow) : null));
            if (player == null) return sections;
            // The game's serialization includes ALL special building settings, inventory item data,
            // quest/dungeon/war progress and future fields; never SaveCurrentState or FromJson.
            JsonValue native = player.ToJson();
            sections["native"] = native.ToString();
            sections["research"] = Json.Encode(TechNode.Nodes.Select(t => Json.Obj("id", t.Identifier, "purchased", player.HasTech(t),
                "locked", t.IsLocked, "hidden", t.Hidden != null && t.Hidden(player), "available", t.IsAvailable)).ToArray());
            sections["unlocks"] = Json.Encode(Json.Obj("itemIds", ItemType.Unlocked.Select(i => i.Identifier).OrderBy(i => i, StringComparer.Ordinal).ToArray(),
                "recipeIds", CraftingRecipe.All.Where(r => r.IsAvailable).Select(r => r.Identifier).OrderBy(i => i, StringComparer.Ordinal).ToArray(),
                "buildingTypes", FlowElement.GetBuildableTypes().Select(b => b.ElementType).OrderBy(i => i, StringComparer.Ordinal).ToArray()));
            sections["upgrades"] = Json.Encode(Json.Obj("slotsByBuildingType", Native(native["UpgradeCounts"]),
                "availableByBuildingType", Native(native["UnlockedFlowUpgrades"]), "resourceUpgradeCounts", Native(native["ResourceUpgradeCount"]),
                "prestige", PrestigeNode.All.Select(p => Json.Obj("id", p.Identifier, "points", p.CurrentPoints, "value", p.CurrentValue)).ToArray()));
            sections["resources"] = Json.Encode(Json.Obj("quotas", ItemType.All.OfType<ResourceItemType>().Select(r => {
                double available = player.GetResourceAvailable(r), demand = player.GetResourceDemand(r);
                return Json.Obj("itemId", r.Identifier, "availablePerSecond", available, "demandPerSecond", demand,
                    "sparePerSecond", Math.Max(0, available - demand), "shortfallPerSecond", Math.Max(0, demand - available),
                    "quotaMultiplier", player.GetResourceQuotaMultiplier(r));
            }).ToArray(), "forest", player.ForestSettings == null ? null : Json.Obj("settings", Native(native["ForestSettings"]),
                "blocked", player.ForestSettings.IsBlocked, "workers", Enum.GetValues(typeof(ForestWorker)).Cast<ForestWorker>().Select(w =>
                    Json.Obj("type", w.ToString(), "count", player.ForestSettings.GetWorkerCount(w), "quotaMultiplier", player.ForestSettings.GetResourceQuotaMultiplier(w),
                        "utilization", player.ForestSettings.GetUtilization(w))).ToArray()),
                "farm", player.FarmSettings == null ? null : Json.Obj("settings", Native(native["FarmSettings"]), "blocked", player.FarmSettings.IsBlocked)));
            sections["economy"] = Json.Encode(Json.Obj("currencies", Native(native["Currency"]), "earned", Native(native["CurrencyEarned"]),
                "exchangeRates", Native(native["ExchangeRate"]), "requisitionItemIds", Native(native["RequisitionTypes"])));
            sections["progression"] = Json.Encode(Json.Obj("wars", Native(native["WarProgress"]), "unlockedDungeonIds", Native(native["UnlockedDungeons"]),
                "quests", Native(native["QuestMeta"]), "dungeons", Native(native["DungeonMeta"]), "register", Native(native["Register"]),
                "ascensionCount", player.AscensionCount, "nightmareCount", player.NightmareCount, "nightmareActive", player.NightmareActive,
                "nightmareCountActive", player.NightmareCountActive, "nightmareProgress", player.NightmareProgress,
                "armory", Native(native["Armory"]), "portal", Native(native["Portal"])));
            var livingFlows = new HashSet<FlowDiagram>(player.Flows);
            foreach (var old in flowIds.Keys.Where(k => !livingFlows.Contains(k)).ToArray()) flowIds.Remove(old);
            sections["factory"] = Json.Encode(Json.Obj("flows", player.Flows.Select((flow, i) => Json.Obj("id", FlowId(flow), "index", i,
                "name", flow.Label, "icon", flow.IconName, "buildings", flow.Elements.Select((building, j) => Building(building, native["Flows"][i]["Elements"][j])).ToArray(),
                "connections", flow.Pipes.Select(pipe => Json.Obj("sourceBuildingId", pipe.ConnectedOutput?.Parent.Guid,
                    "sourcePort", pipe.ConnectedOutput?.ParentIndex, "targetBuildingId", pipe.ConnectedInput?.Parent.Guid,
                    "targetPort", pipe.ConnectedInput?.ParentIndex, "itemRejected", pipe.ItemRejected,
                    "points", pipe.Nodes.Select(p => new[] { p.x, p.y }).ToArray())).ToArray())).ToArray(), "telemetry", Telemetry?.Invoke(player)));
            return sections;
        }
        string FlowId(FlowDiagram flow)
        {
            if (!flowIds.TryGetValue(flow, out var id)) flowIds.Add(flow, id = Guid.NewGuid().ToString("N"));
            return id;
        }
        object Building(FlowElement element, JsonValue state)
        {
            var recipe = element as RecipeElement;
            return Json.Obj("id", element.Guid, "type", element.ElementType, "category", element.Category.ToString(),
                "position", new[] { element.Position.x, element.Position.y }, "mirrored", element.Mirrored, "stackSize", element.StackSize,
                "requiredTechId", element.RequiredTech?.Identifier, "automatic", element.CanAutoCraft, "speed", element.Speed,
                "quality", element.Quality, "productivity", element.Productivity, "progress", element.Progress,
                "productivityProgress", element.ProductivityProgress, "upgradeSlots", element.UpgradeSlots,
                "installedUpgrades", Enumerable.Range(0, element.UpgradeSlots).Select(i => Json.Obj("slot", i, "type", element.GetUpgrade(i)?.UpgradeType)).ToArray(),
                "recipeId", recipe?.Recipe?.Identifier, "crafting", recipe == null ? null : (object)recipe.Crafting,
                "theoreticalCyclesPerSecond", recipe?.Recipe == null || recipe.Recipe.CraftingTime <= 0 ? null : (object)(element.Speed / recipe.Recipe.CraftingTime),
                "resourceItemId", (element as ResourceElement)?.Resource?.Identifier ?? (element as Forest)?.SelectedResource?.Identifier,
                "resourceBasePerSecond", element is ResourceElement resource ? (object)resource.OutputPerSecond : element is Forest forest ? (object)forest.OutputPerSecond : null,
                "inputs", element.Inputs.Select((port, i) => Port(port, state["Inputs"][i])).ToArray(),
                "outputs", element.Outputs.Select((port, i) => Port(port, state["Outputs"][i])).ToArray(), "state", Native(state));
        }
        static object Port(FlowIO io, JsonValue state) => Json.Obj("index", io.ParentIndex, "itemId", io.Item?.Identifier, "hasFilter", io.Filter != null,
            "filterPreview", io.FilterPreview, "capacity", io.BufferSize, "buffered", io.BufferedItems.Count,
            "connected", io.ConnectedPipe != null, "inventory", Native(state["BufferedItems"]));
    }
}
