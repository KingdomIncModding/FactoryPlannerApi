using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KingdomFactoryApi;

static class Tests
{
    static int assertions;
    static void Check(bool condition, string description) { System.Threading.Interlocked.Increment(ref assertions); if (!condition) throw new Exception(description); }
    static JsonElement Read(ApiResponse response) => JsonDocument.Parse(response.Body).RootElement;
    static Dictionary<string, string> Sample(int value = 1) => SnapshotStore.SectionNames.ToDictionary(k => k, k => Json.Encode(Json.Obj("value", value, "section", k)));
    static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--fixture") { FixtureServer(args.Length > 1 ? int.Parse(args[1]) : 0); return; }
        TelemetryLedgerTests.Run(Check);
        var icon = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        var store = new SnapshotStore(3); var router = new ApiRouter(store, .5, "export const test = true;", "{}", new Dictionary<string, byte[]> { { "Icons_1.png", icon } });
        Check(router.Route("GET", "/api/v1/snapshot").Status == 503, "No uninitialized snapshot");
        Check(Read(store.Health(.5)).GetProperty("status").GetString() == "starting", "Starting state");
        var now = new DateTime(2026, 11, 1, 4, 0, 0, DateTimeKind.Utc);
        var freshness = new SnapshotStore(utcNow: () => now);
        freshness.Publish("freshness", Sample());
        string freshTag = freshness.Snapshot().ETag;
        now = now.AddSeconds(4);
        Check(Read(freshness.Health(.5)).GetProperty("stale").GetBoolean(), "Stopped sampler is stale");
        Check(freshness.Snapshot().Status == 503 && Read(freshness.Snapshot()).GetProperty("error").GetProperty("code").GetString() == "stale_snapshot", "Direct clients cannot consume stale snapshot");
        var freshRouter = new ApiRouter(freshness, .5, "");
        Check(freshRouter.Route("GET", "/api/v1/snapshot", freshTag).Status == 503, "Stale conditional GET cannot return 304");
        freshness.Publish("freshness", Sample());
        Check(freshness.Snapshot().Status == 200 && !Read(freshness.Health(.5)).GetProperty("stale").GetBoolean(), "Unchanged sample refreshes liveness");
        var slowSampler = new SnapshotStore(sampleInterval: 10, utcNow: () => now);
        slowSampler.Publish("slow", Sample()); now = now.AddSeconds(20);
        Check(slowSampler.Snapshot().Status == 200 && !Read(slowSampler.Health(10)).GetProperty("stale").GetBoolean(), "Freshness follows configured sampling interval");
        now = now.AddSeconds(13);
        Check(slowSampler.Snapshot().Status == 503 && Read(slowSampler.Health(10)).GetProperty("stale").GetBoolean(), "Slow configured sampler eventually expires");
        var sample = Sample(); int callbacks = 0;
        store.Changed += (r, keys) => { callbacks++; keys[0] = "damaged"; throw new Exception("Bad client"); };
        store.Publish("kingdom-a", sample);
        Check(callbacks == 1, "Subscriber isolated");
        string instance = Read(store.Snapshot()).GetProperty("instanceId").GetString();
        long revision = Read(store.Snapshot()).GetProperty("revision").GetInt64();
        sample["factory"] = "null";
        Check(Read(store.Snapshot()).GetProperty("sections").GetProperty("factory").GetProperty("data").ValueKind == JsonValueKind.Object, "Defensive copy");
        store.Publish("kingdom-a", Sample());
        Check(callbacks == 1, "Unchanged sample does not emit change");
        var changed = Sample(); changed["research"] = "[]"; changed["unlocks"] = "[]";
        store.Publish("kingdom-a", changed);
        var feed = Read(store.Changes(revision, instance));
        Check(!feed.GetProperty("resyncRequired").GetBoolean(), "Valid cursor");
        Check(feed.GetProperty("changes")[0].GetProperty("sections").GetArrayLength() == 2, "Research and item unlock feed");
        Check(Read(store.Changes(0, "previous-process")).GetProperty("resyncRequired").GetBoolean(), "Restart resync");
        Check(Read(store.Changes(999, instance)).GetProperty("resyncRequired").GetBoolean(), "Future cursor resync");
        store.Publish("kingdom-a", Sample(3)); store.Publish("kingdom-a", Sample(4)); store.Publish("kingdom-a", Sample(5));
        Check(Read(store.Changes(0, instance)).GetProperty("resyncRequired").GetBoolean(), "Expired cursor resync");
        Check(store.Changes(-1, instance).Status == 400, "Negative cursor rejected");
        store.Reset("kingdom-b");
        Check(store.Snapshot().Status == 503, "Old kingdom immediately invalidated");
        Check(Read(store.Health(.5)).GetProperty("sessionId").GetString() == "kingdom-b", "New kingdom identity");
        store.Publish("kingdom-b", Sample(6));
        Check(Read(store.Snapshot()).GetProperty("sessionId").GetString() == "kingdom-b", "New kingdom published");
        store.Fault("capture failed");
        Check(store.Snapshot().Status == 503, "Failed capture not silently served");
        Check(Read(store.Health(.5)).GetProperty("stale").GetBoolean(), "Fault marks stale");
        store.Publish("kingdom-b", Sample(6));
        Check(store.Snapshot().Status == 200, "Unchanged capture recovers");
        store.Reset(null); store.Publish(null, Sample());
        Check(Read(store.Health(.5)).GetProperty("status").GetString() == "noKingdom", "Main menu state");
        store.SetCatalog("{\"items\":[]}");
        var cat = router.Route("GET", "/api/v1/catalog");
        Check(router.Route("GET", "/api/v1/catalog", cat.ETag).Status == 304, "Catalog conditional request");
        var subset = router.Route("GET", "/api/v1/snapshot?sections=research,resources");
        Check(Read(subset).GetProperty("sections").EnumerateObject().Count() == 2, "Section selection");
        Check(router.Route("GET", "/api/v1/snapshot?sections=bad").Status == 400, "Unknown section rejected");
        Check(router.Route("GET", "/api/v1/snapshot?sections=").Status == 400, "Empty selection rejected");
        Check(router.Route("GET", "/api/v1/snapshot?sections=research&sections=factory").Status == 400, "Duplicate query rejected");
        Check(router.Route("GET", "/api/v1/changes?after=no").Status == 400, "Malformed cursor rejected");
        Check(router.Route("GET", "/api/v2/snapshot").Status == 404, "Version boundary");
        Check(router.Route("GET", "/api/v1/icons/Icons_1.png").BodyBytes.SequenceEqual(icon), "Icon bytes");
        var image = router.Route("GET", "/api/v1/icons/Icons_1.png");
        Check(router.Route("GET", "/api/v1/icons/Icons_1.png", image.ETag).Status == 304, "Icon conditional request");
        Check(router.Route("GET", "/api/v1/icons/../private").Status == 404, "Only bundled assets accessible");
        foreach (string method in new[] { "POST", "PUT", "PATCH", "DELETE" }) Check(router.Route(method, "/api/v1/snapshot").Status == 405, "Read-only " + method);
        var encoded = JsonDocument.Parse(Json.Encode(Json.Obj("unicode", "🐢\n\"\\", "notFinite", double.NaN, "amount", "9223372036854775807"))).RootElement;
        Check(encoded.GetProperty("unicode").GetString() == "🐢\n\"\\", "JSON escaping");
        Check(encoded.GetProperty("notFinite").ValueKind == JsonValueKind.Null, "Finite JSON values");
        Check(encoded.GetProperty("amount").GetString() == long.MaxValue.ToString(), "Currency precision");
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() => {
            for (int i = 0; i < 300; i++)
            {
                if (worker == 0) store.Publish("thread-test", Sample(i));
                else JsonDocument.Parse(store.Snapshot().Body).Dispose();
            }
        })));
        Check(true, "Concurrent publish/read remains valid JSON");
        string largeTail = new string('x', 1024 * 1024);
        Dictionary<string, string> Large(int generation)
        {
            var next = Sample(generation);
            next["factory"] = Json.Encode(Json.Obj("value", generation, "payload", largeTail));
            return next;
        }
        store.Publish("large", Large(0));
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() => {
            for (int i = 0; i < 16; i++)
            {
                if (worker == 0) store.Publish("large", Large(i));
                else
                {
                    var snapshot = Read(store.Snapshot());
                    var sections = snapshot.GetProperty("sections");
                    int value = sections.GetProperty("factory").GetProperty("data").GetProperty("value").GetInt32();
                    Check(sections.EnumerateObject().All(s => s.Value.GetProperty("data").GetProperty("value").GetInt32() == value), "Large concurrent response contains one generation");
                    Check(sections.EnumerateObject().All(s => s.Value.GetProperty("revision").GetInt64() <= snapshot.GetProperty("revision").GetInt64()), "Large concurrent response metadata is coherent");
                }
            }
        })));
        store.Publish("thread-test", Sample());
        using (var server = new LocalApiServer(router))
        {
            server.Start(0);
            string url = "http://127.0.0.1:" + server.Port;
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            using var request = new HttpRequestMessage(HttpMethod.Get, url + "/api/v1/snapshot");
            request.Headers.Add("Origin", "http://localhost:5173");
            var response = await http.SendAsync(request);
            Check(response.IsSuccessStatusCode, "Real HTTP snapshot");
            Check(response.Headers.GetValues("Access-Control-Allow-Origin").Single() == "http://localhost:5173", "Local web client CORS");
            using var preflight = new HttpRequestMessage(HttpMethod.Options, url + "/api/v1/snapshot");
            preflight.Headers.Add("Origin", "http://127.0.0.1:8000"); preflight.Headers.Add("Access-Control-Request-Method", "GET");
            Check((int)(await http.SendAsync(preflight)).StatusCode == 204, "Browser preflight");
            using var external = new HttpRequestMessage(HttpMethod.Get, url + "/api/v1/snapshot"); external.Headers.Add("Origin", "https://example.com");
            Check((int)(await http.SendAsync(external)).StatusCode == 403, "Remote website rejected");
            using var fileOrigin = new HttpRequestMessage(HttpMethod.Get, url + "/api/v1/snapshot"); fileOrigin.Headers.Add("Origin", "null");
            Check((int)(await http.SendAsync(fileOrigin)).StatusCode == 403, "Opaque file origin rejected");
            using var rebinding = new HttpRequestMessage(HttpMethod.Get, url + "/api/v1/snapshot"); rebinding.Headers.Host = "attacker.example";
            Check((int)(await http.SendAsync(rebinding)).StatusCode == 400, "Non-local Host rejected");
            Check((int)(await http.PostAsync(url + "/api/v1/snapshot", null)).StatusCode == 405, "HTTP write rejected");
            Check((int)(await http.PostAsync(url + "/api/v1/snapshot", new StringContent("body"))).StatusCode == 400, "HTTP request body rejected");
            using var etagRequest = new HttpRequestMessage(HttpMethod.Get, url + "/api/v1/catalog"); etagRequest.Headers.TryAddWithoutValidation("If-None-Match", cat.ETag);
            Check((int)(await http.SendAsync(etagRequest)).StatusCode == 304, "HTTP conditional GET");
            Check((await http.GetAsync(url + "/api/v1/client.js")).Content.Headers.ContentType.MediaType == "text/javascript", "ES module MIME type");
            Check((await http.GetByteArrayAsync(url + "/api/v1/icons/Icons_1.png")).SequenceEqual(icon), "HTTP binary response");
            var many = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => http.GetStringAsync(url + "/api/v1/health")));
            Check(many.All(body => JsonDocument.Parse(body).RootElement.GetProperty("readOnly").GetBoolean()), "Parallel HTTP clients");
            using var bad = new TcpClient(); await bad.ConnectAsync("127.0.0.1", server.Port);
            var raw = Encoding.ASCII.GetBytes("GET /api/v1 HTTP/1.1\r\nHost: localhost:" + server.Port + "\r\nHost: bad\r\n\r\n");
            await bad.GetStream().WriteAsync(raw); byte[] buffer = new byte[1024]; int n = await bad.GetStream().ReadAsync(buffer);
            Check(Encoding.ASCII.GetString(buffer, 0, n).StartsWith("HTTP/1.1 400"), "Duplicate Host rejected");
        }
        Console.WriteLine("PASS: " + assertions + " API contract, session, event, concurrency and real HTTP checks");
        if (args.Length > 0)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(args[0])));
            System.IO.File.WriteAllText(args[0], Json.Encode(Json.Obj("passed", true, "assertions", assertions, "testedAtUtc", DateTime.UtcNow.ToString("O"))));
        }
    }
    static void FixtureServer(int port)
    {
        var store = new SnapshotStore(3); store.SetCatalog("{\"apiVersion\":\"1\",\"items\":[],\"recipes\":[]}");
        store.Publish("fixture-a", Sample());
        string script = System.IO.File.ReadAllText(System.IO.Path.Combine(argsRoot(), "Web", "client.js"));
        using var server = new LocalApiServer(new ApiRouter(store, .5, script)); server.Start(port);
        Console.WriteLine(Json.Encode(Json.Obj("port", server.Port))); Console.Out.Flush();
        string command; int value = 2;
        while ((command = Console.ReadLine()) != null && command != "quit")
        {
            if (command == "fault") store.Fault("Fixture capture failure");
            else if (command == "reset") { store.Reset("fixture-b"); store.Publish("fixture-b", Sample(value++)); }
            else store.Publish("fixture-a", Sample(value++));
            Console.WriteLine("ok"); Console.Out.Flush();
        }
    }
    static string argsRoot() => System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
