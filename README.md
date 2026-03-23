# Sphere Vector Search Benchmark for RavenDB

A benchmark for testing RavenDB vector search performance using the [Sphere](https://github.com/facebookresearch/sphere) dataset. The dataset contains web documents with pre-computed DPR (Dense Passage Retrieval) embeddings, making it suitable for evaluating vector similarity search at scale.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- A running [RavenDB](https://ravendb.net/) instance (v7.2+)
- [Docker](https://www.docker.com/) (for the embedding model used at query time)
- Sphere dataset in JSONL format (plain `.jsonl` or `.tar.gz`)

## Projects

| Project | Purpose |
|---|---|
| **SphereImporter** | Imports Sphere JSONL data into RavenDB, creating documents with vector embeddings stored as binary attachments and a `Items/Semantic` Corax vector index. |
| **SphereQueries** | Runs semantic vector search queries against the indexed data using an embedding model, measures latency/throughput, and reports benchmark statistics (min, max, mean, median, P50, P95, P99, P99.99). |

## Setup

### 1. Start the embedding model

The query benchmark uses a Hugging Face Text Embeddings Inference container to generate query vectors at runtime.

```bash
cd SphereQueries
chmod +x setup-embedder.sh
./setup-embedder.sh
```

This pulls and starts a Docker container running the `sentence-transformers/facebook-dpr-question_encoder-single-nq-base` model on port **8081**.

Wait until the health check passes before running queries:

```bash
curl http://localhost:8081/health
```

### 2. Import the dataset

```bash
cd SphereImporter
dotnet run -- <raven-url> <database> <jsonl-path>
```

**Example:**

```bash
dotnet run -- http://localhost:8080 Sphere sphere.100k.jsonl
```

The importer supports both plain `.jsonl` files and `.tar.gz` archives. It uses parallel bulk insert (8 concurrent writers) and creates the `Items/Semantic` vector index automatically.

### 3. Run the benchmark

```bash
cd SphereQueries
dotnet run -- <raven-url> <database>
```

**Example:**

```bash
dotnet run -- http://localhost:8080 Sphere
```

The benchmark:

1. Loads ~400 natural-language queries from `queries.txt`
2. Runs a warmup pass over all queries
3. Executes 5 iterations of all queries across `Environment.ProcessorCount` parallel threads
4. Reports latency statistics and throughput (queries/sec)

### Configuration

The embedding endpoint can be customised via environment variables:

```bash
export SPHERE_EMBEDDING_ENDPOINT=http://localhost:8081/v1/embeddings
export SPHERE_EMBEDDING_MODEL=sentence-transformers/facebook-dpr-question_encoder-single-nq-base
```

Embedding results are cached in the system temp directory so repeated runs avoid redundant calls to the embedding model.
