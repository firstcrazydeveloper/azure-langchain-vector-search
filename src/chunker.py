# src/chunker.py
from __future__ import annotations
from typing import Any, List, Dict
from langchain_text_splitters import RecursiveCharacterTextSplitter


def _normalize_to_lc_docs(items: List[Any]) -> List[Any]:
    """
    Accepts:
      - LangChain Documents (have .page_content/.metadata)
      - dicts with keys: page_content or content, and optional metadata
    Returns a list of LC-like objects with .page_content and .metadata attributes.
    """
    normalized = []
    for d in items:
        if isinstance(d, dict):
            content = d.get("page_content") or d.get("content") or ""
            meta = d.get("metadata", {}) or {}
            normalized.append(type("Doc", (), {"page_content": str(content), "metadata": dict(meta)}))
        else:
            # assume LC Document or similar object
            normalized.append(d)
    return normalized


def chunk_documents(
    langchain_docs_or_dicts: List[Any],
    size: int = 1000,
    overlap: int = 120
) -> List[Dict[str, Any]]:
    """
    Splits documents into overlapping chunks and returns serializable dicts.
    Compatible with your previous code.
    """
    normalized = _normalize_to_lc_docs(langchain_docs_or_dicts)

    splitter = RecursiveCharacterTextSplitter(
        chunk_size=size,
        chunk_overlap=overlap,
        separators=["\n\n", "\n", " ", ""],
    )

    chunks = splitter.split_documents(normalized)
    return [
        {"id": None, "content": c.page_content, "metadata": dict(c.metadata or {})}
        for c in chunks
    ]


def split_documents(
    items: List[Any],
    chunk_size: int = 1000,
    chunk_overlap: int = 120
) -> List[Dict[str, Any]]:
    """
    Alias for ingest.py — returns [{page_content, metadata}] for direct ingestion.
    """
    normalized = _normalize_to_lc_docs(items)

    splitter = RecursiveCharacterTextSplitter(
        chunk_size=chunk_size,
        chunk_overlap=chunk_overlap,
        separators=["\n\n", "\n", " ", ""],
    )

    chunks = splitter.split_documents(normalized)
    return [
        {"page_content": c.page_content, "metadata": dict(c.metadata or {})}
        for c in chunks
    ]
