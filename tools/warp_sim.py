# symulacja RdvWait/RdvWarpTick 1.47.3: stopnie KSP na szynach, zmiana stopnia płynna (indeks ~2/s w czasie rzeczywistym)
rates=[1,5,10,50,100,1000,10000,100000]
def run(wait, K=15, lead=30, user=None, dt=0.02):
    ut=0; start=wait; stopAt=start-lead
    idx=0; cur=0.0; widx=-1; wuser=False; log=[]; tmax=0
    rt=0
    while True:
        # --- RdvWait ---
        if ut<stopAt:
            tin=stopAt-ut
            if widx>=0 and idx!=widx and not wuser: wuser=True; log.append(('user',rt,ut))
            allowed=len(rates)-1
            while allowed>0 and rates[allowed]*K>tin: allowed-=1
            if idx>allowed:
                inst = rates[idx]*dt*50>tin
                idx=allowed; 
                if inst: cur=idx
                widx=idx
            else:
                if not wuser and idx<allowed and abs(cur-idx)<1e-9: idx+=1
                widx=idx
        else:
            if idx>0:
                if rates[idx]>100: idx-=1
                else: idx=0; cur=0
        if ut>=start: return dict(stop_rate_at_stopAt=stopinfo, real_s=rt, log=log)
        if user and abs(rt-user[0])<dt/2: idx=user[1]   # gracz naciska
        # płynna zmiana
        if cur<idx: cur=min(idx,cur+2*dt)
        elif cur>idx: cur=max(idx,cur-2*dt)
        # bieżące tempo: interpolacja log
        import math
        i0=int(math.floor(cur)); f=cur-i0
        r=rates[i0] if f==0 else math.exp(math.log(rates[i0])*(1-f)+math.log(rates[min(i0+1,7)])*f)
        prev=ut; ut+=r*dt; rt+=dt
        if prev<stopAt<=ut or (prev<stopAt and ut>=stopAt): stopinfo=(round(r,1), round(ut-stopAt,2))
for w in [2667, 18*3600+11*60, 300, 100, 45]:
    print(w, run(w))
print('gracz zwalnia do x10 po 10 s:', run(18*3600, user=(10,2)))
print('gracz przyspiesza x100000 przy 1h:', run(3600, user=(3,7)))
