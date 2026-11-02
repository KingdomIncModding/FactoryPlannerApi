using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KingdomFactoryApi
{
    // Small bounded HTTP/1.1 GET transport. No Unity/game objects enter these worker threads.
    public sealed class LocalApiServer : IDisposable
    {
        readonly ApiRouter router;
        readonly SemaphoreSlim clients = new SemaphoreSlim(8);
        readonly HashSet<TcpClient> active = new HashSet<TcpClient>();
        TcpListener listener;
        volatile bool stopping;
        public int Port { get; private set; }
        public LocalApiServer(ApiRouter router) { this.router = router; }
        public void Start(int port)
        {
            if (listener != null) throw new InvalidOperationException("Already started");
            listener = new TcpListener(IPAddress.Loopback, port);
            try { listener.Start(16); Port = ((IPEndPoint)listener.LocalEndpoint).Port; }
            catch { listener = null; throw; }
            _ = Accept();
        }
        async Task Accept()
        {
            while (!stopping)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (Exception e) when (e is SocketException || e is ObjectDisposedException) { break; }
                if (!clients.Wait(0)) { client.Close(); continue; }
                lock (active) active.Add(client);
                _ = Task.Run(() => Serve(client));
            }
        }
        static bool LocalOrigin(string origin)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
            return (uri.Scheme == "http" || uri.Scheme == "https") && string.IsNullOrEmpty(uri.UserInfo)
                && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
                && (uri.Host == "localhost" || uri.Host == "127.0.0.1" || uri.Host == "[::1]");
        }
        void Serve(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 3000; client.SendTimeout = 3000; client.NoDelay = true;
                using (client)
                using (var stream = client.GetStream())
                {
                    var bytes = new List<byte>(); int next;
                    while (bytes.Count < 8192 && (next = stream.ReadByte()) >= 0)
                    {
                        bytes.Add((byte)next); int n = bytes.Count;
                        if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10) break;
                    }
                    if (bytes.Count == 8192) { Send(stream, ApiResponse.Error(431, "headers_too_large", "Headers exceed 8 KiB."), null); return; }
                    string request = Encoding.ASCII.GetString(bytes.ToArray());
                    if (!request.EndsWith("\r\n\r\n", StringComparison.Ordinal)) return;
                    string[] lines = request.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string[] first = lines[0].Split(' ');
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    bool valid = first.Length == 3 && (first[2] == "HTTP/1.1" || first[2] == "HTTP/1.0");
                    foreach (string line in lines.Skip(1).Where(l => l.Length != 0))
                    {
                        int colon = line.IndexOf(':');
                        if (colon <= 0 || char.IsWhiteSpace(line[0]) || headers.ContainsKey(line.Substring(0, Math.Max(0, colon)))) { valid = false; break; }
                        headers.Add(line.Substring(0, colon), line.Substring(colon + 1).Trim());
                    }
                    if (!valid || !headers.TryGetValue("Host", out var host) || (host != "127.0.0.1:" + Port && host != "localhost:" + Port))
                    { Send(stream, ApiResponse.Error(400, "invalid_request", "Use the localhost API address."), null); return; }
                    headers.TryGetValue("Origin", out var origin);
                    if (origin != null && !LocalOrigin(origin))
                    { Send(stream, ApiResponse.Error(403, "origin_denied", "Serve the client from localhost."), null); return; }
                    if (headers.ContainsKey("Transfer-Encoding") || headers.TryGetValue("Content-Length", out var length) && length != "0")
                    {
                        // Drain a small declared body before closing, so Windows does not reset
                        // the socket and discard the error response while unread bytes remain.
                        if (headers.TryGetValue("Content-Length", out var declared) && int.TryParse(declared, out int remaining) && remaining > 0 && remaining <= 65536)
                        {
                            var discard = new byte[1024];
                            while (remaining > 0) { int read = stream.Read(discard, 0, Math.Min(remaining, discard.Length)); if (read == 0) break; remaining -= read; }
                        }
                        Send(stream, ApiResponse.Error(400, "unexpected_body", "This API accepts no request bodies."), origin); return;
                    }
                    ApiResponse response;
                    if (first[0] == "OPTIONS")
                    {
                        if (headers.TryGetValue("Access-Control-Request-Method", out var requested) && requested != "GET")
                            response = ApiResponse.Error(405, "read_only", "Only GET is supported.");
                        else response = new ApiResponse { Status = 204, Body = "" };
                    }
                    else
                    {
                        headers.TryGetValue("If-None-Match", out var tag);
                        response = router.Route(first[0], first[1], tag);
                    }
                    Send(stream, response, origin);
                }
            }
            catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException) { }
            finally { client.Dispose(); lock (active) active.Remove(client); clients.Release(); }
        }
        static void Send(NetworkStream stream, ApiResponse response, string origin)
        {
            byte[] body = response.BodyBytes ?? Encoding.UTF8.GetBytes(response.Body ?? "");
            string reason = response.Status == 200 ? "OK" : response.Status == 204 ? "No Content" : response.Status == 304 ? "Not Modified" : "API Error";
            var header = new StringBuilder("HTTP/1.1 " + response.Status + " " + reason + "\r\nConnection: close\r\nCache-Control: no-cache\r\nX-Content-Type-Options: nosniff\r\n");
            header.Append("Content-Type: ").Append(response.ContentType).Append("\r\nContent-Length: ").Append(body.Length).Append("\r\n");
            if (response.ETag != null) header.Append("ETag: ").Append(response.ETag).Append("\r\n");
            if (origin != null) header.Append("Access-Control-Allow-Origin: ").Append(origin).Append("\r\nVary: Origin\r\nAccess-Control-Allow-Methods: GET, OPTIONS\r\nAccess-Control-Allow-Headers: If-None-Match\r\nAccess-Control-Expose-Headers: ETag\r\n");
            foreach (var pair in response.Headers) header.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
            header.Append("\r\n"); byte[] head = Encoding.ASCII.GetBytes(header.ToString());
            stream.Write(head, 0, head.Length); stream.Write(body, 0, body.Length);
        }
        public void Dispose()
        {
            stopping = true; listener?.Stop();
            lock (active) foreach (var client in active) client.Close();
        }
    }
}
