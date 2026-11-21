import sys; sys.path.insert(0,'/tmp/mu')
from mesh import *
C=np.array([10501.75,-8041.62]); PSI=85.0; P=350.0
DX=20.0; DZ0=-12.0; DZ1=120.0; DEPTH=40.0; HOLE=3.5
ps=np.radians(PSI); ez=np.array([np.sin(ps),np.cos(ps)]); ex=np.array([np.cos(ps),-np.sin(ps)])
def m2en(x,z): return C[0]+x*ex[0]+z*ez[0], C[1]+x*ex[1]+z*ez[1]
def en2group(E,N):
    M=A[:2]; q=np.stack([E,N],-1)-A[2]
    g=q@np.linalg.inv(M); return g[...,0]+L0[0], g[...,1]+L0[2]
R=eul(0.1,206.73,0.03)
def group2mesh(xg,zg):
    d=np.stack([xg-L0[0],np.zeros_like(xg),zg-L0[2]],-1)
    v=(R.T@d.T).T/0.7; return v[:,0],v[:,2]
