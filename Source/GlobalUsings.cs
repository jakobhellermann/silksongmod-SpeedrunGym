global using static SpeedrunGym.Source.GlobalVariables;
using SpeedrunGym.Source.WorldToasts;
using UnityEngine;

namespace SpeedrunGym.Source;

public static class GlobalVariables {
    internal static void HeroToast(string message, Color color, float? heightOffset = null) {
        WorldToastManager.ShowHero(message, color, heightOffset);
    }
}
