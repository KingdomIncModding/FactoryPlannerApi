using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Assets.Source.Flow;
using Assets.Source.Flow.Elements;
using Assets.Source.Items;
using Assets.Source.Player;
using HarmonyLib;

namespace KingdomFactoryApi
{
    internal sealed class FlowTelemetry : IDisposable
    {
        internal sealed class Trace { public string Producer; }
        internal sealed class Consumed { public string Producer, Item; }
        internal sealed class Scope
        {
            public Scope Parent;
            public FlowElement Element;
            public Dictionary<Currency, BigInteger> Held;
            public readonly Dictionary<Currency, BigInteger> After = new Dictionary<Currency, BigInteger>();
            public readonly Dictionary<Currency, BigInteger> Income = new Dictionary<Currency, BigInteger>();
            public readonly List<Consumed> Consumed = new List<Consumed>();
        }
        static FlowTelemetry active;
        [ThreadStatic] static Scope current;
        readonly int thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        readonly Harmony harmony = new Harmony(FactoryApiPlugin.Id + ".telemetry");
        readonly Stack<Scope> pool = new Stack<Scope>();
        GamePlayer player;
        TelemetryLedger ledger = new TelemetryLedger();
        ConditionalWeakTable<InventoryItem, Trace> traces = new ConditionalWeakTable<InventoryItem, Trace>();
        bool available;
        string failure;
        static readonly HashSet<string> Routers = new HashSet<string> { "Collector", "Distributor", "Manifold", "Filter", "TrackSwitch", "Stockpile", "Inspector", "SingleButton", "Timer" };
        static bool Objective(FlowElement e) => e.Category == ElementCategory.Consumption || new[] { "Arena", "Armory", "Hoard", "Portal" }.Contains(e.ElementType);
        bool Ready()
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != thread || GamePlayer.Current == null) return false;
            if (!ReferenceEquals(player, GamePlayer.Current)) { Reset(); player = GamePlayer.Current; }
            return true;
        }
        public void Reset() { player = null; ledger = new TelemetryLedger(); traces = new ConditionalWeakTable<InventoryItem, Trace>(); pool.Clear(); current = null; }
        public void Start()
        {
            active = this;
            try
            {
                var methods = typeof(FlowElement).Assembly.GetTypes().Where(t => typeof(FlowElement).IsAssignableFrom(t))
                    .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    .Where(m => !m.IsAbstract && (m.Name == "Update" || m.Name == "DoManualCraft" || m.Name == "DoCraft")).Distinct();
                foreach (var method in methods) harmony.Patch(method, prefix: Patch(nameof(Enter)), finalizer: Patch(nameof(Exit)));
                harmony.Patch(AccessTools.Method(typeof(FlowInput), "InputItem"), postfix: Patch(nameof(Transferred)));
                harmony.Patch(AccessTools.Method(typeof(FlowIO), "ConsumeItem", Type.EmptyTypes), postfix: Patch(nameof(ConsumedItem)));
                harmony.Patch(AccessTools.Method(typeof(GamePlayer), "AddCurrency", new[] { typeof(Currency), typeof(long), typeof(bool) }), postfix: Patch(nameof(AddedCurrency)));
                harmony.Patch(AccessTools.Method(typeof(GamePlayer), "SpendCurrency", new[] { typeof(Currency), typeof(long) }), postfix: Patch(nameof(SpentCurrency)));
                available = true;
            }
            catch (Exception e) { failure = e.GetType().Name + ": " + e.Message; try { harmony.UnpatchSelf(); } catch { } active = null; }
        }
        static HarmonyMethod Patch(string name) => new HarmonyMethod(typeof(FlowTelemetry).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        static Dictionary<Currency, BigInteger> Held(FlowElement e, Dictionary<Currency, BigInteger> result)
        {
            result.Clear();
            if (e is Shop shop) result[Currency.Gold] = shop.CollectedGold;
            if (e is Restaurant restaurant) { result[Currency.Gold] = restaurant.CollectedGold; result[Currency.Crown] = restaurant.CollectedCrowns; }
            if (e is RequisitionOffice requisition) result[Currency.Requisition] = requisition.CollectedRequisition;
            if (e is Disenchanter disenchanter) result[Currency.Mana] = disenchanter.CollectedMana;
            return result;
        }
        static void Enter(FlowElement __instance, out Scope __state)
        {
            __state = null;
            try
            {
                if (active == null || !active.Ready() || current?.Element == __instance || Routers.Contains(__instance.ElementType)) return;
                __state = active.pool.Count > 0 ? active.pool.Pop() : new Scope { Held = new Dictionary<Currency, BigInteger>() };
                __state.Element = __instance; __state.Parent = current; Held(__instance, __state.Held); current = __state;
            }
            catch { /* Observation must never interrupt the game. */ }
        }
        static Exception Exit(Exception __exception, Scope __state)
        {
            if (__state == null) return __exception;
            try
            {
                if (__state.Held.Count == 0 && __state.Income.Count == 0) return __exception;
                var after = Held(__state.Element, __state.After);
                foreach (var kind in __state.Income.Keys.Concat(__state.Held.Keys).Concat(after.Keys).Distinct())
                {
                    __state.Income.TryGetValue(kind, out var income); __state.Held.TryGetValue(kind, out var beforeHeld); after.TryGetValue(kind, out var afterHeld);
                    var earned = TelemetryLedger.Accrued(income, beforeHeld, afterHeld);
                    // These sinks consume exactly one sold item per call and have
                    // no passive income; other encounter/passive rewards stay local.
                    bool attributable = __state.Consumed.Count == 1 && (__state.Element is Shop || __state.Element is LuxuryShop || __state.Element is Restaurant || __state.Element is MercenaryGuild);
                    var item = attributable ? __state.Consumed[0] : null;
                    active?.ledger.Earn(__state.Element.Guid, kind.ToString(), earned, item?.Producer, item?.Item, attributable);
                }
            }
            catch { }
            finally { current = __state.Parent; __state.Income.Clear(); __state.Consumed.Clear(); __state.Element = null; __state.Parent = null; active?.pool.Push(__state); }
            return __exception;
        }
        static void Transferred(FlowInput __instance, InventoryItem __0, bool __result)
        {
            try
            {
                if (!__result || active == null || !active.Ready() || __0 == null) return;
                var pipe = __instance.ConnectedPipe;
                if (pipe?.ConnectedOutput == null || pipe.ConnectedInput != __instance || !ReferenceEquals(pipe.ConnectedOutput.AvailableItem, __0)) return;
                var source = pipe.ConnectedOutput.Parent;
                var trace = active.traces.GetValue(__0, _ => new Trace());
                if (!Routers.Contains(source.ElementType)) trace.Producer = source.Guid;
                active.ledger.Transfer(source.Guid, pipe.ConnectedOutput.ParentIndex, __instance.Parent.Guid, __instance.ParentIndex, __0.Type.Identifier, trace.Producer, Objective(__instance.Parent));
            }
            catch { }
        }
        static void ConsumedItem(FlowIO __instance, InventoryItem __result)
        {
            try
            {
                if (__result == null || current == null || !(__instance is FlowInput) || __instance.Parent != current.Element || !Objective(current.Element)) return;
                active.traces.TryGetValue(__result, out var trace);
                current.Consumed.Add(new Consumed { Producer = trace?.Producer, Item = __result.Type.Identifier });
            }
            catch { }
        }
        static void AddedCurrency(Currency __0, long __1, bool __2)
        {
            try { if (current != null && __2 && __1 > 0) { current.Income.TryGetValue(__0, out var old); current.Income[__0] = old + __1; } } catch { }
        }
        static void SpentCurrency(Currency __0, long __1, bool __result)
        { try { if (__result && current != null) active?.ledger.Spend(current.Element.Guid, __0.ToString(), __1); } catch { } }
        public object Snapshot(GamePlayer owner)
        {
            if (!available) return Json.Obj("available", false, "reason", failure ?? "Observer unavailable");
            if (!Ready() || !ReferenceEquals(owner, player)) return null;
            ledger.Retain(new HashSet<string>(owner.Flows.SelectMany(f => f.Elements).Select(e => e.Guid)));
            return ledger.Snapshot();
        }
        public void Dispose() { try { harmony.UnpatchSelf(); } catch { } if (active == this) { active = null; current = null; } }
    }
}
