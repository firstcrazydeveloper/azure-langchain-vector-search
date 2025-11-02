from langchain.text_splitter import RecursiveCharacterTextSplitter

def chunk_documents(langchain_docs_or_dicts, size=1000, overlap=120):
    # Accept list of LC Documents or dicts with page_content
    normalized = []
    for d in langchain_docs_or_dicts:
        if isinstance(d, dict):
            content = d["page_content"]
            meta = d.get("metadata", {})
            normalized.append(type("Doc", (), {"page_content":content, "metadata":meta}))
        else:
            normalized.append(d)

    splitter = RecursiveCharacterTextSplitter(
        chunk_size=size,
        chunk_overlap=overlap,
        separators=["\n\n", "\n", " ", ""]
    )
    chunks = splitter.split_documents(normalized)
    # Convert back to serializable dicts
    return [{"id": None, "content": c.page_content, "metadata": c.metadata} for c in chunks]
