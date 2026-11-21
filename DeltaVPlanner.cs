using System;
using System.Collections.Generic;

namespace RSSBalancer
{
    /// <summary>
    /// Szacunek Δv potrzebnej na misję, liczony z parametrów planet załadowanych w grze
    /// (działa z Sol / RSS / stock – bierze promień, masę, atmosferę i orbity z Kopernicusa).
    /// Transfery: orbity Hohmanna, bez zmiany nachylenia. Dokładność ok. ±10%.
    /// </summary>
    public class DvPlan
    {
        public CelestialBody launch;
        public CelestialBody target;      // null = tylko orbita
        public double orbitAlt;           // m, wysokość orbity parkingowej
        public double targetLowAlt;       // m, niska orbita celu

        public double toOrbit;            // start z powierzchni na orbitę parkingową
        public double transfer;           // odlot do celu (TLI / ucieczka)
        public double capture;            // wejście na niską orbitę celu
        public double landing;            // lądowanie z niskiej orbity celu
        public double ascent;             // start z powierzchni celu na jego niską orbitę
        public bool targetAtmosphere;
        public bool launchAtmosphere;

        public double ToTargetOrbit { get { return toOrbit + transfer + capture; } }
        public double ToLanding { get { return ToTargetOrbit + landing; } }
        /// <summary>Lądowanie i powrót: start z celu, ucieczka (≈ capture), na końcu aerohamowanie w atmosferze startowej planety.</summary>
        public double RoundTrip
        {
            get
            {
                double back = ascent + capture;
                if (!launchAtmosphere) back += transfer; // bez atmosfery trzeba wyhamować silnikiem
                return ToLanding + back;
            }
        }
    }

    public static class DeltaVPlanner
    {
        public static string Name(CelestialBody b)
        {
            if (b == null) return "";
            string n = b.displayName;
            if (string.IsNullOrEmpty(n)) n = b.bodyName;
            int caret = n.IndexOf('^');
            return caret > 0 ? n.Substring(0, caret) : n;
        }

        public static double LowOrbitAlt(CelestialBody b)
        {
            if (b.atmosphere) return b.atmosphereDepth + 10000.0;
            return Math.Max(15000.0, 0.05 * b.Radius);
        }

        /// <summary>Cele osiągalne z danej planety: jej księżyce i planety krążące wokół tej samej gwiazdy.</summary>
        public static List<CelestialBody> Targets(CelestialBody launch)
        {
            List<CelestialBody> list = new List<CelestialBody>();
            if (launch == null) return list;

            if (launch.orbitingBodies != null)
                foreach (CelestialBody b in launch.orbitingBodies)
                    if (b != null && b != launch) list.Add(b);

            CelestialBody parent = launch.referenceBody;
            if (parent != null && parent != launch && parent.orbitingBodies != null)
                foreach (CelestialBody b in parent.orbitingBodies)
                    if (b != null && b != launch && b.orbit != null) list.Add(b);

            return list;
        }

        private static double Circ(double mu, double r) { return Math.Sqrt(mu / r); }

        /// <summary>Prędkość w punkcie r na elipsie o półosi a (równanie vis-viva).</summary>
        private static double VisViva(double mu, double r, double a) { return Math.Sqrt(mu * (2.0 / r - 1.0 / a)); }

        /// <summary>Δv z orbity kołowej r przy nadmiarze prędkości vInf (odlot albo przechwycenie).</summary>
        private static double Hyperbolic(double mu, double r, double vInf)
        {
            return Math.Sqrt(vInf * vInf + 2.0 * mu / r) - Circ(mu, r);
        }

        /// <summary>
        /// Start z powierzchni na orbitę kołową: prędkość orbitalna − pomoc z obrotu planety + straty
        /// (grawitacja, opór, sterowanie). Straty to dopasowanie do znanych wartości:
        /// Kerbin ≈ 3,4 km/s, Ziemia ≈ 9,4 km/s, Księżyc ≈ 1,9 km/s.
        /// </summary>
        public static double ToOrbit(CelestialBody b, double alt, double latDeg)
        {
            double r = b.Radius + alt;
            double v = Circ(b.gravParameter, r);

            double rot = 0.0;
            if (b.rotationPeriod > 0.0)
                rot = 2.0 * Math.PI * b.Radius / b.rotationPeriod * Math.Cos(latDeg * Math.PI / 180.0);

            double loss = b.atmosphere ? 1000.0 + 0.13 * v : 0.08 * v;
            return Math.Max(0.0, v - rot + loss);
        }

        /// <summary>Start na orbitę eliptyczną perygeum × apogeum: start na kołową na wysokości perygeum + dopalenie w perygeum do apogeum.</summary>
        public static double ToOrbit(CelestialBody b, double peAlt, double apAlt, double latDeg)
        {
            if (apAlt <= peAlt) return ToOrbit(b, peAlt, latDeg);
            double rp = b.Radius + peAlt, ra = b.Radius + apAlt, mu = b.gravParameter;
            double vEll = Math.Sqrt(mu * (2.0 / rp - 2.0 / (rp + ra)));
            return ToOrbit(b, peAlt, latDeg) + (vEll - Circ(mu, rp));
        }

        private static double SurfaceToLowOrbitVacuum(CelestialBody b, double lowAlt)
        {
            return Circ(b.gravParameter, b.Radius + lowAlt) * 1.15; // + ok. 15% na straty i manewry
        }

        public static DvPlan Plan(CelestialBody launch, CelestialBody target, double orbitAlt, double latDeg)
        {
            DvPlan p = new DvPlan
            {
                launch = launch,
                target = target,
                orbitAlt = orbitAlt,
                launchAtmosphere = launch.atmosphere
            };
            p.toOrbit = ToOrbit(launch, orbitAlt, latDeg);
            if (target == null) return p;

            double mu = launch.gravParameter;
            double r1 = launch.Radius + orbitAlt;

            double muT = target.gravParameter;
            p.targetLowAlt = LowOrbitAlt(target);
            double rLow = target.Radius + p.targetLowAlt;
            p.targetAtmosphere = target.atmosphere;

            if (target.referenceBody == launch)
            {
                // Księżyc: transfer Hohmanna w polu grawitacyjnym planety startowej.
                double r2 = target.orbit.semiMajorAxis;
                double a = (r1 + r2) / 2.0;
                p.transfer = VisViva(mu, r1, a) - Circ(mu, r1);
                double vInf = Math.Abs(Circ(mu, r2) - VisViva(mu, r2, a));
                p.capture = Hyperbolic(muT, rLow, vInf);
            }
            else
            {
                // Planeta: transfer Hohmanna wokół gwiazdy + ucieczka z orbity parkingowej.
                CelestialBody star = launch.referenceBody;
                double muS = star.gravParameter;
                double a1 = launch.orbit.semiMajorAxis;
                double a2 = target.orbit.semiMajorAxis;
                double a = (a1 + a2) / 2.0;

                double vInfDep = Math.Abs(VisViva(muS, a1, a) - Circ(muS, a1));
                double vInfArr = Math.Abs(Circ(muS, a2) - VisViva(muS, a2, a));

                p.transfer = Hyperbolic(mu, r1, vInfDep);
                p.capture = Hyperbolic(muT, rLow, vInfArr);
            }

            if (target.atmosphere)
            {
                p.landing = 0.0; // spadochrony / aerohamowanie
                p.ascent = ToOrbit(target, p.targetLowAlt, 0.0);
            }
            else
            {
                p.landing = SurfaceToLowOrbitVacuum(target, p.targetLowAlt);
                p.ascent = p.landing;
            }

            return p;
        }
    }
}
