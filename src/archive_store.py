# src/archive_store.py
from __future__ import annotations
import io, os, base64, json, datetime as dt
import numpy as np
import pandas as pd
from azure.storage.blob import BlobServiceClient
from .config import settings

def _blob_clients(subpath: str):
    bs = BlobServiceClient.from_connection_string(settings.AZURE_BLOB_CONNECTION_STRING)
    container = settings.AZURE_BLOB_CONTAINER
    # store under a subfolder called "embeddings-archive"
    return bs.get_container_client(container), f"embeddings-archive/{subpath}"

def _now_utc_iso():
    return dt.datetime.utcnow().replace(tzinfo=None).isoformat(timespec="seconds") + "Z"

def to_records_with_serialized_vectors(chunks: list[dict]) -> pd.DataFrame:
    """
    chunks[i]: { id, chunkId, content, metadata:{source,type}, vector: list[float] }
    """
    rows = []
    for c in chunks:
        vec = np.asarray(c["vector"], dtype=np.float32)
        rows.append({
            "id": c["id"],
            "fileName": c["metadata"].get("source",""),
            "chunkId": c["chunkId"],
            "docType": c["metadata"].get("type","unknown"),
            "content": c["content"],
            "vector_dim": int(vec.shape[0]),
            "vector_b64": base64.b64encode(vec.tobytes()).decode("ascii"),
            "vector_mean": float(vec.mean()),
            "vector_norm": float(np.linalg.norm(vec)),
            "created_at": _now_utc_iso()
        })
    return pd.DataFrame(rows)

def save_parquet_to_blob(df: pd.DataFrame, partition: str | None = None) -> str:
    """
    Writes a Parquet snapshot to Blob: embeddings-archive/parquet/<partition>/vectors.parquet
    Returns the blob path used.
    """
    partition = partition or dt.datetime.utcnow().strftime("y=%Y/m=%m/d=%d/h=%H")
    container_client, path_prefix = _blob_clients(f"parquet/{partition}/vectors.parquet")

    # write to in-memory parquet
    buf = io.BytesIO()
    df.to_parquet(buf, index=False)
    buf.seek(0)

    blob = container_client.get_blob_client(path_prefix)
    blob.upload_blob(buf.getvalue(), overwrite=True, content_type="application/octet-stream")
    return path_prefix

def save_npz_to_blob(chunks: list[dict], partition: str | None = None) -> str:
    """
    Stores only vectors + ids in NPZ (compact, fast reload).
    """
    partition = partition or dt.datetime.utcnow().strftime("y=%Y/m=%m/d=%d/h=%H")
    container_client, path_prefix = _blob_clients(f"npz/{partition}/vectors.npz")

    ids = [c["id"] for c in chunks]
    vecs = np.stack([np.asarray(c["vector"], dtype=np.float32) for c in chunks])
    meta = np.array([json.dumps({"fileName": c["metadata"].get("source",""),
                                 "chunkId": c["chunkId"],
                                 "docType": c["metadata"].get("type","unknown")})
                     for c in chunks], dtype=object)

    buf = io.BytesIO()
    np.savez_compressed(buf, ids=np.array(ids), vectors=vecs, meta=meta)
    buf.seek(0)

    blob = container_client.get_blob_client(path_prefix)
    blob.upload_blob(buf.getvalue(), overwrite=True, content_type="application/octet-stream")
    return path_prefix

def load_parquet_from_blob(blob_path: str) -> pd.DataFrame:
    container_client, _ = _blob_clients("")  # we only need container reference
    bc = container_client.get_blob_client(blob_path)
    data = bc.download_blob().readall()
    return pd.read_parquet(io.BytesIO(data))

def decode_vector_b64(b64: str) -> np.ndarray:
    return np.frombuffer(base64.b64decode(b64), dtype=np.float32)
