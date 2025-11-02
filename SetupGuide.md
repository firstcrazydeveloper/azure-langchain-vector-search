# ⚙️ Setup Guide  
### *Azure LangChain Vector Search Pipeline*  
✍️ By **Abhishek Kumar | #FirstCrazyDeveloper**

---

## 🧭 Introduction  

This guide walks you through **setting up and running the entire end-to-end vector search pipeline**, built with:  

- **Azure Blob Storage** for document storage  
- **LangChain** for text loading and chunking  
- **Azure OpenAI** for embeddings  
- **Azure Cognitive Search (Vector Search)** for indexing and querying  
- **FastAPI** for serving the search API  
- **PowerShell scripts** for easy automation  

You’ll be able to ingest PDFs, Word documents, plain text, and images (via OCR), then run **semantic searches** like:  
> “Find all contracts mentioning termination clauses in Europe”

---

## 📋 Prerequisites  

Before you start, make sure you have:

| Tool | Version | Purpose |
|------|----------|----------|
| **Azure Subscription** | Active | For OpenAI, Cognitive Search, Blob |
| **Azure CLI** | Latest | Resource provisioning |
| **Docker Desktop** | Latest | Run the pipeline locally |
| **PowerShell 7+** | Recommended | Execute helper scripts |
| **Git** | Latest | Clone repository |

Optional but useful:
- **Visual Studio Code** for editing files
- **Python 3.11** (only if testing locally without Docker)

---

## ☁️ Step 1 — Clone the Repository  

```powershell
git clone https://github.com/firstcrazydeveloper/azure-langchain-vector-search-pipeline.git
cd azure-langchain-vector-search-pipeline

# ðŸª£ Step 2 â€” Provision Azure Resources

Run the PowerShell helper to create the required Azure components:

- Resource Group  
- Storage Account + Container  
- Cognitive Search Service  
- Azure OpenAI Resource  

```powershell
.\scripts -Provision.ps1 -ResourceGroup rg-vector-pipeline -Location westeurope
```

This writes a file **`provision-output.json`** containing all endpoints and keys.

---

## ðŸ” Step 3 â€” Create the `.env` Configuration

Generate environment variables automatically:

```powershell
.\scripts-CreateEnv.ps1 -EmbeddingDeployment text-embedding-3-small
```

This creates a `.env` file in your project root.

Example:

```ini
AZURE_SEARCH_ENDPOINT=https://your-search.search.windows.net
AZURE_SEARCH_API_KEY=xxxxxxxxxxxxxxxx
AZURE_SEARCH_INDEX=docs-index

AZURE_OPENAI_ENDPOINT=https://youropenai.cognitiveservices.azure.com
AZURE_OPENAI_API_KEY=xxxxxxxxxxxxxxxx
AZURE_OPENAI_EMBEDDING_DEPLOYMENT=text-embedding-3-small

AZURE_BLOB_CONNECTION_STRING=DefaultEndpointsProtocol=...
AZURE_BLOB_CONTAINER=documents

APP_PORT=8080
```

---

## ðŸ“„ Step 4 â€” Prepare and Upload Test Files

Create test files under `tests/data/`:

| File | Description |
|------|--------------|
| `sample.pdf` | Contract with termination clause |
| `sample.docx` | Data retention policy |
| `sample.txt` | Security guidelines |
| `sample.png` | OCR test image |

Upload them to Blob:

```powershell
.\scripts-UploadSamples.ps1
```

Expected output:

```
Uploading tests\data\sample.pdf -> samples/sample.pdf
Uploading tests\data\sample.docx -> policies/policy.docx
Uploading tests\data\sample.txt -> notes/note.txt
Uploading tests\data\sample.png -> images/clause.png
```

---

## ðŸ§± Step 5 â€” Build and Run the Docker Container

```powershell
.\scripts-DockerUp.ps1
```

Verify API health:

```powershell
Invoke-RestMethod http://localhost:8080/healthz | ConvertTo-Json
```

Output:

```json
{"status":"ok","index":"docs-index"}
```

---

## ðŸ§  Step 6 â€” Run the Ingestion Process

This clears the index, loads files, runs OCR, generates embeddings, and uploads vectors to Cognitive Search.

```powershell
.\scripts-Ingest.ps1
```

Expected log:

```
Clearing indexâ€¦
Found 4 blobs to ingest
Embedding 12 chunksâ€¦
Archived embeddings to /embeddings-archive/parquet/...
Ingestion complete.
```

---

## ðŸ” Step 7 â€” Run Semantic Search Queries

```powershell
.\scripts-Query.ps1 -Query "termination clause europe" -K 3
.\scripts-Query.ps1 -Query "data retention policy" -K 3
.\scripts-Query.ps1 -Query "two factor admin security" -K 3
```

Example output:

```json
[
  {
    "fileName": "samples/sample.pdf",
    "chunkId": "samples/sample.pdf::chunk::0",
    "docType": "pdf",
    "snippet": "Either party may terminate this contract by providing a 30-day written noticeâ€¦",
    "score": 8.37
  }
]
```

---

## ðŸ§¾ Step 8 â€” Verify Vector Archive in Blob

Each ingestion stores Parquet + NPZ snapshots under:
```
embeddings-archive/parquet/...
```

Inspect them:

```bash
docker compose exec vector-pipeline python - << 'PY'
from src.archive_store import load_parquet_from_blob
df = load_parquet_from_blob("embeddings-archive/parquet/y=2025/m=11/d=02/h=19/vectors.parquet")
print(df[["fileName","chunkId","vector_dim","vector_norm"]].head())
PY
```

---

## ðŸ§ª Step 9 â€” Run Tests

```powershell
docker compose exec vector-pipeline pytest -q
```

Validates OCR, chunking, embeddings, and index creation.

---

## ðŸ§± Step 10 â€” Useful PowerShell Commands

| Action | Command |
|--------|----------|
| **View logs** | `docker compose logs -f vector-pipeline` |
| **Rebuild image** | `docker compose build --no-cache` |
| **Stop containers** | `docker compose down` |
| **Remove Azure resources** | `.\scripts\99-Cleanup.ps1 -ResourceGroup rg-vector-pipeline` |

---

## ðŸ§° Troubleshooting

| Problem | Cause | Fix |
|----------|--------|-----|
| `vectorSearchProfile` error | Old index schema | Use updated `search_index.py` |
| `response ended prematurely` | API crash mid-response | Patch `query_api.py` with optional fields |
| `httpx proxies` TypeError | Library mismatch | Pin `httpx<0.28` |
| AOAI 401/404 | Wrong deployment name | Match deployment in `.env` |
| No results | Empty index | Re-run `04-Ingest.ps1` |

---

## ðŸ§  Validation Queries

| Query | Expected Document |
|--------|-------------------|
| â€œtermination clause europeâ€ | `sample.pdf` |
| â€œdata retention policyâ€ | `sample.docx` |
| â€œtwo factor admin securityâ€ | `sample.txt` |
| â€œcancel agreementâ€ | `sample.png` (OCR) |

---

## ðŸ”’ Security Notes

- Use **Managed Identity** for production  
- Enable **Private Endpoints** + **VNet Integration**  
- Store `.env` in **Azure Key Vault** or CI secrets  
- Apply **retention policies** to `embeddings-archive`

---

## ðŸŽ¯ Next Steps

- Add **RAG (Retrieval-Augmented Generation)**  
- Deploy to **Azure App Service** or **Container Apps**  
- Automate re-indexing via **Logic Apps / Event Grid**  
- Integrate with **Azure DevOps Pipelines**

---

## ðŸ‘¨â€ðŸ’» Author

**Abhishek Kumar**  
ðŸŒ [GitHub](https://github.com/firstcrazydeveloper)â€ƒðŸ”— [LinkedIn](https://www.linkedin.com/in/firstcrazydeveloper)

> Building intelligent, context-aware enterprise systems using Azure + AI  

**#Azure #LangChain #OpenAI #VectorSearch #AbhishekKumar #FirstCrazyDeveloper**
