using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace KingdomFactoryApi
{
    public sealed class ApiResponse
    {
        public int Status = 200;
        public string Body, ETag, ContentType = "application/json; charset=utf-8";
        public byte[] BodyBytes;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>();
        public static ApiResponse Error(int code, string error, string message) => new ApiResponse
        { Status = code, Body = Json.Encode(Json.Obj("apiVersion", "1", "error", Json.Obj("code", error, "message", message))) };
    }

    public interface IFactoryData
    {
        event Action<long, string[]> Changed;
        ApiResponse Health(double sampleInterval);
        ApiResponse Catalog();
        ApiResponse Snapshot(IEnumerable<string> requested = null);
        ApiResponse Changes(long after, string instance);
    }

    public sealed class SnapshotStore : IFactoryData
    {
        public const string ApiVersion = "1";
        public static readonly IReadOnlyList<string> SectionNames = Array.AsReadOnly(new[] { "session", "research", "unlocks", "upgrades", "resources", "economy", "factory", "progression", "native" });
        readonly object gate = new object();
        readonly int historyLimit;
        readonly Func<DateTime> utcNow;
        readonly double staleAfterSeconds;
        readonly Queue<Change> history = new Queue<Change>();
        Dictionary<string, string> sections;
        readonly Dictionary<string, long> sectionRevisions = new Dictionary<string, long>();
        string sessionId, capturedAt, catalog, catalogTag, error;
        long revision;
        DateTime lastSample;
        public readonly string InstanceId = Guid.NewGuid().ToString("N");
        public event Action<long, string[]> Changed;
        sealed class Change
        {
            public long Revision; public string SessionId, At; public string[] Sections;
            public object Data => Json.Obj("revision", Revision, "sessionId", SessionId, "capturedAtUtc", At, "sections", Sections);
        }
        public SnapshotStore(int historyLimit = 256, double sampleInterval = .5, Func<DateTime> utcNow = null)
        {
            if (double.IsNaN(sampleInterval) || double.IsInfinity(sampleInterval) || sampleInterval <= 0) throw new ArgumentOutOfRangeException(nameof(sampleInterval));
            this.historyLimit = Math.Max(1, historyLimit); this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            staleAfterSeconds = sampleInterval * 3 + 2;
        }
        bool Stale() => error != null || lastSample == default || (utcNow() - lastSample).TotalSeconds > staleAfterSeconds;
        public static string Hash(string value)
        {
            using (var hash = SHA256.Create()) return string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2")));
        }
        public void SetCatalog(string json)
        {
            lock (gate) { catalog = json; catalogTag = "\"catalog-" + Hash(json) + "\""; }
        }
        // Invalidates old kingdom data BEFORE a replacement kingdom is read.
        public void Reset(string id)
        {
            lock (gate) { sessionId = id; sections = null; sectionRevisions.Clear(); error = null; lastSample = default; }
        }
        public void Fault(string message) { lock (gate) error = message; }
        public void Publish(string id, IDictionary<string, string> data)
        {
            if (data.Count != SectionNames.Count || SectionNames.Any(k => !data.ContainsKey(k) || data[k] == null))
                throw new ArgumentException("Capture must contain all sections");
            string[] changed; long published;
            lock (gate)
            {
                changed = SectionNames.Where(k => sections == null || sessionId != id || sections[k] != data[k]).ToArray();
                lastSample = utcNow(); error = null;
                if (changed.Length == 0) return;
                sessionId = id; revision++; published = revision; capturedAt = lastSample.ToString("O");
                sections = new Dictionary<string, string>(data, StringComparer.Ordinal);
                foreach (string key in changed) sectionRevisions[key] = revision;
                history.Enqueue(new Change { Revision = revision, SessionId = id, At = capturedAt, Sections = changed });
                while (history.Count > historyLimit) history.Dequeue();
            }
            // Subscriber failures cannot break capture or the game loop.
            var listeners = Changed;
            if (listeners != null) foreach (Action<long, string[]> listener in listeners.GetInvocationList())
                try { listener(published, (string[])changed.Clone()); } catch { }
        }
        public ApiResponse Health(double sampleInterval)
        {
            lock (gate) return new ApiResponse { Body = Json.Encode(Json.Obj(
                "apiVersion", ApiVersion, "instanceId", InstanceId, "revision", revision, "sessionId", sessionId,
                "status", error != null ? "error" : sections == null ? "starting" : sessionId == null ? "noKingdom" : "live",
                "lastSampleAtUtc", lastSample == default ? null : lastSample.ToString("O"),
                "sampleIntervalSeconds", sampleInterval, "stale", Stale(),
                "error", error, "readOnly", true)) };
        }
        public ApiResponse Catalog()
        {
            lock (gate) return catalog == null ? ApiResponse.Error(503, "catalog_not_ready", "Game definitions are still initializing.")
                : new ApiResponse { Body = catalog, ETag = catalogTag };
        }
        public ApiResponse Snapshot(IEnumerable<string> requested = null)
        {
            string[] keys = (requested ?? SectionNames).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray();
            if (keys.Length == 0 || keys.Any(k => !SectionNames.Contains(k))) return ApiResponse.Error(400, "invalid_sections", "Unknown or empty section selection.");
            Dictionary<string, object> selected;
            string selectedSession, selectedAt, selectedTag;
            long selectedRevision;
            lock (gate)
            {
                if (sections == null) return ApiResponse.Error(503, "snapshot_not_ready", "Wait for a successful game sample.");
                if (error != null) return ApiResponse.Error(503, "capture_failed", "The last capture failed. Wait for recovery and check health.");
                if (Stale()) return ApiResponse.Error(503, "stale_snapshot", "Game sampling has stopped. Wait for a fresh sample.");
                selected = new Dictionary<string, object>();
                foreach (string key in keys) selected[key] = Json.Obj("revision", sectionRevisions[key], "data", new RawJson(sections[key]));
                selectedSession = sessionId; selectedRevision = revision; selectedAt = capturedAt;
                selectedTag = "\"" + InstanceId + "-" + revision + "-" + string.Join(".", keys) + "\"";
            }
            // Serialize captured immutable strings after releasing the publisher's lock.
            // Slow/large browser responses must not block Unity from publishing a sample.
            return new ApiResponse { ETag = selectedTag,
                Body = Json.Encode(Json.Obj("apiVersion", ApiVersion, "instanceId", InstanceId, "sessionId", selectedSession,
                    "revision", selectedRevision, "capturedAtUtc", selectedAt, "sections", selected)) };
        }
        public ApiResponse Changes(long after, string instance)
        {
            lock (gate)
            {
                if (after < 0) return ApiResponse.Error(400, "invalid_cursor", "after must be a non-negative integer.");
                bool resync = instance != InstanceId || after > revision || (history.Count > 0 && after < history.Peek().Revision - 1);
                return new ApiResponse { Body = Json.Encode(Json.Obj("apiVersion", ApiVersion, "instanceId", InstanceId,
                    "sessionId", sessionId, "revision", revision, "resyncRequired", resync,
                    "changes", resync ? new object[0] : history.Where(c => c.Revision > after).Select(c => c.Data).ToArray())) };
            }
        }
    }
}
