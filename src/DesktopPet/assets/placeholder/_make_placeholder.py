# -*- coding: utf-8 -*-
"""生成占位素材：一只极简 Q 版静静剪影（透明背景）+ 托盘图标。
P0 只为了让窗口/动画/气泡跑起来，素材后面用 ComfyUI + 抠图替换（pet.json 改 assetDir 即可）。
"""
from PIL import Image, ImageDraw

W, H = 240, 340
SKIN   = (248, 224, 210, 255)
HAIR   = (110, 178, 214, 255)     # 水蓝色头发
HAIR_D = (78, 142, 182, 255)
EYE    = (226, 180, 80, 255)      # 金色眼睛
DRESS  = (48, 52, 66, 255)
APRON  = (245, 246, 250, 255)
LINE   = (35, 38, 48, 255)


def base():
    im = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    cx = W // 2

    # 身体（裙子）
    d.rounded_rectangle([cx - 62, 196, cx + 62, 318], radius=34, fill=DRESS)
    # 围裙
    d.rounded_rectangle([cx - 40, 214, cx + 40, 300], radius=18, fill=APRON)
    # 手臂
    d.rounded_rectangle([cx - 76, 200, cx - 52, 268], radius=12, fill=DRESS)
    d.rounded_rectangle([cx + 52, 200, cx + 76, 268], radius=12, fill=DRESS)

    # 头（脸）
    d.ellipse([cx - 58, 74, cx + 58, 196], fill=SKIN)
    # 头发（外轮廓 + 刘海 + 两条麻花辫）
    d.ellipse([cx - 68, 60, cx + 68, 178], fill=HAIR)
    d.pieslice([cx - 68, 60, cx + 68, 190], 0, 180, fill=HAIR_D)
    d.ellipse([cx - 54, 84, cx + 54, 178], fill=SKIN)
    d.rounded_rectangle([cx - 78, 150, cx - 58, 286], radius=10, fill=HAIR)   # 左辫
    d.rounded_rectangle([cx + 58, 150, cx + 78, 286], radius=10, fill=HAIR)   # 右辫
    # 辫子分节
    for yy in (176, 208, 240, 268):
        d.line([cx - 78, yy, cx - 58, yy], fill=HAIR_D, width=3)
        d.line([cx + 58, yy, cx + 78, yy], fill=HAIR_D, width=3)
    return im, d, cx


def draw_face(d, cx, eye_open=True, mouth_open=False, happy=False):
    if happy:
        # 眯眼笑：两条上弯的弧（比睁眼更像"开心"，一眼能看出区别）
        d.arc([cx - 34, 116, cx - 10, 144], 200, 340, fill=LINE, width=3)
        d.arc([cx + 10, 116, cx + 34, 144], 200, 340, fill=LINE, width=3)
    elif eye_open:
        d.ellipse([cx - 30, 122, cx - 14, 142], fill=EYE)
        d.ellipse([cx + 14, 122, cx + 30, 142], fill=EYE)
        d.ellipse([cx - 24, 128, cx - 20, 136], fill=LINE)
        d.ellipse([cx + 20, 128, cx + 24, 136], fill=LINE)
    else:
        d.line([cx - 30, 132, cx - 14, 132], fill=LINE, width=3)
        d.line([cx + 14, 132, cx + 30, 132], fill=LINE, width=3)
    # 嘴
    if happy:
        d.arc([cx - 15, 146, cx + 15, 174], 0, 180, fill=LINE, width=3)   # 咧嘴笑
    elif mouth_open:
        d.ellipse([cx - 7, 158, cx + 7, 170], fill=LINE)
    else:
        d.arc([cx - 9, 152, cx + 9, 166], 200, 340, fill=LINE, width=2)


def save(name, eye_open=True, mouth_open=False, dy=0, happy=False):
    im, d, cx = base()
    draw_face(d, cx, eye_open, mouth_open, happy)
    if dy:
        im = im.transform(im.size, Image.AFFINE, (1, 0, 0, 0, 1, -dy), resample=Image.BICUBIC)
    im.save(name)
    return im


import os
os.makedirs("idle", exist_ok=True)
os.makedirs("talk", exist_ok=True)
os.makedirs("happy", exist_ok=True)

save("idle/1.png", eye_open=True)
save("idle/2.png", eye_open=False, dy=2)      # 眨眼 + 轻微起伏
save("talk/1.png", eye_open=True, mouth_open=False)
save("talk/2.png", eye_open=True, mouth_open=True, dy=2)
save("happy/1.png", happy=True)               # 表情动作：眯眼笑
save("happy/2.png", happy=True, dy=-5)        # 蹦一下（第二帧整体往上挪）

# 托盘图标（取 idle 第一帧缩小）
icon_src = Image.open("idle/1.png")
icon_src.resize((32, 32), Image.LANCZOS).save(
    "tray.ico", sizes=[(16, 16), (32, 32), (48, 48)])
print("占位素材已生成：idle/1-2.png, talk/1-2.png, happy/1-2.png, tray.ico")
