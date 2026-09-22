# -*- coding: utf-8 -*-
"""生成"GIF 动作"演示素材（idle.gif / talk.gif）。

用途：验证/演示桌面精灵直接播 GIF —— GIF 的**每帧延时写在文件里**，
精灵会照它播（所以动作里的 fps 对 GIF 无效，只对 PNG 序列生效）。

看效果：把宠物配置的 assetDir 指到 assets/gif-demo，
actions 改成 {"idle":{"frames":["idle.gif"]}, "talk":{"frames":["talk.gif"]}}。
"""
from PIL import Image, ImageDraw

W, H = 240, 340

# 每帧停留毫秒 —— 故意各不相同，方便一眼看出"精灵用的是 GIF 自己的节奏"
IDLE_MS = [150, 250, 400]
TALK_MS = [120, 120]
HAPPY_MS = [150, 150]

BODY = (110, 178, 214, 255)     # 水蓝
BODY_D = (78, 142, 182, 255)
SKIN = (248, 224, 210, 255)
EYE = (226, 180, 80, 255)
LINE = (60, 66, 80, 255)


def blob(i, n, bob, mouth, happy=False):
    """一帧：会上下浮的蓝色小团子；mouth=True 张嘴（talk），happy=True 眯眼笑（happy）"""
    im = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    cx = W // 2
    top = 96 + bob

    d.ellipse([cx - 70, top, cx + 70, top + 168], fill=BODY)          # 身体
    d.ellipse([cx - 48, top + 46, cx + 48, top + 142], fill=SKIN)     # 脸
    d.ellipse([cx - 70, top, cx + 70, top + 76], fill=BODY_D)         # 刘海
    if happy:
        d.arc([cx - 28, top + 70, cx - 6, top + 94], 200, 340, fill=LINE, width=4)   # 眯眼笑
        d.arc([cx + 6, top + 70, cx + 28, top + 94], 200, 340, fill=LINE, width=4)
    else:
        d.ellipse([cx - 24, top + 76, cx - 10, top + 90], fill=EYE)                  # 左眼
        d.ellipse([cx + 10, top + 76, cx + 24, top + 90], fill=EYE)                  # 右眼
    if mouth:
        d.ellipse([cx - 14, top + 112, cx + 14, top + 132], fill=(90, 60, 60, 255))
    else:
        d.arc([cx - 14, top + 106, cx + 14, top + 126], 0, 180, fill=(90, 60, 60, 255), width=3)

    d.text((8, 8), f"{i + 1}/{n}", fill=(255, 255, 255, 200))
    return im


def save(path, delays, use_mouth, happy=False):
    n = len(delays)
    frames = [blob(i, n, [-9, 0, 9][i % 3], use_mouth, happy) for i in range(n)]
    frames[0].save(
        path, save_all=True, append_images=frames[1:],
        duration=delays, loop=0,
        disposal=2, transparency=0,   # 透明背景必须 restore-to-background，否则会拖影
    )
    print(f"{path}  {n} 帧  延时 {delays} ms")


if __name__ == "__main__":
    import os
    here = os.path.dirname(os.path.abspath(__file__))
    save(os.path.join(here, "idle.gif"), IDLE_MS, use_mouth=False)
    save(os.path.join(here, "talk.gif"), TALK_MS, use_mouth=True)
    save(os.path.join(here, "happy.gif"), HAPPY_MS, use_mouth=True, happy=True)

