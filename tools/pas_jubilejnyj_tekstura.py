import numpy as np
from PIL import Image, ImageDraw
rng=np.random.default_rng(7)
W,H=256,8192; L=4500.0; WID=84.0
M0=8; M1=248  # kolumny betonu
mx=(M1-M0)/WID; my=H/L
def X(x): return M0+(x+WID/2)*mx        # x [-42,42] -> kolumna
def Y(z): return (L/2-z)*my             # z [-2250,2250] -> wiersz (0 = koniec 24)
img=np.zeros((H,W,3),float)
# beton: płyty 6 m (w poprzek 7 pasów po 12 m? PAG 6x2: długość 6 m wzdłuż)
base=np.array([168,166,158.])
img[:]=base
# kolor płyt
for xi in range(14):           # 14 płyt w poprzek po 6 m
    for zi in range(int(L/2)):  # płyty 2 m wzdłuż
        pass
# szybciej: szum na siatce płyt
nx,nz=14,int(L/2)
var=rng.normal(0,4.5,(nz,nx))
cols=np.clip(((np.arange(W)-M0)/mx/6).astype(int),0,nx-1)
rows=np.clip(((L/2-(np.arange(H)+0.5)/my+L/2)/2).astype(int),0,nz-1)
img+=var[rows][:,cols][...,None]
img+=rng.normal(0,3,(H,W))[...,None]
# fugi: wzdłuż co 6 m, w poprzek co 2 m (cienkie)
for xi in range(1,nx):
    c=int(round(X(-42+6*xi))); img[:,c,:]*=0.86
zj=np.arange(-L/2,L/2,2.0)
for z in zj:
    r=int(Y(z)); 
    if 0<=r<H: img[r,M0:M1,:]*=0.9
# ślady opon w strefach przyziemienia
def tyre(z0,z1):
    for _ in range(1300):
        z=rng.uniform(z0,z1); x=rng.normal(0,7)
        r=int(Y(z)); c=int(X(x)); ln=int(rng.uniform(8,60)); w=rng.integers(1,3)
        r0,r1=sorted((r,r+ln*(1 if rng.random()<.5 else -1)))
        img[max(r0,0):min(r1,H),max(c-w,0):c+w,:]*=rng.uniform(0.84,0.95)
tyre(-2250+250,-2250+1100); tyre(2250-1100,2250-250)
im=Image.fromarray(np.clip(img,0,255).astype(np.uint8)); dr=ImageDraw.Draw(im)
WH=(236,236,232)
def rect(x0,x1,z0,z1,col=WH):
    r0,r1=sorted((Y(z0),Y(z1))); c0,c1=sorted((X(x0),X(x1)))
    dr.rectangle([int(round(c0)),int(round(r0)),int(round(c1))-1,int(round(r1))-1],fill=col)
# krawędzie
rect(-41,-40.1,-L/2,L/2); rect(40.1,41,-L/2,L/2)
# oś: 30 m kreska, 20 m przerwy, 0,9 m szer.
for z in np.arange(-L/2+120,L/2-120,50): rect(-0.45,0.45,z,z+30)
# progi + cyfry + strefa przyziemienia dla obu końców
SEG={'0':'abcdef','2':'abged','4':'fgbc','6':'afedcg'}
def digit(ch,xc,zbase,sgn,h=9.0,w=4.5,t=1.0):
    # odcinki 7-seg w układzie glifu (u w prawo, v w górę), potem do (x,z) z kierunkiem sgn
    segs={'a':(0,h-t,w,h),'b':(w-t,h/2,w,h),'c':(w-t,0,w,h/2),'d':(0,0,w,t),'e':(0,0,t,h/2),'f':(0,h/2,t,h),'g':(0,h/2-t/2,w,h/2+t/2)}
    for s in SEG[ch]:
        u0,v0,u1,v1=segs[s]
        if sgn>0: rect(xc+u0-w/2,xc+u1-w/2,zbase+v0,zbase+v1)
        else: rect(xc-(u1-w/2),xc-(u0-w/2),zbase-v1,zbase-v0)
for sgn,txt in ((1,'06'),(-1,'24')):
    zt=-sgn*L/2   # koniec pasa
    # pianino: 8 pasów 1,8 m po każdej stronie osi, długość 30 m od 6 m
    for k in range(8):
        for side in (-1,1):
            xa=side*(3+k*4.6); rect(xa,xa+side*1.8,zt+sgn*6,zt+sgn*36)
    # cyfry 12 m wysokie od 48 m
    zb=zt+sgn*48
    dx=7.0
    for i,ch in enumerate(txt):
        xc=(-dx/2+i*dx)*sgn
        digit(ch,xc,zb,sgn,h=12.0,w=5.5,t=1.3)
    # punkt celowania 400 m: dwa pasy 45x6 m
    for side in (-1,1): rect(side*6.5,side*12.5,zt+sgn*400,zt+sgn*445)
    # strefa przyziemienia: pary po 22 m co 150 m
    for zz,n in ((150,3),(300,3),(550,2),(700,2),(850,1)):
        for side in (-1,1):
            for j in range(n):
                xa=side*(6+j*2.7); rect(xa,xa+side*1.8,zt+sgn*zz,zt+sgn*(zz+22))
# pobocza (grunt)
a=np.asarray(im).copy(); a[:,:M0]=(128,112,88); a[:,M1:]=(128,112,88)
Image.fromarray(a).save('JubilejnyjPas.png',optimize=True)
Image.fromarray(a[:900]).resize((256*2,900*2)).save('/tmp/rwy/preview_24.png')
Image.fromarray(a[-900:][::-1,::-1]).resize((512,1800)).save('/tmp/rwy/preview_06.png')
print('ok')
