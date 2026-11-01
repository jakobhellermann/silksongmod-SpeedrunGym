using System;
using BepInEx.Configuration;
using HarmonyLib;
using SpeedrunGym.Source.WorldToasts;
using UnityEngine;

namespace SpeedrunGym.Source.Moves;

internal static class SilkWasteDetector {
    private static ConfigEntry<bool> enabled = null!;

    private static readonly Color Color = new(1f, 0.4f, 0.4f);

    internal static void BindConfig(ConfigFile config) {
        enabled = config.Bind("Silk Waste", "Enabled", false,
            "Show a popup when a hit would generate silk while the silk spool is already full.");
    }

    internal static void OnSilkGain(HeroController hero, HitInstance hitInstance) {
        if (!enabled.Value) return;

        var generates = hitInstance.SilkGeneration switch {
            HitSilkGeneration.Full => true,
            HitSilkGeneration.FirstHit => hitInstance.IsFirstHit,
            _ => false,
        };
        var pd = PlayerData.instance;
        if (!generates || pd.silk < pd.CurrentSilkMax) return;

        WorldToastManager.Show("silk wasted",
            hero.transform.position + Vector3.up * 1.2f + Vector3.right * (hero.cState.facingRight ? -1 : 1),
            Color, moveUp: false);
    }
}

// ReSharper disable InconsistentNaming
[HarmonyPatch]
internal static class SilkWastePatches {
    [HarmonyPrefix]
    [HarmonyPatch(typeof(HeroController), nameof(HeroController.SilkGain), typeof(HitInstance))]
    private static void SilkGain(HeroController __instance, HitInstance hitInstance) {
        try {
            SilkWasteDetector.OnSilkGain(__instance, hitInstance);
        } catch (Exception e) {
            Log.Error(e);
        }
    }
}
