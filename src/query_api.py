# src/query_api.py
from fastapi import FastAPI, Query, HTTPException
from pydantic import BaseModel
from .embeddings import embed_texts
from .search_index import vector_hybrid_search
from .config import settings
import traceback

app = FastAPI(title="Vector Search Query API")

class SearchResponse(BaseModel):
    fileName: str | None = None
    chunkId: str | None = None
    docType: str | None = None
    snippet: str | None = None
    score: float | None = None   # <-- make optional

@app.get("/healthz")
def health():
    return {"status": "ok", "index": settings.AZURE_SEARCH_INDEX}

@app.get("/search", response_model=list[SearchResponse])
def search(q: str = Query(..., description="Your query"), k: int = 5):
    try:
        vec = embed_texts([q])[0]     # Azure OpenAI call
        results = vector_hybrid_search(q, vec, top_k=k)
        # Ensure all required keys exist
        for r in results:
            r.setdefault("fileName", None)
            r.setdefault("chunkId", None)
            r.setdefault("docType", None)
            r.setdefault("snippet", None)
            r.setdefault("score", None)
        return results
    except Exception as e:
        # Log full stack to server logs and return a clean JSON error
        traceback.print_exc()
        raise HTTPException(status_code=500, detail=f"Search failed: {e}")
