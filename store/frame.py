"""Frames the store screenshots: each raw shot in screenshots/<device>/raw/ gets its caption above it on the brand
gradient, at the same pixel size, in screenshots/<device>/framed/. Run: /usr/bin/python3 store/frame.py (needs Pillow
and the SF Pro font macOS ships)."""
import os
from PIL import Image, ImageDraw, ImageFont, ImageFilter

R = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'screenshots') + '/'
# The captions from the screenshot plan; six words or fewer.
CAP = {
    'I1-today': 'One thing to do now', 'I5-board': 'See the bigger picture',
    'P1-today': 'One thing now, the rest calm', 'P2-details': 'Every detail, right beside it',
    'P3-board': 'Drag work from Inbox to Done', 'P4-plan': 'Plan the week you can manage',
    'P5-reports': 'See where your time went',
    'A1-today': 'One thing to do now', 'A2-later': 'Later means later', 'A4-board': 'See the bigger picture',
    'A5-details': 'One step at a time',
    'M1-today': 'One thing to do now', 'M2-details': 'Every detail, right beside it',
    'M3-board': 'Drag work from Inbox to Done', 'M4-plan': 'Plan the week you can manage',
    'M5-reports': 'See where your time went', 'M6-today-dark': 'Calm in the dark, too',
}
STOPS = [(0x4f, 0x46, 0xe5), (0x7c, 0x3a, 0xed), (0xdb, 0x27, 0x77)]


def gradient(w, h):
    """Diagonal, top left to bottom right, through the three brand stops."""
    small = Image.new('RGB', (64, 64)); p = small.load()
    for y in range(64):
        for x in range(64):
            t = (x + y) / 126
            a, b, u = (STOPS[0], STOPS[1], t * 2) if t < .5 else (STOPS[1], STOPS[2], (t - .5) * 2)
            p[x, y] = tuple(round(a[i] + (b[i] - a[i]) * u) for i in range(3))
    return small.resize((w, h), Image.BICUBIC)


def font(size):
    f = ImageFont.truetype('/System/Library/Fonts/SFNS.ttf', size); f.set_variation_by_name('Bold'); return f


def frame(src, dst, caption):
    shot = Image.open(src).convert('RGBA'); W, H = shot.size
    landscape = W > H
    can = gradient(W, H); d = ImageDraw.Draw(can)
    margin = round(W * 0.07); size = round(W * (0.085 if H > W * 1.6 else 0.062 if not landscape else 0.042))
    f = font(size)
    while d.textlength(caption, font=f) > W - 2 * margin: size -= 4; f = font(size)
    top = round(H * 0.055); d.text((W / 2, top), caption, font=f, fill='white', anchor='mt')
    y0 = top + round(size * 1.2) + round(H * 0.035)
    # A phone or tablet bleeds off the bottom edge; a Mac window sits whole, with room below it.
    s = min((W - 2 * margin) / W, (H - y0 - (round(H * 0.06) if landscape else 0)) / H)
    sw, sh = round(W * s), round(H * s)
    sc = shot.resize((sw, sh), Image.LANCZOS)
    r = round(sw * (0.012 if landscape else 0.06))
    mask = Image.new('L', (sw, sh), 0); ImageDraw.Draw(mask).rounded_rectangle((0, 0, sw - 1, sh - 1), r, fill=255)
    # A window capture has see-through corners of its own: keep them see-through.
    mask = Image.composite(mask, Image.new('L', (sw, sh), 0), sc.getchannel('A'))
    x, y = (W - sw) // 2, y0
    shadow = Image.new('L', (W, H), 0); ImageDraw.Draw(shadow).rounded_rectangle((x, y + 12, x + sw, y + sh + 12), r, fill=90)
    can = Image.composite(Image.new('RGB', (W, H), (30, 10, 60)), can, shadow.filter(ImageFilter.GaussianBlur(28)))
    can.paste(sc.convert('RGB'), (x, y), mask)
    can.save(dst); return can.size


if __name__ == '__main__':
    for dev in sorted(os.listdir(R)):
        if not os.path.isdir(R + dev + '/raw'): continue
        os.makedirs(R + dev + '/framed', exist_ok=True)
        for n in sorted(os.listdir(R + dev + '/raw')):
            if n.endswith('.png'):
                print(dev, n, frame(R + dev + '/raw/' + n, R + dev + '/framed/' + n, CAP[n[:-4]]))
