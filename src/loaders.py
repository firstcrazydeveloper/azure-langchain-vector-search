from langchain_community.document_loaders import PyPDFLoader, TextLoader
import docx2txt, tempfile, os
from .ocr import image_to_text

def load_pdf_bytes(b: bytes, name: str):
    with tempfile.NamedTemporaryFile(delete=False, suffix=".pdf") as f:
        f.write(b); path = f.name
    docs = PyPDFLoader(path).load()
    for d in docs:
        d.metadata["source"] = name
    os.unlink(path)
    return docs

def load_docx_bytes(b: bytes, name: str):
    with tempfile.NamedTemporaryFile(delete=False, suffix=".docx") as f:
        f.write(b); path = f.name
    text = docx2txt.process(path) or ""
    os.unlink(path)
    return [{"page_content": text, "metadata": {"source": name, "type":"docx"}}]

def load_txt_bytes(b: bytes, name: str):
    text = b.decode("utf-8", errors="ignore")
    return [{"page_content": text, "metadata": {"source": name, "type":"txt"}}]

def load_image_bytes(b: bytes, name: str):
    text = image_to_text(b)
    return [{"page_content": text, "metadata": {"source": name, "type":"image-ocr"}}]

def load_by_extension(b: bytes, name: str):
    ext = name.lower().split(".")[-1]
    if ext == "pdf": return load_pdf_bytes(b, name)
    if ext in ["docx", "doc"]: return load_docx_bytes(b, name)
    if ext in ["txt", "md"]: return load_txt_bytes(b, name)
    if ext in ["png", "jpg", "jpeg", "tif", "tiff"]: return load_image_bytes(b, name)
    # fallback try as text
    return load_txt_bytes(b, name)
