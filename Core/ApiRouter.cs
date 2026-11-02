using System;
using System.Collections.Generic;
using System.Linq;

namespace KingdomFactoryApi
{
    public sealed class ApiRouter
    {
        public readonly SnapshotStore Store;
        public readonly double SampleInterval;
        readonly string clientScript, specification;
        readonly IReadOnlyDictionary<string, byte[]> icons;
        public ApiRouter(SnapshotStore store, double interval, string script, string specification = "{}", IReadOnlyDictionary<string, byte[]> icons = null)
        { Store = store; SampleInterval = interval; clientScript = script; this.specification = specification; this.icons = icons; }
        public ApiResponse Route(string method, string target, string ifNoneMatch = null)
        {
            if (method != "GET")
            {
                var denied = ApiResponse.Error(405, "read_only", "Only GET requests are supported."); denied.Headers["Allow"] = "GET, OPTIONS"; return denied;
            }
            if (!target.StartsWith("/", StringComparison.Ordinal) || target.Contains("#") || target.Length > 4096)
                return ApiResponse.Error(400, "invalid_target", "Expected an API path.");
            string[] split = target.Split(new[] { '?' }, 2); string path = split[0];
            var query = new Dictionary<string, string>();
            if (split.Length == 2) foreach (string pair in split[1].Split('&'))
            {
                string[] parts = pair.Split(new[] { '=' }, 2);
                if (parts.Length != 2 || query.ContainsKey(parts[0])) return ApiResponse.Error(400, "invalid_query", "Expected unique query parameters.");
                try { query.Add(parts[0], Uri.UnescapeDataString(parts[1])); }
                catch { return ApiResponse.Error(400, "invalid_query", "Invalid query encoding."); }
            }
            ApiResponse result;
            if (path == "/" || path == "/api/v1") result = new ApiResponse { Body = Json.Encode(Json.Obj(
                "name", "Kingdom Inc Factory API", "apiVersion", "1", "pluginVersion", ApiBuild.Version, "readOnly", true,
                "transport", "HTTP polling with resumable changes", "sections", SnapshotStore.SectionNames,
                "endpoints", new[] { "/api/v1/health", "/api/v1/catalog", "/api/v1/snapshot", "/api/v1/changes", "/api/v1/client.js", "/api/v1/openapi.json", "/api/v1/icons/{name}.png" },
                "units", Json.Obj("time", "seconds", "rate", "items/second", "currency", "decimal integer strings"))) };
            else if (path == "/api/v1/health") result = Store.Health(SampleInterval);
            else if (path == "/api/v1/catalog") result = Store.Catalog();
            else if (path == "/api/v1/openapi.json") result = new ApiResponse { Body = specification, ETag = "\"" + SnapshotStore.Hash(specification) + "\"" };
            else if (path.StartsWith("/api/v1/icons/", StringComparison.Ordinal))
            {
                string name = path.Substring("/api/v1/icons/".Length);
                if (icons != null && icons.TryGetValue(name, out var bytes)) result = new ApiResponse { BodyBytes = bytes, ContentType = "image/png", ETag = "\"" + SnapshotStore.Hash(Convert.ToBase64String(bytes)) + "\"" };
                else result = ApiResponse.Error(404, "icon_not_found", "Unknown bundled icon.");
            }
            else if (path == "/api/v1/client.js") result = new ApiResponse { Body = clientScript, ContentType = "text/javascript; charset=utf-8", ETag = "\"" + SnapshotStore.Hash(clientScript) + "\"" };
            else if (path == "/api/v1/snapshot")
            {
                if (query.Keys.Any(k => k != "sections")) return ApiResponse.Error(400, "invalid_query", "Supported query: sections.");
                result = Store.Snapshot(query.TryGetValue("sections", out var names) ? names.Split(',') : null);
            }
            else if (path.StartsWith("/api/v1/sections/", StringComparison.Ordinal)) result = Store.Snapshot(new[] { path.Substring("/api/v1/sections/".Length) });
            else if (path == "/api/v1/changes")
            {
                if (query.Keys.Any(k => k != "after" && k != "instanceId") || !query.TryGetValue("after", out var cursor) || !long.TryParse(cursor, out long after))
                    return ApiResponse.Error(400, "invalid_cursor", "Supply after and instanceId from the last snapshot.");
                result = Store.Changes(after, query.TryGetValue("instanceId", out var instance) ? instance : null);
            }
            else result = ApiResponse.Error(404, "not_found", "Unknown API endpoint.");
            if (result.ETag != null && ifNoneMatch == result.ETag) { result.Status = 304; result.Body = ""; result.BodyBytes = null; }
            return result;
        }
    }
}
