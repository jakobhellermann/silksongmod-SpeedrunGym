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

// Extra jump height above a normal full jump, measured from the jump's start to its peak.
// A stepjump's coyote re-jump (HeroJump out of the ledge buffer midair) re-impulses exactly like a fresh
// jump, so measured from its own start it would always read +0 — it must extend the FIRST jump's
// measurement instead of re-baselining it. Hence the phase enum.
internal static class StepJumpDetector {
    private enum StepjumpPhase {
        Idle,
        Measuring, // a jump is in flight; peak tracked from its start height
        AwaitingRepress, // jump cut (released) while still rising; a ledge-buffer repress can extend it
    }

    private const string Section = "Step Jump";
    internal static ConfigEntry<bool> enabled = null!;

    private static readonly Color color = new(0.4f, 1f, 0.4f);

    private static StepjumpPhase phase;
    private static bool repressed;
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

        if (phase == StepjumpPhase.AwaitingRepress) {
            // the coyote re-jump: keep the original start, let it extend the same measurement
            phase = StepjumpPhase.Measuring;
            repressed = true;
            return;
        }

        startHeight = hero.rb2d.position.y;
        peakHeight = null;
        repressed = false;
        phase = StepjumpPhase.Measuring;

        Log.Info($"startHeight={FormatDecimals(startHeight)}");
    }

    private static void FinishJump() {
        phase = StepjumpPhase.Idle;
    }
    
    private static void Report(HeroController hero) {
        if (startHeight is not {} start) return;

        var jumpHeight = peakHeight - start;

        Log.Info($"peak at {peakHeight}{(repressed ? " (repress)" : "")}");

        const float baseHeight = 5.483998f;

        HeroToast($"extra height={FormatDecimals(jumpHeight - baseHeight)}{(repressed ? " repress" : "")}", color);
    }

    internal static void LateUpdate() {
        if (!enabled.Value) return;

        var hero = HeroController.UnsafeInstance;
        if (!hero || phase == StepjumpPhase.Idle) return;

        peakHeight = Mathf.Max(peakHeight ?? float.NegativeInfinity, hero.rb2d.position.y);

        // Jump cancelled while still rising: a ledge-buffer repress may re-impulse midair.
        if (phase == StepjumpPhase.Measuring && !hero.cState.jumping && !hero.cState.onGround
            && hero.rb2d.linearVelocity.y > 0f) {
            phase = StepjumpPhase.AwaitingRepress;
        }

        // Over once falling with nothing left that could re-impulse: not jumping anymore, and no open
        // ledge-buffer window. A repress sets jumping (and consumes the ledge) on the same frame falling
        // first flips, so check jumping first — else the report pops at the coyote re-jump's low point
        // instead of at the end.
        if (hero.cState.falling && !hero.cState.jumping && hero.ledgeBufferSteps <= 0) {
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
