using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace VibeWars.Web;

public record DebateEvent(string Type, int Round, string Content, string? BotName);

public class WebDashboardService
{
    private const int MaxQueuedEvents = 500;
    private readonly int _port;
    private HttpListener? _listener;

    // Append-only event log; clients track their own read position via an absolute index.
    private readonly List<DebateEvent> _eventLog = [];
    private int _eventBase; // absolute index of _eventLog[0]
    private readonly object _eventLock = new();

    private object _status = new { status = "idle" };
    private readonly CancellationTokenSource _cts = new();

    // Live audience voting
    private readonly List<(int Round, string Winner)> _votes = [];
    private readonly object _voteLock = new();

    public WebDashboardService(int port = 5050)
    {
        _port = port;
    }

    public int Port => _port;

    public async Task StartAsync(CancellationToken ct = default)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{_port}/");
        _listener.Start();

        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = HandleRequestAsync(context);
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        }, ct);

        await Task.CompletedTask;
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var method = context.Request.HttpMethod;

        try
        {
            if (path == "/" && method == "GET")
            {
                var html = GetEmbeddedDashboardHtml();
                var bytes = Encoding.UTF8.GetBytes(html);
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            else if (path == "/events" && method == "GET")
            {
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.Add("Cache-Control", "no-cache");
                context.Response.Headers.Add("Connection", "keep-alive");

                await using var writer = new StreamWriter(context.Response.OutputStream, leaveOpen: true);

                // Each client tracks its own absolute read position so that
                // multiple simultaneous connections each receive every event.
                int clientAbsIndex;
                lock (_eventLock)
                    clientAbsIndex = _eventBase + _eventLog.Count;

                while (!_cts.IsCancellationRequested)
                {
                    List<DebateEvent> pending;
                    lock (_eventLock)
                    {
                        var startOffset = Math.Max(clientAbsIndex - _eventBase, 0);
                        pending = _eventLog.Skip(startOffset).ToList();
                        clientAbsIndex = _eventBase + _eventLog.Count;
                    }

                    foreach (var evt in pending)
                    {
                        var json = JsonConvert.SerializeObject(evt, new JsonSerializerSettings
                        {
                            ContractResolver = new DefaultContractResolver
                            {
                                NamingStrategy = new CamelCaseNamingStrategy()
                            }
                        });
                        await writer.WriteAsync($"data: {json}\n\n");
                    }

                    if (pending.Count > 0)
                        await writer.FlushAsync();

                    await Task.Delay(500);
                }
            }
            else if (path == "/status" && method == "GET")
            {
                var json = JsonConvert.SerializeObject(_status, new JsonSerializerSettings
                {
                    ContractResolver = new DefaultContractResolver
                    {
                        NamingStrategy = new CamelCaseNamingStrategy()
                    }
                });
                var bytes = Encoding.UTF8.GetBytes(json);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            else if (path == "/topic" && method == "POST")
            {
                using var reader = new StreamReader(context.Request.InputStream);
                await reader.ReadToEndAsync();
                context.Response.StatusCode = 200;
            }
            else if (path == "/vote" && method == "POST")
            {
                using var reader = new StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    var round = doc.RootElement.TryGetProperty("round", out var r) ? r.GetInt32() : 0;
                    var winner = doc.RootElement.TryGetProperty("winner", out var w) ? w.GetString() ?? "" : "";
                    if (round > 0 && !string.IsNullOrEmpty(winner))
                    {
                        lock (_voteLock) { _votes.Add((round, winner)); }
                        context.Response.StatusCode = 200;
                    }
                    else context.Response.StatusCode = 400;
                }
                catch { context.Response.StatusCode = 400; }
            }
            else if (path == "/votes" && method == "GET")
            {
                List<(int Round, string Winner)> snapshot;
                lock (_voteLock) { snapshot = [.. _votes]; }
                var voteJson = System.Text.Json.JsonSerializer.Serialize(
                    snapshot.GroupBy(v => v.Round).Select(g => new
                    {
                        round = g.Key,
                        botA = g.Count(v => v.Winner.Contains("A", StringComparison.OrdinalIgnoreCase)),
                        botB = g.Count(v => v.Winner.Contains("B", StringComparison.OrdinalIgnoreCase)),
                        total = g.Count()
                    }));
                var bytes = Encoding.UTF8.GetBytes(voteJson);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            else
            {
                context.Response.StatusCode = 404;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WebDashboard] Request handler error: {ex.Message}"); }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    public void PublishEvent(DebateEvent evt)
    {
        lock (_eventLock)
        {
            _eventLog.Add(evt);
            // Trim oldest events to prevent unbounded memory growth; bump the base
            // offset so existing clients' absolute indices remain valid.
            while (_eventLog.Count > MaxQueuedEvents)
            {
                _eventLog.RemoveAt(0);
                _eventBase++;
            }
        }
    }

    public void SetStatus(object statusObj)
    {
        _status = statusObj;
    }

    public string GetEmbeddedDashboardHtml() => """
        <!DOCTYPE html>
        <html lang="en">
        <head><meta charset="UTF-8"><title>VibeWars Live Dashboard</title>
        <style>
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body { font-family: -apple-system, sans-serif; background: #0f0f0f; color: #e0e0e0; }
        header { background: #1a1a2e; padding: 16px 24px; border-bottom: 1px solid #333; }
        header h1 { color: #e94560; font-size: 1.4rem; }
        .container { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; padding: 16px; max-width: 1200px; margin: 0 auto; }
        .scoreboard { background: #141414; border: 1px solid #333; border-radius: 8px; padding: 16px; grid-column: span 2; }
        .bot-col { background: #141414; border: 1px solid #333; border-radius: 8px; padding: 16px; }
        .bot-a-col { border-left: 3px solid #4a9eff; }
        .bot-b-col { border-left: 3px solid #4aff8a; }
        .col-header { font-size: 0.9rem; font-weight: 700; text-transform: uppercase; margin-bottom: 12px; }
        .bot-a-col .col-header { color: #4a9eff; }
        .bot-b-col .col-header { color: #4aff8a; }
        .message { background: #1a1a1a; border-radius: 4px; padding: 10px; margin-bottom: 8px; font-size: 0.85rem; }
        #status { font-size: 0.85rem; color: #aaa; margin-top: 4px; }
        </style>
        </head>
        <body>
        <header>
          <h1>⚔ VibeWars Live Dashboard</h1>
          <div id="status">Connecting...</div>
        </header>
        <div class="container">
          <div class="scoreboard">
            <strong>Scoreboard</strong>
            <span id="score-a" style="color:#4a9eff; margin-left:16px;">Bot A: 0</span>
            <span id="score-b" style="color:#4aff8a; margin-left:16px;">Bot B: 0</span>
            <span id="round-display" style="color:#aaa; margin-left:16px;">Round: —</span>
          </div>
          <div class="bot-col bot-a-col"><div class="col-header">Bot A</div><div id="msgs-a"></div></div>
          <div class="bot-col bot-b-col"><div class="col-header">Bot B</div><div id="msgs-b"></div></div>
        </div>
        <script>
        var scoreA = 0, scoreB = 0;
        var es = new EventSource('/events');
        es.onopen = function() { document.getElementById('status').textContent = 'Connected'; };
        es.onerror = function() { document.getElementById('status').textContent = 'Disconnected'; };
        es.onmessage = function(e) {
          var evt = JSON.parse(e.data);
          if (evt.round) document.getElementById('round-display').textContent = 'Round: ' + evt.round;
          var msg = document.createElement('div');
          msg.className = 'message';
          msg.textContent = (evt.botName || 'System') + ': ' + evt.content;
          if (evt.botName === 'Bot A') document.getElementById('msgs-a').prepend(msg);
          else if (evt.botName === 'Bot B') document.getElementById('msgs-b').prepend(msg);
          if (evt.type === 'round-result') {
            if (evt.content && evt.content.includes('Bot A')) { scoreA++; document.getElementById('score-a').textContent = 'Bot A: ' + scoreA; }
            else if (evt.content && evt.content.includes('Bot B')) { scoreB++; document.getElementById('score-b').textContent = 'Bot B: ' + scoreB; }
          }
        };
        </script>
        </body>
        </html>
        """;

    public async Task StopAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        _listener?.Close();
        await Task.CompletedTask;
    }
}
