using System;
using BepInEx.Configuration;
using GlobalEnums;
using HarmonyLib;
using SpeedrunGym.Source.WorldToasts;
using UnityEngine;
using Vector3 = UnityEngine.Vector3;

namespace SpeedrunGym.Source.Moves;

// Detects pogo (downspike) landings and reports whether the endlag cancel was executed,
// and how many ms too early the landing was (vs. using the full spike duration).
internal static class PogoEndlagDetector {
    private const string Section = "Pogo Endlag";
    private static ConfigEntry<bool> enabled = null!;
    private static ConfigEntry<bool> showGood = null!;
    private static ConfigEntry<float> slowThresholdMs = null!;
    private static ConfigEntry<bool> showFrameCounts = null!;

    private static readonly Color GoodColor = new(0.4f, 1f, 0.4f);
    private static readonly Color SlowColor = new(1f, 1f, 0.4f);
    private static readonly Color FailColor = new(1f, 0.3f, 0.4f);

    private static CurrentDownspike? startedDownspike;

    internal static void BindConfig(ConfigFile config) {
        enabled = config.Bind(Section, "Enabled", false,
            "Detect pogo (downspike) endlag cancels and show feedback popups next to Hornet.");
        showGood = config.Bind(Section, "Show good", true,
            "Show a popup on a successful endlag cancel.");
        slowThresholdMs = config.Bind(Section, "Slow threshold (ms)", 60f,
            "Above this many ms, a successful cancel is shown as 'slow' (yellow) instead of green.");
        showFrameCounts = config.Bind(Section, "Show frame counts", false,
            "Include the frame count in popups (e.g. '3f (50ms)') instead of just milliseconds.");
    }


    internal static void OnDownAttack(HeroController hero) {
        if (!enabled.Value) return;
        if (hero.Config.DownSlashType is not HeroControllerConfig.DownSlashTypes.DownSpike) return;

        if (hero.cState.shuttleCock) return;

        startedDownspike = new CurrentDownspike();
    }

    internal static void OnUpdateState(HeroAnimationController anim, ActorStates newState) {
        if (startedDownspike is null) return;
        if (startedDownspike.RelevantInput is not null) return;

        var cState = HeroController.SilentInstance.cState;
        if (cState.downSpiking || cState.downSpikeRecovery) {
            var now = FrameTime.Now;

            switch (newState) {
                case ActorStates.idle:
                    startedDownspike = startedDownspike with {
                        RelevantInput = now
                    };

                    startedDownspike.Success = true;
                    // ToastManager.Toast($"ENDLAG CANCEL: worked {now}");
                    break;
                case ActorStates.airborne:
                    // ToastManager.Toast("pogo ended mid air");
                    break;
                case ActorStates.running: {
                    startedDownspike = startedDownspike with {
                        RelevantInput = now,
                        // The state resolves one frame after the input it reflects, so the last
                        // frame you could still be neutral is the previous input frame.
                        NeutralDeadline = startedDownspike.LastInput,
                        Success = false
                    };

                    // Pressed a direction early: report it now. The "held neutral late" case
                    // is reported later in LateUpdate once neutral is regained.
                    if (startedDownspike.DirectionPress is { } directionPress)
                        HeroToast($"dir repress {now - directionPress} early", FailColor);
                    break;
                }
            }
        }
    }

    // Show a small popup floating up next to Hornet, slightly above her head.
    private static void HeroToast(string message, Color color) {
        var hero = HeroController.SilentInstance;
        if (!hero) return;
        WorldToastManager.Show(message,
            hero.transform.position + Vector3.up * 1.5f + Vector3.right * (hero.cState.facingRight ? 1 : -1), color,
            moveUp: false);
    }

    // Runs in LateUpdate. OnUpdateState always fires earlier in the same frame (physics collision)
    internal static void LateUpdate() {
        var hero = HeroController.SilentInstance;
        if (!hero) return;

        if (startedDownspike is not { } downspike) return;

        if (hero.cState.dashing) {
            startedDownspike = null;
            return;
        }

        var moveInput = InputHandler.SilentInstance.inputActions.MoveVector.Vector.x;

        // Track neutral release / first direction press while waiting for the state to resolve.
        if (moveInput == 0)
            downspike.ReleasedNeutral = true;
        else if (downspike is { ReleasedNeutral: true, DirectionPress: null })
            downspike.DirectionPress = FrameTime.Now;

        downspike.LastInput = FrameTime.Now;

        if (downspike is { RelevantInput: { } relevantInput }) {
            // after relevant frame

            var afterRelevant = FrameTime.Now - relevantInput;
            if (afterRelevant.Ms > 300) {
                // Failed and never returned to neutral within the window: the "neutral late" path
                // never fires (it needs a neutral frame to measure), so report it here instead.
                if (downspike is { Success: false, DirectionPress: null })
                    HeroToast("no neutral", FailColor);
                startedDownspike = null;
                return;
            }

            if (downspike.Success) {
                if (!showGood.Value) {
                    startedDownspike = null;
                    return;
                }

                if (moveInput == 0) return; // still neutral
                if (afterRelevant.Ms >= slowThresholdMs.Value)
                    HeroToast($"slow repress +{afterRelevant}", SlowColor);
                else
                    HeroToast($"good +{afterRelevant}", GoodColor);
            }
            else {
                if (moveInput != 0) return; // still moving
                // The direction-early case already reported in OnUpdateState; only report
                // the "held neutral too late" magnitude here.
                if (downspike.DirectionPress is null) {
                    var deadline = downspike.NeutralDeadline ?? relevantInput;
                    var tooLate = FrameTime.Now - deadline;
                    HeroToast($"neutral {tooLate} late", FailColor);
                }
            }

            startedDownspike = null;
        }
    }

    internal record struct FrameTime {
        private int frame;
        private float Time;

        public static FrameTime Now => new()
            { frame = UnityEngine.Time.frameCount, Time = UnityEngine.Time.realtimeSinceStartup };

        public static FrameTimeDelta operator -(FrameTime a, FrameTime b) {
            return new FrameTimeDelta { Frames = a.frame - b.frame, Ms = (a.Time - b.Time) * 1000f };
        }
    }

    internal record struct FrameTimeDelta {
        public int Frames;
        public float Ms;

        public override string ToString() {
            return showFrameCounts.Value ? $"{Frames}f ({Ms:0}ms)" : $"{Ms:0}ms";
        }
    }

    private record CurrentDownspike {
        public FrameTime? DirectionPress; // first direction press after releasing to neutral
        public FrameTime LastInput; // FrameTime of the most recent EarlyUpdate
        public FrameTime? NeutralDeadline; // input frame that resolution reflects (= RelevantInput - 1 frame)
        public bool ReleasedNeutral; // saw a neutral frame after the attack started
        public FrameTime? RelevantInput; // frame the state resolved to idle/running
        public bool Success;
    }
}

// ReSharper disable InconsistentNaming
[HarmonyPatch]
internal static class PogoEndlagPatches {
    [HarmonyPrefix]
    [HarmonyPatch(typeof(HeroController), "DownAttack")]
#pragma warning disable HARMONIZE001
    private static void DownAttack(HeroController __instance) {
#pragma warning restore HARMONIZE001
        try {
            PogoEndlagDetector.OnDownAttack(__instance);
        } catch (Exception e) {
            Log.Error(e);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(HeroAnimationController), nameof(HeroAnimationController.UpdateState))]
    private static void UpdateState(HeroAnimationController __instance, ActorStates newState) {
        try {
            PogoEndlagDetector.OnUpdateState(__instance, newState);
        } catch (Exception e) {
            Log.Error(e);
        }
    }
}
