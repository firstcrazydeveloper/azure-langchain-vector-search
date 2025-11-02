from PIL import Image
import pytesseract
from io import BytesIO

def image_to_text(image_bytes: bytes) -> str:
    img = Image.open(BytesIO(image_bytes)).convert("RGB")
    text = pytesseract.image_to_string(img, lang="eng")
    return text.strip()
