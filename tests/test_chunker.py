from src.chunker import chunk_documents

def test_chunker_basic():
    docs = [{"page_content":"hello world "*200, "metadata":{"source":"x"}}]
    chunks = chunk_documents(docs, size=200, overlap=20)
    assert len(chunks) > 1
    assert "content" in chunks[0]
