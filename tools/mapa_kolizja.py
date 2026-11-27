# RSSBalancer – dodaje kolizję (MeshCollider z tej samej siatki) do mapy-tekstury Bajkonuru Katnisss (BaikonurMap_RSSB.mu).
# KerbalKonstructs ustawia wszystkim nie-triggerom warstwę 15 (Local Scenery), więc koła i rakiety stoją na mapie.
import sys,struct; sys.path.insert(0,'/tmp/mutool'); import mu
src,dst=sys.argv[1],sys.argv[2]
d=open(src,'rb').read()
i=d.find(struct.pack('<ii',7,13)); assert i>0 and d.find(struct.pack('<ii',7,13),i+1)<0
r=mu.R(d); r.o=i+4; mu.mesh(r,[],''); meshb=d[i+4:r.o]
open(dst,'wb').write(d[:i]+struct.pack('<i',3)+b'\x00'+meshb+d[i:])
