using System.Text;
using System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Amazon.S3;
using VibeWars;
using VibeWars.Analytics;
using VibeWars.Arbiter;
using VibeWars.ArgumentGraph;
using VibeWars.Clients;
using VibeWars.Config;
using VibeWars.Drift;
using VibeWars.FollowUp;
using VibeWars.HiddenObjective;
using VibeWars.HumanPlayer;
using VibeWars.JudgePanel;
using VibeWars.Memory;
using VibeWars.Models;
using VibeWars.Personas;
using VibeWars.Reflection;
using VibeWars.Replay;
using VibeWars.Reports;
using VibeWars.StanceTracker;
using VibeWars.Strategy;
using VibeWars.Tournament;
using VibeWars.Web;
using VibeWars.Webhook;

// ─── Configuration ────────────────────────────────────────────────────────────

var config = ConfigLoader.Load(args);

var openRouterKey       = config.OpenRouterApiKey;
var awsRegion           = config.AwsRegion;
var maxRounds           = config.MaxRounds;
var memoryBackend       = config.MemoryBackend;
var memoryContextTokens = config.MemoryContextTokens;
var memoryTopK          = config.MemoryTopK;
var summarizeThreshold  = config.SummarizeThreshold;
var noMemory            = config.NoMemory;
var debateFormat        = DebateFormatHelper.Parse(config.DebateFormat);

var botAModel  = config.BotAModel  ?? (openRouterKey is not null ? "openai/gpt-4o-mini" : "amazon.nova-lite-v1:0");
var botBModel  = config.BotBModel  ?? "amazon.nova-lite-v1:0";
var judgeModel = config.JudgeModel ?? (openRouterKey is not null ? "openai/gpt-4o-mini" : "amazon.nova-lite-v1:0");

// ─── Helpers ──────────────────────────────────────────────────────────────────

static IChatClient CreateClient(string provider, string model, string? openRouterKey, string awsRegion)
{
    return provider.ToLowerInvariant() switch
    {
        "openrouter" when openRouterKey is not null => new OpenRouterClient(openRouterKey, model),
        "bedrock"                                    => new BedrockClient(model, awsRegion),
        _ when openRouterKey is not null             => new OpenRouterClient(openRouterKey, model),
        _                                            => new BedrockClient(model, awsRegion)
    };
}

static string Separator(char ch = '─', int? overrideWidth = null)
{
    int width;
    try { width = Math.Min(overrideWidth ?? Console.WindowWidth, 100); }
    catch { width = 100; }
    return $"{Ansi.Dim}{new string(ch, width)}{Ansi.Reset}";
}

static string Wrap(string text, int indent = 4)
{
    int width;
    try { width = Math.Min(Console.WindowWidth, 100) - indent; }
    catch { width = 96; }

    var prefix = new string(' ', indent);
    var words = text.Split(' ');
    var lines = new List<string>();
    var current = new System.Text.StringBuilder();

    foreach (var word in words)
    {
        if (current.Length > 0 && current.Length + 1 + word.Length > width)
        {
            lines.Add(prefix + current.ToString());
            current.Clear();
        }
        if (current.Length > 0) current.Append(' ');
        current.Append(word);
    }
    if (current.Length > 0)
        lines.Add(prefix + current.ToString());

    return string.Join('\n', lines);
}

static void PrintMessage(string botName, string color, string text)
{
    Console.WriteLine($"\n{color}{Ansi.Bold}[{botName}]{Ansi.Reset}");
    Console.WriteLine(Wrap(text));
}

static async Task<(string Reply, TokenUsage Usage)> StreamReplyAsync(
    IChatClient client, string systemPrompt, IReadOnlyList<ChatMessage> history, CancellationToken ct)
{
    var sb = new StringBuilder();
    Console.WriteLine();
    await foreach (var chunk in client.ChatStreamAsync(systemPrompt, history, ct))
    {
        Console.Write(chunk);
        sb.Append(chunk);
    }
    Console.WriteLine();
    var reply = sb.ToString();
    // NOTE: SSE streaming does not return token counts, so cost tracking is unavailable when streaming.
    // Use --no-stream if accurate token/cost accounting is required.
    return (reply, new TokenUsage(0, 0, 0, null));
}

static JudgeVerdict ParseJudgeVerdict(string raw)
{
    try
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var winner    = root.TryGetProperty("winner",    out var w)  ? w.GetString()  ?? "Tie" : "Tie";
        var reasoning = root.TryGetProperty("reasoning", out var rs) ? rs.GetString() ?? raw   : raw;
        var newIdeas  = root.TryGetProperty("new_ideas", out var ni) ? ni.GetString() ?? ""    : "";
        return new JudgeVerdict(winner, reasoning, newIdeas);
    }
    catch
    {
        var winner    = "Tie";
        var reasoning = raw;
        var newIdeas  = string.Empty;

        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Winner:", StringComparison.OrdinalIgnoreCase))
                winner = trimmed[7..].Trim();
            else if (trimmed.StartsWith("Reasoning:", StringComparison.OrdinalIgnoreCase))
                reasoning = trimmed[10..].Trim();
            else if (trimmed.StartsWith("New ideas:", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.StartsWith("New idea:", StringComparison.OrdinalIgnoreCase))
                newIdeas = trimmed.Contains(':') ? trimmed[(trimmed.IndexOf(':') + 1)..].Trim() : trimmed;
        }
        return new JudgeVerdict(winner, reasoning, newIdeas);
    }
}

// ─── SQLite extraction helper ──────────────────────────────────────────────────

static SqliteMemoryStore? AsSqlite(IMemoryStore? store) => store switch
{
    SqliteMemoryStore sqlite => sqlite,
    HybridMemoryStore hybrid => hybrid.SqliteStore,
    _ => null
};

// ─── Memory store factory ──────────────────────────────────────────────────────

IMemoryStore CreateMemoryStore(string backend) => backend.ToLowerInvariant() switch
{
    "s3"     => new S3MemoryStore(new AmazonS3Client(Amazon.RegionEndpoint.GetBySystemName(awsRegion)),
                    bucket: config.S3Bucket, prefix: config.S3Prefix),
    "hybrid" => new HybridMemoryStore(
                    new SqliteMemoryStore(config.DbPath),
                    new S3MemoryStore(new AmazonS3Client(Amazon.RegionEndpoint.GetBySystemName(awsRegion)),
                        bucket: config.S3Bucket, prefix: config.S3Prefix)),
    _        => new SqliteMemoryStore(config.DbPath),
};

// ─── Memory context builder ────────────────────────────────────────────────────

static string BuildPriorKnowledgeBlock(IReadOnlyList<MemoryEntry> memories, int tokenBudget)
{
    if (memories.Count == 0) return string.Empty;

    var sb = new StringBuilder();
    sb.AppendLine("Prior knowledge from past debates on related topics:");

    var remaining = tokenBudget * 4;
    foreach (var m in memories)
    {
        var line = $"- [{m.Timestamp:yyyy-MM-dd}] {m.BotName} on \"{m.Topic}\": \"{m.Content}\"";
        if (sb.Length + line.Length > remaining) break;
        sb.AppendLine(line);
    }

    return sb.ToString();
}

// ─── CLI memory sub-commands ───────────────────────────────────────────────────

static async Task RunMemoryCommand(string[] subArgs, IMemoryStore store, IEmbeddingClient? embeddingClientForMemory = null)
{
    if (subArgs.Length == 0)
    {
        PrintMemoryUsage();
        return;
    }

    var sub = subArgs[0].ToLowerInvariant();

    switch (sub)
    {
        case "list":
        {
            var n = subArgs.Length > 1 && int.TryParse(subArgs[1], out var lim) ? lim : 10;
            var sessions = await store.ListSessionsAsync(n);
            if (sessions.Count == 0) { Console.WriteLine("No sessions stored."); return; }
            Console.WriteLine($"\n{"SessionId",-38}  {"Topic",-30}  {"Date",-12}  {"Winner",-10}  {"Format",-14}");
            Console.WriteLine(new string('─', 110));
            foreach (var s in sessions)
                Console.WriteLine($"{s.SessionId,-38}  {Truncate(s.Topic, 30),-30}  {s.StartedAt:yyyy-MM-dd}  {s.OverallWinner,-10}  {s.Format,-14}");
            Console.WriteLine();
            break;
        }

        case "show":
        {
            if (subArgs.Length < 2 || !Guid.TryParse(subArgs[1], out var sid))
            {
                Console.WriteLine("Usage: memory show <sessionId>");
                return;
            }
            var entries = await store.GetSessionEntriesAsync(sid);
            if (entries.Count == 0) { Console.WriteLine("No entries found for that session."); return; }
            foreach (var e in entries)
            {
                Console.WriteLine($"\n[Round {e.Round}] {e.BotName} ({e.Role}) @ {e.Timestamp:yyyy-MM-dd HH:mm}");
                Console.WriteLine(Wrap(e.Content, 2));
                if (e.Tags.Length > 0)
                    Console.WriteLine($"  Tags: {string.Join(", ", e.Tags)}");
            }
            Console.WriteLine();
            break;
        }

        case "search":
        {
            if (subArgs.Length < 2) { Console.WriteLine("Usage: memory search <query>"); return; }
            var query = string.Join(' ', subArgs[1..]);
            var entries = await store.SearchAsync(query);
            if (entries.Count == 0) { Console.WriteLine("No matching entries found."); return; }
            foreach (var e in entries)
            {
                Console.WriteLine($"\n[{e.Timestamp:yyyy-MM-dd}] {e.BotName} | Topic: {e.Topic} | Round {e.Round}");
                Console.WriteLine(Wrap(e.Content, 2));
            }
            Console.WriteLine();
            break;
        }

        case "export":
        {
            if (subArgs.Length < 2 || !Guid.TryParse(subArgs[1], out var sid))
            {
                Console.WriteLine("Usage: memory export <sessionId> [--format json|csv]");
                return;
            }
            var format = "json";
            for (var i = 2; i < subArgs.Length - 1; i++)
                if (subArgs[i] == "--format") format = subArgs[i + 1].ToLowerInvariant();

            var entries = await store.GetSessionEntriesAsync(sid);
            if (format == "csv")
            {
                Console.WriteLine("Id,BotName,Topic,Round,Role,Content,Timestamp,Tags");
                foreach (var e in entries)
                    Console.WriteLine($"{e.Id},{CsvEscape(e.BotName)},{CsvEscape(e.Topic)},{e.Round},{e.Role},{CsvEscape(e.Content)},{e.Timestamp:O},{CsvEscape(string.Join(';', e.Tags))}");
            }
            else
            {
                var settings = new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() }
                };
                Console.WriteLine(JsonConvert.SerializeObject(entries, settings));
            }
            break;
        }

        case "clear":
        {
            if (!subArgs.Contains("--confirm"))
            {
                Console.WriteLine("This will delete ALL stored memories. Re-run with --confirm to proceed.");
                return;
            }
            var sqliteClear = AsSqlite(store);
            if (sqliteClear != null)
            {
                sqliteClear.ClearAll();
                Console.WriteLine("✔ All memories cleared.");
            }
            else
            {
                Console.WriteLine("clear is only supported for the sqlite or hybrid backend.");
            }
            break;
        }

        case "summarize":
        {
            Console.WriteLine("Usage: memory summarize <topic>  (triggered automatically above the threshold)");
            break;
        }

        case "stance":
        {
            if (subArgs.Length < 2) { Console.WriteLine("Usage: memory stance <topic>"); return; }
            var query = string.Join(' ', subArgs[1..]);
            var stanceEntries = await store.SearchAsync(query);
            var relevant = stanceEntries.Where(e => e.Role == "stance").ToList();
            if (relevant.Count == 0) { Console.WriteLine("No stance entries found."); return; }
            Console.WriteLine($"\nStance entries for: {query}");
            foreach (var e in relevant)
                Console.WriteLine($"  [{e.Timestamp:yyyy-MM-dd}] {e.BotName}: {e.Content}");
            Console.WriteLine();
            break;
        }

        case "reindex":
        {
            var sqliteForReindex = AsSqlite(store);
            if (sqliteForReindex is null)
            {
                Console.WriteLine("reindex is only supported for the sqlite or hybrid backend.");
                return;
            }
            if (embeddingClientForMemory is null)
            {
                Console.WriteLine("No embedding backend configured. Set VIBEWARS_EMBED_BACKEND=openrouter or bedrock.");
                return;
            }
            var pending = await sqliteForReindex.GetEntriesWithoutEmbeddingsAsync();
            Console.WriteLine($"Found {pending.Count} entries without embeddings.");
            var batches = pending.Chunk(20);
            var total = 0;
            foreach (var batch in batches)
            {
                var texts = batch.Select(e => e.Content).ToArray();
                var embeddings = await embeddingClientForMemory.EmbedBatchAsync(texts);
                for (var i = 0; i < batch.Length; i++)
                    await sqliteForReindex.SaveEmbeddingAsync(batch[i].Id, embeddings[i]);
                total += batch.Length;
                Console.Write($"\r  Reindexed {total}/{pending.Count} entries...");
            }
            Console.WriteLine($"\n✔ Reindexed {total} entries.");
            break;
        }

        case "report":
        {
            if (subArgs.Length < 2 || !Guid.TryParse(subArgs[1], out var sid))
            {
                Console.WriteLine("Usage: memory report <sessionId> [--format html|md|json|podcast] [--out <path>]");
                return;
            }
            var format = "md";
            string? outPath = null;
            for (var i = 2; i < subArgs.Length - 1; i++)
            {
                if (subArgs[i] == "--format") format = subArgs[i + 1].ToLowerInvariant();
                if (subArgs[i] == "--out")    outPath = subArgs[i + 1];
            }

            var sessions = await store.ListSessionsAsync(1000);
            var session  = sessions.FirstOrDefault(s => s.SessionId == sid);
            if (session is null) { Console.WriteLine("Session not found."); return; }

            var entries = await store.GetSessionEntriesAsync(sid);
            string output;
            if (format == "html")
                output = VibeWars.Reports.DebateReportGenerator.GenerateHtml(session, entries);
            else if (format == "json")
            {
                var settings = new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() }
                };
                output = JsonConvert.SerializeObject(new { session, entries }, settings);
            }
            else if (format == "podcast")
                output = VibeWars.Reports.DebateReportGenerator.GeneratePodcast(session, entries);
            else
                output = VibeWars.Reports.DebateReportGenerator.GenerateMarkdown(session, entries);

            if (outPath != null) { File.WriteAllText(outPath, output); Console.WriteLine($"✔ Report written to {outPath}"); }
            else Console.Write(output);
            break;
        }

        case "graph":
        {
            if (subArgs.Length < 2 || !Guid.TryParse(subArgs[1], out var sid))
            {
                Console.WriteLine("Usage: memory graph <sessionId> [--format mermaid|dot]");
                return;
            }
            var fmt = "mermaid";
            for (var i = 2; i < subArgs.Length - 1; i++)
                if (subArgs[i] == "--format") fmt = subArgs[i + 1].ToLowerInvariant();

            var entries = await store.GetSessionEntriesAsync(sid);
            var nodeEntries = entries.Where(e => e.Role == "argument-node").ToList();
            if (nodeEntries.Count == 0)
            {
                Console.WriteLine("No argument graph data for this session. Run with --argument-graph to capture.");
                return;
            }

            var nodes = nodeEntries.Select(e => {
                var parts = e.Content.Split('|', 2);
                var claimType = Enum.TryParse<ClaimType>(parts[0], out var ct) ? ct : ClaimType.Assertion;
                return new ArgumentNode(e.Id, sid, e.Round, e.BotName, parts.Length > 1 ? parts[1] : e.Content, claimType);
            }).ToList();

            if (fmt == "dot")
                Console.Write(ArgumentGraphService.ToDot(nodes, []));
            else
                Console.Write(ArgumentGraphService.ToMermaid(nodes, []));
            break;
        }

        case "graph-stats":
        {
            if (subArgs.Length < 2 || !Guid.TryParse(subArgs[1], out var sid))
            {
                Console.WriteLine("Usage: memory graph-stats <sessionId>");
                return;
            }
            var entries = await store.GetSessionEntriesAsync(sid);
            var nodeEntries = entries.Where(e => e.Role == "argument-node").ToList();
            if (nodeEntries.Count == 0)
            {
                Console.WriteLine("No argument graph data. Run with --argument-graph to capture.");
                return;
            }
            var nodes = nodeEntries.Select(e => {
                var parts = e.Content.Split('|', 2);
                var claimType = Enum.TryParse<ClaimType>(parts[0], out var ct2) ? ct2 : ClaimType.Assertion;
                return new ArgumentNode(e.Id, sid, e.Round, e.BotName, parts.Length > 1 ? parts[1] : e.Content, claimType);
            }).ToList();
            var (total, rebuttalRate, concessions, mostChallenged) = ArgumentGraphService.ComputeStats(nodes, []);
            Console.WriteLine($"\nArgument Graph Stats for session {sid}:");
            Console.WriteLine($"  Total claims:  {total}");
            Console.WriteLine($"  Rebuttal rate: {rebuttalRate:P0}");
            foreach (var (bot, count) in concessions)
                Console.WriteLine($"  {bot} concessions: {count}");
            if (mostChallenged != null)
                Console.WriteLine($"  Most challenged: \"{mostChallenged.ClaimText[..Math.Min(60, mostChallenged.ClaimText.Length)]}\"");
            Console.WriteLine();
            break;
        }

        case "follow-ups":
        {
            if (subArgs.Length < 2)
            {
                Console.WriteLine("Usage: memory follow-ups <topic>");
                return;
            }
            var topic = string.Join(' ', subArgs[1..]);
            // Fetch a larger set of entries to avoid missing older follow-ups
            var allEntries = await store.SearchAsync(topic, topK: 1000);
            var followUpEntries = allEntries.Where(e => e.Role == "follow-up").ToList();
            if (followUpEntries.Count == 0) { Console.WriteLine("No follow-up entries found for that topic."); return; }
            var allTopics = followUpEntries
                .Select(e => FollowUpService.ParseFollowUps(e.Content))
                .SelectMany(t => t)
                .ToList();
            // Sort by recurrence — topics suggested more often float to the top
            var sortedTopics = FollowUpService.SortByRecurrence(allTopics, allTopics);
            Console.WriteLine(FollowUpService.FormatFollowUpDisplay(sortedTopics));
            break;
        }

        case "drift":
        {
            if (subArgs.Length < 2) { Console.WriteLine("Usage: memory drift <topic>"); return; }
            var topic = string.Join(' ', subArgs[1..]);
            var sqliteForDrift = AsSqlite(store);
            if (sqliteForDrift is null)
            {
                Console.WriteLine("drift sub-command is only supported for the sqlite or hybrid backend.");
                return;
            }
            var driftSvc = new OpinionDriftService(sqliteForDrift.GetConnection());
            var records = await driftSvc.GetDriftRecordsAsync(topic);
            if (records.Count == 0)
            {
                Console.WriteLine("No drift records found. Run debates with --stance-tracking to accumulate cross-session stance data.");
                return;
            }
            Console.WriteLine($"\nOpinion Drift for topic: \"{topic}\"");
            foreach (var botGroup in records.GroupBy(r => r.BotName))
            {
                Console.WriteLine($"\n[{botGroup.Key}]");
                Console.WriteLine(OpinionDriftService.RenderDriftTimeline(botGroup.ToList()));
            }
            Console.WriteLine();
            break;
        }

        case "drift-compare":
        {
            if (subArgs.Length < 4) { Console.WriteLine("Usage: memory drift-compare <topic> <model-a> <model-b>"); return; }
            var topic  = string.Join(' ', subArgs[1..^2]);
            var modelA = subArgs[^2];
            var modelB = subArgs[^1];
            var sqliteForDriftCmp = AsSqlite(store);
            if (sqliteForDriftCmp is null)
            {
                Console.WriteLine("drift-compare sub-command is only supported for the sqlite or hybrid backend.");
                return;
            }
            var driftSvcCmp = new OpinionDriftService(sqliteForDriftCmp.GetConnection());
            var allRecords = await driftSvcCmp.GetDriftRecordsAsync(topic);
            if (allRecords.Count == 0)
            {
                Console.WriteLine("No drift records found. Run debates with --stance-tracking to accumulate cross-session stance data.");
                return;
            }
            var recordsA = allRecords.Where(r => r.Model.Contains(modelA, StringComparison.OrdinalIgnoreCase) ||
                                                  r.BotName.Contains(modelA, StringComparison.OrdinalIgnoreCase)).ToList();
            var recordsB = allRecords.Where(r => r.Model.Contains(modelB, StringComparison.OrdinalIgnoreCase) ||
                                                  r.BotName.Contains(modelB, StringComparison.OrdinalIgnoreCase)).ToList();
            var velA = OpinionDriftService.ComputeDriftVelocity(recordsA);
            var velB = OpinionDriftService.ComputeDriftVelocity(recordsB);
            Console.WriteLine($"\nDrift Comparison — topic: \"{topic}\"");
            Console.WriteLine($"  {modelA}: {recordsA.Count} sessions  velocity: {velA:+0.00;-0.00;0.00}  trend: {OpinionDriftService.ClassifyTrend(velA)}");
            Console.WriteLine($"  {modelB}: {recordsB.Count} sessions  velocity: {velB:+0.00;-0.00;0.00}  trend: {OpinionDriftService.ClassifyTrend(velB)}");
            Console.WriteLine();
            break;
        }

        case "analytics":
        {
            if (subArgs.Length < 2 || !Guid.TryParse(subArgs[1], out var sid))
            {
                Console.WriteLine("Usage: memory analytics <sessionId>");
                return;
            }
            var entries = await store.GetSessionEntriesAsync(sid);
            var scoreEntries = entries.Where(e => e.Role == "strength-score").ToList();
            if (scoreEntries.Count == 0)
            {
                Console.WriteLine("No analytics data for this session. Run with --analytics to capture.");
                return;
            }
            var scores = scoreEntries.Select(e => {
                var parts = e.Content.Split('|');
                if (parts.Length >= 4 &&
                    double.TryParse(parts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var rigor) &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var novelty) &&
                    double.TryParse(parts[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var persuasion) &&
                    double.TryParse(parts[3], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var composite))
                    return new ArgumentStrengthScore(e.Round, e.BotName, rigor, novelty, persuasion, composite);
                return ArgumentStrengthScore.Default(e.Round, e.BotName);
            }).ToList();
            Console.WriteLine($"\nAnalytics for session {sid}:\n");
            Console.Write(HeatmapRenderer.RenderHeatmap(scores));
            Console.WriteLine();
            break;
        }

        case "reflections":
        {
            if (subArgs.Length < 2 || !Guid.TryParse(subArgs[1], out var sid))
            {
                Console.WriteLine("Usage: memory reflections <sessionId>");
                return;
            }
            var entries = await store.GetSessionEntriesAsync(sid);
            var reflectionEntries = entries.Where(e => e.Role == "reflection").OrderBy(e => e.Round).ThenBy(e => e.BotName).ToList();
            if (reflectionEntries.Count == 0)
            {
                Console.WriteLine("No reflection entries. Run with --reflect to capture.");
                return;
            }
            Console.WriteLine($"\nSelf-Reflection Timeline for session {sid}:\n");
            foreach (var e in reflectionEntries)
                Console.WriteLine(e.Content);
            Console.WriteLine();
            break;
        }

        case "strategies":
        {
            if (subArgs.Length < 2) { Console.WriteLine("Usage: memory strategies <contestant-id>"); return; }
            var contestantId = subArgs[1];
            var sqliteForStrategy = AsSqlite(store);
            if (sqliteForStrategy is null)
            {
                Console.WriteLine("strategies sub-command is only supported for the sqlite or hybrid backend.");
                return;
            }
            var engine = new StrategyEngine(sqliteForStrategy.GetConnection());
            var past = engine.GetPastSuccesses(contestantId);
            if (past.Count == 0) { Console.WriteLine($"No strategy records for contestant '{contestantId}'."); return; }
            var rates = StrategyEngine.GetHistoricalTacticSuccessRates(past);
            Console.WriteLine($"\nTactic Win Rates for: {contestantId}\n");
            Console.WriteLine($"{"Tactic",-30} {"Win Rate",10} {"Uses",6}");
            Console.WriteLine(new string('─', 50));
            foreach (var (tactic, rate) in rates.OrderByDescending(kv => kv.Value))
            {
                var uses = past.Count(r => r.TacticName == tactic);
                Console.WriteLine($"{tactic,-30} {rate,10:P0} {uses,6}");
            }
            Console.WriteLine();
            break;
        }

        case "autopsy":
        {
            if (subArgs.Length < 2 || !Guid.TryParse(subArgs[1], out var sid))
            {
                Console.WriteLine("Usage: memory autopsy <sessionId>");
                return;
            }
            var entries = await store.GetSessionEntriesAsync(sid);
            var nodeEntries = entries.Where(e => e.Role == "argument-node").ToList();
            if (nodeEntries.Count == 0)
            {
                Console.WriteLine("No argument graph data. Run with --argument-graph to capture.");
                return;
            }
            var autopsyNodes = nodeEntries.Select(e => {
                var parts = e.Content.Split('|', 2);
                var claimType = Enum.TryParse<ClaimType>(parts[0], out var ct) ? ct : ClaimType.Assertion;
                return new ArgumentNode(e.Id, sid, e.Round, e.BotName, parts.Length > 1 ? parts[1] : e.Content, claimType);
            }).ToList();
            var lifecycleEntries = entries.Where(e => e.Role == "claim-lifecycle").ToList();
            var lifecycleEvents = lifecycleEntries.Select(e => {
                var parts = e.Content.Split('|', 4);
                if (parts.Length < 4 || !Guid.TryParse(parts[0], out var claimId) ||
                    !Enum.TryParse<ClaimLifecycle>(parts[2], out var status)) return null;
                return new ClaimLifecycleEvent(claimId, e.Round, status, parts[3]);
            }).Where(ev => ev != null).Cast<ClaimLifecycleEvent>().ToList();
            var analyzer = new ClaimSurvivalAnalyzer();
            Console.WriteLine($"\nArgument Autopsy for session {sid}:\n");
            Console.WriteLine(ClaimSurvivalAnalyzer.RenderAutopsy(autopsyNodes, lifecycleEvents));
            Console.WriteLine();
            break;
        }

        case "brief-impact":
        {
            if (subArgs.Length < 2) { Console.WriteLine("Usage: memory brief-impact <topic>"); return; }
            var topic = string.Join(' ', subArgs[1..]);
            var sessions = await store.ListSessionsAsync(500);
            var topicSessions = sessions.Where(s => s.Topic.Contains(topic, StringComparison.OrdinalIgnoreCase)).ToList();
            if (topicSessions.Count == 0) { Console.WriteLine($"No sessions found for topic: {topic}"); return; }
            var briefed   = new List<DebateSession>();
            var unbriefed = new List<DebateSession>();
            foreach (var s in topicSessions)
            {
                var sEntries = await store.GetSessionEntriesAsync(s.SessionId);
                if (sEntries.Any(e => e.Role == "briefing"))
                    briefed.Add(s);
                else
                    unbriefed.Add(s);
            }
            Console.WriteLine($"\nAdversarial Briefing Impact for topic: \"{topic}\"");
            Console.WriteLine($"  Briefed sessions:   {briefed.Count}");
            Console.WriteLine($"  Unbriefed sessions: {unbriefed.Count}");
            if (briefed.Count > 0 && unbriefed.Count > 0)
                Console.WriteLine($"  Hypothesis: briefed sessions produce higher-quality arguments (accumulate more sessions for statistical significance).");
            Console.WriteLine();
            break;
        }

        case "card":
        {
            if (subArgs.Length < 2 || !Guid.TryParse(subArgs[1], out var sid))
            {
                Console.WriteLine("Usage: memory card <sessionId> [--out <path>]");
                return;
            }
            string? outPath = null;
            for (var i = 2; i < subArgs.Length - 1; i++)
                if (subArgs[i] == "--out") outPath = subArgs[i + 1];

            var sessions = await store.ListSessionsAsync(1000);
            var session = sessions.FirstOrDefault(s => s.SessionId == sid);
            if (session is null) { Console.WriteLine("Session not found."); return; }
            var entries = await store.GetSessionEntriesAsync(sid);
            var botAWins = entries.Count(e => e.Tags.Contains("verdict") && e.Content.Contains("Bot A", StringComparison.OrdinalIgnoreCase));
            var botBWins = entries.Count(e => e.Tags.Contains("verdict") && e.Content.Contains("Bot B", StringComparison.OrdinalIgnoreCase));
            var bestArg = entries.Where(e => e.Role == "assistant" && e.BotName is "Bot A" or "Bot B")
                .OrderByDescending(e => e.Content.Length).FirstOrDefault();
            var svg = VibeWars.Reports.DebateCardGenerator.GenerateSvg(session, roundsA: botAWins, roundsB: botBWins,
                highlightQuote: bestArg?.Content);
            if (outPath != null) { File.WriteAllText(outPath, svg); Console.WriteLine($"✔ Card saved to {outPath}"); }
            else Console.Write(svg);
            break;
        }

        default:
            PrintMemoryUsage();
            break;
    }

    static void PrintMemoryUsage()
    {
        Console.WriteLine("""

          Usage: dotnet run -- memory <sub-command>

          Sub-commands:
            list [n]                          Print the last N sessions (default 10)
            show <sessionId>                  Print all entries for a session
            search <query>                    Search memories and print matches
            export <sessionId> [--format json|csv]  Dump a session to stdout
            report <sessionId> [--format html|md|json|podcast] [--out <path>]  Generate a report
            clear [--confirm]                 Delete all stored memories
            graph <sessionId> [--format mermaid|dot]  Render argument graph
            graph-stats <sessionId>           Show argument graph statistics
            follow-ups <topic>                Show stored follow-up topics for a topic
            drift <topic>                     Show stance evolution across sessions for a topic
            drift-compare <topic> <a> <b>     Compare drift trajectories for two bots/models
            analytics <sessionId>             Render argument strength heatmap for a session
            reflections <sessionId>           Show self-reflection timeline for a session
            strategies <contestant-id>        Show tactic win rates for a contestant
            autopsy <sessionId>               Render argument autopsy (claim survival analysis)
            brief-impact <topic>              Compare briefed vs unbriefed session outcomes
            card <sessionId> [--out <path>]   Generate an SVG debate card
        """);
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
    static string CsvEscape(string s) => $"\"{s.Replace("\"", "\"\"").Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ")}\"";
}

// ─── Sub-commands: persona, config ────────────────────────────────────────────

if (args.Length > 0 && args[0].Equals("persona", StringComparison.OrdinalIgnoreCase))
{
    var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
    if (sub == "list")
    {
        Console.WriteLine($"\n{Ansi.Bold}Available Personas:{Ansi.Reset}\n");
        foreach (var p in PersonaLibrary.ListAll())
        {
            Console.WriteLine($"  {Ansi.Bold}{p.Name,-20}{Ansi.Reset}  [{p.Archetype}]");
            Console.WriteLine($"    {Ansi.Dim}{p.StyleDescription}{Ansi.Reset}");
            Console.WriteLine($"    ✅ {p.StrengthBias}   ⚠️  {p.WeaknessBias}");
            Console.WriteLine();
        }
    }
    return;
}

if (args.Length > 0 && args[0].Equals("config", StringComparison.OrdinalIgnoreCase))
{
    var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "help";
    switch (sub)
    {
        case "init":
        {
            var path = ConfigLoader.GetConfigPath(args[2..]);
            if (File.Exists(path))
            {
                Console.WriteLine($"Config file already exists at: {path}");
                Console.WriteLine("Delete it first if you want to regenerate.");
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, ConfigLoader.GenerateStarterConfig());
                Console.WriteLine($"✔ Created starter config at: {path}");
            }
            break;
        }
        case "validate":
        {
            var path = ConfigLoader.GetConfigPath(args[2..]);
            if (!File.Exists(path))
            {
                Console.WriteLine($"No config file found at: {path}");
                Console.WriteLine("Run `config init` to create one.");
            }
            else
            {
                Console.WriteLine($"✔ Config file found at: {path}");
                Console.WriteLine($"  BotAProvider:  {config.BotAProvider ?? "(auto)"}");
                Console.WriteLine($"  BotAModel:     {config.BotAModel ?? "(default)"}");
                Console.WriteLine($"  BotBProvider:  {config.BotBProvider ?? "(auto)"}");
                Console.WriteLine($"  BotBModel:     {config.BotBModel ?? "(default)"}");
                Console.WriteLine($"  JudgeProvider: {config.JudgeProvider ?? "(auto)"}");
                Console.WriteLine($"  JudgeModel:    {config.JudgeModel ?? "(default)"}");
                Console.WriteLine($"  MaxRounds:     {config.MaxRounds}");
                Console.WriteLine($"  DebateFormat:  {config.DebateFormat}");
                Console.WriteLine($"  Complexity:    {config.Complexity}");
                Console.WriteLine($"  MemoryBackend: {config.MemoryBackend}");
                Console.WriteLine($"  BotAPersona:   {config.BotAPersona ?? "(default)"}");
                Console.WriteLine($"  BotBPersona:   {config.BotBPersona ?? "(default)"}");
                Console.WriteLine($"  MaxCostUsd:    {config.MaxCostUsd?.ToString("F2") ?? "(unlimited)"}");
                // Feature flags
                var enabled = new List<string>();
                if (config.EloTracking)        enabled.Add("elo");
                if (config.StanceTracking)     enabled.Add("stance");
                if (config.FactCheck)          enabled.Add("fact-check");
                if (config.ArgumentGraph)      enabled.Add("argument-graph");
                if (config.AudienceSimulation) enabled.Add("audience");
                if (config.Commentator)        enabled.Add("commentator");
                if (config.Challenges)         enabled.Add("challenges");
                if (config.Strategy)           enabled.Add("strategy");
                if (config.RedTeam)            enabled.Add("red-team");
                if (config.Reflect)            enabled.Add("reflect");
                if (config.Arbiter)            enabled.Add("arbiter");
                if (config.Brief)              enabled.Add("brief");
                if (config.Analytics)          enabled.Add("analytics");
                if (config.Chain)              enabled.Add($"chain(depth={config.ChainDepth})");
                // Wave 4-6
                if (config.Momentum)           enabled.Add("momentum");
                if (config.PreDebateHype)      enabled.Add("hype");
                if (config.Highlights)         enabled.Add("highlights");
                if (config.StakesMode != "flat") enabled.Add($"stakes({config.StakesMode})");
                if (config.Plan)               enabled.Add("plan");
                if (config.Lookahead)          enabled.Add("lookahead");
                if (config.OpponentModel)      enabled.Add("opponent-model");
                if (config.Balance)            enabled.Add("balance");
                if (config.KnowledgeSource != null) enabled.Add($"knowledge({config.KnowledgeSource})");
                if (config.FallacyCheck)       enabled.Add("fallacy-check");
                if (config.PersonalityEvolution) enabled.Add("personality");
                if (config.DebateCard)         enabled.Add("debate-card");
                Console.WriteLine($"  Features:      {(enabled.Count > 0 ? string.Join(", ", enabled) : "(none)")}");
            }
            break;
        }
        default:
            Console.WriteLine("Usage: dotnet run -- config <init|validate>");
            break;
    }
    return;
}

if (args.Length > 0 && args[0].Equals("elo", StringComparison.OrdinalIgnoreCase))
{
    using var eloStore = new SqliteMemoryStore(config.DbPath);
    var eloSvc = new VibeWars.Elo.EloService(eloStore.GetConnection());
    var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "leaderboard";
    if (sub is "leaderboard" or "list")
    {
        var topN = args.Length > 2 && int.TryParse(args[2], out var n) ? n : 20;
        var records = await eloSvc.GetLeaderboardAsync(topN);
        if (records.Count == 0) { Console.WriteLine("No ELO records yet. Run some debates first."); return; }
        Console.WriteLine($"\n{"#",-4} {"Contestant",-45} {"ELO",7} {"W",4} {"L",4} {"D",4}");
        Console.WriteLine(new string('─', 72));
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var badge = eloSvc.IsUnrated(r) ? " ?" : "";
            Console.WriteLine($"{i + 1,-4} {r.ContestantId,-45} {r.Rating,7:F0}{badge} {r.Wins,4} {r.Losses,4} {r.Draws,4}");
        }
        Console.WriteLine();
    }
    else if (sub == "history" && args.Length > 2)
    {
        var contestantId = args[2];
        var history = await eloSvc.GetRatingHistoryAsync(contestantId);
        if (history.Count == 0) { Console.WriteLine($"No history for '{contestantId}'."); return; }
        Console.WriteLine($"\nELO History for: {contestantId}");
        var sparkline = VibeWars.Elo.EloService.RatingToSparkline(history.Select(h => h.Rating).ToList());
        Console.WriteLine($"  {sparkline}  ({history[0].Rating:F0} → {history[^1].Rating:F0})");
        Console.WriteLine();
    }
    else if (sub == "personality" && args.Length > 2)
    {
        var contestantId = args[2];
        var personalitySvc = new VibeWars.Personality.PersonalityEvolutionService(eloStore.GetConnection());
        var profile = personalitySvc.GetProfile(contestantId);
        Console.WriteLine(VibeWars.Personality.PersonalityEvolutionService.RenderProfile(profile));
    }
    else
    {
        Console.WriteLine("Usage: elo [leaderboard [N] | history <contestant-id> | personality <contestant-id>]");
    }
    return;
}

if (args.Length > 0 && args[0].Equals("tournament", StringComparison.OrdinalIgnoreCase))
{
    await RunTournamentCommand(args[1..], config);
    return;
}

if (args.Length > 0 && args[0].Equals("batch", StringComparison.OrdinalIgnoreCase))
{
    await RunBatchCommand(args[1..], config);
    return;
}

if (args.Length > 0 && args[0].Equals("replay", StringComparison.OrdinalIgnoreCase))
{
    await RunReplayCommand(args[1..], config);
    return;
}

if (args.Length > 0 && args[0].Equals("webhook", StringComparison.OrdinalIgnoreCase))
{
    var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "help";
    if (sub == "test")
    {
        var webhookCfg = WebhookService.LoadFromEnvironment();
        if (config.WebhookUrl != null) webhookCfg.WebhookUrl = config.WebhookUrl;
        if (!string.IsNullOrEmpty(config.WebhookProvider) &&
            Enum.TryParse<WebhookProvider>(config.WebhookProvider, true, out var wp))
            webhookCfg.WebhookProvider = wp;

        using var httpClient = new HttpClient();
        var testWebhookService = new WebhookService(httpClient);
        Console.Write("Testing webhook... ");
        var ok = await testWebhookService.TestWebhookAsync(webhookCfg);
        Console.WriteLine(ok ? "✔ Success" : "✘ Failed (check URL and connectivity)");
    }
    else
    {
        Console.WriteLine("Usage: dotnet run -- webhook test");
    }
    return;
}

// ─── Startup ──────────────────────────────────────────────────────────────────

if (args.Length > 0 && args[0].Equals("memory", StringComparison.OrdinalIgnoreCase))
{
    using var memStore = CreateMemoryStore(memoryBackend);
    using var embeddingClientForMemory = EmbeddingHelper.CreateEmbeddingClient(config.VibewarsEmbedBackend, openRouterKey, awsRegion, config.VibewarsEmbedModel);
    await RunMemoryCommand(args[1..], memStore, embeddingClientForMemory);
    return;
}

// ─── Helper: resolve persona with optional custom description ─────────────────

static BotPersona ResolvePersona(string? name, string? customDesc)
{
    var persona = PersonaLibrary.Resolve(name ?? string.Empty);
    if (customDesc is null) return persona;
    if (name is null)
        return new BotPersona("Custom", PersonaArchetype.Custom, customDesc, "Custom", "Custom");
    return persona with { StyleDescription = customDesc };
}

// Resolve personas
var botAPersona = ResolvePersona(config.BotAPersona, config.BotAPersonaDesc);
var botBPersona = ResolvePersona(config.BotBPersona, config.BotBPersonaDesc);

// Collect topic from non-flag args
var knownFlagsWithValue = new HashSet<string> { "--persona-a", "--persona-b", "--format", "--human", "--think-time", "--config", "--profile", "--bot-a-provider", "--bot-b-provider", "--judge-provider", "--audience-split", "--complexity", "--webhook-url", "--webhook-provider", "--chain-depth", "--hidden-objective-a", "--hidden-objective-b", "--proposal", "--commentator-model", "--commentator-style", "--max-cost-usd", "--stakes", "--knowledge", "--bots" };
var topicParts = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("--"))
    {
        if (knownFlagsWithValue.Contains(args[i])) i++; // skip value
        continue;
    }
    topicParts.Add(args[i]);
}

var useSpectre = !config.NoTui;
var spectreRenderer = useSpectre ? new VibeWars.TUI.SpectreRenderer() : null;

// ─── Web dashboard (Feature 9) ─────────────────────────────────────────────

WebDashboardService? webDashboard = null;
if (config.WebPort.HasValue)
{
    webDashboard = new WebDashboardService(config.WebPort.Value);
    await webDashboard.StartAsync();
    webDashboard.SetStatus(new { status = "waiting", topic = (string?)null });
    Console.WriteLine($"{Ansi.Dim}✔ Web dashboard running at http://localhost:{config.WebPort.Value}/{Ansi.Reset}");

    if (!config.NoBrowser)
    {
        try
        {
            var url = $"http://localhost:{config.WebPort.Value}/";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* browser open is best-effort */ }
    }
}

// ─── Webhook service (Feature 7) ──────────────────────────────────────────

HttpClient? webhookHttpClient = null;
WebhookService? webhookService = null;
WebhookConfig? webhookConfig = null;
if (!string.IsNullOrEmpty(config.WebhookUrl))
{
    webhookHttpClient = new HttpClient();
    webhookService = new WebhookService(webhookHttpClient);
    webhookConfig = new WebhookConfig
    {
        WebhookUrl = config.WebhookUrl,
        WebhookOnComplete = config.WebhookOnComplete,
        WebhookOnRound = config.WebhookOnRound,
    };
    if (Enum.TryParse<WebhookProvider>(config.WebhookProvider, true, out var wp))
        webhookConfig.WebhookProvider = wp;
}

using IChatClient botAClient  = new ResilientChatClient(CreateClient(config.BotAProvider ?? (openRouterKey is not null ? "openrouter" : "bedrock"), botAModel, openRouterKey, awsRegion), config.RetryMax, config.RetryBaseDelayMs);
using IChatClient botBClient  = new ResilientChatClient(CreateClient(config.BotBProvider ?? "bedrock", botBModel, openRouterKey, awsRegion), config.RetryMax, config.RetryBaseDelayMs);
using IChatClient judgeClient = new ResilientChatClient(CreateClient(config.JudgeProvider ?? (openRouterKey is not null ? "openrouter" : "bedrock"), judgeModel, openRouterKey, awsRegion), config.RetryMax, config.RetryBaseDelayMs);

// ─── Judge panel ──────────────────────────────────────────────────────────────

JudgePanelService? judgePanel = null;
if (!string.IsNullOrEmpty(config.JudgePanel))
{
    var panelSpecs = config.JudgePanel.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var panelists = new List<(string Name, IChatClient Client)>();
    foreach (var spec in panelSpecs)
    {
        var parts    = spec.Split(':', 2);
        var provider = parts.Length > 1 ? parts[0] : (openRouterKey != null ? "openrouter" : "bedrock");
        var model    = parts.Length > 1 ? parts[1] : parts[0];
        var client   = new ResilientChatClient(CreateClient(provider, model, openRouterKey, awsRegion), config.RetryMax, config.RetryBaseDelayMs);
        panelists.Add(($"{provider}/{model}", client));
    }
    judgePanel = new JudgePanelService(panelists);
}

// ─── Stance tracking ──────────────────────────────────────────────────────────

var botATimeline = new StanceTimeline("Bot A");
var botBTimeline = new StanceTimeline("Bot B");
var stanceMeterService = config.StanceTracking
    ? new StanceMeterService(judgeClient)
    : null;

// ─── Embedding client ─────────────────────────────────────────────────────────

var embeddingClient = EmbeddingHelper.CreateEmbeddingClient(config.VibewarsEmbedBackend, openRouterKey, awsRegion, config.VibewarsEmbedModel);

// ─── Argument graph ───────────────────────────────────────────────────────────

var allNodes = new List<ArgumentNode>();
var allEdges = new List<ArgumentEdge>();
var argumentGraphService = config.ArgumentGraph
    ? new ArgumentGraphService(judgeClient)
    : null;

// ─── Wave 3 services ─────────────────────────────────────────────────────────

var analyticsScorer     = config.Analytics ? new ArgumentStrengthScorer(judgeClient) : null;
var allStrengthScores   = new List<ArgumentStrengthScore>();
var selfReflectionSvc   = config.Reflect   ? new SelfReflectionService(judgeClient) : null;
var dialecticalArbiter  = config.Arbiter   ? new DialecticalArbiter(judgeClient)    : null;
SelfReflectionEntry? lastBotAReflection = null;
SelfReflectionEntry? lastBotBReflection = null;

// Track selected tactics for post-verdict outcome recording
DebateStrategy? currentRoundStratA = null;
DebateStrategy? currentRoundStratB = null;

// ─── Challenges (formal interruptions) ────────────────────────────────────────

var challengeService = config.Challenges ? new VibeWars.Challenges.ChallengeService(judgeClient) : null;
string? pendingChallengeInjection = null; // injected into the next opponent prompt

// ─── Audience simulation ──────────────────────────────────────────────────────

VibeWars.Audience.AudienceSimulator? audienceSimulator = null;
if (config.AudienceSimulation)
{
    var splitParts = config.AudienceSplit.Split('/');
    int startA = 50, startB = 50;
    if (splitParts.Length == 2 && int.TryParse(splitParts[0], out var sa) && int.TryParse(splitParts[1], out var sb2))
    { startA = sa; startB = sb2; }
    audienceSimulator = new VibeWars.Audience.AudienceSimulator(startA, startB);
}

// ─── Commentator ──────────────────────────────────────────────────────────────

VibeWars.Commentator.CommentatorService? commentatorService = null;
IChatClient? commentatorClient = null;
if (config.Commentator)
{
    var commentatorModel = !string.IsNullOrEmpty(config.CommentatorModel) ? config.CommentatorModel : judgeModel;
    var commentatorProvider = openRouterKey is not null ? "openrouter" : "bedrock";
    commentatorClient = new ResilientChatClient(
        CreateClient(commentatorProvider, commentatorModel, openRouterKey, awsRegion),
        config.RetryMax, config.RetryBaseDelayMs);
    var style = VibeWars.Commentator.CommentatorService.ParseStyle(config.CommentatorStyle);
    commentatorService = new VibeWars.Commentator.CommentatorService(commentatorClient, style);
}

// ─── Wave 4: Momentum tracker ──────────────────────────────────────────────

var momentumTracker = config.Momentum ? new VibeWars.Momentum.MomentumTracker() : null;
var allArguments = new List<(int Round, string BotName, string Content)>();

// ─── Wave 5: Smarter bots services ────────────────────────────────────────

var argumentPlanner = config.Plan ? new VibeWars.Planning.ArgumentPlanner(judgeClient) : null;
var lookaheadService = config.Lookahead ? new VibeWars.Planning.LookaheadService(judgeClient) : null;
var fallacyDetector = config.FallacyCheck ? new VibeWars.Fallacy.FallacyDetectorService(judgeClient) : null;
string? pendingFallacyCalloutA = null; // fallacy found in Bot B, injected into Bot A's next prompt
string? pendingFallacyCalloutB = null; // fallacy found in Bot A, injected into Bot B's prompt

VibeWars.Knowledge.IKnowledgeSource? knowledgeSource = null;
if (!string.IsNullOrEmpty(config.KnowledgeSource))
{
    knowledgeSource = config.KnowledgeSource.Equals("wikipedia", StringComparison.OrdinalIgnoreCase)
        ? new VibeWars.Knowledge.WikipediaKnowledgeSource()
        : null; // future: LocalFileKnowledgeSource
}

// ─── Wave 6: Personality evolution ─────────────────────────────────────────

VibeWars.Personality.PersonalityEvolutionService? personalityService = null;

var humanReader = new HumanInputReader();

if (spectreRenderer != null)
{
    spectreRenderer.PrintBanner(botAClient.ModelId, botAClient.ProviderName, botAPersona.Name,
        botBClient.ModelId, botBClient.ProviderName, botBPersona.Name,
        judgeClient.ModelId, judgeClient.ProviderName,
        maxRounds, memoryBackend, noMemory, config.DebateFormat);
}
else
{
    Console.WriteLine($"\n{Ansi.Bold}⚔  VibeWars{Ansi.Reset}  {Ansi.Dim}— AI Debate Arena{Ansi.Reset}\n");
    Console.WriteLine($"  {Ansi.Blue}{Ansi.Bold}Bot A{Ansi.Reset}  {Ansi.Dim}{botAClient.ModelId} ({botAClient.ProviderName}) — {botAPersona.Name}{Ansi.Reset}");
    Console.WriteLine($"  {Ansi.Green}{Ansi.Bold}Bot B{Ansi.Reset}  {Ansi.Dim}{botBClient.ModelId} ({botBClient.ProviderName}) — {botBPersona.Name}{Ansi.Reset}");
    Console.WriteLine($"  {Ansi.Yellow}{Ansi.Bold}Judge{Ansi.Reset}  {Ansi.Dim}{judgeClient.ModelId} ({judgeClient.ProviderName}){Ansi.Reset}");
    Console.WriteLine($"  {Ansi.Dim}Max rounds: {maxRounds}  Format: {debateFormat}{Ansi.Reset}");
    if (!noMemory)
        Console.WriteLine($"  {Ansi.Dim}Memory backend: {memoryBackend}{Ansi.Reset}");
    if (config.MaxCostUsd.HasValue)
        Console.WriteLine($"  {Ansi.Dim}Budget: ${config.MaxCostUsd.Value:F2}{Ansi.Reset}");
    Console.WriteLine();
}

// ─── Get debate topic ─────────────────────────────────────────────────────────

string topic;
if (topicParts.Count > 0)
{
    topic = string.Join(' ', topicParts);
}
else
{
    Console.Write($"{Ansi.Bold}Enter debate topic:{Ansi.Reset} ");
    topic = Console.ReadLine()?.Trim() ?? string.Empty;
    if (string.IsNullOrEmpty(topic))
    {
        Console.WriteLine($"{Ansi.Red}No topic provided. Exiting.{Ansi.Reset}");
        return;
    }
}

Console.WriteLine($"\n{Separator('═')}");
Console.WriteLine($"  {Ansi.Bold}Topic:{Ansi.Reset} {topic}");
Console.WriteLine(Separator('═'));

// Update dashboard status now that we have the topic
webDashboard?.SetStatus(new { status = "debating", topic });

// ─── Memory context injection ─────────────────────────────────────────────────

using var memory = noMemory ? null : (IMemoryStore)CreateMemoryStore(memoryBackend);
var sessionId    = Guid.NewGuid();
var sessionStart = DateTimeOffset.UtcNow;

string priorKnowledgeBlock = string.Empty;
if (memory is not null)
{
    try
    {
        var pastEntries = await memory.SearchAsync(topic, memoryTopK);
        priorKnowledgeBlock = BuildPriorKnowledgeBlock(pastEntries, memoryContextTokens);
        if (!string.IsNullOrEmpty(priorKnowledgeBlock))
            Console.WriteLine($"  {Ansi.Dim}Injecting {pastEntries.Count} prior memories into bot prompts…{Ansi.Reset}\n");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{Ansi.Dim}[Memory] Search failed: {ex.Message}{Ansi.Reset}");
    }
}

// ─── System prompts ───────────────────────────────────────────────────────────

var priorKnowledgeSuffix = string.IsNullOrEmpty(priorKnowledgeBlock)
    ? string.Empty
    : $"\n\n{priorKnowledgeBlock}";

var formatSystemNote = DebateFormatHelper.GetFormatSystemNote(debateFormat);
var complexity = VibeWars.Complexity.DebateComplexityService.Parse(config.Complexity);
var complexitySuffix = VibeWars.Complexity.DebateComplexityService.GetBotPromptSuffix(complexity);
var complexityJudgeSuffix = VibeWars.Complexity.DebateComplexityService.GetJudgePromptSuffix(complexity);
if (!string.IsNullOrEmpty(complexitySuffix))
    complexitySuffix = " " + complexitySuffix;

var botASystem = $"""
You are Bot A, debating the topic: "{topic}".
{botAPersona.StyleDescription}
Your goal is to present compelling arguments, engage with Bot B's points directly,
and work collaboratively toward an agreed-upon solution.
Be concise (3-5 sentences per turn). Build on previous points.{formatSystemNote}{complexitySuffix}{priorKnowledgeSuffix}
""";

var botBSystem = $"""
You are Bot B, debating the topic: "{topic}".
{botBPersona.StyleDescription}
Your goal is to challenge assumptions, offer fresh perspectives,
and work collaboratively with Bot A toward an agreed-upon solution.
Be concise (3-5 sentences per turn). Engage directly with Bot A's arguments.{formatSystemNote}{complexitySuffix}{priorKnowledgeSuffix}
""";

// ─── Hidden objective injection ───────────────────────────────────────────────

if (!string.IsNullOrWhiteSpace(config.HiddenObjectiveA))
    botASystem += $"\n\n{ObjectiveLibrary.FormatInjection(config.HiddenObjectiveA)}";
if (!string.IsNullOrWhiteSpace(config.HiddenObjectiveB))
    botBSystem += $"\n\n{ObjectiveLibrary.FormatInjection(config.HiddenObjectiveB)}";

// ─── Adversarial briefing injection ──────────────────────────────────────────

var pendingBriefingEntries = new List<MemoryEntry>();
if (config.Brief && memory is not null)
{
    try
    {
        var pastForBriefA = await memory.SearchAsync(topic, topK: 50);
        if (AdversarialBriefingService.ShouldBrief(pastForBriefA, topic))
        {
            var briefingA = await AdversarialBriefingService.BuildBriefingAsync(memory, topic, "Bot A", "", 5);
            if (!string.IsNullOrEmpty(briefingA))
            {
                botASystem += $"\n\n{briefingA}";
                Console.WriteLine($"  {Ansi.Dim}{AdversarialBriefingService.FormatBriefingNotice("Bot A", Math.Min(pastForBriefA.Count, 5))}{Ansi.Reset}");
                pendingBriefingEntries.Add(new MemoryEntry(Guid.NewGuid(), "Bot A", topic, 0, "briefing", briefingA, DateTimeOffset.UtcNow, [topic, "brief"]));
            }
        }
        var pastForBriefB = await memory.SearchAsync(topic, topK: 50);
        if (AdversarialBriefingService.ShouldBrief(pastForBriefB, topic))
        {
            var briefingB = await AdversarialBriefingService.BuildBriefingAsync(memory, topic, "Bot B", "", 5);
            if (!string.IsNullOrEmpty(briefingB))
            {
                botBSystem += $"\n\n{briefingB}";
                Console.WriteLine($"  {Ansi.Dim}{AdversarialBriefingService.FormatBriefingNotice("Bot B", Math.Min(pastForBriefB.Count, 5))}{Ansi.Reset}");
                pendingBriefingEntries.Add(new MemoryEntry(Guid.NewGuid(), "Bot B", topic, 0, "briefing", briefingB, DateTimeOffset.UtcNow, [topic, "brief"]));
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{Ansi.Dim}[Brief] Briefing failed: {ex.Message}{Ansi.Reset}");
    }
}

var judgeSystem = $$"""
You are an impartial debate judge evaluating a discussion about: "{{topic}}".
After each exchange you must respond with a JSON object in this exact format:
{
  "winner": "<Bot A | Bot B | Tie>",
  "reasoning": "<1-2 sentences explaining who made the stronger argument this round>",
  "new_ideas": "<1-2 new angles or ideas for the debaters to explore next>"
}
Be concise and constructive.
""";
if (!string.IsNullOrEmpty(complexityJudgeSuffix))
    judgeSystem += " " + complexityJudgeSuffix;

// ─── Dry-run early exit ───────────────────────────────────────────────────────

if (config.BotCount > 2)
{
    Console.WriteLine($"{Ansi.Yellow}Multi-bot free-for-all (--bots {config.BotCount}) is not yet implemented. Running as a standard 2-bot debate.{Ansi.Reset}");
}

if (config.DryRun)
{
    Console.WriteLine($"\n{Ansi.Dim}[Dry Run] Configuration validated. System prompts would be:{Ansi.Reset}");
    Console.WriteLine($"  Bot A system prompt preview: {botASystem[..Math.Min(200, botASystem.Length)]}…");
    Console.WriteLine($"  Bot B system prompt preview: {botBSystem[..Math.Min(200, botBSystem.Length)]}…");
    Console.WriteLine($"\n{Ansi.Dim}No API calls will be made.{Ansi.Reset}\n");
    return;
}

// ─── Wave 4: Pre-debate hype card ─────────────────────────────────────────────

if (config.PreDebateHype && memory is not null)
{
    try
    {
        var sqliteForHype = AsSqlite(memory);
        if (sqliteForHype != null)
        {
            var eloSvcHype = new VibeWars.Elo.EloService(sqliteForHype.GetConnection());
            var contestantIdA = $"{botAClient.ProviderName}/{botAClient.ModelId}/{botAPersona.Name}";
            var contestantIdB = $"{botBClient.ProviderName}/{botBClient.ModelId}/{botBPersona.Name}";
            var recA = await eloSvcHype.GetOrCreateAsync(contestantIdA);
            var recB = await eloSvcHype.GetOrCreateAsync(contestantIdB);
            var card = VibeWars.Matchup.MatchupService.BuildCard(recA, recB);
            Console.WriteLine(VibeWars.Matchup.MatchupService.RenderCard(card));
        }
    }
    catch { /* non-fatal */ }
}

// ─── Wave 6: Initialize personality evolution ─────────────────────────────────

if (config.PersonalityEvolution && memory is not null)
{
    var sqliteP = AsSqlite(memory);
    if (sqliteP != null)
    {
        personalityService = new VibeWars.Personality.PersonalityEvolutionService(sqliteP.GetConnection());
        var contestantIdA = $"{botAClient.ProviderName}/{botAClient.ModelId}/{botAPersona.Name}";
        var contestantIdB = $"{botBClient.ProviderName}/{botBClient.ModelId}/{botBPersona.Name}";
        var profileA = personalityService.GetProfile(contestantIdA);
        var profileB = personalityService.GetProfile(contestantIdB);
        var traitInjA = VibeWars.Personality.PersonalityEvolutionService.FormatTraitInjection(profileA);
        var traitInjB = VibeWars.Personality.PersonalityEvolutionService.FormatTraitInjection(profileB);
        if (!string.IsNullOrEmpty(traitInjA)) botASystem += "\n\n" + traitInjA;
        if (!string.IsNullOrEmpty(traitInjB)) botBSystem += "\n\n" + traitInjB;
    }
}

// ─── Debate loop ──────────────────────────────────────────────────────────────

// Set up fact-checker if enabled
IChatClient? factCheckerClientRaw = config.FactCheck
    ? CreateClient(openRouterKey is not null ? "openrouter" : "bedrock",
                   config.FactCheckModel ?? judgeModel, openRouterKey, awsRegion)
    : null;
using var factCheckerClient = factCheckerClientRaw != null
    ? new ResilientChatClient(factCheckerClientRaw, config.RetryMax, config.RetryBaseDelayMs)
    : null;
var factCheckerService = factCheckerClient is not null
    ? new VibeWars.FactChecker.FactCheckerService(factCheckerClient)
    : null;

var botAHistory   = new List<ChatMessage>();
var botBHistory   = new List<ChatMessage>();
var judgeHistory  = new List<ChatMessage>();
var roundWinners  = new List<string>();
var memoryEntries = new List<MemoryEntry>();
memoryEntries.AddRange(pendingBriefingEntries); // briefing entries collected before loop
var costAccumulator = new CostAccumulator();
var cts           = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

string? lastBotAMessage = null;
string? lastBotBMessage = null;
bool budgetExceeded = false;
string? prevBotBFactFlags = null; // fact-check flags from Bot B's previous turn, injected into Bot A's next prompt

// ─── Wave 3: Strategy engine (requires SQLite connection) ─────────────────────

var sqliteForStrategyLoop = AsSqlite(memory);
StrategyEngine? strategyEngine = config.Strategy && sqliteForStrategyLoop != null
    ? new StrategyEngine(sqliteForStrategyLoop.GetConnection())
    : null;

// ─── Wave 3: Red Team vulnerability tracker ──────────────────────────────────

var vulnerabilityTracker = config.RedTeam ? new VibeWars.RedTeam.VulnerabilityTracker() : null;

for (int round = 1; round <= maxRounds && !cts.IsCancellationRequested && !budgetExceeded; round++)
{
    bool isFinalRound = round == maxRounds;

    if (spectreRenderer != null)
        spectreRenderer.PrintRoundHeader(round, maxRounds);
    else
    {
        Console.WriteLine($"\n{Separator()}");
        Console.WriteLine($"  {Ansi.Bold}Round {round} of {maxRounds}{Ansi.Reset}");
        Console.WriteLine(Separator());
    }

    var turnInstruction = DebateFormatHelper.GetTurnInstruction(debateFormat, round, maxRounds, isFinalRound);
    webDashboard?.PublishEvent(new DebateEvent("round_start", round, $"Round {round} of {maxRounds}", null));

    string? botALowConfidenceFlags = null;
    try
    {
        var proposalNote = round == 1 && !string.IsNullOrWhiteSpace(config.Proposal)
            ? $" Your initial proposal is: \"{config.Proposal}\". Defend and elaborate on this position."
            : "";
        var challengeNote = !string.IsNullOrEmpty(pendingChallengeInjection) ? $" {pendingChallengeInjection}" : "";
        pendingChallengeInjection = null;
        var botABasePrompt = round == 1
            ? $"The debate topic is: \"{topic}\". Please present your opening argument.{turnInstruction}{proposalNote}"
            : $"Bot B said: \"{lastBotBMessage}\". Respond to their argument and advance your position.{turnInstruction}{challengeNote}";

        var botAUsedHuman = false;
        if (config.HumanRole?.Equals("A", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (lastBotBMessage is not null)
            {
                Console.WriteLine($"\n{Separator()}");
                Console.WriteLine($"  {Ansi.Green}{Ansi.Bold}[Bot B — Previous argument]{Ansi.Reset}");
                Console.WriteLine(Wrap(lastBotBMessage));
                Console.WriteLine(Separator());
                if (config.ThinkTime > 0) await Task.Delay(config.ThinkTime * 1000, cts.Token);
            }
            var humanInput = humanReader.ReadArgument($"\n{Ansi.Bold}[Your turn — Bot A]{Ansi.Reset} Enter your argument (or blank to auto-generate): ");
            if (!string.IsNullOrEmpty(humanInput))
            {
                botAHistory.Add(new ChatMessage("user", botABasePrompt));
                botAHistory.Add(new ChatMessage("assistant", humanInput));
                lastBotAMessage = humanInput;
                memoryEntries.Add(new MemoryEntry(Guid.NewGuid(), "Human", topic, round, "assistant", humanInput, DateTimeOffset.UtcNow, [topic, "human"]));
                allArguments.Add((round, "Bot A", humanInput));
                PrintMessage("Bot A (Human)", Ansi.Blue, humanInput);
                botAUsedHuman = true;
            }
        }

        if (!botAUsedHuman)
        {
            botAHistory.Add(new ChatMessage("user", botABasePrompt));
            // Fact-check note is injected only for this call; history stores the clean base prompt.
            IReadOnlyList<ChatMessage> botACallHistory = string.IsNullOrEmpty(prevBotBFactFlags)
                ? botAHistory
                : [..botAHistory.Take(botAHistory.Count - 1),
                   new ChatMessage("user", $"{botABasePrompt} Note: {prevBotBFactFlags}")];

            // Strategy hint for Bot A (injected into the per-round system prompt only, not stored in history)
            var botASystemForRound = botASystem;
            if (strategyEngine != null)
            {
                try
                {
                    var oppHistory  = string.Join("\n", botBHistory.Select(m => m.Content).TakeLast(6));
                    var selfHistory = string.Join("\n", botAHistory.Select(m => m.Content).TakeLast(6));
                    var pastA = strategyEngine.GetPastSuccesses("Bot A");
                    var stratA = await strategyEngine.SelectStrategyAsync(botAClient, oppHistory, selfHistory, pastA, cts.Token);
                    var hint = StrategyEngine.FormatStrategyHint(stratA);
                    if (!string.IsNullOrEmpty(hint))
                    {
                        botASystemForRound = botASystem + "\n\n" + hint;
                        currentRoundStratA = stratA;
                        Console.WriteLine($"  {Ansi.Dim}📋 Bot A Tactic: {stratA.TacticName} (confidence: {stratA.ConfidenceScore:F2}){Ansi.Reset}");
                    }
                }
                catch { /* graceful degradation */ }
            }

            // Reflection hint from last round
            if (lastBotAReflection is not null)
            {
                var reflectionHint = SelfReflectionService.FormatReflectionInjection(lastBotAReflection);
                if (!string.IsNullOrEmpty(reflectionHint))
                    botASystemForRound = botASystemForRound + "\n\n" + reflectionHint;
            }

            // Wave 5: Fallacy callout from opponent's last turn
            if (!string.IsNullOrEmpty(pendingFallacyCalloutA))
            {
                botASystemForRound += "\n\n" + pendingFallacyCalloutA;
                pendingFallacyCalloutA = null;
            }

            // Wave 5: Chain-of-thought planning
            if (argumentPlanner != null)
            {
                try
                {
                    var plan = await argumentPlanner.PlanAsync(topic, "Bot A", lastBotBMessage ?? "", lastBotAMessage ?? "", round, cts.Token);
                    var planHint = VibeWars.Planning.ArgumentPlanner.FormatPlanInjection(plan);
                    if (!string.IsNullOrEmpty(planHint))
                        botASystemForRound += "\n\n" + planHint;
                }
                catch { /* non-fatal */ }
            }

            // Wave 5: Knowledge retrieval
            if (knowledgeSource != null)
            {
                try
                {
                    var passages = await knowledgeSource.SearchAsync(
                        $"{topic} {lastBotBMessage ?? ""}", topK: 2, cts.Token);
                    var knowledgeHint = VibeWars.Knowledge.KnowledgeFormatter.FormatForPrompt(passages);
                    if (!string.IsNullOrEmpty(knowledgeHint))
                        botASystemForRound += "\n\n" + knowledgeHint;
                }
                catch { /* non-fatal */ }
            }

            // Wave 5: Difficulty balancing
            if (config.Balance && analyticsScorer != null && round > 1)
            {
                var avgA = allStrengthScores.Where(s => s.BotName == "Bot A").Select(s => s.Composite).DefaultIfEmpty(5.0).Average();
                var avgB = allStrengthScores.Where(s => s.BotName == "Bot B").Select(s => s.Composite).DefaultIfEmpty(5.0).Average();
                var wA = roundWinners.Count(w => w.Contains("Bot A", StringComparison.OrdinalIgnoreCase));
                var wB = roundWinners.Count(w => w.Contains("Bot B", StringComparison.OrdinalIgnoreCase));
                var adjustment = VibeWars.Balancing.DifficultyBalancer.Evaluate(wA, wB, avgA, avgB);
                if (adjustment?.TargetBot == "Bot A")
                {
                    botASystemForRound += "\n\n" + adjustment.PromptSupplement;
                    Console.WriteLine($"  {Ansi.Dim}[Balance] {adjustment.Reason}{Ansi.Reset}");
                }
            }

            // Wave 5: Lookahead — pick the hardest-to-rebut argument
            if (lookaheadService != null)
            {
                try
                {
                    var lookahead = await lookaheadService.SelectBestArgumentAsync(
                        botASystemForRound, botACallHistory,
                        $"You are Bot B debating \"{topic}\".", cts.Token);
                    if (!string.IsNullOrEmpty(lookahead.SelectedArgument))
                        botASystemForRound += $"\n\n[LOOKAHEAD] Build on this angle: {lookahead.SelectedArgument}";
                }
                catch { /* non-fatal */ }
            }

            string botAReply;
            TokenUsage botAUsage;
            if (!config.NoStream)
            {
                Console.WriteLine($"\n{Ansi.Blue}{Ansi.Bold}[Bot A [{botAPersona.Name}]]{Ansi.Reset}{Ansi.Blue}{Ansi.Reset}");
                (botAReply, botAUsage) = await StreamReplyAsync(botAClient, botASystemForRound, botACallHistory, cts.Token);
                if (spectreRenderer != null) spectreRenderer.PrintBotMessage($"Bot A [{botAPersona.Name}]", "blue", botAReply);
            }
            else
            {
                (botAReply, botAUsage) = await botAClient.ChatAsync(botASystemForRound, botACallHistory, cts.Token);
                if (spectreRenderer != null) spectreRenderer.PrintBotMessage($"Bot A [{botAPersona.Name}]", "blue", botAReply);
                else PrintMessage($"Bot A [{botAPersona.Name}]", Ansi.Blue, botAReply);
            }
            botAHistory.Add(new ChatMessage("assistant", botAReply));
            lastBotAMessage = botAReply;
            costAccumulator.Add(botAUsage);
            memoryEntries.Add(new MemoryEntry(Guid.NewGuid(), "Bot A", topic, round, "assistant", botAReply, DateTimeOffset.UtcNow, [topic, botAPersona.Name]));
            webDashboard?.PublishEvent(new DebateEvent("bot_a", round, botAReply, "Bot A"));
            allArguments.Add((round, "Bot A", botAReply));

            // Wave 5: Offensive fallacy check on Bot A's argument (result injected into Bot B)
            if (fallacyDetector != null)
            {
                try
                {
                    var fallacy = await fallacyDetector.DetectAsync(botAReply, cts.Token);
                    if (fallacy.HasFallacy)
                    {
                        pendingFallacyCalloutB = VibeWars.Fallacy.FallacyDetectorService.FormatCallout(fallacy);
                        Console.WriteLine($"  {Ansi.Dim}[Fallacy] Bot A: {fallacy.FallacyName} — {fallacy.Explanation}{Ansi.Reset}");
                    }
                }
                catch { /* non-fatal */ }
            }

            // Analytics scoring for Bot A
            if (analyticsScorer != null)
            {
                try
                {
                    var priorArgs = botAHistory.Where(m => m.Role == "assistant").SkipLast(1).Select(m => m.Content).ToList();
                    var scoreA = await analyticsScorer.ScoreAsync(botAReply, priorArgs, round, "Bot A", cts.Token);
                    allStrengthScores.Add(scoreA);
                    var scoreContent = $"{scoreA.LogicalRigor.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{scoreA.Novelty.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{scoreA.PersuasiveImpact.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{scoreA.Composite.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                    memoryEntries.Add(new MemoryEntry(Guid.NewGuid(), "Bot A", topic, round, "strength-score", scoreContent, DateTimeOffset.UtcNow, [topic, "analytics"]));
                    Console.WriteLine($"  {Ansi.Dim}📊 Bot A strength: {scoreA.Composite:F1} (rigor:{scoreA.LogicalRigor:F1} novelty:{scoreA.Novelty:F1} impact:{scoreA.PersuasiveImpact:F1}){Ansi.Reset}");
                }
                catch { /* non-fatal */ }
            }

            // Argument graph for Bot A
            if (argumentGraphService != null)
            {
                var newNodes = await argumentGraphService.ExtractClaimsAsync(botAReply, sessionId, round, "Bot A", cts.Token);
                if (allNodes.Count > 0)
                {
                    var previousNodes = allNodes.TakeLast(Math.Min(10, allNodes.Count)).ToList();
                    var edges = await argumentGraphService.ExtractRelationsAsync(newNodes, previousNodes, cts.Token);
                    allEdges.AddRange(edges);
                }
                foreach (var node in newNodes)
                    memoryEntries.Add(new MemoryEntry(node.Id, node.BotName, topic, round, "argument-node",
                        $"{node.ClaimType}|{node.ClaimText}", DateTimeOffset.UtcNow, [topic, "argument-graph"]));
                allNodes.AddRange(newNodes);
            }

            // Stance tracking for Bot A
            if (stanceMeterService != null)
            {
                var stanceEntry = await stanceMeterService.MeasureAsync(botAReply, round, cts.Token);
                botATimeline.Add(stanceEntry);
                memoryEntries.Add(new MemoryEntry(Guid.NewGuid(), "Bot A", topic, round, "stance",
                    $"Stance: {stanceEntry.Stance}, Concessions: {string.Join("; ", stanceEntry.Concessions)}",
                    DateTimeOffset.UtcNow, [topic, "stance"]));
            }

            // Fact-check Bot A
            if (factCheckerService != null)
            {
                var fcResult = await factCheckerService.CheckAsync(botAReply, cts.Token);
                VibeWars.FactChecker.FactCheckerService.Print(fcResult);
                botALowConfidenceFlags = VibeWars.FactChecker.FactCheckerService.FormatLowConfidenceFlags(fcResult);
                if (fcResult.Claims.Count > 0)
                    memoryEntries.Add(new MemoryEntry(Guid.NewGuid(), "Bot A", topic, round, "fact-check",
                        string.Join("\n", fcResult.Claims.Select(c => $"{c.Confidence}: {c.Claim}")),
                        DateTimeOffset.UtcNow, [topic, "fact-check"]));
            }

            // Red Team: check if Bot A patched previously discovered vulnerabilities
            if (vulnerabilityTracker != null && round > 1)
            {
                try
                {
                    var open = vulnerabilityTracker.Records.Where(v => v.Status == VibeWars.RedTeam.VulnerabilityStatus.Open).ToList();
                    if (open.Count > 0)
                        await vulnerabilityTracker.UpdateStatusAsync(judgeClient, botAReply, open, cts.Token);
                }
                catch { /* non-fatal */ }
            }

            // Challenges: detect challengeable claims in Bot A's argument
            if (challengeService != null)
            {
                try
                {
                    var challengePrompt = challengeService.BuildChallengePrompt(botAReply);
                    var (challengeReply, _) = await judgeClient.ChatAsync(
                        "You detect formal debate challenges.", [new ChatMessage("user", challengePrompt)], cts.Token);
                    var result = VibeWars.Challenges.ChallengeService.ParseChallengeResult(challengeReply);
                    if (result is { ShouldChallenge: true })
                    {
                        pendingChallengeInjection = challengeService.BuildInterruptionInjection("Bot B", result);
                        Console.WriteLine($"  {Ansi.Dim}⚡ {VibeWars.Challenges.ChallengeService.FormatInterruption(new VibeWars.Challenges.DebateInterruption("Bot B", round, VibeWars.Challenges.ChallengeType.DirectChallenge, result.Target, true))}{Ansi.Reset}");
                    }
                }
                catch { /* non-fatal */ }
            }
        }
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        Console.WriteLine($"\n{Ansi.Red}⏺ Bot A error: {ex.Message}{Ansi.Reset}");
        break;
    }

    if (cts.IsCancellationRequested) break;

    // Budget check after Bot A
    if (costAccumulator.ExceedsBudget(config.MaxCostUsd))
    {
        Console.WriteLine($"\n{Ansi.Yellow}⚠ Budget limit ${config.MaxCostUsd:F2} reached after Bot A's turn. Stopping.{Ansi.Reset}");
        budgetExceeded = true;
        break;
    }

    // ── Bot B turn ────────────────────────────────────────────────────────────

    string? botBLowConfidenceFlags = null;
    try
    {
        var botBChallengeNote = !string.IsNullOrEmpty(pendingChallengeInjection) ? $" {pendingChallengeInjection}" : "";
        pendingChallengeInjection = null;
        var botBBasePrompt = round == 1
            ? $"The debate topic is: \"{topic}\". Bot A opened with: \"{lastBotAMessage}\". Present your response and opening argument.{turnInstruction}{botBChallengeNote}"
            : $"Bot A said: \"{lastBotAMessage}\". Respond to their argument and advance your position.{turnInstruction}{botBChallengeNote}";

        var botBUsedHuman = false;
        if (config.HumanRole?.Equals("B", StringComparison.OrdinalIgnoreCase) == true && lastBotAMessage is not null)
        {
            Console.WriteLine($"\n{Separator()}");
            Console.WriteLine($"  {Ansi.Blue}{Ansi.Bold}[Bot A — Previous argument]{Ansi.Reset}");
            Console.WriteLine(Wrap(lastBotAMessage));
            Console.WriteLine(Separator());
            if (config.ThinkTime > 0) await Task.Delay(config.ThinkTime * 1000, cts.Token);
            var humanInput = humanReader.ReadArgument($"\n{Ansi.Bold}[Your turn — Bot B]{Ansi.Reset} Enter your argument (or blank to auto-generate): ");
            if (!string.IsNullOrEmpty(humanInput))
            {
                botBHistory.Add(new ChatMessage("user", botBBasePrompt));
                botBHistory.Add(new ChatMessage("assistant", humanInput));
                lastBotBMessage = humanInput;
                memoryEntries.Add(new MemoryEntry(Guid.NewGuid(), "Human", topic, round, "assistant", humanInput, DateTimeOffset.UtcNow, [topic, "human"]));
                allArguments.Add((round, "Bot B", humanInput));
                PrintMessage("Bot B (Human)", Ansi.Green, humanInput);
                botBUsedHuman = true;
            }
        }

        if (!botBUsedHuman)
        {
            botBHistory.Add(new ChatMessage("user", botBBasePrompt));
            // Fact-check note is injected only for this call; history stores the clean base prompt.
            IReadOnlyList<ChatMessage> botBCallHistory = string.IsNullOrEmpty(botALowConfidenceFlags)
                ? botBHistory
                : [..botBHistory.Take(botBHistory.Count - 1),
                   new ChatMessage("user", $"{botBBasePrompt} Note: {botALowConfidenceFlags}")];

            // Strategy hint for Bot B
            var botBSystemForRound = botBSystem;
            if (strategyEngine != null)
            {
                try
                {
                    var oppHistoryB  = string.Join("\n", botAHistory.Select(m => m.Content).TakeLast(6));
                    var selfHistoryB = string.Join("\n", botBHistory.Select(m => m.Content).TakeLast(6));
                    var pastB = strategyEngine.GetPastSuccesses("Bot B");
                    var stratB = await strategyEngine.SelectStrategyAsync(botBClient, oppHistoryB, selfHistoryB, pastB, cts.Token);
                    var hintB = StrategyEngine.FormatStrategyHint(stratB);
                    if (!string.IsNullOrEmpty(hintB))
                    {
                        botBSystemForRound = botBSystem + "\n\n" + hintB;
                        currentRoundStratB = stratB;
                        Console.WriteLine($"  {Ansi.Dim}📋 Bot B Tactic: {stratB.TacticName} (confidence: {stratB.ConfidenceScore:F2}){Ansi.Reset}");
                    }
                }
                catch { /* graceful degradation */ }
            }

            // Reflection hint from last round
            if (lastBotBReflection is not null)
            {
                var reflHintB = SelfReflectionService.FormatReflectionInjection(lastBotBReflection);
                if (!string.IsNullOrEmpty(reflHintB))
                    botBSystemForRound = botBSystemForRound + "\n\n" + reflHintB;
            }

            // Wave 5: Fallacy callout from Bot A's turn
            if (!string.IsNullOrEmpty(pendingFallacyCalloutB))
            {
                botBSystemForRound += "\n\n" + pendingFallacyCalloutB;
                pendingFallacyCalloutB = null;
            }

            // Wave 5: Planning, knowledge, balance for Bot B (same as Bot A above)
            if (argumentPlanner != null)
            {
                try
                {
                    var planB = await argumentPlanner.PlanAsync(topic, "Bot B", lastBotAMessage ?? "", lastBotBMessage ?? "", round, cts.Token);
                    var planHintB = VibeWars.Planning.ArgumentPlanner.FormatPlanInjection(planB);
                    if (!string.IsNullOrEmpty(planHintB))
                        botBSystemForRound += "\n\n" + planHintB;
                }
                catch { /* non-fatal */ }
            }
            if (knowledgeSource != null)
            {
                try
                {
                    var passagesB = await knowledgeSource.SearchAsync(
                        $"{topic} {lastBotAMessage ?? ""}", topK: 2, cts.Token);
                    var knowledgeHintB = VibeWars.Knowledge.KnowledgeFormatter.FormatForPrompt(passagesB);
                    if (!string.IsNullOrEmpty(knowledgeHintB))
                        botBSystemForRound += "\n\n" + knowledgeHintB;
                }
                catch { /* non-fatal */ }
            }
            if (config.Balance && analyticsScorer != null && round > 1)
            {
                var avgA2 = allStrengthScores.Where(s => s.BotName == "Bot A").Select(s => s.Composite).DefaultIfEmpty(5.0).Average();
                var avgB2 = allStrengthScores.Where(s => s.BotName == "Bot B").Select(s => s.Composite).DefaultIfEmpty(5.0).Average();
                var wA2 = roundWinners.Count(w => w.Contains("Bot A", StringComparison.OrdinalIgnoreCase));
                var wB2 = roundWinners.Count(w => w.Contains("Bot B", StringComparison.OrdinalIgnoreCase));
                var adjB = VibeWars.Balancing.DifficultyBalancer.Evaluate(wA2, wB2, avgA2, avgB2);
                if (adjB?.TargetBot == "Bot B")
                    botBSystemForRound += "\n\n" + adjB.PromptSupplement;
            }

            // Wave 5: Lookahead for Bot B
            if (lookaheadService != null)
            {
                try
                {
                    var lookaheadB = await lookaheadService.SelectBestArgumentAsync(
                        botBSystemForRound, botBCallHistory,
                        $"You are Bot A debating \"{topic}\".", cts.Token);
                    if (!string.IsNullOrEmpty(lookaheadB.SelectedArgument))
                        botBSystemForRound += $"\n\n[LOOKAHEAD] Build on this angle: {lookaheadB.SelectedArgument}";
                }
                catch { /* non-fatal */ }
            }

            string botBReply;
            TokenUsage botBUsage;
            if (!config.NoStream)
            {
                Console.WriteLine($"\n{Ansi.Green}{Ansi.Bold}[Bot B [{botBPersona.Name}]]{Ansi.Reset}{Ansi.Green}{Ansi.Reset}");
                (botBReply, botBUsage) = await StreamReplyAsync(botBClient, botBSystemForRound, botBCallHistory, cts.Token);
                if (spectreRenderer != null) spectreRenderer.PrintBotMessage($"Bot B [{botBPersona.Name}]", "green", botBReply);
            }
            else
            {
                (botBReply, botBUsage) = await botBClient.ChatAsync(botBSystemForRound, botBCallHistory, cts.Token);
                if (spectreRenderer != null) spectreRenderer.PrintBotMessage($"Bot B [{botBPersona.Name}]", "green", botBReply);
                else PrintMessage($"Bot B [{botBPersona.Name}]", Ansi.Green, botBReply);
            }
            botBHistory.Add(new ChatMessage("assistant", botBReply));
            lastBotBMessage = botBReply;
            costAccumulator.Add(botBUsage);
            memoryEntries.Add(new MemoryEntry(Guid.NewGuid(), "Bot B", topic, round, "assistant", botBReply, DateTimeOffset.UtcNow, [topic, botBPersona.Name]));
            webDashboard?.PublishEvent(new DebateEvent("bot_b", round, botBReply, "Bot B"));
            allArguments.Add((round, "Bot B", botBReply));

            // Wave 5: Offensive fallacy check on Bot B's argument (result injected into Bot A)
            if (fallacyDetector != null)
            {
                try
                {
                    var fallacyB = await fallacyDetector.DetectAsync(botBReply, cts.Token);
                    if (fallacyB.HasFallacy)
                    {
                        pendingFallacyCalloutA = VibeWars.Fallacy.FallacyDetectorService.FormatCallout(fallacyB);
                        Console.WriteLine($"  {Ansi.Dim}[Fallacy] Bot B: {fallacyB.FallacyName} — {fallacyB.Explanation}{Ansi.Reset}");
                    }
                }
                catch { /* non-fatal */ }
            }

            // Argument graph for Bot B
            if (argumentGraphService != null)
            {
                var newNodes = await argumentGraphService.ExtractClaimsAsync(botBReply, sessionId, round, "Bot B", cts.Token);
                if (allNodes.Count > 0)
                {
                    var previousNodes = allNodes.TakeLast(Math.Min(10, allNodes.Count)).ToList();
                    var edges = await argumentGraphService.ExtractRelationsAsync(newNodes, previousNodes, cts.Token);
                    allEdges.AddRange(edges);
                }
                foreach (var node in newNodes)
                    memoryEntries.Add(new MemoryEntry(node.Id, node.BotName, topic, round, "argument-node",
                        $"{node.ClaimType}|{node.ClaimText}", DateTimeOffset.UtcNow, [topic, "argument-graph"]));
                allNodes.AddRange(newNodes);
            }

            // Analytics scoring for Bot B
            if (analyticsScorer != null)
            {
                try
                {
                    var priorArgsB = botBHistory.Where(m => m.Role == "assistant").SkipLast(1).Select(m => m.Content).ToList();
                    var scoreB = await analyticsScorer.ScoreAsync(botBReply, priorArgsB, round, "Bot B", cts.Token);
                    allStrengthScores.Add(scoreB);
                    var scoreContentB = $"{scoreB.LogicalRigor.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{scoreB.Novelty.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{scoreB.PersuasiveImpact.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{scoreB.Composite.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                    memoryEntries.Add(new MemoryEntry(Guid.NewGuid(), "Bot B", topic, round, "strength-score", scoreContentB, DateTimeOffset.UtcNow, [topic, "analytics"]));
                    Console.WriteLine($"  {Ansi.Dim}📊 Bot B strength: {scoreB.Composite:F1} (rigor:{scoreB.LogicalRigor:F1} novelty:{scoreB.Novelty:F1} impact:{scoreB.PersuasiveImpact:F1}){Ansi.Reset}");
                }
                catch { /* non-fatal */ }
            }

            // Stance tracking for Bot B
            if (stanceMeterService != null)
            {
                var stanceEntry = await stanceMeterService.MeasureAsync(botBReply, round, cts.Token);
                botBTimeline.Add(stanceEntry);
                memoryEntries.Add(new MemoryEntry(Guid.NewGuid(), "Bot B", topic, round, "stance",
                    $"Stance: {stanceEntry.Stance}, Concessions: {string.Join("; ", stanceEntry.Concessions)}",
                    DateTimeOffset.UtcNow, [topic, "stance"]));
            }

            // Fact-check Bot B
            if (factCheckerService != null)
            {
                var fcResult = await factCheckerService.CheckAsync(botBReply, cts.Token);
                VibeWars.FactChecker.FactCheckerService.Print(fcResult);
                botBLowConfidenceFlags = VibeWars.FactChecker.FactCheckerService.FormatLowConfidenceFlags(fcResult);
                if (fcResult.Claims.Count > 0)
                    memoryEntries.Add(new MemoryEntry(Guid.NewGuid(), "Bot B", topic, round, "fact-check",
                        string.Join("\n", fcResult.Claims.Select(c => $"{c.Confidence}: {c.Claim}")),
                        DateTimeOffset.UtcNow, [topic, "fact-check"]));
            }

            // Red Team: extract new vulnerabilities from Bot B's argument
            if (vulnerabilityTracker != null)
            {
                try
                {
                    var newVulns = await vulnerabilityTracker.AddVulnerabilityAsync(judgeClient, botBReply, round, cts.Token);
                    if (newVulns.Count > 0)
                        Console.WriteLine($"  {Ansi.Dim}🔴 Red Team: {newVulns.Count} new vulnerabilit{(newVulns.Count == 1 ? "y" : "ies")} found{Ansi.Reset}");
                }
                catch { /* non-fatal */ }
            }

            // Challenges: detect challengeable claims in Bot B's argument
            if (challengeService != null)
            {
                try
                {
                    var challengePrompt = challengeService.BuildChallengePrompt(botBReply);
                    var (challengeReply, _) = await judgeClient.ChatAsync(
                        "You detect formal debate challenges.", [new ChatMessage("user", challengePrompt)], cts.Token);
                    var result = VibeWars.Challenges.ChallengeService.ParseChallengeResult(challengeReply);
                    if (result is { ShouldChallenge: true })
                    {
                        pendingChallengeInjection = challengeService.BuildInterruptionInjection("Bot A", result);
                        Console.WriteLine($"  {Ansi.Dim}⚡ {VibeWars.Challenges.ChallengeService.FormatInterruption(new VibeWars.Challenges.DebateInterruption("Bot A", round, VibeWars.Challenges.ChallengeType.DirectChallenge, result.Target, true))}{Ansi.Reset}");
                    }
                }
                catch { /* non-fatal */ }
            }
        }
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        Console.WriteLine($"\n{Ansi.Red}⏺ Bot B error: {ex.Message}{Ansi.Reset}");
        break;
    }

    prevBotBFactFlags = botBLowConfidenceFlags;

    if (cts.IsCancellationRequested) break;

    // Budget check after Bot B
    if (costAccumulator.ExceedsBudget(config.MaxCostUsd))
    {
        Console.WriteLine($"\n{Ansi.Yellow}⚠ Budget limit ${config.MaxCostUsd:F2} reached after Bot B's turn. Stopping.{Ansi.Reset}");
        budgetExceeded = true;
        break;
    }

    // ── Judge evaluation ──────────────────────────────────────────────────────

    try
    {
        Console.WriteLine($"\n  {Ansi.Dim}Judge evaluating...{Ansi.Reset}");

        var judgePrompt = $"""
Round {round} exchange:

Bot A: "{lastBotAMessage}"

Bot B: "{lastBotBMessage}"

Evaluate this round and provide new ideas for the next exchange.
""";

        JudgeVerdict verdict;
        if (config.HumanRole?.Equals("judge", StringComparison.OrdinalIgnoreCase) == true)
        {
            var (humanWinner, humanReasoning) = humanReader.ReadJudgeVerdict();
            verdict = new JudgeVerdict(humanWinner, humanReasoning, string.Empty);
            var humanJudgeReply = $"{{\"winner\":\"{humanWinner}\",\"reasoning\":\"{humanReasoning}\",\"new_ideas\":\"\"}}";
            judgeHistory.Add(new ChatMessage("user", judgePrompt));
            judgeHistory.Add(new ChatMessage("assistant", humanJudgeReply));
            memoryEntries.Add(new MemoryEntry(Guid.NewGuid(), "Judge (Human)", topic, round, "assistant", humanJudgeReply, DateTimeOffset.UtcNow, [topic, "verdict"]));
        }
        else if (judgePanel != null)
        {
            verdict = await judgePanel.EvaluateAsync(judgeSystem, judgeHistory, judgePrompt, cts.Token);
            var panelReply = JsonConvert.SerializeObject(new { winner = verdict.Winner, reasoning = verdict.Reasoning, new_ideas = verdict.NewIdeas });
            judgeHistory.Add(new ChatMessage("user", judgePrompt));
            judgeHistory.Add(new ChatMessage("assistant", panelReply));
            memoryEntries.Add(new MemoryEntry(Guid.NewGuid(), "Judge (Panel)", topic, round, "assistant", panelReply, DateTimeOffset.UtcNow, [topic, "verdict"]));
        }
        else
        {
            judgeHistory.Add(new ChatMessage("user", judgePrompt));
            var (judgeReply, judgeUsage) = await judgeClient.ChatAsync(judgeSystem, judgeHistory, cts.Token);
            judgeHistory.Add(new ChatMessage("assistant", judgeReply));
            costAccumulator.Add(judgeUsage);
            verdict = ParseJudgeVerdict(judgeReply);
            memoryEntries.Add(new MemoryEntry(Guid.NewGuid(), "Judge", topic, round, "assistant", judgeReply, DateTimeOffset.UtcNow, [topic, "verdict"]));
        }

        roundWinners.Add(verdict.Winner);

        // Publish round result to web dashboard and webhook
        webDashboard?.PublishEvent(new DebateEvent("judge", round, verdict.Reasoning, "Judge"));
        webDashboard?.PublishEvent(new DebateEvent("round-result", round, verdict.Winner, null));
        webDashboard?.SetStatus(new { status = "debating", round, roundWinner = verdict.Winner });
        if (webhookService != null && webhookConfig != null && webhookConfig.WebhookOnRound)
            _ = webhookService.PostRoundSummaryAsync(round, verdict.Winner, verdict.Reasoning, webhookConfig, cts.Token);

        if (spectreRenderer != null)
            spectreRenderer.PrintJudgeVerdict(verdict.Winner, verdict.Reasoning, verdict.NewIdeas);
        else
        {
            Console.WriteLine($"\n{Ansi.Yellow}{Ansi.Bold}[Judge]{Ansi.Reset}");
            Console.WriteLine(Wrap($"🏆 Round winner: {Ansi.Bold}{verdict.Winner}{Ansi.Reset}"));
            Console.WriteLine(Wrap($"📋 {verdict.Reasoning}"));
            if (!string.IsNullOrWhiteSpace(verdict.NewIdeas))
                Console.WriteLine(Wrap($"💡 New ideas: {verdict.NewIdeas}"));
        }

        if (!string.IsNullOrWhiteSpace(verdict.NewIdeas) && round < maxRounds)
        {
            var ideaHint = $"(The judge suggests exploring: {verdict.NewIdeas})";
            botAHistory.Add(new ChatMessage("user", ideaHint));
            botAHistory.Add(new ChatMessage("assistant", "Understood, I will incorporate those ideas."));
            botBHistory.Add(new ChatMessage("user", ideaHint));
            botBHistory.Add(new ChatMessage("assistant", "Understood, I will incorporate those ideas."));
        }

        // Self-reflection after each round
        if (selfReflectionSvc != null && lastBotAMessage is not null && lastBotBMessage is not null)
        {
            try
            {
                lastBotAReflection = await selfReflectionSvc.ReflectAsync("Bot A", lastBotAMessage, lastBotBMessage, verdict.Reasoning, round, cts.Token);
                var reflRenderA = SelfReflectionService.RenderReflection(lastBotAReflection);
                Console.WriteLine($"  {Ansi.Dim}{reflRenderA}{Ansi.Reset}");
                memoryEntries.Add(new MemoryEntry(Guid.NewGuid(), "Bot A", topic, round, "reflection", reflRenderA, DateTimeOffset.UtcNow, [topic, "reflection"]));

                lastBotBReflection = await selfReflectionSvc.ReflectAsync("Bot B", lastBotBMessage, lastBotAMessage, verdict.Reasoning, round, cts.Token);
                var reflRenderB = SelfReflectionService.RenderReflection(lastBotBReflection);
                Console.WriteLine($"  {Ansi.Dim}{reflRenderB}{Ansi.Reset}");
                memoryEntries.Add(new MemoryEntry(Guid.NewGuid(), "Bot B", topic, round, "reflection", reflRenderB, DateTimeOffset.UtcNow, [topic, "reflection"]));
            }
            catch { /* non-fatal */ }
        }

        // Strategy outcome recording — persist which tactic was used and whether each bot won
        if (strategyEngine != null)
        {
            try
            {
                var botAWonRound = verdict.Winner.Contains("Bot A", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                var botBWonRound = verdict.Winner.Contains("Bot B", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                if (currentRoundStratA != null)
                {
                    strategyEngine.RecordOutcome("Bot A", currentRoundStratA.TacticName, round, sessionId.ToString(), botAWonRound);
                    if (config.OpponentModel)
                        strategyEngine.RecordOpponentOutcome("Bot A",
                            $"{botBClient.ProviderName}/{botBClient.ModelId}/{botBPersona.Name}",
                            currentRoundStratA.TacticName, botAWonRound == 1);
                }
                if (currentRoundStratB != null)
                {
                    strategyEngine.RecordOutcome("Bot B", currentRoundStratB.TacticName, round, sessionId.ToString(), botBWonRound);
                    if (config.OpponentModel)
                        strategyEngine.RecordOpponentOutcome("Bot B",
                            $"{botAClient.ProviderName}/{botAClient.ModelId}/{botAPersona.Name}",
                            currentRoundStratB.TacticName, botBWonRound == 1);
                }
                currentRoundStratA = null;
                currentRoundStratB = null;
            }
            catch { /* non-fatal */ }
        }

        // Audience simulation: shift support based on judge verdict
        if (audienceSimulator != null && lastBotAMessage is not null && lastBotBMessage is not null)
        {
            try
            {
                var audiencePrompt = $"Round {round}:\nBot A: \"{lastBotAMessage}\"\nBot B: \"{lastBotBMessage}\"\nJudge: \"{verdict.Reasoning}\"\n\n" +
                    "How does the audience react? Return JSON: {\"shift_a\": <-10 to +10>, \"shift_b\": <-10 to +10>, \"mood\": \"excited|skeptical|engaged|bored\"}";
                var (audienceReply, _) = await judgeClient.ChatAsync(
                    "You simulate a live debate audience. Based on the exchange, determine how audience support shifts.",
                    [new ChatMessage("user", audiencePrompt)], cts.Token);
                var shift = VibeWars.Audience.AudienceSimulator.ParseShiftResult(audienceReply);
                if (shift != null)
                {
                    audienceSimulator.ApplyShift(shift);
                    Console.WriteLine($"\n{audienceSimulator.RenderPollBar("Bot A", "Bot B")}");
                    Console.WriteLine($"  Mood: {VibeWars.Audience.AudienceSimulator.MoodEmoji(shift.Mood)} {shift.Mood}");
                }
            }
            catch { /* non-fatal */ }
        }

        // Commentator: provide live color commentary
        if (commentatorService != null && lastBotAMessage is not null && lastBotBMessage is not null)
        {
            try
            {
                var commentary = await commentatorService.CommentAsync(lastBotAMessage, lastBotBMessage, cts.Token);
                Console.WriteLine($"\n  {Ansi.Dim}🎙 {commentary}{Ansi.Reset}");
            }
            catch { /* non-fatal */ }
        }

        // Wave 4: Momentum tracking
        if (momentumTracker != null)
        {
            var wA = roundWinners.Count(w => w.Contains("Bot A", StringComparison.OrdinalIgnoreCase));
            var wB = roundWinners.Count(w => w.Contains("Bot B", StringComparison.OrdinalIgnoreCase));
            momentumTracker.RecordRound(round, verdict.Winner,
                audienceSimulator?.SupportA, audienceSimulator?.SupportB);
            momentumTracker.CheckClutchRound(round, maxRounds, wA, wB);
            momentumTracker.CheckBlowout(round, wA, wB);
            foreach (var evt in momentumTracker.GetEventsForRound(round))
            {
                Console.WriteLine($"\n  {Ansi.Yellow}{Ansi.Bold}[MOMENTUM] {evt.Description}{Ansi.Reset}");
                webDashboard?.PublishEvent(new DebateEvent("momentum", round, evt.Description, evt.BotName));
            }
        }

        // Budget check after judge
        if (costAccumulator.ExceedsBudget(config.MaxCostUsd))
        {
            if (config.CostInteractive && !Console.IsInputRedirected)
            {
                Console.Write($"\n{Ansi.Yellow}⚠ Budget limit ${config.MaxCostUsd:F2} reached. Continue? (y/N): {Ansi.Reset}");
                var answer = Console.ReadLine()?.Trim();
                if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
                {
                    budgetExceeded = true;
                    Console.WriteLine($"  {Ansi.Dim}Stopping.{Ansi.Reset}");
                }
                else
                {
                    Console.WriteLine($"  {Ansi.Dim}Continuing past budget limit.{Ansi.Reset}");
                }
            }
            else
            {
                Console.WriteLine($"\n{Ansi.Yellow}⚠ Budget limit ${config.MaxCostUsd:F2} reached. Stopping after round {round}.{Ansi.Reset}");
                budgetExceeded = true;
                if (config.CostHardStop) break; // skip remaining post-judge features
            }
        }
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        Console.WriteLine($"\n{Ansi.Red}⏺ Judge error: {ex.Message}{Ansi.Reset}");
    }
}

// ─── Final verdict ────────────────────────────────────────────────────────────

var overallWinner  = "Tie";
var finalSynthesis = string.Empty;

if (roundWinners.Count > 0)
{
    var botAWins = roundWinners.Count(w => w.Contains("Bot A", StringComparison.OrdinalIgnoreCase));
    var botBWins = roundWinners.Count(w => w.Contains("Bot B", StringComparison.OrdinalIgnoreCase));
    var ties     = roundWinners.Count - botAWins - botBWins;

    // Wave 4: Escalating stakes
    if (config.StakesMode.Equals("escalating", StringComparison.OrdinalIgnoreCase))
    {
        double scoreA = 0, scoreB = 0;
        for (var r = 0; r < roundWinners.Count; r++)
        {
            var weight = 1.0 + r * 0.5; // round 1=1.0, round 2=1.5, round 3=2.0, etc.
            if (roundWinners[r].Contains("Bot A", StringComparison.OrdinalIgnoreCase)) scoreA += weight;
            else if (roundWinners[r].Contains("Bot B", StringComparison.OrdinalIgnoreCase)) scoreB += weight;
        }
        overallWinner = scoreA > scoreB ? "Bot A" : scoreB > scoreA ? "Bot B" : "Tie";
        Console.WriteLine($"  {Ansi.Dim}Escalating stakes — weighted: A={scoreA:F1} B={scoreB:F1}{Ansi.Reset}");
    }
    else if (config.StakesMode.Equals("winner-take-all", StringComparison.OrdinalIgnoreCase) && roundWinners.Count > 0)
    {
        var lastWinner = roundWinners[^1];
        overallWinner = lastWinner.Contains("Bot A", StringComparison.OrdinalIgnoreCase) ? "Bot A"
            : lastWinner.Contains("Bot B", StringComparison.OrdinalIgnoreCase) ? "Bot B" : "Tie";
        Console.WriteLine($"  {Ansi.Dim}Winner-take-all — final round decides{Ansi.Reset}");
    }
    else
    {
        overallWinner = botAWins > botBWins ? "Bot A" : botBWins > botAWins ? "Bot B" : "Tie";
    }

    if (!budgetExceeded)
    {
        try
        {
            Console.WriteLine($"\n  {Ansi.Dim}Generating final synthesis...{Ansi.Reset}");
            var finalPrompt = $"""
The debate on "{topic}" has concluded after {roundWinners.Count} rounds.
Please provide a final 2-3 sentence synthesis: what was the best agreed-upon insight
from this debate, and what is the most promising direction going forward?
""";
            judgeHistory.Add(new ChatMessage("user", finalPrompt));
            var (synthesis, synthUsage) = await judgeClient.ChatAsync(judgeSystem, judgeHistory);
            costAccumulator.Add(synthUsage);

            try
            {
                var doc = JsonDocument.Parse(synthesis);
                if (doc.RootElement.TryGetProperty("reasoning", out var rs))
                    synthesis = rs.GetString() ?? synthesis;
            }
            catch { /* not JSON, use as-is */ }

            finalSynthesis = synthesis;
            if (spectreRenderer == null)
            {
                Console.WriteLine($"\n{Ansi.Yellow}{Ansi.Bold}[Judge — Final Synthesis]{Ansi.Reset}");
                Console.WriteLine(Wrap(synthesis));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n{Ansi.Red}⏺ Synthesis error: {ex.Message}{Ansi.Reset}");
        }
    }

    if (spectreRenderer != null)
        spectreRenderer.PrintFinalVerdict(overallWinner, finalSynthesis, botAWins, botBWins, ties);
    else
    {
        Console.WriteLine($"\n{Separator('═')}");
        Console.WriteLine($"  {Ansi.Bold}⚔  Debate Complete{Ansi.Reset}");
        Console.WriteLine(Separator('═'));
        Console.WriteLine($"\n  {Ansi.Dim}Round wins —{Ansi.Reset}  " +
            $"{Ansi.Blue}Bot A: {botAWins}{Ansi.Reset}  " +
            $"{Ansi.Green}Bot B: {botBWins}{Ansi.Reset}  " +
            $"{Ansi.Dim}Ties: {ties}{Ansi.Reset}");
        var winnerColor = overallWinner == "Bot A" ? Ansi.Blue : overallWinner == "Bot B" ? Ansi.Green : Ansi.Dim;
        Console.WriteLine($"\n  {Ansi.Bold}Overall winner:{Ansi.Reset} {winnerColor}{Ansi.Bold}{overallWinner}{Ansi.Reset}");
    }
}
else
{
    if (spectreRenderer == null)
    {
        Console.WriteLine($"\n{Separator('═')}");
        Console.WriteLine($"  {Ansi.Bold}⚔  Debate Complete{Ansi.Reset}");
        Console.WriteLine(Separator('═'));
    }
}

// Print cost summary
if (spectreRenderer != null)
    spectreRenderer.PrintCostSummary(costAccumulator.FormatSummary());
else
{
    Console.WriteLine($"\n  {Ansi.Dim}{costAccumulator.FormatSummary()}{Ansi.Reset}");
    Console.WriteLine($"\n{Separator('═')}\n");
}

// Print stance evolution if enabled
if (stanceMeterService != null && (botATimeline.Entries.Count > 0 || botBTimeline.Entries.Count > 0))
{
    StanceMeterService.PrintStanceEvolution(botATimeline, botBTimeline);
    var ips = StanceMeterService.CalculateIntellectualProgressScore(botATimeline, botBTimeline, maxRounds);
    Console.WriteLine($"  Intellectual Progress Score: {ips:F2}");

    // Persist cross-session drift records for use by `memory drift` / `memory drift-compare`
    var sqliteForDriftSave = AsSqlite(memory);
    if (sqliteForDriftSave != null)
    {
        try
        {
            var driftSvc = new OpinionDriftService(sqliteForDriftSave.GetConnection());
            if (botATimeline.InitialStance.HasValue && botATimeline.FinalStance.HasValue)
            {
                await driftSvc.SaveDriftRecordAsync(new OpinionDriftRecord
                {
                    SessionId     = sessionId,
                    Topic         = topic,
                    BotName       = "Bot A",
                    Model         = botAClient.ModelId,
                    Persona       = botAPersona.Name,
                    InitialStance = botATimeline.InitialStance.Value,
                    FinalStance   = botATimeline.FinalStance.Value,
                    StanceDelta   = botATimeline.StanceDelta,
                    SessionDate   = sessionStart,
                });
            }
            if (botBTimeline.InitialStance.HasValue && botBTimeline.FinalStance.HasValue)
            {
                await driftSvc.SaveDriftRecordAsync(new OpinionDriftRecord
                {
                    SessionId     = sessionId,
                    Topic         = topic,
                    BotName       = "Bot B",
                    Model         = botBClient.ModelId,
                    Persona       = botBPersona.Name,
                    InitialStance = botBTimeline.InitialStance.Value,
                    FinalStance   = botBTimeline.FinalStance.Value,
                    StanceDelta   = botBTimeline.StanceDelta,
                    SessionDate   = sessionStart,
                });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{Ansi.Dim}[Drift] Failed to save drift records: {ex.Message}{Ansi.Reset}");
        }
    }
}

// ─── Wave 3: Analytics heatmap ────────────────────────────────────────────────

if (analyticsScorer != null && allStrengthScores.Count > 0)
{
    Console.WriteLine($"\n{Ansi.Bold}Argument Strength Heatmap:{Ansi.Reset}");
    Console.Write(HeatmapRenderer.RenderHeatmap(allStrengthScores));
}

// ─── Wave 3: Dialectical Arbiter ─────────────────────────────────────────────

if (dialecticalArbiter != null && !string.IsNullOrEmpty(finalSynthesis))
{
    try
    {
        Console.WriteLine($"\n  {Ansi.Dim}Dialectical synthesis in progress...{Ansi.Reset}");
        var synthesis = await dialecticalArbiter.SynthesizeAsync(memoryEntries, finalSynthesis);
        static string TruncBox(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
        Console.WriteLine($"\n╔{'═',58}╗");
        Console.WriteLine($"║{"  Dialectical Synthesis",-58}║");
        Console.WriteLine($"╠{'═',58}╣");
        Console.WriteLine($"║ Thesis:      {TruncBox(synthesis.CoreThesis, 44),-44} ║");
        Console.WriteLine($"║ Antithesis:  {TruncBox(synthesis.CoreAntithesis, 44),-44} ║");
        Console.WriteLine($"║ Synthesis:   {TruncBox(synthesis.Synthesis, 44),-44} ║");
        foreach (var q in synthesis.OpenQuestions.Take(3))
            Console.WriteLine($"║ Open:        {TruncBox(q, 44),-44} ║");
        Console.WriteLine($"╚{'═',58}╝");
        memoryEntries.Add(new MemoryEntry(Guid.NewGuid(), "Arbiter", topic, maxRounds, "dialectical-synthesis",
            synthesis.Synthesis, DateTimeOffset.UtcNow, [topic, "arbiter"]));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{Ansi.Dim}[Arbiter] Synthesis failed: {ex.Message}{Ansi.Reset}");
    }
}

// ─── Wave 3: Red Team scorecard ──────────────────────────────────────────────

if (vulnerabilityTracker != null && vulnerabilityTracker.Records.Count > 0)
{
    Console.WriteLine(vulnerabilityTracker.RenderScorecard());
}

// ─── Wave 4: Highlight reel ──────────────────────────────────────────────────

if (config.Highlights && allArguments.Count > 0)
{
    var highlights = VibeWars.Highlights.HighlightService.ExtractHighlights(
        allStrengthScores, momentumTracker?.Events ?? [], allArguments);
    highlights = VibeWars.Highlights.HighlightService.AddNarratives(
        highlights, momentumTracker?.Events ?? []);
    Console.Write(VibeWars.Highlights.HighlightService.RenderHighlights(highlights));
}

// ─── Wave 3: Hidden objective detection ──────────────────────────────────────

if (!string.IsNullOrWhiteSpace(config.HiddenObjectiveA) || !string.IsNullOrWhiteSpace(config.HiddenObjectiveB))
{
    if (config.RevealObjectives)
    {
        Console.WriteLine($"\n  {Ansi.Dim}🕵 Hidden Objectives Revealed:{Ansi.Reset}");
        if (!string.IsNullOrWhiteSpace(config.HiddenObjectiveA))
            Console.WriteLine($"    Bot A: {config.HiddenObjectiveA}");
        if (!string.IsNullOrWhiteSpace(config.HiddenObjectiveB))
            Console.WriteLine($"    Bot B: {config.HiddenObjectiveB}");
    }
    try
    {
        var detector = new ObjectiveDetectorService(judgeClient);
        var transcript = string.Join("\n\n", memoryEntries
            .Where(e => e.Role == "assistant" && (e.BotName == "Bot A" || e.BotName == "Bot B"))
            .Select(e => $"[{e.BotName}]: {e.Content}"));
        var detection = await detector.DetectAsync(transcript);
        Console.WriteLine($"\n  🕵 Hidden Objective Detection:");
        Console.WriteLine($"    Bot A: \"{detection.BotADetected}\" (execution: {detection.BotAScore}/10)");
        Console.WriteLine($"    Bot B: \"{detection.BotBDetected}\" (execution: {detection.BotBScore}/10)");
        memoryEntries.Add(new MemoryEntry(Guid.NewGuid(), "Judge", topic, maxRounds, "hidden-objective",
            $"A: {detection.BotADetected} ({detection.BotAScore}/10) | B: {detection.BotBDetected} ({detection.BotBScore}/10)",
            DateTimeOffset.UtcNow, [topic, "hidden-objective"]));
    }
    catch { /* non-fatal */ }
}

// ─── Follow-up topic chain ───────────────────────────────────────────────────

if (config.Chain && !string.IsNullOrEmpty(finalSynthesis))
{
    try
    {
        var chainDepth = Math.Max(1, config.ChainDepth);
        var chainSynthesis = finalSynthesis;
        var allChainTopics = new List<FollowUpTopic>();

        for (var depth = 1; depth <= chainDepth; depth++)
        {
            Console.WriteLine($"\n  {Ansi.Dim}Generating follow-up topics (depth {depth}/{chainDepth})...{Ansi.Reset}");
            var followUpPrompt = FollowUpService.BuildFollowUpPrompt(chainSynthesis);
            var (followUpReply, _) = await judgeClient.ChatAsync(
                "You generate follow-up debate topics based on a completed debate synthesis.",
                [new ChatMessage("user", followUpPrompt)]);
            var followUps = FollowUpService.ParseFollowUps(followUpReply);
            if (followUps.Count == 0) break;

            allChainTopics.AddRange(followUps);
            memoryEntries.Add(new MemoryEntry(Guid.NewGuid(), "Judge", topic, 0, "follow-up",
                followUpReply, DateTimeOffset.UtcNow, [topic, "follow-up", $"depth-{depth}"]));

            // Feed the first generated topic back as the next synthesis to go deeper
            if (depth < chainDepth)
                chainSynthesis = $"Building on the topic \"{followUps[0].Topic}\": {followUps[0].Rationale}";
        }

        if (allChainTopics.Count > 0)
            Console.WriteLine(FollowUpService.FormatFollowUpDisplay(allChainTopics));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{Ansi.Dim}[FollowUp] Follow-up generation failed: {ex.Message}{Ansi.Reset}");
    }
}

// ─── Session persistence ──────────────────────────────────────────────────────

if (memory is not null && memoryEntries.Count > 0)
{
    try
    {
        var session = new DebateSession(
            sessionId,
            topic,
            sessionStart,
            DateTimeOffset.UtcNow,
            overallWinner,
            finalSynthesis,
            debateFormat.ToString(),
            costAccumulator.TotalTokens,
            costAccumulator.TotalEstimatedCostUsd,
            config.Complexity);

        await memory.SaveSessionAsync(session, memoryEntries);
        Console.WriteLine($"{Ansi.Dim}✔ Session saved (id: {sessionId}){Ansi.Reset}\n");

        // Auto-generate HTML report if requested
        if (config.PostDebateReport)
        {
            try
            {
                var reportPath = $"vibewars-report-{sessionId}.html";
                var html = VibeWars.Reports.DebateReportGenerator.GenerateHtml(session, memoryEntries);
                File.WriteAllText(reportPath, html);
                Console.WriteLine($"{Ansi.Dim}✔ Report written to {reportPath}{Ansi.Reset}\n");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{Ansi.Dim}⚠ Report generation failed: {ex.Message}{Ansi.Reset}");
            }
        }

        // ─── Auto-summarization (Bot Growth) ──────────────────────────────────
        var sqliteStore = AsSqlite(memory);
        if (sqliteStore != null)
        {
            var topicEntryCount = sqliteStore.CountEntriesForTopic(topic);
            if (topicEntryCount >= summarizeThreshold)
            {
                Console.WriteLine($"{Ansi.Dim}🧠 Auto-summarizing accumulated knowledge for topic \"{topic}\"…{Ansi.Reset}");
                try
                {
                    var recentEntries = await memory.SearchAsync(topic, summarizeThreshold);
                    const int maxSummarizeChars = 2000;
                    var entriesText = string.Join("\n", recentEntries.Select(e => $"- [{e.BotName}]: {e.Content}"));
                    if (entriesText.Length > maxSummarizeChars)
                        entriesText = entriesText[..maxSummarizeChars] + "…";
                    var summaryPrompt = $"""
The following are debate entries about "{topic}". Distill them into a 3-5 sentence canonical knowledge summary that captures the most important insights.

{entriesText}
""";
                    var (summaryText, _) = await judgeClient.ChatAsync(judgeSystem, [new ChatMessage("user", summaryPrompt)]);
                    var summaryEntry = new MemoryEntry(
                        Guid.NewGuid(),
                        "Judge",
                        topic,
                        0,
                        "summary",
                        summaryText,
                        DateTimeOffset.UtcNow,
                        ["auto-summary", topic]);

                    await memory.SaveSessionAsync(session with { SessionId = Guid.NewGuid() }, [summaryEntry]);
                    Console.WriteLine($"{Ansi.Dim}✔ Knowledge summary saved for \"{topic}\".{Ansi.Reset}\n");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"{Ansi.Dim}[Memory] Auto-summarization failed: {ex.Message}{Ansi.Reset}");
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{Ansi.Dim}⚠ Session save failed: {ex.Message}{Ansi.Reset}");
    }
}

// ─── Embedding generation ────────────────────────────────────────────────────

var sqliteForEmbedding = AsSqlite(memory);
if (embeddingClient != null && sqliteForEmbedding != null && memoryEntries.Count > 0)
{
    try
    {
        var batches = memoryEntries.Chunk(20);
        foreach (var batch in batches)
        {
            var texts = batch.Select(e => e.Content).ToArray();
            var embeddings = await embeddingClient.EmbedBatchAsync(texts);
            for (var i = 0; i < batch.Length; i++)
                await sqliteForEmbedding.SaveEmbeddingAsync(batch[i].Id, embeddings[i]);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{Ansi.Dim}[Embeddings] Failed to generate embeddings: {ex.Message}{Ansi.Reset}");
    }
}

embeddingClient?.Dispose();
judgePanel?.Dispose();
commentatorClient?.Dispose();

// ─── ELO rating update ──────────────────────────────────────────────────────

var sqliteForElo = AsSqlite(memory);
if (config.EloTracking && sqliteForElo != null && roundWinners.Count > 0)
{
    try
    {
        var eloService = new VibeWars.Elo.EloService(sqliteForElo.GetConnection());
        var contestantA = $"{botAClient.ProviderName}/{botAClient.ModelId}/{botAPersona.Name}";
        var contestantB = $"{botBClient.ProviderName}/{botBClient.ModelId}/{botBPersona.Name}";
        var isDraw = overallWinner == "Tie";
        var winnerId = overallWinner == "Bot A" ? contestantA : contestantB;
        var loserId  = overallWinner == "Bot A" ? contestantB : contestantA;
        await eloService.UpdateRatingsAsync(winnerId, loserId, isDraw, isTournament: false);
        var winnerRecord = await eloService.GetOrCreateAsync(winnerId);
        var loserRecord  = await eloService.GetOrCreateAsync(loserId);
        var eloA = winnerRecord.ContestantId == contestantA ? winnerRecord.Rating : loserRecord.Rating;
        var eloB = loserRecord.ContestantId == contestantB ? loserRecord.Rating : winnerRecord.Rating;
        Console.WriteLine($"\n  {Ansi.Dim}📈 ELO: {contestantA} → {eloA:F0}  {contestantB} → {eloB:F0}{Ansi.Reset}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{Ansi.Dim}[ELO] Rating update failed: {ex.Message}{Ansi.Reset}");
    }
}

// ─── Wave 6: Personality evolution update ─────────────────────────────────────

if (personalityService != null && roundWinners.Count > 0)
{
    try
    {
        var contestantIdA = $"{botAClient.ProviderName}/{botAClient.ModelId}/{botAPersona.Name}";
        var contestantIdB = $"{botBClient.ProviderName}/{botBClient.ModelId}/{botBPersona.Name}";
        personalityService.UpdateAfterDebate(contestantIdA, overallWinner == "Bot A", false,
            roundWinners.Count(w => w.Contains("Bot A", StringComparison.OrdinalIgnoreCase)), 0, "");
        personalityService.UpdateAfterDebate(contestantIdB, overallWinner == "Bot B", false,
            roundWinners.Count(w => w.Contains("Bot B", StringComparison.OrdinalIgnoreCase)), 0, "");
        Console.WriteLine($"\n  {Ansi.Dim}Personality traits updated.{Ansi.Reset}");
    }
    catch { /* non-fatal */ }
}

// ─── Wave 6: Debate card generation ──────────────────────────────────────────

if (config.DebateCard && roundWinners.Count > 0)
{
    try
    {
        var botAWinsCard = roundWinners.Count(w => w.Contains("Bot A", StringComparison.OrdinalIgnoreCase));
        var botBWinsCard = roundWinners.Count(w => w.Contains("Bot B", StringComparison.OrdinalIgnoreCase));
        var cardSession = new DebateSession(sessionId, topic, sessionStart, DateTimeOffset.UtcNow,
            overallWinner, finalSynthesis, debateFormat.ToString(), costAccumulator.TotalTokens,
            costAccumulator.TotalEstimatedCostUsd, config.Complexity);
        var bestArg = allArguments.OrderByDescending(a =>
            allStrengthScores.FirstOrDefault(s => s.Round == a.Round && s.BotName == a.BotName)?.Composite ?? 0)
            .FirstOrDefault();
        var svgCard = VibeWars.Reports.DebateCardGenerator.GenerateSvg(
            cardSession, 1200, 1200, botAWinsCard, botBWinsCard,
            bestArg.Content);
        var cardPath = $"vibewars-card-{sessionId}.svg";
        File.WriteAllText(cardPath, svgCard);
        Console.WriteLine($"{Ansi.Dim}✔ Debate card saved to {cardPath}{Ansi.Reset}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{Ansi.Dim}[DebateCard] Generation failed: {ex.Message}{Ansi.Reset}");
    }
}

// ─── Cleanup knowledge source ────────────────────────────────────────────────

(knowledgeSource as IDisposable)?.Dispose();

// ─── Webhook on-complete ──────────────────────────────────────────────────────

if (webhookService != null && webhookConfig != null && webhookConfig.WebhookOnComplete && memoryEntries.Count > 0)
{
    try
    {
        var completionSession = new DebateSession(
            sessionId, topic, sessionStart, DateTimeOffset.UtcNow,
            overallWinner, finalSynthesis, debateFormat.ToString(),
            costAccumulator.TotalTokens, costAccumulator.TotalEstimatedCostUsd,
            config.Complexity);
        await webhookService.PostDebateSummaryAsync(completionSession, memoryEntries, webhookConfig);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Webhook] Failed to post completion summary: {ex.Message}");
    }
}

webhookHttpClient?.Dispose();

// ─── Web dashboard final status & shutdown ────────────────────────────────────

if (webDashboard != null)
{
    webDashboard.SetStatus(new { status = "complete", winner = overallWinner });
    webDashboard.PublishEvent(new DebateEvent("complete", maxRounds, $"Debate complete. Winner: {overallWinner}", null));
    await webDashboard.StopAsync();
}

// ─── Tournament command ────────────────────────────────────────────────────────

static async Task RunTournamentCommand(string[] args, VibeWarsConfig config)
{
    var topic = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "artificial intelligence";
    Console.WriteLine($"\n⚔ VibeWars Tournament: \"{topic}\"");

    // Default contestants
    var contestants = new List<TournamentContestant>
    {
        new("Pragmatist-GPT4o",  "openrouter", "openai/gpt-4o-mini",        "Pragmatist"),
        new("Idealist-Nova",     "bedrock",    "amazon.nova-lite-v1:0",      "Idealist"),
        new("Empiricist-Haiku",  "openrouter", "anthropic/claude-3-5-haiku", "Empiricist"),
        new("Ethicist-Nova",     "bedrock",    "amazon.nova-micro-v1:0",     "Ethicist"),
    };

    string? contestantsFile = null;
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == "--contestants") contestantsFile = args[i + 1];

    if (contestantsFile != null && File.Exists(contestantsFile))
    {
        contestants = File.ReadAllLines(contestantsFile)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
            .Select(l => {
                var parts = l.Split(',');
                if (parts.Length < 3) return null;
                return new TournamentContestant(parts[0].Trim(), parts[1].Trim(), parts[2].Trim(),
                    parts.Length > 3 ? parts[3].Trim() : "Pragmatist");
            })
            .Where(c => c is not null)
            .Cast<TournamentContestant>()
            .ToList();
    }

    var bracket = new TournamentBracket(contestants);
    var results = new List<TournamentResult>();
    var current = new List<TournamentContestant>(contestants);

    Console.WriteLine(TournamentBracket.RenderBracket(contestants));

    var openRouterKey = config.OpenRouterApiKey;
    var awsRegion     = config.AwsRegion;

    while (current.Count > 1)
    {
        var nextRound = new List<TournamentContestant>();
        for (var i = 0; i + 1 < current.Count; i += 2)
        {
            var cA = current[i];
            var cB = current[i + 1];
            Console.WriteLine($"\n  Match: {cA.Name} vs {cB.Name}");

            using var clientA = new ResilientChatClient(CreateClient(cA.Provider, cA.Model, openRouterKey, awsRegion), config.RetryMax, config.RetryBaseDelayMs);
            using var clientB = new ResilientChatClient(CreateClient(cB.Provider, cB.Model, openRouterKey, awsRegion), config.RetryMax, config.RetryBaseDelayMs);
            using var judge   = new ResilientChatClient(CreateClient(config.JudgeProvider ?? (openRouterKey is not null ? "openrouter" : "bedrock"),
                config.JudgeModel ?? (openRouterKey is not null ? "openai/gpt-4o-mini" : "amazon.nova-lite-v1:0"),
                openRouterKey, awsRegion), config.RetryMax, config.RetryBaseDelayMs);

            var (winner, aScore, bScore) = await RunMinimatchAsync(topic, cA, cB, clientA, clientB, judge, config);
            var result = new TournamentResult(
                new TournamentMatch(i + 1, cA, cB),
                winner == "A" ? cA : cB,
                winner == "A" ? cB : cA,
                winner == "A" ? aScore : bScore,
                winner == "A" ? bScore : aScore);
            results.Add(result);
            nextRound.Add(result.Winner);
            Console.WriteLine($"  → Winner: {result.Winner.Name}");
        }
        if (current.Count % 2 == 1) nextRound.Add(current[^1]); // bye
        current = nextRound;
    }

    var champion = current.FirstOrDefault();
    Console.WriteLine(TournamentBracket.RenderBracket(contestants, results));
    Console.WriteLine($"\n🏆 Champion: {champion?.Name ?? "No winner"} ({champion?.Model})");
    Console.WriteLine($"   Persona: {champion?.Persona}");
    Console.WriteLine();
}

static async Task<(string Winner, int AScore, int BScore)> RunMinimatchAsync(
    string topic, TournamentContestant cA, TournamentContestant cB,
    IChatClient clientA, IChatClient clientB, IChatClient judgeClient, VibeWarsConfig config)
{
    var botASystem = $"You are {cA.Name}, a {cA.Persona} debater. Topic: \"{topic}\". Be concise (2-3 sentences).";
    var botBSystem = $"You are {cB.Name}, a {cB.Persona} debater. Topic: \"{topic}\". Be concise (2-3 sentences).";
    var judgeSystem = $$"""
You are a debate judge. Topic: "{{topic}}". After the exchange, respond with JSON only:
{"winner": "<A|B|Tie>", "reasoning": "<1 sentence>"}
""";

    var histA = new List<ChatMessage>();
    var histB = new List<ChatMessage>();
    var histJ = new List<ChatMessage>();
    var aWins = 0; var bWins = 0;
    string lastReplyA = string.Empty;
    string lastReplyB = string.Empty;

    try
    {
        for (var round = 1; round <= Math.Min(config.MaxRounds, 2); round++)
        {
            histA.Add(new ChatMessage("user", round == 1
                ? $"Debate topic: {topic}. Give your opening."
                : $"{cB.Name} said: \"{lastReplyB}\". Respond."));
            var (replyA, _) = await clientA.ChatAsync(botASystem, histA);
            histA.Add(new ChatMessage("assistant", replyA));
            lastReplyA = replyA;

            histB.Add(new ChatMessage("user", round == 1
                ? $"Topic: {topic}. {cA.Name} opened with: \"{lastReplyA}\". Give your response."
                : $"{cA.Name} said: \"{lastReplyA}\". Respond."));
            var (replyB, _) = await clientB.ChatAsync(botBSystem, histB);
            histB.Add(new ChatMessage("assistant", replyB));
            lastReplyB = replyB;

            var judgePrompt = $"Round {round}:\n{cA.Name}: \"{replyA}\"\n{cB.Name}: \"{replyB}\"";
            histJ.Add(new ChatMessage("user", judgePrompt));
            var (judgeReply, _) = await judgeClient.ChatAsync(judgeSystem, histJ);
            histJ.Add(new ChatMessage("assistant", judgeReply));

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(judgeReply);
                var w = doc.RootElement.TryGetProperty("winner", out var wr) ? wr.GetString() ?? "Tie" : "Tie";
                if (w.Equals("A", StringComparison.OrdinalIgnoreCase) || w.Contains("Bot A", StringComparison.OrdinalIgnoreCase)) aWins++;
                else if (w.Equals("B", StringComparison.OrdinalIgnoreCase) || w.Contains("Bot B", StringComparison.OrdinalIgnoreCase)) bWins++;
            }
            catch { /* ignore parse errors */ }
        }
    }
    catch { /* graceful degradation */ }

    // On a genuine tie, pick the winner randomly to avoid systematic first-seed bias.
    var winner = aWins > bWins ? "A" : bWins > aWins ? "B" : (Random.Shared.Next(2) == 0 ? "A" : "B");
    return (winner, aWins, bWins);
}

// ─── Batch command ─────────────────────────────────────────────────────────────

static async Task RunBatchCommand(string[] args, VibeWarsConfig config)
{
    string? topicsFile = null;
    var parallel = 1;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--parallel" && i + 1 < args.Length) int.TryParse(args[++i], out parallel);
        else if (!args[i].StartsWith('-')) topicsFile = args[i];
    }

    topicsFile ??= Environment.GetEnvironmentVariable("VIBEWARS_BATCH_TOPICS_FILE");
    if (topicsFile == null || !File.Exists(topicsFile))
    {
        Console.WriteLine("Usage: batch <topics-file> [--parallel <n>]");
        Console.WriteLine("Topics file: one topic per line.");
        return;
    }

    var topics = File.ReadAllLines(topicsFile)
        .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
        .ToList();

    if (topics.Count == 0)
    {
        Console.WriteLine("No topics found in file.");
        return;
    }

    Console.WriteLine($"\n⚔ VibeWars Batch Mode: {topics.Count} topics, parallel={Math.Max(1, parallel)}");
    Console.WriteLine(new string('─', 60));

    var openRouterKey = config.OpenRouterApiKey;
    var awsRegion     = config.AwsRegion;
    var summaryTable  = new System.Collections.Concurrent.ConcurrentBag<(string Topic, string Winner, int ARounds, int BRounds)>();
    var consoleLock   = new object();

    var semaphore = new SemaphoreSlim(Math.Max(1, parallel));

    var tasks = topics.Select(async topic =>
    {
        await semaphore.WaitAsync();
        try
        {
            lock (consoleLock) Console.WriteLine($"  ▶ Starting: {topic}");

            using var clientA = new ResilientChatClient(
                CreateClient(config.BotAProvider ?? (openRouterKey is not null ? "openrouter" : "bedrock"),
                    config.BotAModel ?? (openRouterKey is not null ? "openai/gpt-4o-mini" : "amazon.nova-lite-v1:0"),
                    openRouterKey, awsRegion), config.RetryMax, config.RetryBaseDelayMs);
            using var clientB = new ResilientChatClient(
                CreateClient(config.BotBProvider ?? "bedrock",
                    config.BotBModel ?? "amazon.nova-lite-v1:0",
                    openRouterKey, awsRegion), config.RetryMax, config.RetryBaseDelayMs);
            using var judgeC = new ResilientChatClient(
                CreateClient(config.JudgeProvider ?? (openRouterKey is not null ? "openrouter" : "bedrock"),
                    config.JudgeModel ?? (openRouterKey is not null ? "openai/gpt-4o-mini" : "amazon.nova-lite-v1:0"),
                    openRouterKey, awsRegion), config.RetryMax, config.RetryBaseDelayMs);

            var cA = new TournamentContestant("Bot A", config.BotAProvider ?? "bedrock",
                config.BotAModel ?? "amazon.nova-lite-v1:0", config.BotAPersona ?? "Pragmatist");
            var cB = new TournamentContestant("Bot B", config.BotBProvider ?? "bedrock",
                config.BotBModel ?? "amazon.nova-lite-v1:0", config.BotBPersona ?? "Idealist");

            var (winner, aWins, bWins) = await RunMinimatchAsync(topic, cA, cB, clientA, clientB, judgeC, config);
            var winnerName = winner == "A" ? "Bot A" : winner == "B" ? "Bot B" : "Tie";
            summaryTable.Add((topic, winnerName, aWins, bWins));
            lock (consoleLock) Console.WriteLine($"  ✔ Done:     {topic} → {winnerName} (A:{aWins} B:{bWins})");
        }
        catch (Exception ex)
        {
            summaryTable.Add((topic, "Error", 0, 0));
            lock (consoleLock) Console.Error.WriteLine($"  ✘ Failed:   {topic}: {ex.Message}");
        }
        finally
        {
            semaphore.Release();
        }
    }).ToList();

    await Task.WhenAll(tasks);

    Console.WriteLine("\n═══════════════════════════════════════════════════════════");
    Console.WriteLine("  Batch Summary");
    Console.WriteLine("═══════════════════════════════════════════════════════════");
    Console.WriteLine($"{"Topic",-40} {"Winner",-10} {"A wins",-8} {"B wins",-8}");
    Console.WriteLine(new string('─', 68));
    foreach (var (t, w, a, b) in summaryTable.OrderBy(x => x.Topic))
        Console.WriteLine($"{(t.Length > 38 ? t[..38] + "…" : t),-40} {w,-10} {a,-8} {b,-8}");
    Console.WriteLine();
}

// ─── Replay command ────────────────────────────────────────────────────────────

static async Task RunReplayCommand(string[] args, VibeWarsConfig config)
{
    if (args.Length > 0 && args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
    {
        using var store = new SqliteMemoryStore(config.DbPath);
        var listSessions = await store.ListSessionsAsync(200);
        var replaySessions = listSessions.Where(s => s.Format?.Equals("replay", StringComparison.OrdinalIgnoreCase) == true).ToList();
        if (replaySessions.Count == 0) { Console.WriteLine("No replay sessions found."); return; }
        Console.WriteLine($"\n{"SessionId",-38}  {"Topic",-30}  {"Date",-12}  {"Winner",-10}");
        Console.WriteLine(new string('─', 96));
        foreach (var s in replaySessions)
            Console.WriteLine($"{s.SessionId,-38}  {(s.Topic.Length > 30 ? s.Topic[..29] + "…" : s.Topic),-30}  {s.StartedAt:yyyy-MM-dd}  {s.OverallWinner,-10}");
        Console.WriteLine();
        return;
    }

    if (!Guid.TryParse(args.FirstOrDefault(a => !a.StartsWith('-')), out var originalSessionId))
    {
        Console.WriteLine("Usage: replay <originalSessionId> [--bot-a-model <model>] [--bot-a-persona <name>] [--bot-b-model <model>] [--bot-b-persona <name>] [--keep-judge]");
        Console.WriteLine("       replay list");
        return;
    }

    string? replaceBotAModel = null, replaceBotAPersona = null, replaceBotBModel = null, replaceBotBPersona = null;
    var keepJudge = false;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--bot-a-model"   when i + 1 < args.Length: replaceBotAModel   = args[++i]; break;
            case "--bot-a-persona" when i + 1 < args.Length: replaceBotAPersona = args[++i]; break;
            case "--bot-b-model"   when i + 1 < args.Length: replaceBotBModel   = args[++i]; break;
            case "--bot-b-persona" when i + 1 < args.Length: replaceBotBPersona = args[++i]; break;
            case "--keep-judge": keepJudge = true; break;
        }
    }

    var replayConfig = new ReplayConfig(originalSessionId, replaceBotAModel, replaceBotAPersona, replaceBotBModel, replaceBotBPersona, keepJudge);
    if (!replayConfig.IsValid)
    {
        Console.WriteLine("At least one of --bot-a-model, --bot-a-persona, --bot-b-model, --bot-b-persona must be specified.");
        return;
    }

    using var replayStore = new SqliteMemoryStore(config.DbPath);
    var originalEntries = await replayStore.GetSessionEntriesAsync(originalSessionId);
    if (originalEntries.Count == 0)
    {
        Console.WriteLine($"No entries found for session {originalSessionId}.");
        return;
    }

    var sessions = await replayStore.ListSessionsAsync(1000);
    var originalSession = sessions.FirstOrDefault(s => s.SessionId == originalSessionId);
    if (originalSession is null) { Console.WriteLine("Original session not found."); return; }

    Console.WriteLine($"\n⚔  VibeWars Replay");
    Console.WriteLine($"  Original: {originalSessionId}  Topic: {originalSession.Topic}");
    var judgeLabel = keepJudge ? "(original model)" : "(same as config)";
    Console.WriteLine($"  Replacing: BotA={replaceBotAModel ?? "(same)"}  BotB={replaceBotBModel ?? "(same)"}  Judge={judgeLabel}");
    Console.WriteLine();

    var openRouterKey = config.OpenRouterApiKey;
    var awsRegion     = config.AwsRegion;
    var botAModelReplay  = replaceBotAModel ?? config.BotAModel ?? (openRouterKey is not null ? "openai/gpt-4o-mini" : "amazon.nova-lite-v1:0");
    var botBModelReplay  = replaceBotBModel ?? config.BotBModel ?? "amazon.nova-lite-v1:0";

    // When --keep-judge is set, derive the judge model from the original session's stored judge verdict entries
    // (they share the same judge model that was used during the original debate run).
    // Fall back to config when the original session predates this tracking.
    var judgeModelReplay = config.JudgeModel ?? (openRouterKey is not null ? "openai/gpt-4o-mini" : "amazon.nova-lite-v1:0");
    if (keepJudge)
        Console.WriteLine($"  {Ansi.Dim}--keep-judge: using configured judge model ({judgeModelReplay}) to match original session.{Ansi.Reset}");

    using var replayBotA  = new ResilientChatClient(CreateClient(config.BotAProvider ?? (openRouterKey is not null ? "openrouter" : "bedrock"), botAModelReplay, openRouterKey, awsRegion), config.RetryMax, config.RetryBaseDelayMs);
    using var replayBotB  = new ResilientChatClient(CreateClient(config.BotBProvider ?? "bedrock", botBModelReplay, openRouterKey, awsRegion), config.RetryMax, config.RetryBaseDelayMs);
    using var replayJudge = new ResilientChatClient(CreateClient(config.JudgeProvider ?? (openRouterKey is not null ? "openrouter" : "bedrock"), judgeModelReplay, openRouterKey, awsRegion), config.RetryMax, config.RetryBaseDelayMs);

    // Reconstruct debate history and re-run with new models
    var replaySessionId = Guid.NewGuid();
    var replayEntries   = new List<MemoryEntry>();
    var roundResults    = new List<CounterfactualRoundResult>();

    var botAPersonaReplay = PersonaLibrary.Resolve(replaceBotAPersona ?? config.BotAPersona ?? "");
    var botBPersonaReplay = PersonaLibrary.Resolve(replaceBotBPersona ?? config.BotBPersona ?? "");

    var topic = originalSession.Topic;
    var botASystemReplay = $"You are Bot A, debating: \"{topic}\". {botAPersonaReplay.StyleDescription} Be concise (3-5 sentences per turn).";
    var botBSystemReplay = $"You are Bot B, debating: \"{topic}\". {botBPersonaReplay.StyleDescription} Be concise (3-5 sentences per turn).";
    var judgeSystemReplay = $$"""
You are a debate judge. Topic: "{{topic}}". Respond with JSON: {"winner": "<Bot A|Bot B|Tie>", "reasoning": "<1-2 sentences>", "new_ideas": ""}
""";

    var histA = new List<ChatMessage>();
    var histB = new List<ChatMessage>();
    var histJ = new List<ChatMessage>();
    string? lastBotAReplay = null, lastBotBReplay = null;

    var originalByRound = originalEntries.Where(e => e.Role == "assistant")
        .GroupBy(e => e.Round)
        .ToDictionary(g => g.Key, g => g.ToList());
    var originalWinnersByRound = originalEntries.Where(e => e.Role == "assistant" && e.BotName == "Judge")
        .GroupBy(e => e.Round)
        .ToDictionary(g => g.Key, g => {
            var e = g.First();
            try { var doc = JsonDocument.Parse(e.Content); return doc.RootElement.TryGetProperty("winner", out var w) ? w.GetString() : null; }
            catch { return (string?)null; }
        });

    var maxReplayRounds = originalByRound.Count > 0 ? originalByRound.Keys.Max() : config.MaxRounds;
    for (var round = 1; round <= maxReplayRounds; round++)
    {
        try
        {
            var botAPrompt = round == 1
                ? $"Topic: \"{topic}\". Present your opening argument."
                : $"Bot B said: \"{lastBotBReplay}\". Respond and advance your position.";
            histA.Add(new ChatMessage("user", botAPrompt));
            var (replyA, _) = await replayBotA.ChatAsync(botASystemReplay, histA);
            histA.Add(new ChatMessage("assistant", replyA));
            lastBotAReplay = replyA;
            Console.WriteLine($"\n  [Bot A — Round {round}] {(replyA.Length > 80 ? replyA[..79] + "…" : replyA)}");

            var botBPrompt = round == 1
                ? $"Topic: \"{topic}\". Bot A: \"{lastBotAReplay}\". Give your response."
                : $"Bot A said: \"{lastBotAReplay}\". Respond and advance your position.";
            histB.Add(new ChatMessage("user", botBPrompt));
            var (replyB, _) = await replayBotB.ChatAsync(botBSystemReplay, histB);
            histB.Add(new ChatMessage("assistant", replyB));
            lastBotBReplay = replyB;
            Console.WriteLine($"  [Bot B — Round {round}] {(replyB.Length > 80 ? replyB[..79] + "…" : replyB)}");

            var judgePrompt = $"Round {round}:\nBot A: \"{replyA}\"\nBot B: \"{replyB}\"";
            histJ.Add(new ChatMessage("user", judgePrompt));
            var (judgeReply, _) = await replayJudge.ChatAsync(judgeSystemReplay, histJ);
            histJ.Add(new ChatMessage("assistant", judgeReply));

            string? replayWinner = null;
            try { using var doc = JsonDocument.Parse(judgeReply); replayWinner = doc.RootElement.TryGetProperty("winner", out var w) ? w.GetString() : "Tie"; }
            catch { replayWinner = "Tie"; }

            originalWinnersByRound.TryGetValue(round, out var originalWinner);
            roundResults.Add(new CounterfactualRoundResult(round, originalWinner, replayWinner));
            Console.WriteLine($"  [Judge — Round {round}] Original: {originalWinner ?? "?"} → Replay: {replayWinner}");

            replayEntries.Add(new MemoryEntry(Guid.NewGuid(), "Bot A", topic, round, "assistant", replyA, DateTimeOffset.UtcNow, [topic, "replay"]));
            replayEntries.Add(new MemoryEntry(Guid.NewGuid(), "Bot B", topic, round, "assistant", replyB, DateTimeOffset.UtcNow, [topic, "replay"]));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [Round {round}] Error: {ex.Message}");
            break;
        }
    }

    var replayBotAWins = roundResults.Count(r => r.ReplayWinner?.Contains("Bot A", StringComparison.OrdinalIgnoreCase) == true);
    var replayBotBWins = roundResults.Count(r => r.ReplayWinner?.Contains("Bot B", StringComparison.OrdinalIgnoreCase) == true);
    var replayOverallWinner = roundResults.Count == 0 || replayBotAWins == replayBotBWins
        ? "Tie"
        : replayBotAWins > replayBotBWins ? "Bot A" : "Bot B";

    var comparisonReport = CounterfactualReplayService.BuildComparisonReport(
        originalSessionId, replaySessionId, roundResults, originalSession.OverallWinner, replayOverallWinner);

    Console.WriteLine(CounterfactualReplayService.RenderComparisonReport(comparisonReport));

    // Save replay session
    var replaySession = new DebateSession(replaySessionId, topic, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        replayOverallWinner, $"Replay of {originalSessionId}", "replay", 0, null);
    await replayStore.SaveSessionAsync(replaySession, replayEntries);
    Console.WriteLine($"{Ansi.Dim}✔ Replay session saved (id: {replaySessionId}){Ansi.Reset}");
    Console.WriteLine($"  Use `memory report {replaySessionId} --format html` to view the full report.");
}
