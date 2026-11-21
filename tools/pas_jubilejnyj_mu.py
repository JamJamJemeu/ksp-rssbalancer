import struct, numpy as np
class Wr:
    def __init__(s): s.b=bytearray()
    def i(s,*v): s.b+=struct.pack('<%di'%len(v),*v)
    def f(s,*v): s.b+=struct.pack('<%df'%len(v),*v)
    def by(s,v): s.b+=bytes([v])
    def s(s,t):
        t=t.encode(); n=len(t)
        while True:
            c=n&0x7f; n>>=7
            s.b+=bytes([c|(0x80 if n else 0)])
            if not n: break
        s.b+=t
def transform(w,pos=(0,0,0),rot=(0,0,0,1)):
    w.f(*pos); w.f(*rot); w.f(1,1,1)
def mesh(w,V,N,UVs,Tg,tris):
    nv=len(V); w.i(13,nv,1)
    w.i(14); w.f(*np.asarray(V,float).ravel())
    w.i(15); w.f(*np.asarray(UVs,float).ravel())
    w.i(17); w.f(*np.asarray(N,float).ravel())
    w.i(18); w.f(*np.asarray(Tg,float).ravel())
    t=np.asarray(tris,int).ravel(); w.i(19,len(t)); w.i(*t)
    w.i(22)
L=4500.0; HW=42.0; SH=10.0; DROP=2.5
M0,M1=8/256,248/256; soil=(3/256,0.5)
V=[];N=[];UV=[];TG=[];T=[]
def quad(a,b,c,d,uva,uvb,uvc,uvd):
    # a(-x,-z) b(-x,+z) c(+x,+z) d(+x,-z) widziane z góry -> zgodnie z ruchem wskazówek
    a,b,c,d=map(np.array,(a,b,c,d)); n=np.cross(b-a,c-a); n=n/np.linalg.norm(n)
    t=(d-a); t=t/np.linalg.norm(t)
    k=len(V)
    for p,uv in ((a,uva),(b,uvb),(c,uvc),(d,uvd)): V.append(p); N.append(n); UV.append(uv); TG.append((*t,-1.0))
    T.extend([k,k+1,k+2,k,k+2,k+3])
z0,z1=-L/2,L/2
# nawierzchnia
quad((-HW,0,z0),(-HW,0,z1),(HW,0,z1),(HW,0,z0),(M0,0),(M0,1),(M1,1),(M1,0))
# pobocza lewe/prawe (spadek na zewnątrz)
quad((-HW-SH,-DROP,z0),(-HW-SH,-DROP,z1),(-HW,0,z1),(-HW,0,z0),soil,soil,soil,soil)
quad((HW,0,z0),(HW,0,z1),(HW+SH,-DROP,z1),(HW+SH,-DROP,z0),soil,soil,soil,soil)
# końce
quad((-HW,0,z1),(-HW-SH,-DROP,z1+SH),(HW+SH,-DROP,z1+SH),(HW,0,z1),soil,soil,soil,soil)
quad((-HW-SH,-DROP,z0-SH),(-HW,0,z0),(HW,0,z0),(HW+SH,-DROP,z0-SH),soil,soil,soil,soil)
# narożniki
for sx in (-1,1):
    for sz in (-1,1):
        a=np.array((sx*HW,0,sz*L/2)); b=np.array((sx*(HW+SH),-DROP,sz*L/2)); c=np.array((sx*(HW+SH),-DROP,sz*(L/2+SH))); d=np.array((sx*HW,-DROP,sz*(L/2+SH)))
        for tri in ((a,b,c),(a,c,d)):
            p0,p1,p2=tri; n=np.cross(p1-p0,p2-p0)
            if n[1]<0: p1,p2=p2,p1; n=-n
            n=n/np.linalg.norm(n); k=len(V)
            for p in (p0,p1,p2): V.append(p); N.append(n); UV.append(soil); TG.append((1,0,0,-1))
            T.extend([k,k+1,k+2])
w=Wr(); w.i(76543,5); w.s('JubilejnyjPas')
w.s('JubilejnyjPas'); transform(w)
w.i(24); w.s('Untagged'); w.i(15)
w.i(28); w.by(0); w.f(2*HW,2.0,L); w.f(0,-1.0,0)            # kolizja: wierzch na y=0
w.i(0); w.s('nawierzchnia'); transform(w); w.i(24); w.s('Untagged'); w.i(15)
w.i(7); mesh(w,V,N,UV,TG,T)
w.i(8); w.by(0); w.by(1); w.i(1,0)
w.i(1)
for nm,z,rot in (('spawn06',-L/2+60,(0,0,0,1)),('spawn24',L/2-60,(0,1,0,0))):
    w.i(0); w.s(nm); transform(w,(0,0.5,z),rot); w.i(24); w.s('Untagged'); w.i(15); w.i(1)
w.i(10,1); w.s('JubilejnyjPas'); w.s('KSP/Diffuse'); w.i(2)
w.s('_Color'); w.i(0); w.f(1,1,1,1)
w.s('_MainTex'); w.i(4); w.i(0); w.f(1,1,0,0)
w.i(12,1); w.s('JubilejnyjPas'); w.i(0)
open('JubilejnyjPas.mu','wb').write(bytes(w.b)); print(len(w.b),'bytes',len(V),'verts',len(T)//3,'tris')
