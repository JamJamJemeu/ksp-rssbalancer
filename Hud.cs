
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
            GameEvents.onHideUI.Add(OnHide);
            GameEvents.onShowUI.Add(OnShow);
            GameEvents.onGameSceneLoadRequested.Add(OnScene);
        }

        private static void OnHide() { Hidden = true; }
        private static void OnShow() { Hidden = false; }
        private static void OnScene(GameScenes s) { Hidden = false; }
    }
}
