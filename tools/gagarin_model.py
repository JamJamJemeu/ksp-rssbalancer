import sys; sys.path.insert(0,'/tmp/gag'); sys.path.insert(0,'/tmp/mu'); sys.path.insert(0,'/tmp/rwy')
from geom import *
from uvq import uvquery
from PIL import Image, ImageDraw, ImageFilter
import struct
Image.MAX_IMAGE_PIXELS=None
rng=np.random.default_rng(3)
# ---------- tekstura płyty (mapa Katnisss + beton) ----------
N=1024
tex=np.asarray(Image.open('/mnt/user-data/uploads/Kerbal Space Program/GameData/KatnisssBaikonurCosmodrome/Assets/BaikonurColor.png').convert('RGB')).astype(float)
EN,alt=setup()
pix=(np.arange(N)+0.5)/N*2*P-P
X,Z=np.meshgrid(pix,pix[::-1])   # wiersz 0 = +z
E_,N_=m2en(X.ravel(),Z.ravel())
uv=uvquery(EN,E_,N_); H,Wd=tex.shape[:2]
u=uv[:,0]*(Wd-1); v=(1-uv[:,1])*(H-1)
iu=np.floor(u).astype(int); iv=np.floor(v).astype(int); fu=(u-iu)[:,None]; fv=(v-iv)[:,None]
g=lambda a,b: tex[np.clip(b,0,H-1),np.clip(a,0,Wd-1)]
img=((g(iu,iv)*(1-fu)+g(iu+1,iv)*fu)*(1-fv)+(g(iu,iv+1)*(1-fu)+g(iu+1,iv+1)*fu)*fv).reshape(N,N,3)
img+=rng.normal(0,5,(N,N))[...,None]   # trochę szczegółu
def m2px(x,z): return (x+P)/(2*P)*N, (P-z)/(2*P)*N
im=Image.fromarray(np.clip(img,0,255).astype(np.uint8)); dr=ImageDraw.Draw(im)
def rect(x0,x1,z0,z1,col):
    c0,r1=m2px(x0,z0); c1,r0=m2px(x1,z1); dr.rectangle([c0,r0,c1,r1],fill=col)
# betonowy plac wokół wykopu
rect(-70,70,-75,DZ1+15,(150,148,140))
# płyty betonowe 6 m
for x in np.arange(-70,71,6): rect(x,x+0.25,-75,DZ1+15,(128,126,120))
for z in np.arange(-75,DZ1+16,6): rect(-70,70,z,z+0.25,(128,126,120))
# osmalenie przy wylocie wykopu i wokół stanowiska
a=np.asarray(im).astype(float)
cx,cz=np.meshgrid(pix,pix[::-1])
soot=np.exp(-((cx/45)**2+((cz-DZ1)/35)**2))*0.55+np.exp(-((cx/30)**2+(cz/30)**2))*0.35
a*=(1-soot)[...,None]
# drogi dojazdowe
im=Image.fromarray(np.clip(a,0,255).astype(np.uint8)); dr=ImageDraw.Draw(im)
rect(-4,4,-P,-75,(120,116,108)); rect(-P,-70,30,38,(120,116,108))
im.save('/tmp/gag/GagarinPlac.png')
# ---------- tekstura konstrukcji: ściany | dno | stal ----------
S=512; b=np.zeros((S,S,3))
w=S//3
b[:,:w]=np.array([112,110,104])+rng.normal(0,6,(S,w))[...,None]
for r in range(0,S,32): b[r:r+2,:w]*=0.8                           # pasy betonowania
b[:,:w]*= (0.6+0.4*np.linspace(1,0,S))[:,None,None]**0.5           # okopcenie u dołu (v=0 dół)
b[:,w:2*w]=np.array([58,54,50])+rng.normal(0,8,(S,w))[...,None]    # dno – okopcony beton
b[:,2*w:]=np.array([92,96,100])+rng.normal(0,4,(S,S-2*w))[...,None]
for r in range(0,S,16): b[r:r+3,2*w:]=[60,62,66]
for c in range(2*w,S,16): b[:,c:c+3]=[60,62,66]
Image.fromarray(np.clip(b,0,255).astype(np.uint8)).save('/tmp/gag/GagarinKonstrukcja.png')
# ---------- model ----------
from writemu import Wr, transform
def mesh(w,V,Nn,UVs,tris):
    nv=len(V); w.i(13,nv,1)
    w.i(14); w.f(*np.asarray(V,float).ravel()); w.i(15); w.f(*np.asarray(UVs,float).ravel())
    w.i(17); w.f(*np.asarray(Nn,float).ravel()); w.i(18); w.f(*np.tile([1,0,0,-1],nv))
    t=np.asarray(tris,int).ravel(); w.i(19,len(t)); w.i(*t); w.i(22)
class MB:
    def __init__(s): s.V=[];s.N=[];s.UV=[];s.T=[]
    def quad(s,p,uv):
        p=[np.array(q,float) for q in p]; n=np.cross(p[1]-p[0],p[2]-p[0]); n/=np.linalg.norm(n); k=len(s.V)
        for q,t in zip(p,uv): s.V.append(q); s.N.append(n); s.UV.append(t)
        s.T+= [k,k+1,k+2,k,k+2,k+3]
def puv(x,z): return ((x+P)/(2*P),(z+P)/(2*P))
plate=MB()
def ptop(x0,x1,z0,z1):  # poziomy prostokąt, przód do góry
    pts=[(x0,0,z0),(x0,0,z1),(x1,0,z1),(x1,0,z0)]; plate.quad(pts,[puv(p[0],p[2]) for p in pts])
ptop(-P,P,-P,DZ0); ptop(-P,P,DZ1,P); ptop(-P,-DX,DZ0,DZ1); ptop(DX,P,DZ0,DZ1)
st=MB(); W3=1/3
def wall(p,u0,u1,vh):
    st.quad(p,[(u0,vh),(u0,0),(u1,0),(u1,vh)])
D=-DEPTH
# ściany (normalne do środka wykopu): kolejność tak, by normalna wskazywała do środka
st.quad([(-DX,0,DZ0),(-DX,D,DZ0),(-DX,D,DZ1),(-DX,0,DZ1)],[(0,1),(0,0),(W3,0),(W3,1)])[0:0] if False else None
def wq(a,b,c,d,inward):
    a,b,c,d=map(np.array,(a,b,c,d)); n=np.cross(b-a,c-a)
    if np.dot(n,inward)<0: a,b,c,d=d,c,b,a
    st.quad([a,b,c,d],[(0,1),(0,0),(W3*0.99,0),(W3*0.99,1)])
wq((-DX,0,DZ0),(-DX,D,DZ0),(-DX,D,DZ1),(-DX,0,DZ1),(1,0,0))
wq((DX,0,DZ0),(DX,D,DZ0),(DX,D,DZ1),(DX,0,DZ1),(-1,0,0))
wq((-DX,0,DZ0),(-DX,D,DZ0),(DX,D,DZ0),(DX,0,DZ0),(0,0,1))
wq((-DX,0,DZ1),(-DX,D,DZ1),(DX,D,DZ1),(DX,0,DZ1),(0,0,-1))
# dno
a,b_,c,d=np.array((-DX,D,DZ0)),np.array((-DX,D,DZ1)),np.array((DX,D,DZ1)),np.array((DX,D,DZ0))
st.quad([a,b_,c,d],[(W3*1.01,0),(W3*1.01,1),(W3*1.99,1),(W3*1.99,0)])
# odbijacz płomieni pod stanowiskiem (skos od dna przy z=DZ0 w górę? – klin)
st.quad([(-DX,D+18,DZ0),(-DX,D,DZ0+35),(DX,D,DZ0+35),(DX,D+18,DZ0)],[(W3*1.01,0),(W3*1.01,1),(W3*1.99,1),(W3*1.99,0)])
# pomost stalowy nad wykopem z otworem
DY=0.02; ZD=12.0
def deck(x0,x1,z0,z1):
    st.quad([(x0,DY,z0),(x0,DY,z1),(x1,DY,z1),(x1,DY,z0)],[(0.68,0.01),(0.68,0.99),(0.99,0.99),(0.99,0.01)])
for x0,x1,z0,z1 in ((-DX,DX,DZ0,-HOLE),(-DX,DX,HOLE,ZD),(-DX,-HOLE,-HOLE,HOLE),(HOLE,DX,-HOLE,HOLE)): deck(x0,x1,z0,z1)
# boki pomostu (2 m grubości, widoczne z wykopu)
for z in (ZD,): wq((-DX,DY,z),(-DX,-2,z),(DX,-2,z),(DX,DY,z),(0,0,1))
w=Wr(); w.i(76543,5); w.s('GagarinStart')
w.s('GagarinStart'); transform(w); w.i(24); w.s('Untagged'); w.i(15)
boxes=[((P-DX,2,2*P),(-(P+DX)/2,-1,0)),((P-DX,2,2*P),((P+DX)/2,-1,0)),((2*DX,2,DZ0+P),(0,-1,(-P+DZ0)/2)),((2*DX,2,P-DZ1),(0,-1,(P+DZ1)/2)),
       ((2*DX,2,DZ1-DZ0),(0,D-1,(DZ0+DZ1)/2)),
       ((1,-D,DZ1-DZ0),(-DX-0.5,D/2,(DZ0+DZ1)/2)),((1,-D,DZ1-DZ0),(DX+0.5,D/2,(DZ0+DZ1)/2)),((2*DX,-D,1),(0,D/2,DZ0-0.5)),((2*DX,-D,1),(0,D/2,DZ1+0.5)),
       ((2*DX,1,-HOLE-DZ0),(0,-0.5,(DZ0-HOLE)/2)),((2*DX,1,ZD-HOLE),(0,-0.5,(ZD+HOLE)/2)),((DX-HOLE,1,2*HOLE),(-(DX+HOLE)/2,-0.5,0)),((DX-HOLE,1,2*HOLE),((DX+HOLE)/2,-0.5,0))]
for i,(sz,c) in enumerate(boxes):
    w.i(0); w.s('kolizja%02d'%i); transform(w); w.i(24); w.s('Untagged'); w.i(15)
    w.i(28); w.by(0); w.f(*sz); w.f(*c); w.i(1)
for name,mb,mat in (('plac',plate,0),('wykop',st,1)):
    w.i(0); w.s(name); transform(w); w.i(24); w.s('Untagged'); w.i(15)
    w.i(7); mesh(w,mb.V,mb.N,mb.UV,mb.T); w.i(8); w.by(1 if mat else 0); w.by(1); w.i(1,mat); w.i(1)
w.i(0); w.s('spawn'); transform(w,(0,0.3,0)); w.i(24); w.s('Untagged'); w.i(15); w.i(1)
w.i(10,2)
for nm,ti in (('GagarinPlac',0),('GagarinKonstrukcja',1)):
    w.s(nm); w.s('KSP/Diffuse'); w.i(2); w.s('_Color'); w.i(0); w.f(1,1,1,1); w.s('_MainTex'); w.i(4); w.i(ti); w.f(1,1,0,0)
w.i(12,2); w.s('GagarinPlac'); w.i(0); w.s('GagarinKonstrukcja'); w.i(0)
open('/tmp/gag/GagarinStart.mu','wb').write(bytes(w.b)); print('mu',len(w.b))
# kontrola normalnych
for nm,mb in (('plac',plate),('wykop',st)):
    V=np.array(mb.V);T=np.array(mb.T).reshape(-1,3); n=np.cross(V[T[:,1]]-V[T[:,0]],V[T[:,2]]-V[T[:,0]]); print(nm,len(T),'normals:',np.round(n/np.linalg.norm(n,axis=1)[:,None],2).tolist()[:: max(1,len(T)//12)])
