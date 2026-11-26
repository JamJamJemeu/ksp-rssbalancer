# Symulacja offline automatu spotkania (Rendezvous.cs) – 2D, dwa ciała, skończone impulsy jak AxisBurn.
import math, sys
MU=3.986004418e14; R=6371000.0
def per(a): return 2*math.pi*math.sqrt(a**3/MU)
def elems(r,v):
    rr=math.hypot(*r); vv2=v[0]**2+v[1]**2
    a=1/(2/rr-vv2/MU); h=r[0]*v[1]-r[1]*v[0]
    e=math.sqrt(max(0,1-h*h/(MU*a)))
    return a,e,a*(1-e)-R,a*(1+e)-R,h
def acc(r):
    rr=math.hypot(*r); k=-MU/rr**3; return (k*r[0],k*r[1])
def step(r,v,dt,a_th=(0,0)):
    def f(r,v):
        g=acc(r); return v,(g[0]+a_th[0],g[1]+a_th[1])
    k1=f(r,v); r2=(r[0]+k1[0][0]*dt/2,r[1]+k1[0][1]*dt/2); v2=(v[0]+k1[1][0]*dt/2,v[1]+k1[1][1]*dt/2)
    k2=f(r2,v2); r3=(r[0]+k2[0][0]*dt/2,r[1]+k2[0][1]*dt/2); v3=(v[0]+k2[1][0]*dt/2,v[1]+k2[1][1]*dt/2)
    k3=f(r3,v3); r4=(r[0]+k3[0][0]*dt,r[1]+k3[0][1]*dt); v4=(v[0]+k3[1][0]*dt,v[1]+k3[1][1]*dt)
    k4=f(r4,v4)
    nr=(r[0]+dt/6*(k1[0][0]+2*k2[0][0]+2*k3[0][0]+k4[0][0]), r[1]+dt/6*(k1[0][1]+2*k2[0][1]+2*k3[0][1]+k4[0][1]))
    nv=(v[0]+dt/6*(k1[1][0]+2*k2[1][0]+2*k3[1][0]+k4[1][0]), v[1]+dt/6*(k1[1][1]+2*k2[1][1]+2*k3[1][1]+k4[1][1]))
    return nr,nv
def state_from(pe,ap,nu,rot):
    rp=R+pe*1000; ra=R+ap*1000; a=(rp+ra)/2; e=(ra-rp)/(ra+rp); p=a*(1-e*e)
    rr=p/(1+e*math.cos(nu)); vr=math.sqrt(MU/p)*e*math.sin(nu); vt=math.sqrt(MU/p)*(1+e*math.cos(nu))
    th=nu+rot
    r=(rr*math.cos(th),rr*math.sin(th)); v=(vr*math.cos(th)-vt*math.sin(th), vr*math.sin(th)+vt*math.cos(th))
    return r,v
def ang(r): return math.atan2(r[1],r[0])
def phase(rm,rt):  # cel przed nami (+)
    d=ang(rt)-ang(rm); return (d+math.pi)%(2*math.pi)-math.pi
def kepler_prop(r,v,t):  # propagacja numeryczna (do przewidywań), dt 5 s
    n=int(abs(t)//5); dt=5.0 if t>=0 else -5.0
    for _ in range(n): r,v=step(r,v,dt)
    rem=t-n*dt
    if abs(rem)>1e-9: r,v=step(r,v,rem)
    return r,v
def hoh(r1,r2):
    a=(r1+r2)/2; return math.sqrt(MU*(2/r1-1/a))-math.sqrt(MU/r1), math.sqrt(MU/r2)-math.sqrt(MU*(2/r2-1/a)), per(a)/2
def miss(theta1,rB1,rph,rT,dtB3,stop):
    nT=2*math.pi/per(rT); t=0; th=theta1
    a1=(rB1+rph)/2; h=per(a1)/2; t+=h; th+=nT*h-math.pi
    dt=max(0,dtB3-t); nph=2*math.pi/per(rph); th+=(nT-nph)*dt; t+=dt
    d1,d2,th3=hoh(rph,rT); th+=nT*th3-math.pi; t+=th3
    return th-stop/rT, t
def solve(theta1,rB1,rOpp,rT,dtB3,stop,rMin):
    best=None
    for k in (-1,0,1,2):
        th=theta1+2*math.pi*k
        lo,hi=rMin,rT-5000; flo,_=miss(th,rB1,lo,rT,dtB3,stop); fhi,_=miss(th,rB1,hi,rT,dtB3,stop)
        if flo*fhi>0: continue
        for _ in range(80):
            mid=(lo+hi)/2; fm,_=miss(th,rB1,mid,rT,dtB3,stop)
            if flo*fm<=0: hi=mid
            else: lo=mid; flo=fm
        rph=(lo+hi)/2
        a1=(rB1+rph)/2; aI=(rB1+rOpp)/2
        b1=math.sqrt(MU*(2/rB1-1/a1))-math.sqrt(MU*(2/rB1-1/aI)); b2=math.sqrt(MU/rph)-math.sqrt(MU*(2/rph-1/a1)); d3,d4,_=hoh(rph,rT)
        dv=abs(b1)+abs(b2)+abs(d3)+abs(d4)
        if best is None or dv<best[1]: best=(rph,dv)
    return best


def apsis_burn(rm,vm,rOpp,dirs,ACC,K=1.5,tol=0.05):
    r=math.hypot(*rm); v=math.hypot(*vm)
    vNeed=math.sqrt(MU*(2/r-2/(r+rOpp)))
    dvRem=vNeed-v
    if dirs[0]==0: dirs[0]=1 if dvRem>0 else -1
    if dvRem*dirs[0]<=tol: return None,0
    thr=max(0.02,min(1,abs(dvRem)/(ACC*K)))
    s=dirs[0]
    return (s*ACC*thr*vm[0]/v,s*ACC*thr*vm[1]/v), ACC*thr

def run(phase0_deg, pe0=190.15, ap0=270.8, tpe=251, tap=263, ACC=10.4, ASC=498.0, verbose=True):
    # chwila 0 = start rakiety; stan w T+546 s: jesteśmy ~46 s za perygeum (wstawienie przy perygeum w T+500)
    t=546.0; launch=0.0
    rm,vm=state_from(pe0,ap0,0.0,0.0); rm,vm=kepler_prop(rm,vm,46)
    # cel: kołowo-eliptyczny, faza phase0 przed nami
    rt,vt=state_from(tpe,tap,0.3,ang(rm)+math.radians(phase0_deg)-0.3)
    st='B1'; burnUT=None; dirs=[0]; rph=None; altT=None; b3plan=None; dvused=0; log=[]
    dt=2.0; h_last=0.2
    def L(s): log.append("T+%7.0f  %s"%(t,s))
    while t<launch+40*3600:
        a,e,pe,ap,_=elems(rm,vm); T=per(a)
        thrust=(0,0)
        if st=='B1':
            if burnUT is None:
                # perygeum po tMin = launch + 3T
                # czas do perygeum: szukamy numerycznie minimum promienia
                tMin=launch+3*T
                # znajdź najbliższe perygeum >= tMin
                rr,vv=rm,vm; tt=t; best=None; prevr=math.hypot(*rr); dec=False
                while True:
                    rr,vv=step(rr,vv,10); tt+=10; cr=math.hypot(*rr)
                    if cr>prevr and dec and tt-10>=tMin: best=tt-10; break
                    dec = cr<prevr; prevr=cr
                tPe=best
                rmP,vmP=kepler_prop(rm,vm,tPe-t); rtP,vtP=kepler_prop(rt,vt,tPe-t)
                th1=phase(rmP,rtP)
                b3plan=launch+ASC+16*T+ASC
                at,et,_,_,_=elems(rt,vt); rT=at
                res=solve(th1,a*(1-e),a*(1+e),rT,b3plan-tPe,10000,max(R+195000,a*(1-e)+2000))
                if res is None: L("POZA PROFILEM faza %.1f"%math.degrees(th1)); print("\n".join(log)); return None
                rph=res[0]; burnUT=tPe
                L("plan B1 za %.0f s, faza %.1f°, r_ph %.1f km, dv %.1f"%(tPe-t,math.degrees(th1),(rph-R)/1000,res[1]))
            if t>=burnUT-2: st='B1Burn'; L("B1 start %.1f×%.1f"%(pe/1000,ap/1000))
        elif st=='B1Burn':
            th_,ac_=apsis_burn(rm,vm,rph,dirs,ACC)
            if th_ is None: st='B2'; burnUT=None; dirs=[0]; L("B1Burn gotowe %.1f×%.1f"%(pe/1000,ap/1000))
            else: thrust=th_; dvused+=ac_*h_last
        elif st=='B2':
            if burnUT is None:
                rr,vv=rm,vm; tt=t; prevr=math.hypot(*rr); inc=False
                while True:
                    rr,vv=step(rr,vv,10); tt+=10; cr=math.hypot(*rr)
                    if cr<prevr and inc: burnUT=tt-10; break
                    inc = cr>prevr; prevr=cr
            if t>=burnUT-2: st='B2Burn'; L("B2 start")
        elif st=='B2Burn':
            th_,ac_=apsis_burn(rm,vm,rph,dirs,ACC)
            if th_ is None: st='B3'; burnUT=None; dirs=[0]; L("B2Burn gotowe %.1f×%.1f"%(pe/1000,ap/1000))
            else: thrust=th_; dvused+=ac_*h_last
        elif st=='B3':
            if burnUT is None:
                at,et,_,_,_=elems(rt,vt); rT=at; nT=2*math.pi/per(rT)
                def missf(t3):
                    # dokładnie: gdzie będzie cel w chwili przybycia (t3 + pół elipsy) względem punktu przybycia (−r(t3))
                    rmX,_=kepler_prop(rm,vm,t3-t)
                    rtA=math.hypot(*kepler_prop(rt,vt,t3-t+per((math.hypot(*rmX)+rT)/2)/2)[0])
                    th=per((math.hypot(*rmX)+rtA)/2)/2
                    rtX,_=kepler_prop(rt,vt,t3-t+th)
                    arr=(-rmX[0],-rmX[1])
                    return phase(arr,rtX)-10000/rtA
                t0=max(t+300,b3plan-T); prevM=missf(t0); best=None
                tt=t0+30
                while tt<b3plan+2*T:
                    c=missf(tt)
                    if prevM>0 and c<=0 and prevM-c<1: best=tt; break
                    prevM=c; tt+=30
                if best is None: best=max(t+300,b3plan); L("B3 poza oknem")
                burnUT=best
                rmX,_=kepler_prop(rm,vm,best-t); a_h=(math.hypot(*rmX)+rT)/2
                rtA,_=kepler_prop(rt,vt,best-t+per(a_h)/2); altT=math.hypot(*rtA)-R
                L("B3 za %.0f s (plan %.0f), apogeum -> %.1f km"%(best-t,b3plan-t,altT/1000))
            if t>=burnUT-1: st='B3Burn'; L("B3 start")
        elif st=='B3Burn':
            th_,ac_=apsis_burn(rm,vm,R+altT,dirs,ACC)
            if th_ is None: st='B4'; burnUT=None; dirs=[0]; L("B3Burn gotowe %.1f×%.1f"%(pe/1000,ap/1000))
            else: thrust=th_; dvused+=ac_*h_last
        elif st=='B4':
            if burnUT is None:
                rr,vv=rm,vm; tt=t; prevr=math.hypot(*rr); inc=False
                while True:
                    rr,vv=step(rr,vv,10); tt+=10; cr=math.hypot(*rr)
                    if cr<prevr and inc: burnUT=tt-10; break
                    inc = cr>prevr; prevr=cr
            if t>=burnUT-1: st='B4Burn'; lastdv=1e9; L("B4 start")
        elif st=='B4Burn':
            dv=(vt[0]-vm[0],vt[1]-vm[1]); m=math.hypot(*dv)
            if m<0.3 or (m>lastdv+0.05 and lastdv<3):
                d=math.hypot(rt[0]-rm[0],rt[1]-rm[1]); dvr=math.hypot(vt[0]-vm[0],vt[1]-vm[1])
                a2,e2,pe2,ap2,_=elems(rm,vm)
                L("B4 gotowe %.1f×%.1f; odległość %.2f km, faza %.3f°, v_wzgl %.2f m/s, Δv razem %.1f m/s"%(pe2/1000,ap2/1000,d/1000,math.degrees(phase(rm,rt)),dvr,dvused))
                return d, dvr, dvused, log, t
            lastdv=min(lastdv,m)
            thr=max(0.02,min(1,m/(ACC*1.5))); thrust=(ACC*thr*dv[0]/m,ACC*thr*dv[1]/m); dvused+=ACC*thr*h_last
        # krok: przy impulsie 0.2 s, poza – większy
        h=0.2 if st.endswith('Burn') else (2.0 if (burnUT is not None and abs(burnUT-t)<60) else 20.0)
        if burnUT is not None and st in('B1','B2','B3','B4') and t+h>burnUT-2: h=max(0.2,burnUT-2-t) if burnUT-2-t>0.2 else 0.2
        rm,vm=step(rm,vm,h,thrust); rt,vt=step(rt,vt,h); t+=h; h_last=h
    print("\n".join(log)); print("KONIEC CZASU st",st); return None

if __name__=='__main__':
    ph=float(sys.argv[1]) if len(sys.argv)>1 else 14.6
    r=run(ph)
    if r: 
        for l in r[3]: print(l)
