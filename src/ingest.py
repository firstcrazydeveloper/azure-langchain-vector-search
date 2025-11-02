import os, uuid
from azure.storage.blob import BlobServiceClient
from tqdm import tqdm
from .config import settings
from .loaders import load_by_extension
from .chunker import chunk_documents
from .embeddings import embed_texts
from .search_index import ensure_index, clear_index, upload_docs
from .logging_setup import setup_logging
from .archive_store import to_records_with_serialized_vectors, save_parquet_to_blob, save_npz_to_blob

log = setup_logging()

def list_blobs():
    bs = BlobServiceClient.from_connection_string(settings.AZURE_BLOB_CONNECTION_STRING)
    cc = bs.get_container_client(settings.AZURE_BLOB_CONTAINER)
    return list(cc.list_blobs())

def download_blob(name: str) -> bytes:
    bs = BlobServiceClient.from_connection_string(settings.AZURE_BLOB_CONNECTION_STRING)
    cc = bs.get_container_client(settings.AZURE_BLOB_CONTAINER)
    return cc.get_blob_client(name).download_blob().readall()

def run_ingestion(clear=True):
    ensure_index()
    if clear:
        log.info("Clearing index…")
        clear_index(); ensure_index()

    blobs = list_blobs()
    log.info(f"Found {len(blobs)} blobs to ingest")

    all_chunks = []
    for b in tqdm(blobs, desc="Loading"):
        content = download_blob(b.name)
        docs = load_by_extension(content, b.name)
        chunks = chunk_documents(docs, size=1000, overlap=120)
        for i, c in enumerate(chunks):
            c["id"] = str(uuid.uuid4())
            c["chunkId"] = f"{b.name}::chunk::{i}"
        all_chunks.extend(chunks)

    log.info(f"Embedding {len(all_chunks)} chunks…")
    vectors = []
    batch_size = 64
    for i in tqdm(range(0, len(all_chunks), batch_size), desc="Embedding"):
        texts = [c["content"] for c in all_chunks[i:i+batch_size]]
        vectors.extend(embed_texts(texts))

    for i, v in enumerate(vectors):
        all_chunks[i]["vector"] = v

    # ── Persist an offline snapshot for audit/replay ─
    df = to_records_with_serialized_vectors(all_chunks)
    parquet_path = save_parquet_to_blob(df)   # e.g., embeddings-archive/parquet/y=2025/m=11/d=02/h=19/vectors.parquet
    npz_path = save_npz_to_blob(all_chunks)   # e.g., embeddings-archive/npz/y=.../vectors.npz
    log.info(f"Archived embeddings to: parquet=/{parquet_path}, npz=/{npz_path}")

    log.info("Uploading to Azure Cognitive Search…")
    upload_docs(all_chunks)
    log.info("Ingestion complete.")
