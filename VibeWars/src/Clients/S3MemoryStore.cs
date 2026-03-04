using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Amazon.S3;
using Amazon.S3.Model;
using VibeWars.Models;

namespace VibeWars.Clients;

/// <summary>
/// <see cref="IMemoryStore"/> implementation backed by AWS S3.
/// Each session is stored as a single JSON object at
/// <c>{prefix}{sessionId}.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SearchAsync"/> performs an in-process filter across all downloaded
/// session manifests — O(n) in the number of sessions.  For large session counts,
/// consider enabling <c>VIBEWARS_S3_SELECT=true</c> which uses S3 Select to
/// push the filter down to the service.
/// </para>
/// <para>
/// Set <c>VIBEWARS_S3_CACHE_SIZE</c> (default 20) to tune the in-memory LRU
/// cache that avoids redundant S3 downloads within a single run.
/// </para>
/// </remarks>
public sealed class S3MemoryStore : IMemoryStore, IDisposable
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new DefaultContractResolver
        {
            NamingStrategy = new CamelCaseNamingStrategy()
        },
        Formatting = Formatting.None
    };

    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly bool _useS3Select;

    // Simple LRU cache: key = sessionId string, value = deserialized manifest.
    private readonly int _cacheSize;
    private readonly Dictionary<string, (SessionManifest Manifest, LinkedListNode<string> Node)> _cacheMap;
    private readonly LinkedList<string> _cacheLru;
    private readonly object _cacheLock = new();

    public S3MemoryStore(IAmazonS3 s3Client, string? bucket = null, string? prefix = null)
    {
        _s3 = s3Client;

        _bucket = bucket
            ?? Environment.GetEnvironmentVariable("VIBEWARS_S3_BUCKET")
            ?? throw new InvalidOperationException(
                "S3 bucket name must be provided via constructor or VIBEWARS_S3_BUCKET env var.");

        _prefix = prefix
            ?? Environment.GetEnvironmentVariable("VIBEWARS_S3_PREFIX")
            ?? "vibewars/";

        _useS3Select = string.Equals(
            Environment.GetEnvironmentVariable("VIBEWARS_S3_SELECT"), "true",
            StringComparison.OrdinalIgnoreCase);

        _cacheSize = int.TryParse(Environment.GetEnvironmentVariable("VIBEWARS_S3_CACHE_SIZE"), out var cs) ? cs : 20;
        _cacheMap  = new Dictionary<string, (SessionManifest, LinkedListNode<string>)>(_cacheSize + 1, StringComparer.Ordinal);
        _cacheLru  = new LinkedList<string>();
    }

    // ── IMemoryStore ──────────────────────────────────────────────────────────

    public async Task SaveSessionAsync(DebateSession session, IEnumerable<MemoryEntry> entries, CancellationToken ct = default)
    {
        var manifest = new SessionManifest(session, entries.ToList());
        var json = JsonConvert.SerializeObject(manifest, JsonSettings);

        var key = $"{_prefix}{session.SessionId}.json";
        var request = new PutObjectRequest
        {
            BucketName        = _bucket,
            Key               = key,
            ContentBody       = json,
            ContentType       = "application/json",
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
        };

        await _s3.PutObjectAsync(request, ct).ConfigureAwait(false);

        // Update cache
        CachePut(session.SessionId.ToString(), manifest);
    }

    public async Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int topK = 10, CancellationToken ct = default)
    {
        if (_useS3Select)
            return await S3SelectSearchAsync(query, topK, ct).ConfigureAwait(false);

        var all = await LoadAllSessionsAsync(ct).ConfigureAwait(false);
        var matched = all
            .SelectMany(m => m.Entries)
            .Where(e => e.Content.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || e.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => e.Timestamp)
            .Take(topK)
            .ToList();

        return matched;
    }

    public async Task<IReadOnlyList<DebateSession>> ListSessionsAsync(int limit = 50, CancellationToken ct = default)
    {
        var all = await LoadAllSessionsAsync(ct).ConfigureAwait(false);
        return all
            .Select(m => m.Session)
            .OrderByDescending(s => s.StartedAt)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<MemoryEntry>> GetSessionEntriesAsync(Guid sessionId, CancellationToken ct = default)
    {
        var manifest = await LoadManifestAsync(sessionId.ToString(), ct).ConfigureAwait(false);
        return manifest?.Entries ?? [];
    }

    // ── S3 Select search path ─────────────────────────────────────────────────

    /// <remarks>
    /// Uses S3 Select with a SQL expression to filter entries server-side,
    /// reducing the volume of data downloaded when the session bucket is large.
    /// Only content fields matching the query are projected.
    /// </remarks>
    private async Task<IReadOnlyList<MemoryEntry>> S3SelectSearchAsync(string query, int topK, CancellationToken ct)
    {
        var keys = await ListObjectKeysAsync(ct).ConfigureAwait(false);
        var results = new List<MemoryEntry>();

        // Escape single-quotes for the SQL LIKE literal; all other sanitization is
        // handled by S3 Select's own SQL parser. We no longer strip valid query
        // characters (previously dots, commas, etc. were silently removed, causing
        // queries like "U.S. policy" to return zero results).
        var escapedQuery = query.Replace("'", "''");
        if (string.IsNullOrWhiteSpace(escapedQuery))
            return results;

        foreach (var key in keys)
        {
            if (results.Count >= topK) break;

            var selectRequest = new SelectObjectContentRequest
            {
                BucketName           = _bucket,
                Key                  = key,
                ExpressionType       = ExpressionType.SQL,
                Expression           = $"SELECT * FROM s3object[*].entries[*] e WHERE LOWER(e.content) LIKE LOWER('%{escapedQuery}%')",
                InputSerialization   = new InputSerialization  { JSON = new JSONInput  { JsonType = Amazon.S3.JsonType.Document } },
                OutputSerialization  = new OutputSerialization { JSON = new JSONOutput { RecordDelimiter = "\n" } },
            };

            try
            {
                var tcs      = new TaskCompletionSource<string>();
                var sb       = new System.Text.StringBuilder();
                var response = await _s3.SelectObjectContentAsync(selectRequest, ct).ConfigureAwait(false);

                response.Payload.RecordsEventReceived += (_, e) =>
                {
                    using var reader = new StreamReader(e.EventStreamEvent.Payload);
                    sb.Append(reader.ReadToEnd());
                };
                response.Payload.EndEventReceived       += (_, _) => tcs.TrySetResult(sb.ToString());
                response.Payload.ExceptionReceived      += (_, e) => tcs.TrySetException(e.EventStreamException);

                await tcs.Task.WaitAsync(ct).ConfigureAwait(false);

                foreach (var line in sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var entry = JsonConvert.DeserializeObject<MemoryEntry>(line, JsonSettings);
                    if (entry is not null) results.Add(entry);
                    if (results.Count >= topK) break;
                }
            }
            catch (AmazonS3Exception)
            {
                // Object may not support Select; skip silently.
            }
        }

        return results;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private async Task<List<SessionManifest>> LoadAllSessionsAsync(CancellationToken ct)
    {
        var keys = await ListObjectKeysAsync(ct).ConfigureAwait(false);
        var tasks = keys.Select(k =>
        {
            // S3 keys use '/' as separator; do not use Path.GetFileNameWithoutExtension
            // which may treat platform path separators differently on Windows.
            var relative = k[_prefix.Length..];
            var lastSlash = relative.LastIndexOf('/');
            var filename = lastSlash >= 0 ? relative[(lastSlash + 1)..] : relative;
            var dotIndex = filename.LastIndexOf('.');
            var sid = dotIndex > 0 ? filename[..dotIndex] : filename;
            return LoadManifestAsync(sid, ct);
        });
        var manifests = await Task.WhenAll(tasks).ConfigureAwait(false);
        return [.. manifests.Where(m => m is not null).Select(m => m!)];
    }

    private async Task<List<string>> ListObjectKeysAsync(CancellationToken ct)
    {
        var keys = new List<string>();
        string? continuationToken = null;
        do
        {
            var request = new ListObjectsV2Request
            {
                BucketName            = _bucket,
                Prefix                = _prefix,
                ContinuationToken     = continuationToken,
            };
            var response = await _s3.ListObjectsV2Async(request, ct).ConfigureAwait(false);
            keys.AddRange(response.S3Objects
                .Where(o => o.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Select(o => o.Key));
            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);
        return keys;
    }

    private async Task<SessionManifest?> LoadManifestAsync(string sessionId, CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(sessionId, out var cached))
            {
                // Move to front of LRU list.
                _cacheLru.Remove(cached.Node);
                _cacheLru.AddFirst(cached.Node);
                return cached.Manifest;
            }
        }

        var key = $"{_prefix}{sessionId}.json";
        try
        {
            var response = await _s3.GetObjectAsync(_bucket, key, ct).ConfigureAwait(false);
            using var stream = response.ResponseStream;
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync().ConfigureAwait(false);
            var manifest = JsonConvert.DeserializeObject<SessionManifest>(json, JsonSettings);
            if (manifest is not null)
                CachePut(sessionId, manifest);
            return manifest;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private void CachePut(string sessionId, SessionManifest manifest)
    {
        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(sessionId, out var existing))
            {
                _cacheLru.Remove(existing.Node);
                _cacheMap.Remove(sessionId);
            }

            var node = _cacheLru.AddFirst(sessionId);
            _cacheMap[sessionId] = (manifest, node);

            // Evict least-recently-used entries if over capacity.
            while (_cacheLru.Count > _cacheSize && _cacheLru.Last is not null)
            {
                var lruKey = _cacheLru.Last.Value;
                _cacheLru.RemoveLast();
                _cacheMap.Remove(lruKey);
            }
        }
    }

    public void Dispose() => _s3.Dispose();

    // ── Serialization model ───────────────────────────────────────────────────

    private record SessionManifest(DebateSession Session, List<MemoryEntry> Entries);
}
