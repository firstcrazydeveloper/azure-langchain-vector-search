from __future__ import annotations

import io
import os
import uuid
import shutil
import logging
import tempfile
from pathlib import Path
from typing import Iterable, List, Dict

from azure.storage.blob import BlobServiceClient

from .config import settings
from .loaders import load_document
from .chunker import split_documents
from .embeddings import embed_texts
from .search_index import ensure_index, upload_docs, clear_index
# archival is optional; if you don't want it, you can comment these 3 lines
from .archive_store import (
    to_records_with_serialized_vectors,
    save_parquet_to_blob,
    save_npz_to_blob,
)

log = logging.getLogger("ingest")
logging.basicConfig(level=logging.INFO, format="%(asctime)s | %(levelname)s | %(message)s")

BATCH_SIZE = 128  # tune for your AOAI throughput


def _blob_client() -> BlobServiceClient:
    return BlobServiceClient.from_connection_string(settings.AZURE_BLOB_CONNECTION_STRING)


def _iter_blob_names(prefix: str | None = None) -> Iterable[str]:
    bs = _blob_client()
    cc = bs.get_container_client(settings.AZURE_BLOB_CONTAINER)
    for b in cc.list_blobs(name_starts_with=prefix or ""):
        # skip folders / zero-length pseudo-dirs
        if not b.name or b.name.endswith("/"):
            continue
        yield b.name


def _download_blob_to_temp(blob_name: str) -> Path:
    """Download a blob to a temp file, preserving extension for loader routing."""
    bs = _blob_client()
    cc = bs.get_container_client(settings.AZURE_BLOB_CONTAINER)
    bc = cc.get_blob_client(blob_name)
    suffix = Path(blob_name).suffix or ""
    tmp = Path(tempfile.mkdtemp(prefix="ingest_")) / f"blob{suffix}"
    with open(tmp, "wb") as f:
        stream = bc.download_blob()
        stream.readinto(f)
    return tmp


def _cleanup_temp(path: Path):
    try:
        shutil.rmtree(path.parent, ignore_errors=True)
    except Exception:
        pass


def _to_chunks_for_index(docs: List[Dict]) -> List[Dict]:
    """
    Input docs format expected from loaders.load_document:
        [{ "page_content": "...", "metadata": {"source": "...", "type": "...", ...} }, ...]
    Output chunks for indexing:
        [{ id, chunkId, content, metadata }, ...]
    """
    chunks = []
    # split_documents should accept that docs format; adjust if your signature differs
    for ch in split_documents(docs):
        text = ch.get("page_content") if isinstance(ch, dict) else getattr(ch, "page_content", "")
        if not text or not str(text).strip():
            continue
        meta = ch.get("metadata", {}) if isinstance(ch, dict) else getattr(ch, "metadata", {}) or {}
        source = meta.get("source") or "unknown"
        # deterministic-ish chunk id per source + running index
        chunk_idx = len([c for c in chunks if c["metadata"].get("source") == source])
        cid = f"{source}::chunk::{chunk_idx}"
        chunks.append({
            "id": str(uuid.uuid4()),
            "chunkId": cid,
            "content": str(text),
            "metadata": {
                **meta,
                "source": source,
                "type": meta.get("type", "unknown"),
            },
        })
    return chunks


def _embed_in_place(all_chunks: List[Dict], batch_size: int = BATCH_SIZE):
    """Compute embeddings in batches and store under chunk['vector']."""
    texts = [c["content"] for c in all_chunks]
    vectors: List[List[float]] = []

    for i in range(0, len(texts), batch_size):
        batch = texts[i:i + batch_size]
        log.info(f"Embedding batch {i}-{i+len(batch)-1} / {len(texts)-1}")
        vecs = embed_texts(batch)
        if len(vecs) != len(batch):
            raise RuntimeError(f"Embedding count mismatch: got {len(vecs)} for {len(batch)} inputs")
        vectors.extend(vecs)

    for c, v in zip(all_chunks, vectors):
        c["vector"] = v


def run_ingestion(clear: bool = True, prefix: str | None = None):
    """
    End-to-end:
      1) (optional) clear index
      2) ensure index exists
      3) list blobs (optionally by prefix)
      4) download → load_document → split → collect chunks
      5) embed in batches
      6) (optional) archive
      7) upload to Azure Cognitive Search
    """
    if clear:
        log.info("Clearing index…")
        try:
            clear_index()
        except Exception:
            # clear_index may already handle exceptions; ignore to proceed
            pass

    log.info("Ensuring index exists…")
    ensure_index()

    blob_names = list(_iter_blob_names(prefix=prefix))
    if not blob_names:
        log.warning("No blobs found in container '%s' with prefix '%s'", settings.AZURE_BLOB_CONTAINER, prefix or "")
        return

    log.info("Found %d blobs to ingest", len(blob_names))

    all_chunks: List[Dict] = []

    for name in blob_names:
        tmp = None
        try:
            log.info(f"Loading blob: {name}")
            tmp = _download_blob_to_temp(name)
            docs = load_document(str(tmp))  # <-- uses your unified loader (pdf/docx/txt/img/csv/xlsx)
            if not docs:
                log.warning("No documents parsed from %s; skipping.", name)
                continue
            chunks = _to_chunks_for_index(docs)
            if not chunks:
                log.warning("No chunks produced for %s; skipping.", name)
                continue
            all_chunks.extend(chunks)
        except Exception as e:
            log.exception("Failed processing blob %s: %s", name, e)
        finally:
            if tmp:
                _cleanup_temp(Path(tmp))

    if not all_chunks:
        log.warning("No chunks to index. Exiting.")
        return

    log.info("Total chunks to embed: %d", len(all_chunks))
    _embed_in_place(all_chunks, batch_size=BATCH_SIZE)

    # ── Archive snapshot (optional but recommended for audits/migrations) ─────────
    try:
        df = to_records_with_serialized_vectors(all_chunks)
        parquet_path = save_parquet_to_blob(df)
        npz_path = save_npz_to_blob(all_chunks)
        log.info(f"Archived embeddings to Blob: parquet=/{parquet_path}, npz=/{npz_path}")
    except Exception as e:
        log.warning("Archival failed (continuing to index): %s", e)

    # ── Upload to Azure Cognitive Search ──────────────────────────────────────────
    log.info("Uploading %d chunks to Azure Cognitive Search…", len(all_chunks))
    upload_docs(all_chunks)
    log.info("Ingestion complete.")


if __name__ == "__main__":
    # default: full clear + full reindex
    run_ingestion(clear=True)
