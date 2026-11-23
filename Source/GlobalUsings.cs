global using static SpeedrunGym.Source.GlobalVariables;
using SpeedrunGym.Source.WorldToasts;
using UnityEngine;

namespace SpeedrunGym.Source;

public static class GlobalVariables {
    internal static void HeroToast(string message, Color color, float? heightOffset = null) {
        WorldToastManager.ShowHero(message, color, heightOffset);
    }

    internal static string FormatDecimals(float? value) {
        if (value is not {} v) return "";
        
        const int decimals = 4;
        string format = $"F{decimals}";
        return v.ToString(format);
        // return $"({value.x.ToString(format)}, {value.y.ToString(format)})";
    }

}
