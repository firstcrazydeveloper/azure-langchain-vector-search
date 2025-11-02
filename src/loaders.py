# src/loaders.py
import io, os
import pandas as pd
from langchain.document_loaders import PyPDFLoader, UnstructuredWordDocumentLoader, TextLoader
from PIL import Image
import pytesseract

def load_document(file_path: str):
    ext = os.path.splitext(file_path.lower())[1]
    docs = []

    if ext == ".pdf":
        loader = PyPDFLoader(file_path)
        docs = loader.load()

    elif ext in [".docx", ".doc"]:
        loader = UnstructuredWordDocumentLoader(file_path)
        docs = loader.load()

    elif ext in [".txt", ".log"]:
        loader = TextLoader(file_path, encoding="utf-8")
        docs = loader.load()

    elif ext in [".png", ".jpg", ".jpeg"]:
        # OCR for image files
        text = pytesseract.image_to_string(Image.open(file_path))
        docs = [{"page_content": text, "metadata": {"source": file_path, "type": "image"}}]

    elif ext == ".csv":
        df = pd.read_csv(file_path)
        text = df.to_string(index=False)
        docs = [{"page_content": text, "metadata": {"source": file_path, "type": "csv"}}]

    elif ext in [".xls", ".xlsx"]:
        df = pd.read_excel(file_path)
        text = df.to_string(index=False)
        docs = [{"page_content": text, "metadata": {"source": file_path, "type": "excel"}}]

    else:
        print(f"⚠️ Unsupported file type: {ext}")

    return docs
