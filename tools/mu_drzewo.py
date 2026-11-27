import struct,numpy as np
class R:
    def __init__(s,d): s.d=d; s.o=0
    def i(s): v=struct.unpack_from('<i',s.d,s.o)[0]; s.o+=4; return v
    def f(s,n=1): v=struct.unpack_from('<%df'%n,s.d,s.o); s.o+=4*n; return v
    def b(s): v=s.d[s.o]; s.o+=1; return v
    def s(s):
        n=0;sh=0
        while True:
            c=s.d[s.o]; s.o+=1; n|=(c&0x7f)<<sh; sh+=7
            if not c&0x80: break
        v=s.d[s.o:s.o+n].decode('latin1'); s.o+=n; return v
def mesh(r):
    t=r.i(); assert t==13,t
    nv=r.i(); sub=r.i(); V=None; T=[]
    while True:
        t=r.i()
        if t==22: break
        if t==14: V=np.array(r.f(3*nv)).reshape(-1,3)
        elif t in(15,16): r.o+=8*nv
        elif t==17: r.o+=12*nv
        elif t==18: r.o+=16*nv
        elif t==19: n=r.i(); T.append(np.array(struct.unpack_from('<%di'%n,r.d,r.o)).reshape(-1,3)); r.o+=4*n
        elif t==20: r.o+=nv*32
        elif t==21: r.o+=nv*16   # colors
        else: raise Exception('mesh entry %d'%t)
    return V,(np.vstack(T) if T else None)
class Node:
    def __init__(s,name,tr,parent): s.name=name; s.pos=np.array(tr[0:3]); s.rot=np.array(tr[3:7]); s.scl=np.array(tr[7:10]); s.parent=parent; s.kids=[]; s.meshes=[]; s.cols=[]
def qrot(q,v):
    x,y,z,w=q; u=np.array([x,y,z]); return v+2*np.cross(u,np.cross(u,v)+w*v)
def world(n,p):
    while n is not None:
        p=qrot(n.rot,p*n.scl)+n.pos; n=n.parent
    return p
def wdir(n,v):
    while n is not None:
        v=qrot(n.rot,v); n=n.parent
    return v
def obj(r,parent,ver):
    name=r.s(); tr=r.f(10); n=Node(name,tr,parent)
    if parent: parent.kids.append(n)
    while r.o<len(r.d):
        t=r.i()
        if t==0: obj(r,n,ver)
        elif t==1: return n
        elif t==24: r.s(); r.i()
        elif t==3: r.b(); n.cols.append(mesh(r))
        elif t==25: r.b(); r.b(); n.cols.append(mesh(r))
        elif t==6: r.f(6)
        elif t==28: r.b(); r.f(6)
        elif t==4: r.f(4)
        elif t==26: r.b(); r.f(4)
        elif t==5: r.f(1);r.f(1);r.i();r.f(3)
        elif t==27: r.b(); r.f(1);r.f(1);r.i();r.f(3)
        elif t==7: n.meshes.append(mesh(r))
        elif t==8:
            if ver>=1: r.b(); r.b()
            k=r.i(); r.o+=4*k
        elif t==2:
            nc=r.i()
            for _ in range(nc):
                r.s(); r.f(6); r.i(); cc=r.i()
                for _ in range(cc):
                    r.s(); r.s(); r.i(); r.i(); r.i(); kc=r.i(); r.o+=kc*(16+ (4 if ver>=0 else 0))
            r.s(); r.b()
        elif t==10 and ver<4:
            k=r.i()
            TX=lambda: (r.i(), r.f(4))
            for _ in range(k):
                r.s(); st=r.i()
                if st==1: TX()
                elif st==2: TX(); r.f(4); r.f(1)
                elif st==3: TX(); TX()
                elif st==4: TX(); TX(); r.f(4); r.f(1)
                elif st==5: TX(); TX(); r.f(4)
                elif st==6: TX(); r.f(4); r.f(1); TX(); r.f(4)
                elif st==7: TX(); TX(); r.f(4); r.f(1); TX(); r.f(4)
                elif st==8: TX(); r.f(1)
                elif st==9: TX(); TX(); r.f(1)
                elif st==10: TX()
                elif st==11: TX(); r.f(1); r.f(4); r.f(1)
                elif st in (12,13): TX(); r.f(4)
                elif st in (14,15): TX(); r.f(4); r.f(1)
                else: raise Exception('shader type %d'%st)
        elif t==10:
            k=r.i()
            for _ in range(k):
                r.s(); r.s(); np_=r.i()
                for _ in range(np_):
                    r.s(); pt=r.i()
                    if pt in(0,1): r.f(4)
                    elif pt in(2,3): r.f(1)
                    elif pt==4: r.i(); r.f(4)
                    else: raise Exception('prop %d'%pt)
        elif t==12:
            k=r.i()
            for _ in range(k): r.s(); r.i()
        elif t==23:
            r.i(); r.f(1); r.f(1); r.f(4); r.i()
            if ver>1: r.f(1)
        elif t==9:
            k=r.i(); r.o+=4*k; r.f(6); r.i(); r.b(); nb=r.i()
            for _ in range(nb): r.s()
            n.meshes.append(mesh(r))
        else: raise Exception('entry %d at %d in %s'%(t,r.o,name))
    return n
def load(path):
    r=R(open(path,'rb').read()); r.i(); ver=r.i(); r.s()
    return obj(r,None,ver)
def walk(n):
    yield n
    for k in n.kids: yield from walk(k)
def find(root,name): return [n for n in walk(root) if n.name==name]
