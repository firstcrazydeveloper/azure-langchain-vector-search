from src.ocr import image_to_text
from PIL import Image, ImageDraw, ImageFont
from io import BytesIO

def test_ocr_roundtrip():
    img = Image.new("RGB",(300,100),"white")
    d = ImageDraw.Draw(img)
    d.text((10,40),"Hello OCR!", fill="black")
    buf = BytesIO(); img.save(buf, format="PNG")
    text = image_to_text(buf.getvalue())
    assert "Hello" in text
