using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Raven.Client.Documents;

if (args.Length < 2)
{
    Console.WriteLine("Usage: dotnet run -- <raven-url> <database>");
    Console.WriteLine("Example: dotnet run -- http://localhost:8080 Sphere");
    return;
}

var ravenUrl = args[0];
var database = args[1];
var queriesFile = Path.Combine(Environment.CurrentDirectory, "queries.txt");

if (!File.Exists(queriesFile))
{
    Console.Error.WriteLine($"Queries file not found: {queriesFile}");
    return;
}

using var httpClient = new HttpClient();
using var store = new DocumentStore
{
    Urls = [ravenUrl],
    Database = database
};

store.Initialize();

var allQueries = File.ReadAllLines(queriesFile)
    .Where(line => !string.IsNullOrWhiteSpace(line))
    .ToList();

Console.WriteLine($"Loaded {allQueries.Count} queries from {queriesFile}");
Console.WriteLine();

// Warmup
Console.WriteLine("Running warmup queries...");
await Parallel.ForEachAsync(allQueries, async (query, cancellationToken) =>
{
    await QuerySemanticAsync(store, query);
});

Console.WriteLine("Warmup complete. Starting main benchmarks...");
Console.WriteLine();

int numThreads = Environment.ProcessorCount;

var stopwatchTotal = Stopwatch.StartNew();

var tasks = new List<Task<List<long>>>();
for (int t = 0; t < numThreads; t++)
{
    int threadId = t;
    var task = Task.Run(async () =>
    {
        var localLatencies = new List<long>();
        var stopwatch = new Stopwatch();
        for (var i = 0; i < 5; i++)
        {
            foreach (var query in allQueries)
            {
                stopwatch.Restart();
                await QuerySemanticAsync(store, query);
                localLatencies.Add(stopwatch.ElapsedMilliseconds);
            }
        }
        return localLatencies;
    });
    tasks.Add(task);
}

await Task.WhenAll(tasks);
stopwatchTotal.Stop();

// Aggregate latencies from all tasks
var latencies = new List<long>();
foreach (var task in tasks)
{
    latencies.AddRange(task.Result);
}

latencies.Sort();

var min = latencies.Min();
var max = latencies.Max();
var mean = latencies.Average();
var median = latencies.Count % 2 == 0
    ? (latencies[latencies.Count / 2 - 1] + latencies[latencies.Count / 2]) / 2.0
    : latencies[latencies.Count / 2];

var p50 = Percentile(latencies, 0.50);
var p95 = Percentile(latencies, 0.95);
var p99 = Percentile(latencies, 0.99);
var p9999 = Percentile(latencies, 0.9999);

Console.WriteLine("=== Benchmark Results ===");
Console.WriteLine($"Total Queries: {latencies.Count}");
Console.WriteLine($"Total Time: {stopwatchTotal.ElapsedMilliseconds} ms ({stopwatchTotal.ElapsedMilliseconds / 1000.0:F2}s)");
Console.WriteLine($"Threads: {numThreads}");
Console.WriteLine();
Console.WriteLine("=== Latency Statistics (ms) ===");
Console.WriteLine($"Min:        {min}");
Console.WriteLine($"Max:        {max}");
Console.WriteLine($"Mean:       {mean:F2}");
Console.WriteLine($"Median:     {median:F2}");
Console.WriteLine($"P50:        {p50}");
Console.WriteLine($"P95:        {p95}");
Console.WriteLine($"P99:        {p99}");
Console.WriteLine($"P99.99:     {p9999}");
Console.WriteLine();
Console.WriteLine($"Throughput: {latencies.Count / (stopwatchTotal.ElapsedMilliseconds / 1000.0):F2} queries/sec");

static long Percentile(List<long> sortedValues, double percentile)
{
    if (sortedValues.Count == 0) return 0;
    var index = (int)Math.Ceiling(percentile * sortedValues.Count) - 1;
    index = Math.Max(0, Math.Min(index, sortedValues.Count - 1));
    return sortedValues[index];
}

static async Task<List<QueryResult>> QuerySemanticAsync(IDocumentStore store, string queryText)
{
    var embedding = await GetCachedEmbeddingAsync(queryText);

    using var session = store.OpenAsyncSession();
    return await session.Advanced.AsyncRawQuery<QueryResult>(
        """
        from index 'Items/Semantic'
        where vector.search(Vector, $v)
        select id() as Id, Title
        limit 10
        """)
    .AddParameter("v", embedding)
    .ToListAsync();
}

static async Task<float[]> GetCachedEmbeddingAsync(string queryText)
{
    var hash = ComputeSha256Hash(queryText);
    var cacheFile = Path.Combine(Path.GetTempPath(), $"vec-{hash}");

    if (File.Exists(cacheFile))
    {
        var cached = File.ReadAllText(cacheFile);
        var floats = cached.Split(',').Select(s => float.Parse(s.Trim())).ToArray();
        return floats;
    }

    var embedding = await GenerateEmbeddingAsync(queryText);
    var serialized = string.Join(",", embedding);
    File.WriteAllText(cacheFile, serialized);
    return embedding;
}

static string ComputeSha256Hash(string input)
{
    using var sha256 = SHA256.Create();
    var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
    return Convert.ToBase64String(hashBytes).Replace("/", "_");
}

async Task<float[]> GenerateEmbeddingAsync(string queryText)
{
    var endpoint = Environment.GetEnvironmentVariable("SPHERE_EMBEDDING_ENDPOINT") ?? "http://localhost:8081/v1/embeddings";
    var model = Environment.GetEnvironmentVariable("SPHERE_EMBEDDING_MODEL") ?? "sentence-transformers/facebook-dpr-question_encoder-single-nq-base";


    var openAiRequest = new OpenAiEmbeddingsRequest(model, queryText);
    var openAiResponse = await httpClient.PostAsJsonAsync(endpoint, openAiRequest);
    openAiResponse.EnsureSuccessStatusCode();

    var openAiContent = await openAiResponse.Content.ReadFromJsonAsync<OpenAiEmbeddingsResponse>();
    var openAiEmbedding = openAiContent?.Data?.FirstOrDefault()?.Embedding;
    if (openAiEmbedding is null || openAiEmbedding.Length == 0)
        throw new InvalidOperationException("Embedding endpoint returned an empty embedding vector.");

    return openAiEmbedding;
}

record QueryResult(string Id, string? Title);

record OpenAiEmbeddingsRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] string Input
);

record OpenAiEmbeddingsResponse(
    [property: JsonPropertyName("data")] OpenAiEmbeddingData[]? Data
);

record OpenAiEmbeddingData(
    [property: JsonPropertyName("embedding")] float[]? Embedding
);
