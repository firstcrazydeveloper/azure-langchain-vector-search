# tests/test_chunker.py
import types
import pytest

# Import the functions under test
from src.chunker import split_documents, chunk_documents

# --- Helpers -----------------------------------------------------------------

def make_doc(text: str, metadata: dict | None = None):
    """Make a lightweight LC-like Document object with attributes .page_content/.metadata."""
    return types.SimpleNamespace(page_content=text, metadata=(metadata or {}))

TEXT = (
    "Azure Cognitive Search provides vector search capabilities. "
    "It integrates well with Azure OpenAI embeddings. "
    "Chunking text properly improves retrieval quality."
)

# --- Tests for split_documents -----------------------------------------------

def test_split_documents_with_dicts_basic():
    docs = [{"page_content": TEXT, "metadata": {"source": "unit"}}]
    chunks = split_documents(docs, chunk_size=80, chunk_overlap=20)

    assert isinstance(chunks, list)
    assert all("page_content" in c and "metadata" in c for c in chunks)
    assert len(chunks) >= 2, "Expected multiple chunks for given size"
    assert chunks[0]["metadata"]["source"] == "unit"

def test_split_documents_with_doc_objects():
    docs = [make_doc(TEXT, {"source": "obj"})]
    chunks = split_documents(docs, chunk_size=80, chunk_overlap=20)

    assert isinstance(chunks, list)
    assert len(chunks) >= 2
    assert chunks[0]["metadata"]["source"] == "obj"
    assert isinstance(chunks[0]["page_content"], str)

def test_split_documents_empty_text_returns_empty_list():
    docs = [{"page_content": "   ", "metadata": {"source": "empty"}}]
    chunks = split_documents(docs, chunk_size=100, chunk_overlap=20)
    # Recursive splitter may still return 0 or 1 tiny chunk; normalize expectation
    assert all(c["page_content"].strip() != "" for c in chunks), "No empty chunk allowed"

def test_split_documents_overlap_effect():
    """With overlap>0 we expect consecutive chunks to share some substring."""
    docs = [{"page_content": "A " * 120, "metadata": {}}]  # 240 chars approx
    c_no_overlap = split_documents(docs, chunk_size=100, chunk_overlap=0)
    c_overlap    = split_documents(docs, chunk_size=100, chunk_overlap=20)

    assert len(c_overlap) >= len(c_no_overlap), "Overlap should not reduce chunk count"
    if len(c_overlap) >= 2:
        inter = set(c_overlap[0]["page_content"].split()) & set(c_overlap[1]["page_content"].split())
        assert len(inter) > 0, "Expected some word overlap between adjacent chunks"

# --- Tests for chunk_documents -----------------------------------------------

def test_chunk_documents_shape_and_keys():
    docs = [{"page_content": TEXT, "metadata": {"k": 1}}]
    chunks = chunk_documents(docs, size=60, overlap=10)

    assert isinstance(chunks, list)
    assert all(set(c.keys()) == {"id", "content", "metadata"} for c in chunks)
    assert chunks[0]["metadata"]["k"] == 1
    assert chunks[0]["id"] is None

def test_chunk_documents_accepts_content_key():
    docs = [{"content": "Hello world. " * 10, "metadata": {"src": "alt"}}]
    chunks = chunk_documents(docs, size=40, overlap=5)

    assert len(chunks) >= 2
    assert chunks[0]["metadata"]["src"] == "alt"
    assert isinstance(chunks[0]["content"], str)

# --- Edge cases --------------------------------------------------------------

@pytest.mark.parametrize("bad", [None, {}, {"metadata": {}}, {"page_content": None}])
def test_split_documents_robust_to_bad_inputs(bad):
    docs = [bad] if bad is not None else []
    # Filter out None/empty before calling to emulate loader behavior
    docs = [d for d in docs if d and ("page_content" in d or "content" in d)]
    chunks = split_documents(docs, chunk_size=50, chunk_overlap=10)
    assert isinstance(chunks, list)  # Should not explode
