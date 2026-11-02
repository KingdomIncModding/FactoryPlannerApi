using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Reflection;
using Assets.Source.Player;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace KingdomFactoryApi
{
    [BepInPlugin(Id, "Kingdom Inc Factory API", ApiBuild.Version)]
    public sealed partial class FactoryApiPlugin : BaseUnityPlugin
    {
        public const string Id = "urakka.kingdominc.factoryapi";
        SnapshotStore store;
        LocalApiServer server;
        GameReader reader;
        FlowTelemetry telemetry;
        GamePlayer previous;
        string sessionId, lastError;
        double interval;
        float nextCapture;
        bool initialized, catalogReady, previousBackground;
        public static IFactoryData Data { get; private set; }
        void Awake()
        {
            int port = Config.Bind("Server", "Port", 17840, new ConfigDescription("Localhost HTTP port.", new AcceptableValueRange<int>(1024, 65535))).Value;
            interval = Config.Bind("Capture", "IntervalSeconds", 0.5d, new ConfigDescription("Sample the game on Unity's thread at this interval.", new AcceptableValueRange<double>(0.1, 10))).Value;
            previousBackground = Application.runInBackground;
            if (Config.Bind("Capture", "RunInBackground", true, "Continue running while the browser has focus.").Value) Application.runInBackground = true;
            store = new SnapshotStore(sampleInterval: interval); Data = store;
            reader = new GameReader(Resource("catalog-index.json"), Resource("provenance.json"));
            var icons = new Dictionary<string, byte[]>();
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("icons.zip"))
            using (var zip = new ZipArchive(resource, ZipArchiveMode.Read))
                foreach (var entry in zip.Entries)
                    using (var input = entry.Open()) using (var output = new MemoryStream())
                    { input.CopyTo(output); icons.Add(entry.FullName, output.ToArray()); }
            server = new LocalApiServer(new ApiRouter(store, interval, Resource("client.js"), Resource("openapi.json"), icons));
            try { server.Start(port); Logger.LogInfo("FACTORY_API_READY http://127.0.0.1:" + server.Port + "/api/v1 (read-only)"); }
            catch (Exception e) { Logger.LogError("Factory API could not bind its port. " + e.Message); enabled = false; return; }
            telemetry = new FlowTelemetry(); reader.Telemetry = telemetry.Snapshot;
            nextCapture = Time.realtimeSinceStartup + 3;
#if FACTORY_API_DIAGNOSTICS
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-factory-api-smoke") >= 0) StartCoroutine(RuntimeCheck());
#endif
        }
        static string Resource(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var text = new StreamReader(stream)) return text.ReadToEnd();
        }
        void Update()
        {
            if (Time.realtimeSinceStartup < nextCapture) return;
            nextCapture = Time.realtimeSinceStartup + (float)interval;
            var player = GamePlayer.Current;
            if (!initialized || !ReferenceEquals(previous, player))
            {
                initialized = true; previous = player; sessionId = player == null ? null : Guid.NewGuid().ToString("N");
                telemetry?.Reset(); reader.Reset(); store.Reset(sessionId);
            }
            try
            {
                if (!catalogReady) { store.SetCatalog(reader.Catalog()); catalogReady = true; telemetry.Start(); Logger.LogInfo("FACTORY_API_CATALOG_READY"); }
                store.Publish(sessionId, reader.Capture(player, sessionId));
                if (lastError != null) Logger.LogInfo("FACTORY_API_CAPTURE_RECOVERED");
                lastError = null;
            }
            catch (Exception e)
            {
                store.Fault("Game data capture failed; retrying.");
                string message = e.GetType().Name + ": " + e.Message;
                if (message != lastError) Logger.LogWarning("FACTORY_API_CAPTURE " + e);
                lastError = message;
            }
        }
        void OnDestroy()
        {
            telemetry?.Dispose(); server?.Dispose(); Data = null; Application.runInBackground = previousBackground;
        }
    }
}
