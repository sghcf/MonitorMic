#!/usr/bin/env python3
"""生成 MonitorMic 应用图标：macOS 风格圆角矩形 + 麦克风图形，输出 1024x1024 PNG。"""
from PIL import Image, ImageDraw
import math

S = 4096  # 4x 超采样，最后缩到 1024
U = S / 1024.0  # 设计坐标(1024) -> 像素

def px(v):
    return v * U

# ---------- 1. 背景：对角线渐变 + 圆角矩形 ----------
BODY = 824          # macOS 图标主体尺寸（1024 网格内）
INSET = (1024 - BODY) / 2

c1 = (99, 102, 241)   # indigo-500
c2 = (37, 99, 235)    # blue-600

# 小尺寸线性渐变 → 旋转45° → 放大
g = Image.new("RGB", (256, 1))
for x in range(256):
    t = x / 255.0
    g.putpixel((x, 0), tuple(int(c1[i] + (c2[i] - c1[i]) * t) for i in range(3)))
g = g.resize((256, 256))
g = g.rotate(45, expand=True, resample=Image.BICUBIC)
w, h = g.size
side = int(min(w, h) / math.sqrt(2))
grad = g.crop(((w - side) // 2, (h - side) // 2, (w + side) // 2, (h + side) // 2))
grad = grad.resize((int(px(BODY)), int(px(BODY))), Image.LANCZOS)

# 顶部高光：让图标更有质感
highlight = Image.new("L", grad.size, 0)
hd = ImageDraw.Draw(highlight)
hd.ellipse([-grad.size[0] * 0.3, -grad.size[1] * 0.9,
            grad.size[0] * 1.3, grad.size[1] * 0.5], fill=28)
from PIL import ImageChops
whiter = ImageChops.add(grad, Image.merge("RGB", (highlight, highlight, highlight)))

# 圆角矩形蒙版
body_px = int(px(BODY))
mask = Image.new("L", (body_px, body_px), 0)
md = ImageDraw.Draw(mask)
md.rounded_rectangle([0, 0, body_px - 1, body_px - 1], radius=int(px(185)), fill=255)

img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
img.paste(whiter.convert("RGBA"), (int(px(INSET)), int(px(INSET))), mask)

# ---------- 2. 麦克风图形 ----------
d = ImageDraw.Draw(img)
cx, cy = 512, 468          # 麦克风中心（略偏上，给支架留位）
white = (255, 255, 255, 255)
soft  = (255, 255, 255, 205)

def bbox(center, r):
    x, y = px(center[0]), px(center[1])
    rr = px(r)
    return [x - rr, y - rr, x + rr, y + rr]

# 音波弧线（左右各两条）
for r, span in ((252, 22), (308, 22)):
    d.arc(bbox((cx, cy), r), start=-span, end=span, fill=soft, width=int(px(26)))      # 右侧
    d.arc(bbox((cx, cy), r), start=180 - span, end=180 + span, fill=soft, width=int(px(26)))  # 左侧

# 麦克风头（胶囊）
mw, mh = 176, 296
d.rounded_rectangle([px(cx - mw / 2), px(cy - mh / 2), px(cx + mw / 2), px(cy + mh / 2)],
                    radius=px(mw / 2), fill=white)

# U 型支架弧
d.arc(bbox((cx, cy), 172), start=18, end=162, fill=white, width=int(px(34)))

# 竖杆 + 底座
d.line([(px(cx), px(cy + 158)), (px(cx), px(cy + 232))], fill=white, width=int(px(34)))
d.line([(px(cx - 74), px(cy + 244)), (px(cx + 74), px(cy + 244))], fill=white, width=int(px(34)))

# ---------- 3. 输出 ----------
img = img.resize((1024, 1024), Image.LANCZOS)
img.save("AppIcon_1024.png")
print("OK AppIcon_1024.png")
