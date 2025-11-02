# src/embeddings.py
from __future__ import annotations
import os
from typing import List
from openai import AzureOpenAI

# Lazily initialized singleton
_client: AzureOpenAI | None = None

def _require_env(name: str) -> str:
    val = os.getenv(name)
    if not val:
        raise RuntimeError(f"Missing required environment variable: {name}")
    return val

def _get_client() -> AzureOpenAI:
    global _client
    if _client is None:
        # Read env only when needed; fail fast with a clear message
        api_key = _require_env("AZURE_OPENAI_API_KEY")
        endpoint = _require_env("AZURE_OPENAI_ENDPOINT")
        # NOTE: pin httpx<0.28 in requirements.txt to avoid 'proxies' kwarg issue
        _client = AzureOpenAI(
            api_key=api_key,
            azure_endpoint=endpoint,
            api_version="2024-07-01-preview",
        )
    return _client

def embed_texts(texts: List[str]) -> List[List[float]]:
    """
    Returns one embedding vector per input text.
    Uses the deployment name from AZURE_OPENAI_EMBEDDING_DEPLOYMENT.
    """
    if not texts:
        return []
    model = _require_env("AZURE_OPENAI_EMBEDDING_DEPLOYMENT")
    client = _get_client()
    resp = client.embeddings.create(model=model, input=texts)
    # preserve order
    return [d.embedding for d in resp.data]
