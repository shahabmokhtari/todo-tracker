"""The Play feature graphic (1024×500, no alpha): the icon, name and line on the left, the real focus card from the
Android Today shot on the right. Run: /usr/bin/python3 store/graphic.py"""
import os
from PIL import Image, ImageDraw, ImageFont, ImageFilter
HERE=os.path.dirname(os.path.abspath(__file__))
R=os.path.join(HERE,'screenshots')+'/'
W,H=1024,500
STOPS=[(0x4f,0x46,0xe5),(0x7c,0x3a,0xed),(0xdb,0x27,0x77)]
def gradient(w,h):
    small=Image.new('RGB',(64,32)); p=small.load()
    for y in range(32):
        for x in range(64):
            t=(x/63)*.8+(y/31)*.2
            a,b,u=(STOPS[0],STOPS[1],t*2) if t<.5 else (STOPS[1],STOPS[2],(t-.5)*2)
            p[x,y]=tuple(round(a[i]+(b[i]-a[i])*u) for i in range(3))
    return small.resize((w,h),Image.BICUBIC)
def font(size,w='Bold'):
    f=ImageFont.truetype('/System/Library/Fonts/SFNS.ttf',size); f.set_variation_by_name(w); return f
can=gradient(W,H); d=ImageDraw.Draw(can)
# right: the real focus card from the Android Today shot, on a white sheet so it reads against the gradient
a1=Image.open(R+'android-phone/raw/A1-today.png').convert('RGB')
card=a1.crop((22,792,1058,1410))   # the "Do this now" card with a little page around it
cw=560; ch=round(card.height*cw/card.width); card=card.resize((cw,ch),Image.LANCZOS)
x,y=W-cw-40,(H-ch)//2
mask=Image.new('L',(cw,ch),0); ImageDraw.Draw(mask).rounded_rectangle((0,0,cw-1,ch-1),28,fill=255)
shadow=Image.new('L',(W,H),0); ImageDraw.Draw(shadow).rounded_rectangle((x,y+10,x+cw,y+ch+10),28,fill=110)
can=Image.composite(Image.new('RGB',(W,H),(25,8,50)),can,shadow.filter(ImageFilter.GaussianBlur(22)))
can.paste(card,(x,y),mask); d=ImageDraw.Draw(can)
# left: icon, name, line
icon=Image.open(os.path.join(HERE,'..','apple','App','Assets.xcassets','AppIcon.appiconset','ios.png')).convert('RGB').resize((96,96),Image.LANCZOS)
im=Image.new('L',(96,96),0); ImageDraw.Draw(im).rounded_rectangle((0,0,95,95),22,fill=255)
lx=72
ring=Image.new("L",(W,H),0); ImageDraw.Draw(ring).rounded_rectangle((lx-2,116,lx+97,215),24,fill=70); can=Image.composite(Image.new("RGB",(W,H),(255,255,255)),can,ring); d=ImageDraw.Draw(can)
can.paste(icon,(lx,118),im)
d.text((lx,236),'One Thing',font=font(50),fill='white')
f=font(22,'Medium')
for i,line in enumerate(['The to-do list that tells','you what to do now.']):
    d.text((lx,304+i*30),line,font=f,fill=(255,255,255))
can.save(os.path.join(HERE,'play-feature-graphic.png')); print(can.size, can.mode)
