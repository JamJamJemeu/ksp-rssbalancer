import sys; sys.path.insert(0,'.')
from mesh import *
import texture2ddecoder as t2d, etcpak, pickle
from scipy.ndimage import maximum_filter, grey_closing, binary_dilation
W=1024; DU,DV=0.5,0.75; GAP=0.3
base="/mnt/user-data/uploads/Kerbal Space Program/GameData/Sol-Textures/PluginData/_Launchsites/kz_baikonur/"
tiles=[('n45e062',46.597633361816406,72.80838012695312,45,62),('n45e063',69.93588256835938,83.24458312988281,45,63),('n46e062',44.12699890136719,90.59005737304688,46,62),('n46e063',74.51050567626953,179.03246307373047,46,63)]
EN,alt=setup()
def cub(p0,p1,p2,p3,t): return p1+0.5*t*(p2-p0+t*(2*p0-5*p1+4*p2-p3+t*(3*(p1-p2)+p3-p0)))
def samp(img,v,u):
    iu=np.floor(u).astype(int); iv=np.floor(v).astype(int); fu=u-iu; fv=v-iv
    g=lambda a,b: img[np.clip(b,0,W-1),np.clip(a,0,W-1)]
    bi=(g(iu,iv)*(1-fu)+g(iu+1,iv)*fu)*(1-fv)+(g(iu,iv+1)*(1-fu)+g(iu+1,iv+1)*fu)*fv
    rows=[cub(g(iu-1,iv+j),g(iu,iv+j),g(iu+1,iv+j),g(iu+2,iv+j),fu) for j in (-1,0,1,2)]
    return np.maximum(bi, cub(*rows,fv))
res={}
def _main():
  for (k,off,df,la,lo) in tiles:
    d=open(base+k+'_Height.dds','rb').read()
    hdr=d[:148]; oblocks=np.frombuffer(d[148:],np.uint8).reshape(256,256,16)
    img0=np.frombuffer(t2d.decode_bc7(d[148:],W,W),np.uint8).reshape(W,W,4)[...,0].astype(float)
    dem=off+df*img0/255
    I,J=np.mgrid[0:W,0:W]
    lat=la+1.000139-(I-DV)/(W-1); lon=lo-0.000139+(J-DU)/(W-1)
    box=(lat>45.87)&(lat<46.12)&(lon>62.88)&(lon<63.53)
    rows=np.where(box.any(1))[0]; cols=np.where(box.any(0))[0]
    r0,r1=max(rows.min()-6,0),min(rows.max()+7,W); c0,c1=max(cols.min()-6,0),min(cols.max()+7,W)
    S=4
    fv,fu=np.mgrid[r0:r1-1+1e-9:1.0/S, c0:c1-1+1e-9:1.0/S]
    flat=(la+1.000139)-(fv-DV)/(W-1); flon=(lo-0.000139)+(fu-DU)/(W-1)
    e,n=ll2en(flat.ravel(),flon.ravel()); tex=query(EN,alt,e,n).reshape(fv.shape)
    t0=np.where(np.isnan(tex),-1e3,tex)
    filled=grey_closing(t0,size=(10*S+1,10*S+1))
    pit=binary_dilation((filled-t0>5)&(t0>-500),iterations=S)
    teff=t0.copy()  # wykopy otwarte
    # Site 1/5 (start Gagarina): wykop modelu RSSBalancer – teren nisko pod rowem (+100 m zapasu, siatka terenu gry jest rzadka)
    import sys; sys.path.insert(0,'/tmp/gag'); import geom as G
    dE=np.stack(ll2en(flat.ravel(),flon.ravel()),-1)-G.C
    gx=(dE@G.ex).reshape(fv.shape); gz=(dE@G.ez).reshape(fv.shape)
    core=(np.abs(gx)<G.DX+60)&(gz>G.DZ0-60)&(gz<G.DZ1+60)
    from scipy.ndimage import distance_transform_edt
    if core.any():
        mpp=np.radians(1/(W-1)/S)*6371000*np.array([1.0,np.cos(np.radians(46))])   # metry na próbkę (wiersz, kolumna)
        dist=distance_transform_edt(~core,sampling=mpp)
        ramp=142.81-G.DEPTH-2.0+dist*(75.0/150.0)
        teff=np.where(~np.isnan(tex), np.minimum(teff,ramp), teff)
        pit=pit|(ramp<t0-0.5)
    teff=np.where(np.isnan(tex),np.nan,teff)
    demf=samp(dem,fv,fu)
    # pixel target
    pv=teff[::S,::S]; pdem=dem[r0:r0+pv.shape[0], c0:c0+pv.shape[1]]
    ppit=pit[::S,::S]
    tgt=dem.copy(); sub=np.where(np.isnan(pv),pdem,np.where(ppit,pv-GAP,np.maximum(pdem,pv-GAP))); tgt[r0:r0+pv.shape[0],c0:c0+pv.shape[1]]=sub
    noff=min(off,float(np.nanmin(tgt))-1.0)
    ndf=max(df+off-noff,float(np.nanmax(tgt))-noff+1.0); rq=(ndf!=df) or (noff!=off); ooff=off; off=noff
    q=np.where(np.abs(tgt-dem)>0.01, np.floor((tgt-off)/ndf*255), np.round((dem-off)/ndf*255)).clip(0,255)
    for it in range(45):
        if rq: bmask=np.ones((256,256),bool)
        else:
            ch=np.abs(q-img0)>0; bmask=np.zeros((256,256),bool); a,b=np.where(ch); bmask[a//4,b//4]=True
        rgba=np.zeros((W,W,4),np.uint8); g=q.astype(np.uint8); rgba[...,0]=g; rgba[...,1]=g; rgba[...,2]=g; rgba[...,3]=255
        enc=np.frombuffer(etcpak.compress_bc7(rgba.tobytes(),W,W),np.uint8).reshape(256,256,16)
        nb=oblocks.copy(); nb[bmask]=enc[bmask]
        dec=np.frombuffer(t2d.decode_bc7(nb.tobytes(),W,W),np.uint8).reshape(W,W,4)[...,0].astype(float)
        h=off+ndf*dec/255
        hf=samp(h,fv,fu)
        act=(~np.isnan(teff))&((hf>demf+0.05)|pit)
        viol=np.where(act, hf-(teff-0.1), -1.0)
        nv=int((viol>0).sum()); mx=float(viol.max())
        if nv==0 or it==44: break
        vp=np.full((W,W),-1.0)
        iv=np.round(fv).astype(int); iu=np.round(fu).astype(int)
        np.maximum.at(vp,(iv.ravel(),iu.ravel()),viol.ravel())
        vp=maximum_filter(vp,size=5)
        q=np.clip(q-(np.ceil(np.clip(vp,0,None)/ndf*255)+(vp>0)),0,255)
    plat=(~np.isnan(teff))&(teff-demf>3)
    gap=(teff-hf)[plat]
    print(k,'df',round(df,2),'->',round(ndf,2),'iters',it,'viol',nv,round(mx,2),'gap pct 1/5/50/95/99',np.round(np.percentile(gap,[1,5,50,95,99]),2),'pits px',int(pit[::S,::S].sum()))
    # outside-box DEM change
    dd=(h-dem)[~box]
    print('   offset',ooff,'->',off); print('   DEM change outside box: max abs',round(float(np.abs(dd).max()),2),'rms',round(float(np.sqrt((dd**2).mean())),2))
    res[k]=dict(blocks=nb,h=h,hdr=hdr,off=off,ooff=ooff,df=ndf,odf=df,la=la,lo=lo)
if __name__=="__main__":
    _main(); pickle.dump(res,open("final6.pkl","wb"))
