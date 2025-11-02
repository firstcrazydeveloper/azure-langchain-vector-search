# 🚀 Azure LangChain Vector Search Pipeline  
*End-to-End Semantic Document Search using Azure OpenAI + Cognitive Search + LangChain*  

✍️ **By Abhishek Kumar | #FirstCrazyDeveloper**

---

## 🧭 Overview  

This project demonstrates a **production-ready vector search pipeline** using the **Azure ecosystem** combined with **LangChain**.  

It automatically:  
1️⃣ Ingests documents (PDF, Word, Text, and Images via OCR) from **Azure Blob Storage**  
2️⃣ Generates **embeddings** using **Azure OpenAI**  
3️⃣ Indexes semantic vectors in **Azure Cognitive Search** (Vector + Hybrid)  
4️⃣ Exposes a simple **FastAPI endpoint** for querying by meaning  
5️⃣ Archives all processed vectors as **Parquet** & **NPZ** snapshots for future reference  

> “You’re not just searching words — you’re searching meaning.”  

---

## 🧩 Tech Stack  

| Layer | Technology |
|-------|-------------|
| Storage | Azure Blob Storage |
| Embeddings | Azure OpenAI (`text-embedding-3-small`) |
| Vector Database | Azure Cognitive Search (Vector Search) |
| Framework | LangChain |
| API | FastAPI |
| Container | Docker / Docker Compose |
| Automation | PowerShell Scripts |
| Archive Format | Parquet + NPZ (stored in Blob) |

---

## 🏗️ Architecture  

```text
Azure Blob  →  LangChain Processor  →  Azure OpenAI (Embeddings)
         →  Vector Archive (Parquet / NPZ in Blob)
         →  Azure Cognitive Search (Vector + Hybrid Index)
         →  FastAPI Query Endpoint  →  Applications (C#, Python, JS)
