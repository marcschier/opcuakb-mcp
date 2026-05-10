import math
from PIL import Image, ImageDraw

OUT = r"D:\git\uakb\agents\m365-agent\appPackage"

def color_icon():
    size = 192
    img = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    d = ImageDraw.Draw(img)
    cx, cy = size // 2, size // 2
    accent = (139, 92, 246, 255)
    d.ellipse((cx-51, cy-51, cx+51, cy+51), outline=accent, width=6)
    d.ellipse((cx-22, cy-22, cx+22, cy+22), outline=accent, width=4)
    d.ellipse((cx-8, cy-8, cx+8, cy+8), fill=accent)
    for ang in [-90,-30,30,90,150,210]:
        rad = math.radians(ang)
        x1, y1 = cx + math.cos(rad)*51, cy + math.sin(rad)*51
        x2, y2 = cx + math.cos(rad)*67, cy + math.sin(rad)*67
        d.line((x1,y1,x2,y2), fill=accent, width=6)
    img.save(f"{OUT}/color.png", "PNG")

def outline_icon():
    size = 32
    img = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    d = ImageDraw.Draw(img)
    cx, cy = size//2, size//2
    white = (255,255,255,255)
    d.ellipse((cx-9, cy-9, cx+9, cy+9), outline=white, width=2)
    d.ellipse((cx-2, cy-2, cx+2, cy+2), fill=white)
    for ang in [-90,-30,30,90,150,210]:
        rad = math.radians(ang)
        x1, y1 = cx + math.cos(rad)*9, cy + math.sin(rad)*9
        x2, y2 = cx + math.cos(rad)*14, cy + math.sin(rad)*14
        d.line((x1,y1,x2,y2), fill=white, width=2)
    img.save(f"{OUT}/outline.png", "PNG")

color_icon(); outline_icon()
print("OK")
