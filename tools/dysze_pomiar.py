import mut,numpy as np,sys
B='/mnt/user-data/uploads/Kerbal Space Program/GameData/'
def allverts(root):
    P=[]
    for n in mut.walk(root):
        for V,T in n.meshes:
            if V is None: continue
            W=np.array([mut.world(n,v) for v in V]); P.append(W)
    return np.vstack(P)
def frame(n):
    o=mut.world(n,np.zeros(3)); z=mut.wdir(n,np.array([0,0,1.]));x=mut.wdir(n,np.array([1,0,0.])); y=np.cross(z,x)
    return o,x,y,z
def measure(p,name,rlim=3.0):
    root=mut.load(B+p); W=allverts(root); res=[]
    for n in mut.find(root,name):
        o,x,y,z=frame(n); L=W-o; lz=L@z; lr=np.hypot(L@x,L@y)
        m=(lr<rlim)&(lz>-rlim)&(lz<rlim*3)
        if m.sum()==0: res.append((o,z,None)); continue
        zmax=lz[m].max(); ring=m&(lz>zmax-0.02*max(1,abs(zmax)))
        res.append((np.round(o,3),np.round(z,3),round(zmax,3),round(lr[ring].max(),3), round(lz[m].min(),3)))
    return res
if __name__=='__main__':
    for r in measure(sys.argv[1],sys.argv[2],float(sys.argv[3]) if len(sys.argv)>3 else 3.0): print(r)
