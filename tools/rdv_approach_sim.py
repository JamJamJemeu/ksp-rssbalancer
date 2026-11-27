# Symulacja klocka „approach” (zbliżanie jak „Igła”): 2 ciała, KTDU 10 m/s², obrót przed impulsem
import math, sys
MU=3.986004418e14; R=6371000.0
def acc(r): rr=math.hypot(*r); k=-MU/rr**3; return (k*r[0],k*r[1])
def step(r,v,dt,a=(0,0)):
    def f(r,v): g=acc(r); return v,(g[0]+a[0],g[1]+a[1])
    k1=f(r,v); r2=(r[0]+k1[0][0]*dt/2,r[1]+k1[0][1]*dt/2); v2=(v[0]+k1[1][0]*dt/2,v[1]+k1[1][1]*dt/2)
    k2=f(r2,v2); r3=(r[0]+k2[0][0]*dt/2,r[1]+k2[0][1]*dt/2); v3=(v[0]+k2[1][0]*dt/2,v[1]+k2[1][1]*dt/2)
    k3=f(r3,v3); r4=(r[0]+k3[0][0]*dt,r[1]+k3[0][1]*dt); v4=(v[0]+k3[1][0]*dt,v[1]+k3[1][1]*dt); k4=f(r4,v4)
    return ((r[0]+dt/6*(k1[0][0]+2*k2[0][0]+2*k3[0][0]+k4[0][0]), r[1]+dt/6*(k1[0][1]+2*k2[0][1]+2*k3[0][1]+k4[0][1])),
            (v[0]+dt/6*(k1[1][0]+2*k2[1][0]+2*k3[1][0]+k4[1][0]), v[1]+dt/6*(k1[1][1]+2*k2[1][1]+2*k3[1][1]+k4[1][1])))
def run(d0=9230, behind=True, ACC=10.0, rateK=0.002, vmax=10, vmin=0.3, stop=200, tolFar=0.5, tolNear=0.1, turnRate=5.0, verbose=False):
    a=R+257000; vc=math.sqrt(MU/a)
    rt=(a,0.0); vt=(0.0,vc)
    th=-d0/a if behind else d0/a
    rm=(a*math.cos(th),a*math.sin(th)); vm=(-vc*math.sin(th),vc*math.cos(th))
    # mała niedopasowana prędkość względna 0.3 m/s
    vm=(vm[0]+0.2,vm[1]+0.2)
    t=0; dvused=0; burning=False; att=None; aligned_at=None; nburn=0; log=[]
    h=0.1
    while t<4*3600:
        rel=(rt[0]-rm[0],rt[1]-rm[1]); d=math.hypot(*rel); u=(vm[0]-vt[0],vm[1]-vt[1])
        los=(rel[0]/d,rel[1]/d)
        vdes=0 if d<=stop else max(vmin,min(vmax,d*rateK))
        udes=(los[0]*vdes,los[1]*vdes)
        e=(udes[0]-u[0],udes[1]-u[1]); em=math.hypot(*e)
        tol=tolNear if d<1000 else tolFar
        if d<=stop and math.hypot(*u)<0.15 and not burning:
            return dict(t=t,d=d,u=math.hypot(*u),dv=dvused,n=nburn)
        thrust=(0,0)
        if not burning and em>tol: burning=True; nburn+=1; aligned_at=None
        if burning:
            if em<max(0.03,0.5*tol): burning=False
            else:
                want=math.atan2(e[1],e[0])
                if att is None: att=want
                da=(want-att+math.pi)%(2*math.pi)-math.pi
                mx=math.radians(turnRate)*h
                att+=max(-mx,min(mx,da))
                if abs(da)<math.radians(3):
                    thr=max(0.02,min(1,em/(ACC*1.5)))
                    thrust=(ACC*thr*math.cos(att),ACC*thr*math.sin(att)); dvused+=ACC*thr*h
        if not burning and att is not None:
            want=math.atan2(los[1],los[0]); da=(want-att+math.pi)%(2*math.pi)-math.pi; mx=math.radians(turnRate)*1.0
            att+=max(-mx,min(mx,da))
        rm,vm=step(rm,vm,h,thrust); rt,vt=step(rt,vt,h); t+=h
        if not burning: h=1.0
        else: h=0.1
    return dict(t=t,d=d,u=math.hypot(*u),dv=dvused,n=nburn,fail=True)
if __name__=='__main__':
    for d0 in (9230,12000,3000):
        for beh in (True,False):
            r=run(d0,beh); print(d0,'za' if beh else 'przed',{k:(round(v,2) if isinstance(v,float) else v) for k,v in r.items()})
