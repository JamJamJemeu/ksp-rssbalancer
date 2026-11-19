
namespace RSSBalancer
{
    /// <summary>Stan interfejsu KSP: F2 chowa interfejs, a razem z nim okna moda.</summary>
    public static class Hud
    {
        public static bool Hidden { get; private set; }
        private static bool hooked;

        public static void Hook()
        {
            if (hooked) return;
            hooked = true;
            // KSP's EventVoid nie przyjmuje metod statycznych (NullReferenceException w EvtDelegate) – obiekt pośredni
            var l = new Listener();
            GameEvents.onHideUI.Add(l.OnHide);
            GameEvents.onShowUI.Add(l.OnShow);
            GameEvents.onGameSceneLoadRequested.Add(l.OnScene);
        }

        private class Listener
        {
            public void OnHide() { Hidden = true; }
            public void OnShow() { Hidden = false; }
            public void OnScene(GameScenes s) { Hidden = false; }
        }
    }
}
