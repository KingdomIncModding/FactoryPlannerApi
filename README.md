# Kingdom Inc Factory API — 0.1.3

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/S6V62AX30M)

The data foundation for an external Factory Planner. The game publishes a read-only API at **http://127.0.0.1:17840/api/v1** while it runs. A browser client can use a separate window or monitor. This project does not implement a planner UI or change the factory.

For the complete browser application, use Factory Planner. This download contains the API plugin, its browser SDK and API contract.

## Install the release

Requires Kingdom Inc. Windows x64 Mono and BepInEx 5.4.23.5+ in the 5.x series, installed separately. Close the game, then extract `KingdomFactoryApi-0.1.3.zip` into the directory containing `KingdomInc.exe`. The plugin belongs at `BepInEx/plugins/KingdomFactoryApi/KingdomFactoryApi.dll`; documentation and the SDK are in `FactoryApi/`. Start the game and load a kingdom, then request `http://127.0.0.1:17840/api/v1/health`. The main menu reports that no kingdom is loaded.

To update, close the game and replace this plugin's files. To uninstall, close the game and remove `BepInEx/plugins/KingdomFactoryApi`; optional documentation under `FactoryApi/` and the API configuration may also be removed. This API does not add save content. Manual installation is verified; Vortex deployment is untested.

## Build from source

Clone this repository into `<game>/_Mods/FactoryPlannerApi`; the folder name matters for local references. Builds require the .NET SDK, installed game/BepInEx assemblies, and the existing local `Extracted/CalculatorData` and asset extraction. These references and generated checksum provenance are not included in Git. Run preparation before a fresh-checkout build.

From the game directory in PowerShell:

```powershell
python _Mods/FactoryPlannerApi/Tools/prepare_catalog.py
dotnet build _Mods/FactoryPlannerApi/KingdomFactoryApi.csproj -c Release
& _Mods/FactoryPlannerApi/install.ps1
```

Close the game before installing. Requires the game's Mono build and BepInEx 5.4.23.5+ (5.x). The installer preserves other plugins and profiles. This plugin has a new ID and does not reactivate the archived Workshop. Remove `BepInEx/plugins/KingdomFactoryApi` with the game closed to disable it.

Configuration: `BepInEx/config/urakka.kingdominc.factoryapi.cfg`. Defaults: port **17840**, capture every **0.5 seconds**, game runs in the background while the browser has focus. A port conflict logs an error and disables this plugin rather than selecting an unpredictable port. No game assemblies, saves or research are written by the API.

## HTTP contract

| GET endpoint | Data |
|---|---|
| `/api/v1` | Version, units, capabilities, endpoint discovery |
| `/api/v1/health` | Capture state, freshness, kingdom session and process identity |
| `/api/v1/catalog` | Runtime item/recipe/research definitions, building/upgrade types, indexed reference rules and localization |
| `/api/v1/snapshot` | One coherent snapshot of every section |
| `/api/v1/snapshot?sections=research,unlocks,resources,factory` | Only selected sections, at the same snapshot revision |
| `/api/v1/sections/{name}` | One section in the same snapshot envelope |
| `/api/v1/changes?after=42&instanceId=...` | Bounded resumable list of changed sections |
| `/api/v1/client.js` | Browser ES-module client, including reconnect and watch support |
| `/api/v1/openapi.json` | OpenAPI 3.1 contract |
| `/api/v1/icons/{name}.png` | Bundled item/building/technology icons for the external client |

HTTP binds **IPv4 loopback only**. `localhost`/`127.0.0.1` browser origins on other ports are supported with CORS and preflight. Remote website origins, opaque `file://` origins, non-local Host headers and writes are rejected. No admin URL reservation or external service is required. The API has no authentication and is intended for trusted programs on this machine. Clients should use `127.0.0.1` for the API address to avoid IPv6 localhost resolution differences.

GET returns JSON. Unsupported methods return 405; bad selections/cursors return 400; unavailable, failed, or stale samples return 503. Snapshot freshness uses the configured capture interval, including conditional requests. Catalogs, snapshots and the client module provide ETags; `If-None-Match` can return 304. Additive fields may appear in API v1; breaking contract changes require another API major version.

## Snapshot sections

Every response carries `apiVersion`, `instanceId`, `sessionId`, `revision`, `capturedAtUtc` and `sections`. Each selected section contains `{ revision, data }`. A sample is published atomically; failed captures do not partially overwrite it. The stable API uses camelCase; data under `state`, `settings`, `reference`, and `native` follows game/reference formats.

| Section | Contents |
|---|---|
| `session` | Kingdom loaded status, session ID, active workflow ID and game time |
| `research` | Every research ID with purchased, locked, hidden and available flags |
| `unlocks` | Currently unlocked item IDs, available recipe IDs and purchasable building types |
| `upgrades` | Slots and unlocked upgrade types by building, resource upgrades and prestige values |
| `resources` | Total/spare resource quotas, demand, shortfalls, quota multipliers, Forest workers and Farm configuration |
| `economy` | Exact currency balances/earnings, exchange rates and current Requisition item types |
| `factory` | All workflows, building GUIDs, locations, stacks, recipes, effective stats, upgrades, port inventories, utility settings and pipe connections |
| `progression` | War, quest and dungeon state, unlock flags, ascension/nightmare state, Armory and Portal contents |
| `native` | Full `GamePlayer.ToJson()` representation as a game-version-specific escape hatch |

Each factory building's `state` includes its native settings: Stockpile contents/capacity, Manifold ports, Switch position, Filter selection, Timer interval, Button state and other special behaviors. Nothing is created, consumed, purchased or moved during capture. Runtime capture reads the game on Unity's main thread and HTTP workers only read serialized cached strings. Building settings and inventories reuse the same native capture. HTTP snapshot serialization runs outside the publication lock. The native C# `FactoryApiPlugin.Data` interface exposes the same snapshots and change events without HTTP; subscriber failures are isolated.

**Units:** rates in items/second; durations in seconds; positions in game grid coordinates. Port `capacity` is the game's nominal buffer threshold; quest/batch output can legitimately contain more items than that threshold. Inventory counts report the actual contents without clipping. Currency amounts are decimal integer **strings** to preserve 64-bit precision in JavaScript. Recipe output probability is 1 for deterministic results. Filtered/custom recipes are explicitly marked; their exact accepted inventory types and effects cannot always be expressed as fixed ingredients. Factory `theoreticalCyclesPerSecond` uses current effective speed and recipe time, including stack speed, but excludes productivity, resource starvation, transport and manual-work limitations. It is **not measured actual output**. Recipe productivity and equipment rules are provided separately for future planner calculations.

Resource quotas are totals shared across the kingdom, not allowances per production entry. `demandPerSecond` is the game's rolling demand estimate. Do not subtract it twice when accounting for existing production. The extracted reference has a pinned Assembly-CSharp hash; `referenceMatchesRuntime` becomes false after an unmatched game patch. Runtime definitions remain authoritative, and reference-only calculations should wait for a refreshed extraction when this flag is false.

## Changes and lifecycle

1. Check health and fetch the catalog and initial snapshot.
2. Keep its `instanceId` and `revision` and poll changes (500 ms is the default SDK interval).
3. Fetch a fresh snapshot for relevant changed sections. Advance the cursor to the revision of that snapshot, so intervening changes are included.
4. If no selected section changed, advance to the feed's revision. When `resyncRequired` is true, discard the cursor and fetch a fresh snapshot.

`instanceId` changes each game process. `sessionId` changes when a different `GamePlayer` loads and is null at the main menu. Workflow IDs persist only for the current loaded session; building GUIDs are the game's own IDs. Old kingdom snapshots are invalidated before reading a replacement. An unloaded kingdom publishes `session.loaded=false` and null other sections, clearing the browser's previous data.

The feed keeps 256 changed samples, usually about two minutes at the default interval in an active factory. It reports the latest observed state and changed sections; it is not an audit of every action. Multiple changes between samples can coalesce. Purely transient states between samples may not appear. Health reports stale/error if sampling fails or stops; clients must display disconnection/staleness and avoid treating old data as current. The SDK does this and requests a full fresh snapshot after recovery. The API is offline when the game exits; offline planning/persisting a last snapshot belongs to the future web client.

## Web client usage

Serve the web client over HTTP on localhost. Import the SDK directly from the game API or copy `Web/client.js` into the web project:

```javascript
import { FactoryApiClient } from 'http://127.0.0.1:17840/api/v1/client.js';

const api = new FactoryApiClient();
const catalog = await api.catalog();
const watch = api.watch({
  sections: ['session', 'research', 'unlocks', 'upgrades', 'resources', 'factory'],
  onSnapshot(snapshot) {
    // Replace local data with this coherent sample. Null sections clear old kingdom data.
    console.log(snapshot.sections);
  },
  onStatus({ connected, health, error }) {
    console.log(connected ? health.status : error.message);
  }
});

// Before disposing or starting another watch:
watch.stop();
await watch.done;
```

## Verification

```powershell
dotnet run --project _Mods/FactoryPlannerApi/Tests/ApiTests.csproj -c Release -- _Mods/FactoryPlannerApi/Verification/core-tests.json
python _Mods/FactoryPlannerApi/Tests/verify_browser.py
# Read-only audit when a kingdom is already loaded:
python _Mods/FactoryPlannerApi/Tests/verify_live_game.py
& _Mods/FactoryPlannerApi/Tests/verify_runtime_http.ps1
python _Mods/FactoryPlannerApi/Tools/package.py
```

Tests cover coherent snapshots, research/unlock notifications, cursor expiration/restarts, kingdom transitions, failed-capture recovery, 64-bit currency representation, concurrent readers, real HTTP, conditional requests, cross-port browser access, and browser reconnect/cancellation. A diagnostic build (`-p:DefineConstants=FACTORY_API_DIAGNOSTICS`) with an opt-in `-factory-api-smoke` launch checks the adapter in Unity using a temporary in-memory player at the main menu and quits. The diagnostic fixture is excluded from normal builds. It does not load or save a kingdom. See `Verification` for actual results and scope; a fixture check is not evidence of performance in a large saved factory.

The release has 163 standalone API/HTTP/telemetry assertions and browser integration coverage for native fetch, cross-port modules, change delivery, session changes, failure recovery, reconnect, conditional requests and cancellation. Unity telemetry and normal-build HTTP checks were also recorded for version 0.1.3; these do not establish performance in every saved kingdom. Generated reports remain local and are excluded from the public repository.

## Measured flow telemetry

`factory.telemetry` is an additive field in the existing factory section. It reports `available`, an observer `epoch`, monotonic real-time `clockSeconds`, and cumulative `transfers`, `deliveries`, and per-building `earned`/`spent` counters. Amounts and item counts are decimal integer strings. Clients subtract counters within one epoch and divide by elapsed clock seconds; never treat an unavailable observer as zero activity.

Transfers count successful physical pipe input acceptance, with source/target GUIDs, port numbers and item IDs. Deliveries count only arrival at objective inputs, preserving the last producing/preparing building through routing utilities. Per-item provenance uses weak references, so counters do not keep consumed inventory alive. Items already in a routing buffer when observation starts can have unknown provenance. Buildings removed from the kingdom release their counters.

A read-only Harmony observer wraps building updates/manual crafts and successful currency operations. It never changes arguments, return values, inventories or saves. Operating spend excludes research, construction and unrelated player purchases. Shop, Restaurant, Requisition Office and Disenchanter earnings account for held currency as well as paid-out currency, so collecting an existing till is not new income. Per-item external revenue attribution is provided for single-item Shop, Luxury Shop, Restaurant and Mercenary Guild sales. Passive and encounter rewards remain attached to their destination building; they are not assigned to an arbitrary upstream producer.

Container profit is scoped: owned destination income plus attributable external sales, minus operating costs of buildings in the container (including descendants). Shared/imported production costs are not allocated. Separate currencies remain separate. If external income or item provenance cannot be attributed, clients display it as unavailable rather than estimating it. Counter epochs change on kingdom replacement or observer restart. Topology changes and reconnect gaps require a fresh client rate window.

Install with the game closed, then launch the game and load a kingdom. A previous API binary has no measured counters; the dashboard can show final product topology but leaves measured rates pending. If patch installation fails, `available=false` includes a reason and the rest of the API remains usable. The opt-in main-menu diagnostic checks real patched transfer and Shop sale paths without loading or saving a kingdom.

## Support

If you enjoy these Kingdom Inc mods and tools, consider supporting development on Ko-fi!

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/S6V62AX30M)
