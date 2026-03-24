using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using System.Formats.Tar;
using System.IO.Compression;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using System.Collections.Concurrent;

await Main(args);

async Task Main(string[] args)
{
    if (args.Length < 3)
    {
        Console.WriteLine("Usage: dotnet run -- <raven-url> <database> <jsonl-path-or-url>");
        Console.WriteLine("Example: dotnet run -- http://localhost:8080 Sphere sphere.100k.jsonl");
        Console.WriteLine("Example: dotnet run -- http://localhost:8080 Sphere https://example.com/sphere.jsonl");
        return;
    }

    var ravenUrl = args[0];
    var database = args[1];
    var jsonlPath = args[2];

    using var store = new DocumentStore
    {
        Urls = [ravenUrl],
        Database = database
    };

    store.Initialize();
    await CreateVectorIndex(store);

    const int N = 8;
    var collections = Enumerable.Range(0, N)
        .Select(_ => new BlockingCollection<string>(16))
        .ToArray();

    var bulkTasks = collections
        .Select(c => Task.Run(() => BulkInsertItems(store, c)))
        .ToArray();

    using var sourceStream = await GetSourceStream(jsonlPath);
    using var reader = new StreamReader(sourceStream);

    int roundRobin = 0;
    int linesRead = 0;
    string? line;
    while ((line = await reader.ReadLineAsync()) is not null)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;

        linesRead++;
        if (linesRead % 10_000 == 0)
            Console.WriteLine($"Read {linesRead:#,#} lines...");

        bool added = false;
        for (int attempt = 0; attempt < N; attempt++)
        {
            int idx = (roundRobin + attempt) % N;
            if (collections[idx].TryAdd(line))
            {
                roundRobin = (idx + 1) % N;
                added = true;
                break;
            }
        }
        if (!added)
        {
            collections[roundRobin].Add(line);
            roundRobin = (roundRobin + 1) % N;
        }
    }

    foreach (var col in collections)
        col.CompleteAdding();

    await Task.WhenAll(bulkTasks);

    Console.WriteLine($"Done. Imported: {linesRead:#,#}");


    async Task<Stream> GetSourceStream(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            Console.WriteLine($"Streaming from URL: {path}");
            var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromHours(96); // Set a long timeout for large files
            var response = await httpClient.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            return await GetInnerStream(path, await response.Content.ReadAsStreamAsync());
        }

        if (!File.Exists(path))
            throw new FileNotFoundException($"JSONL file not found: {path}");

        return await GetInnerStream(path, File.OpenRead(path));
    }

    async Task<Stream> GetInnerStream(string path, Stream stream)
    {
        if (!path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            return stream;

        var gzipStream = new GZipStream(stream, CompressionMode.Decompress);
        var tarReader = new TarReader(gzipStream);
        var entry = await tarReader.GetNextEntryAsync(copyData: false);

        if (entry?.DataStream is null)
            throw new InvalidOperationException("No valid file found in tar.gz archive");

        Console.WriteLine($"Reading from tar entry: {entry.Name}");
        return entry.DataStream;
    }
}

static async Task BulkInsertItems(IDocumentStore store, BlockingCollection<string> lines)
{
    using var bulkInsert = store.BulkInsert();
    using var vectorStream = new MemoryStream();

    foreach (var line in lines.GetConsumingEnumerable())
    {
        var item = JsonSerializer.Deserialize<ItemRecord>(line);
        if (item?.Id is null)
        {
            continue;
        }

        var id = item.Id;
        var vector = item.Vector ?? Array.Empty<float>();
        var document = new Item(id, item.Url, item.Title, item.Sha, item.Raw);

        await bulkInsert.StoreAsync(document, id);

        if (vector.Length > 0)
        {
            WriteFloatBinaryStream(vector, vectorStream);
            await bulkInsert.AttachmentsFor(id).StoreAsync("embedding", vectorStream, "application/octet-stream");
        }
    }
}


static Task CreateVectorIndex(IDocumentStore store)
{
    return store.Maintenance.SendAsync(new PutIndexesOperation(new IndexDefinition
    {
        Name = "Items/Semantic",
        Maps =
            {
                """
                from i in docs.Items
                let attachment = LoadAttachment(i, "embedding")
                where attachment != null
                select new {
                    Vector = CreateVector(attachment.GetContentAsStream()),
                    i.Title,
                }
                """
            },
        Configuration =
            {
                ["Indexing.Static.SearchEngineType"] = "Corax"
            }
    }));
}

static void WriteFloatBinaryStream(float[] values, MemoryStream stream)
{
    stream.Position = 0;
    stream.SetLength(0);
    stream.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    stream.Position = 0;
}

record ItemRecord(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("sha")] string? Sha,
    [property: JsonPropertyName("raw")] string? Raw,
    [property: JsonPropertyName("vector")] float[]? Vector
);

record Item(
    string Id,
    string? Url,
    string? Title,
    string? Sha,
    string? Raw
);
