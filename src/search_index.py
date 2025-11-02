# src/search_index.py
from azure.core.credentials import AzureKeyCredential
from azure.search.documents import SearchClient
from azure.search.documents.indexes import SearchIndexClient
from azure.search.documents.indexes.models import (
    SearchIndex,
    SimpleField,
    SearchField,
    SearchFieldDataType,
    VectorSearch,
    HnswAlgorithmConfiguration,
    VectorSearchProfile,
)
from azure.search.documents.models import VectorizedQuery

from typing import Iterable
from .config import settings

# 1536 for text-embedding-3-small (Azure OpenAI)
DIM = 1536

INDEX_NAME = settings.AZURE_SEARCH_INDEX
ALGO_NAME = "hnsw-default"
PROFILE_NAME = "vector-profile-default"


def get_index_client():
    return SearchIndexClient(
        endpoint=settings.AZURE_SEARCH_ENDPOINT,
        credential=AzureKeyCredential(settings.AZURE_SEARCH_API_KEY),
    )


def get_search_client():
    return SearchClient(
        endpoint=settings.AZURE_SEARCH_ENDPOINT,
        index_name=INDEX_NAME,
        credential=AzureKeyCredential(settings.AZURE_SEARCH_API_KEY),
    )


def ensure_index():
    ic = get_index_client()
    try:
        # If it exists, do nothing
        ic.get_index(INDEX_NAME)
        return
    except Exception:
        pass

    # ---- Fields ----
    fields = [
        SimpleField(name="id", type=SearchFieldDataType.String, key=True),
        SearchField(name="content", type=SearchFieldDataType.String, searchable=True),
        # IMPORTANT: use 'vector_search_profile_name' (new SDK) and dimensions
        SearchField(
            name="contentVector",
            type=SearchFieldDataType.Collection(SearchFieldDataType.Single),
            searchable=True,
            vector_search_dimensions=DIM,
            vector_search_profile_name=PROFILE_NAME,
        ),
        SimpleField(name="fileName", type=SearchFieldDataType.String, filterable=True, facetable=True),
        SimpleField(name="chunkId", type=SearchFieldDataType.String, filterable=True),
        SimpleField(name="docType", type=SearchFieldDataType.String, filterable=True),
    ]

    # ---- VectorSearch with algorithm + profile ----
    vector_search = VectorSearch(
        algorithms=[
            # metric defaults to cosine in service; omit 'metric' to avoid warnings on newer SDKs
            HnswAlgorithmConfiguration(name=ALGO_NAME),
        ],
        profiles=[
            VectorSearchProfile(
                name=PROFILE_NAME,
                algorithm_configuration_name=ALGO_NAME,
            )
        ],
    )

    index = SearchIndex(
        name=INDEX_NAME,
        fields=fields,
        vector_search=vector_search,
    )

    ic.create_index(index)


def clear_index():
    ic = get_index_client()
    try:
        ic.delete_index(INDEX_NAME)
    except Exception:
        # already gone, ignore
        pass
    ensure_index()


def upload_docs(items: Iterable[dict]):
    sc = get_search_client()
    batch = []
    for it in items:
        batch.append({
            "id": it["id"],
            "content": it["content"],
            "contentVector": it["vector"],
            "fileName": it["metadata"].get("source", ""),
            "chunkId": it["chunkId"],
            "docType": it["metadata"].get("type", "unknown"),
        })
        if len(batch) == 1000:
            sc.upload_documents(batch)
            batch.clear()
    if batch:
        sc.upload_documents(batch)


def vector_hybrid_search(query: str, query_vector: list[float], top_k=5):
    sc = get_search_client()

    try:
        # New API (11.6.x): use vector_queries + VectorizedQuery
        vq = VectorizedQuery(
            vector=query_vector,
            fields="contentVector",
            k=top_k,
            profile=PROFILE_NAME,   # matches your index's vector profile
        )

        results = sc.search(
            search_text=query,              # keep lexical text for hybrid
            vector_queries=[vq],            # ← new way
            top=top_k,
            select=["fileName", "chunkId", "content", "docType"],
        )

    except TypeError:
        # Fallback for older SDKs that still use legacy 'vector=' dict
        results = sc.search(
            search_text=query,
            vector={"value": query_vector, "fields": "contentVector", "k": top_k, "profile": PROFILE_NAME},
            top=top_k,
            select=["fileName", "chunkId", "content", "docType"],
        )

    out = []
    for r in results:
        out.append({
            "fileName": r.get("fileName"),
            "chunkId": r.get("chunkId"),
            "docType": r.get("docType"),
            "snippet": (r.get("content") or "")[:400],
            "score": r.get("@search.score"),
        })
    return out
