import sys; sys.path.insert(0,'/tmp/gag'); sys.path.insert(0,'/tmp/mutool')
from geom import *
import struct, shapely
from shapely.geometry import Polygon
import mapbox_earcut as earcut
src='/mnt/user-data/uploads/Kerbal Space Program/GameData/KatnisssBaikonurCosmodrome/Assets/BaikonurMap.mu'
d=open(src,'rb').read()
nv=445; o=146+16
Vm=np.array(struct.unpack_from('<%df'%(3*nv),d,o)).reshape(-1,3); o+=12*nv
attrs={}
while True:
    t=struct.unpack_from('<i',d,o)[0]; o+=4
    if t==15: attrs['uv']=np.array(struct.unpack_from('<%df'%(2*nv),d,o)).reshape(-1,2); o+=8*nv
    elif t==17: attrs['n']=np.array(struct.unpack_from('<%df'%(3*nv),d,o)).reshape(-1,3); o+=12*nv
    elif t==18: attrs['t']=np.array(struct.unpack_from('<%df'%(4*nv),d,o)).reshape(-1,4); o+=16*nv
    elif t==19: n=struct.unpack_from('<i',d,o)[0]; Tm=np.array(struct.unpack_from('<%di'%n,d,o+4)).reshape(-1,3); o+=4+4*n
    elif t==22: mesh_end=o; break
# hole polygon in mesh local xz
Q=P-1.0
cx=np.array([-Q,Q,Q,-Q]); cz=np.array([-Q,-Q,Q,Q])
E_,N_=m2en(cx,cz); xg,zg=en2group(E_,N_); hx,hz=group2mesh(xg,zg)
hole=Polygon(list(zip(hx,hz)))
print('hole area m2 (mesh units*0.49)',hole.area*0.49)
newV=[];newUV=[];newN=[];newT=[];tris=[]
def addv(p,bary,t):
    w=np.array(bary)
    newV.append(w@Vm[t]); newUV.append(w@attrs['uv'][t]); n=w@attrs['n'][t]; newN.append(n/np.linalg.norm(n)); newT.append(w@attrs['t'][t])
    return len(newV)-1
cut=0
for t in Tm:
    a,b,c=Vm[t][:,[0,2]]
    tri=Polygon([a,b,c])
    if tri.area<1e-9 or not tri.intersects(hole) or tri.touches(hole):
        base=[addv(None,np.eye(3)[k],t) for k in range(3)]; tris.append(base); continue
    cut+=1
    diff=tri.difference(hole)
    geoms=[diff] if diff.geom_type=='Polygon' else list(diff.geoms)
    det=(b[1]-c[1])*(a[0]-c[0])+(c[0]-b[0])*(a[1]-c[1])
    def bary(p):
        l1=((b[1]-c[1])*(p[0]-c[0])+(c[0]-b[0])*(p[1]-c[1]))/det; l2=((c[1]-a[1])*(p[0]-c[0])+(a[0]-c[0])*(p[1]-c[1]))/det
        return (l1,l2,1-l1-l2)
    for g in geoms:
        if g.is_empty or g.area<1e-6: continue
        rings=[np.array(g.exterior.coords)[:-1]]+[np.array(r.coords)[:-1] for r in g.interiors]
        verts=np.vstack(rings); ends=np.cumsum([len(r) for r in rings]).astype(np.uint32)
        idx=earcut.triangulate_float64(verts,ends).reshape(-1,3)
        ids=[addv(None,bary(p),t) for p in verts]
        for i3 in idx:
            # keep original winding orientation (same as source triangle in xz)
            p=verts[i3]; s=(p[1,0]-p[0,0])*(p[2,1]-p[0,1])-(p[1,1]-p[0,1])*(p[2,0]-p[0,0])
            s0=(b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0])
            tri3=[ids[i] for i in i3]
            if np.sign(s)!=np.sign(s0): tri3=[tri3[0],tri3[2],tri3[1]]
            tris.append(tri3)
print('triangles cut',cut,'new verts',len(newV),'tris',len(tris))
newV=np.array(newV);newUV=np.array(newUV);newN=np.array(newN);newT=np.array(newT);tris=np.array(tris)
# rebuild mesh block
nb=struct.pack('<iii',13,len(newV),1)
nb+=struct.pack('<i',14)+newV.astype('<f4').tobytes()
nb+=struct.pack('<i',15)+newUV.astype('<f4').tobytes()
nb+=struct.pack('<i',17)+newN.astype('<f4').tobytes()
nb+=struct.pack('<i',18)+newT.astype('<f4').tobytes()
nb+=struct.pack('<ii',19,tris.size)+tris.astype('<i4').tobytes()+struct.pack('<i',22)
out=d[:146]+nb+d[mesh_end:]
open('/tmp/gag/BaikonurMap_RSSB.mu','wb').write(out); print('written',len(out))
