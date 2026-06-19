# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Project Does

End-to-end semantic document search pipeline:
1. Pulls documents from **Azure Blob Storage** (PDF, DOCX, TXT, CSV, XLSX, images)
2. Chunks and embeds them via **Azure OpenAI** (`text-embedding-3-small`, 1536 dims)
3. Indexes into **Azure Cognitive Search** (HNSW vector + hybrid)
4. Archives embedding snapshots (Parquet + NPZ) back to Blob
5. Serves search via a **FastAPI** `/search` endpoint

## Commands

### Run locally without Docker (requires `.env` and Tesseract installed)
```bash
# Install deps
pip install -r requirements.txt

# Run ingestion (clears index, reindexes all blobs)
python -m src.main

# Start query API
uvicorn src.query_api:app --host 0.0.0.0 --port 8080
```

### Docker (primary deployment path)
```bash
docker compose build
docker compose up -d
docker compose logs -f vector-pipeline

# Health check
curl http://localhost:8080/healthz

# Run ingestion inside container
docker compose exec vector-pipeline python -m src.main

# Query
curl "http://localhost:8080/search?q=termination+clause&k=5"
```

### Tests
```bash
# All tests
pytest -q

# Single test file
pytest tests/test_chunker.py -v

# Inside Docker
docker compose exec vector-pipeline pytest -q
```

`test_chunker.py` runs with no Azure credentials. `test_search_index.py` gracefully swallows exceptions when credentials are absent.

### PowerShell automation scripts (numbered workflow)
```powershell
.\scripts\00-Provision.ps1 -ResourceGroup rg-vector-pipeline -Location westeurope
.\scripts\01-CreateEnv.ps1 -EmbeddingDeployment text-embedding-3-small
.\scripts\02-UploadSamples.ps1
.\scripts\03-DockerUp.ps1
.\scripts\04-Ingest.ps1
.\scripts\05-Query.ps1 -Query "vendor contract termination" -K 5
```

## Environment Variables

Copy `.env.example` to `.env`. Required:

| Variable | Purpose |
|---|---|
| `AZURE_SEARCH_ENDPOINT` | Cognitive Search service URL |
| `AZURE_SEARCH_API_KEY` | Admin key for Search |
| `AZURE_SEARCH_INDEX` | Index name (default: `docs-index`) |
| `AZURE_OPENAI_ENDPOINT` | Azure OpenAI resource URL |
| `AZURE_OPENAI_API_KEY` | Key for Azure OpenAI |
| `AZURE_OPENAI_EMBEDDING_DEPLOYMENT` | Deployment name (default: `text-embedding-3-small`) |
| `AZURE_BLOB_CONNECTION_STRING` | Full Blob Storage connection string |
| `AZURE_BLOB_CONTAINER` | Container holding source docs (default: `documents`) |
| `APP_PORT` | API port (default: `8080`) |

## Architecture and Data Flow

### Module responsibilities (`src/`)

| Module | Role |
|---|---|
| `config.py` | `Settings` singleton — reads `.env` once at import time |
| `loaders.py` | `load_document(path)` — routes by file extension to LangChain loaders or raw parsers; returns list of dicts `{page_content, metadata}` or LangChain Documents |
| `ocr.py` | `image_to_text(bytes)` — Tesseract wrapper (used directly by `loaders.py` for `.png/.jpg/.jpeg`) |
| `chunker.py` | Two public functions: `split_documents()` returns `[{page_content, metadata}]`; `chunk_documents()` returns `[{id, content, metadata}]`. Both normalize LangChain Document objects and plain dicts. |
| `embeddings.py` | `embed_texts(texts)` — lazily initializes a module-level `AzureOpenAI` singleton; batch-safe |
| `search_index.py` | `ensure_index()`, `clear_index()`, `upload_docs()`, `vector_hybrid_search()` — creates HNSW index with `VectorSearchProfile`; has a fallback code path for older Azure SDK versions |
| `archive_store.py` | Serializes embedding snapshots to Parquet (with base64-encoded vectors) and NPZ; stores under `embeddings-archive/` prefix in Blob |
| `ingest.py` | `run_ingestion()` — orchestrates the full pipeline; `BATCH_SIZE = 128` controls embedding batching |
| `query_api.py` | FastAPI app: `GET /healthz` and `GET /search?q=...&k=N` |
| `main.py` | CLI entry point: calls `run_ingestion(clear=True)` |

### Key design constraints

- **Vector dimension is hardcoded to 1536** in `search_index.py` (`DIM = 1536`). Changing the embedding model requires updating this constant and recreating the index.
- **`chunker.py` has two separate public APIs** with different output key names (`page_content` vs `content`). `ingest.py` calls `split_documents()`; tests use both. Do not confuse them.
- **`loaders.py` returns heterogeneous types**: LangChain Document objects (from `PyPDFLoader`, `TextLoader`, `UnstructuredWordDocumentLoader`) and plain dicts (for image, CSV, Excel). `chunker.py`'s `_normalize_to_lc_docs()` handles both.
- **`httpx<0.28` is pinned** in `requirements.txt` to avoid a `proxies` kwarg incompatibility with the `openai` library.
- **Archive step in `ingest.py` is non-fatal** — wrapped in try/except; a Blob failure will log a warning but not abort indexing.
- The Docker `CMD` in `Dockerfile` binds to `localhost`, but `docker-compose.yml` overrides this with `--host 0.0.0.0` for external access.
- **`numpy` appears twice** in `requirements.txt` — harmless but worth noting if editing that file.
