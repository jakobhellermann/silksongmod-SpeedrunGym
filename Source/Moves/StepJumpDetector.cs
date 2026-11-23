using System;
using BepInEx.Configuration;
using GlobalEnums;
using HarmonyLib;
using SpeedrunGym.Source.WorldToasts;
using UnityEngine;
using FrameTime = SpeedrunGym.Source.Moves.PogoEndlagDetector.FrameTime;
using FrameTimeDelta = SpeedrunGym.Source.Moves.PogoEndlagDetector.FrameTimeDelta;
using Vector3 = UnityEngine.Vector3;

namespace SpeedrunGym.Source.Moves;

internal static class StepJumpDetector {
    private const string Section = "Step Jump";
    internal static ConfigEntry<bool> enabled = null!;

    private static readonly Color color = new(0.4f, 1f, 0.4f);

    private static float? startHeight;
    private static float? peakHeight;
    // private static FrameTime? released; // when the button was let go, awaiting a repress
    // private static bool shuttlecockActive; // a dashjump is in flight; gates the release-yvel popup

    internal static void BindConfig(ConfigFile config) {
        enabled = config.Bind(Section,
            "Enabled",
            true, // TODO false
            "todo");
    }



    internal static void OnHeroJumped() {
        var hero = HeroController.instance;

        startHeight = hero.rb2d.position.y;
        peakHeight = null;

        Log.Info($"startHeight={FormatDecimals(startHeight)}");
    }

    private static void FinishJump() {
        startHeight = null;
    }
    
    private static void Report(HeroController hero) {
        if (startHeight is not {} start) return;

        var jumpHeight = peakHeight - start;

        Log.Info($"peak at {peakHeight}");

        const float baseHeight = 5.483998f;

        HeroToast($"extra height={FormatDecimals(jumpHeight - baseHeight)}", color);
    }

    internal static void LateUpdate() {
        if (!enabled.Value) return;

        var hero = HeroController.UnsafeInstance;
        if (!hero) return;

        if (startHeight != null) {
            var height = hero.rb2d.position.y;
            peakHeight = Mathf.Max(peakHeight ?? float.NegativeInfinity, height);
        }

        if (hero.cState.falling) {
            Report(hero);
            FinishJump();
        }
    }
}

[HarmonyPatch]
internal static class StepJumpPatches {
    [HarmonyPostfix]
    [HarmonyPatch(typeof(HeroController), nameof(HeroController.HeroJump), [typeof(bool)])]
    private static void HeroControllerJump() {
        try {
            StepJumpDetector.OnHeroJumped();
        } catch (Exception e) {
            Log.Error(e);
        }
    }
}
