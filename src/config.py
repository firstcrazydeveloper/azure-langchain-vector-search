import os
from dotenv import load_dotenv

load_dotenv()

class Settings:
    AZURE_SEARCH_ENDPOINT = os.getenv("AZURE_SEARCH_ENDPOINT")
    AZURE_SEARCH_API_KEY = os.getenv("AZURE_SEARCH_API_KEY")
    AZURE_SEARCH_INDEX = os.getenv("AZURE_SEARCH_INDEX", "docs-index")

    AZURE_OPENAI_ENDPOINT = os.getenv("AZURE_OPENAI_ENDPOINT")
    AZURE_OPENAI_API_KEY = os.getenv("AZURE_OPENAI_API_KEY")
    AZURE_OPENAI_EMBEDDING_DEPLOYMENT = os.getenv("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", "text-embedding-3-small")

    AZURE_BLOB_CONNECTION_STRING = os.getenv("AZURE_BLOB_CONNECTION_STRING")
    AZURE_BLOB_CONTAINER = os.getenv("AZURE_BLOB_CONTAINER", "documents")

    APP_PORT = int(os.getenv("APP_PORT", "8080"))

settings = Settings()
