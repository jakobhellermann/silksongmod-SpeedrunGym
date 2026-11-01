using System;
using BepInEx.Configuration;
using HarmonyLib;
using SpeedrunGym.Source.WorldToasts;
using UnityEngine;
using FrameTime = SpeedrunGym.Source.Moves.PogoEndlagDetector.FrameTime;
using FrameTimeDelta = SpeedrunGym.Source.Moves.PogoEndlagDetector.FrameTimeDelta;
using Vector3 = UnityEngine.Vector3;

namespace SpeedrunGym.Source.Moves;

// Walljump timing popups. A queued walljump (QWJ, HeroController.TryQueueWallJumpInterrupt) fires from a jump press
// with a wall within WALLJUMP_BROLLY_RAY_LENGTH in front and a direction held, without touching the wall; it stays
// queued until input is accepted again. Reports:
//   QWJ +Nf [queued Mf] — press frames after a QWJ first became possible; frames it waited queued before firing.
//   WJ +Nf (contact +Mf) — a contact walljump: frames after a QWJ was possible, and after wall contact; hidden when
//                          late beyond WallJumpMaxLatenessMs (e.g. after a long wallslide).
//   QWJ Nf early         — an air jump pressed while the wall was out of range, which then came in range.
//   QWJ Xu too far       — such a jump whose wall never came in range: how far beyond range it was at the press.
//   QWJ Nf late (window) — an air jump pressed just after the QWJ window closed: how long it was open, and why it closed.
//   QWJ: no direction    — wall in range but no direction held, so no QWJ.
//   QWJ not queued, …    — a QWJ looked possible but the press didn't queue one.
internal enum QwjBlock {
    NoWalljump,
    TouchingNonSlider,
    CenterRayMissed,
    NonSliderWall,
    WallTooShort,
    DashingDown,
    NoDirection,
}

internal static class WallJumpDetector {
    private const string Section = "Walljump";
    private static ConfigEntry<bool> enabled = null!;

    private const float TooFarStep = 0.05f;
    private const float MaxTooFar = 3f;
    // Beyond these, a press is most likely unrelated to the wall, so it isn't reported.
    private const float EarlyWindowMs = 200f;
    private const float LateWindowMs = 200f;
    private const float WallJumpMaxLatenessMs = 150f;
    private const int WallLayerMask = 8448; // Terrain + layer 13, as in HeroController.IsFacingNearWall

    private static readonly Color GoodColor = new(0.4f, 1f, 0.4f);
    private static readonly Color SlowColor = new(1f, 1f, 0.4f);
    private static readonly Color FailColor = new(1f, 0.3f, 0.4f);

    internal static readonly AccessTools.FieldRef<HeroController, bool> QueuedWallJumpInterrupt =
        AccessTools.FieldRefAccess<HeroController, bool>("queuedWallJumpInterrupt");
    private static readonly AccessTools.FieldRef<HeroController, int> WallJumpChainStepsLeft =
        AccessTools.FieldRefAccess<HeroController, int>("wallJumpChainStepsLeft");

    private static FrameTime? possibleSince; // first frame a jump press would have queued a QWJ
    private static FrameTime? lastPossible;
    private static (FrameTime Since, FrameTime Last, FrameTime Closed, QwjBlock Reason)? closedWindow;
    private static FrameTime? contactSince; // first frame of the current wall contact
    private static (FrameTime At, FrameTimeDelta Late)? queued; // QWJ queue time and its lateness at that moment
    // Air jump without a QWJ, waiting to see whether a wall comes in range; TooFar is the wall's distance beyond QWJ
    // range at the press, if there was one nearby.
    private static (FrameTime At, float? TooFar)? earlyPress;

    // Per-frame events from the HeroController patches, consumed in LateUpdate.
    private static bool wallJumpedThisFrame;

    // This frame's jump press is a walljump or gets a walljump popup, so other jump popups skip it.
    internal static bool ClaimedPress { get; private set; }
    private static bool inTryDoWallJumpFromMove;

    internal static void BindConfig(ConfigFile config) {
        enabled = config.Bind(Section, "Enabled", false, "Show walljump timing popups");
    }

    internal static void BeforeHeroUpdate(HeroController hero) {
        if (!enabled.Value) return;

        var now = FrameTime.Now;
        if (hero.cState.onGround) {
            possibleSince = null;
            lastPossible = null;
            closedWindow = null;
            contactSince = null;
            earlyPress = null;
            return;
        }

        var touching = hero.cState.touchingWall || hero.cState.wallSliding;
        if (touching) contactSince ??= now;
        else contactSince = null;

        var block = FindQwjBlock(hero);
        if (block is null) {
            possibleSince ??= now;
            lastPossible = now;
            closedWindow = null;
            if (earlyPress is var (pressedAt, _)) {
                earlyPress = null;
                var early = now - pressedAt;
                if (early.Ms <= EarlyWindowMs) HeroToast(hero, $"QWJ {early} early", FailColor);
            }
        } else if (!touching) {
            if (possibleSince is { } since && lastPossible is { } last) closedWindow = (since, last, now, block.Value);
            possibleSince = null;
            lastPossible = null;
        }
    }

    // Also called by side-effect-free polls that restore the flag afterwards (e.g. CelesteTAS' info panel), so this only
    // timestamps a queue; whether a press actually queued is read from the flag itself.
    internal static void OnTryQueue(HeroController hero, bool wasQueued) {
        if (!enabled.Value) return;

        if (!wasQueued && QueuedWallJumpInterrupt(hero)) {
            var now = FrameTime.Now;
            // The game just confirmed a QWJ is possible, even if it got queued before this frame's BeforeHeroUpdate.
            possibleSince ??= now;
            queued = (now, now - possibleSince.Value);
        }
    }

    internal static void OnTryDoWallJumpFromMove(bool entering) => inTryDoWallJumpFromMove = entering;

    internal static void OnWallJump(HeroController hero) {
        if (!enabled.Value) return;

        wallJumpedThisFrame = true;
        var now = FrameTime.Now;

        if (inTryDoWallJumpFromMove && queued is var (queuedAt, late)) {
            var message = $"QWJ +{late}";
            var waited = now - queuedAt;
            if (waited.Frames > 0) message += $" queued {waited}";
            HeroToast(hero, message, late.Frames == 0 ? GoodColor : SlowColor);
        } else {
            var sincePossible = possibleSince is { } since ? now - since : (FrameTimeDelta?)null;
            var sinceContact = contactSince is { } contact ? now - contact : (FrameTimeDelta?)null;
            if ((sincePossible ?? sinceContact) is { } lateness && lateness.Ms < WallJumpMaxLatenessMs) {
                var message = sincePossible is { } p ? $"WJ +{p}" : "WJ";
                if (sinceContact is { } c) message += $" (contact +{c})";
                HeroToast(hero, message, SlowColor);
            }
        }

        queued = null;
        possibleSince = null;
        earlyPress = null;
    }

    internal static void LateUpdate() {
        ClaimedPress = false;
        if (!enabled.Value) return;

        var hero = HeroController.SilentInstance;
        var input = InputHandler.SilentInstance;
        if (!hero || input == null) return;

        var pressed = input.inputActions.Jump.WasPressed;
        if (pressed && !hero.cState.onGround && hero.playerData.hasWalljump) {
            // A queued or performed walljump gets reported when it fires (OnWallJump).
            ClaimedPress = QueuedWallJumpInterrupt(hero) || wallJumpedThisFrame || ReportMissedQwj(hero, input);
        }

        if (earlyPress is var (pressedAt, tooFar) && (FrameTime.Now - pressedAt).Ms > EarlyWindowMs) {
            if (tooFar is { } distance) HeroToast(hero, $"QWJ {distance:0.00}u too far", FailColor);
            earlyPress = null;
        }

        wallJumpedThisFrame = false;
    }

    // Reports an air jump press that didn't queue a QWJ; false if it isn't reported (yet).
    private static bool ReportMissedQwj(HeroController hero, InputHandler input) {
        var now = FrameTime.Now;
        if (possibleSince is not null) {
            var frozenInput = Mathf.Abs(hero.move_input) <= Mathf.Epsilon
                              && Mathf.Abs(HeldDirection(input)) > Mathf.Epsilon;
            HeroToast(hero, $"QWJ not queued, {(frozenInput ? "move_input 0 while held" : "jump went elsewhere")}",
                FailColor);
        } else if (FindQwjBlock(hero) == QwjBlock.NoDirection) {
            HeroToast(hero, "QWJ: no direction", FailColor);
        } else if (closedWindow is var (since, last, closed, reason) && (now - closed).Ms <= LateWindowMs) {
            // Visible in the walljump debug rays, so not repeated.
            var shownReason = reason is QwjBlock.CenterRayMissed or QwjBlock.NonSliderWall
                ? ""
                : $", {Describe(reason)}";
            HeroToast(hero, $"QWJ {now - last} late, window {closed - since}{shownReason}", FailColor);
        } else {
            // Reported later, once a wall comes in range or the early window runs out.
            earlyPress = (now, WallDistanceBeyondRange(hero));
            return false;
        }

        return true;
    }

    internal static void Reset() {
        possibleSince = null;
        lastPossible = null;
        closedWindow = null;
        contactSince = null;
        queued = null;
        earlyPress = null;
    }

    // Why TryQueueWallJumpInterrupt would fail right now, or null if it would queue, following IsFacingNearWall's checks
    // to name the failing one. CanChainWallJumps is evaluated with the held direction: move_input is frozen while the
    // hero doesn't accept input, and RegainControl refreshes it before re-evaluating a queued jump.
    private static QwjBlock? FindQwjBlock(HeroController hero) {
        if (!hero.playerData.hasWalljump) return QwjBlock.NoWalljump;
        if (hero.cState.touchingNonSlider) return QwjBlock.TouchingNonSlider;

        var range = hero.WALLJUMP_BROLLY_RAY_LENGTH;
        var direction = hero.cState.facingRight ? Vector2.right : Vector2.left;
        if (!Helper.IsRayHittingNoTriggers(hero.transform.position, direction, range, WallLayerMask, out var hit))
            return QwjBlock.CenterRayMissed;
        if (NonSlider.TryGetNonSlider(hit.collider, out var nonSlider) && nonSlider.IsActive)
            return QwjBlock.NonSliderWall;
        if (!hero.IsFacingNearWall(hero.cState.facingRight, range, Color.green)) return QwjBlock.WallTooShort;

        if (hero.dashingDown) return QwjBlock.DashingDown;
        var chained = hero.wallLocked || WallJumpChainStepsLeft(hero) > 0;
        if (!chained && Mathf.Abs(HeldDirection(InputHandler.SilentInstance)) <= Mathf.Epsilon)
            return QwjBlock.NoDirection;

        return null;
    }

    private static string Describe(QwjBlock block) => block switch {
        QwjBlock.NoWalljump => "no walljump",
        QwjBlock.TouchingNonSlider => "touching non-slider",
        QwjBlock.CenterRayMissed => "center ray missed",
        QwjBlock.NonSliderWall => "non-slider wall",
        QwjBlock.WallTooShort => "wall too short",
        QwjBlock.DashingDown => "dashing down",
        QwjBlock.NoDirection => "no direction",
        _ => throw new ArgumentOutOfRangeException(nameof(block), block, null),
    };

    // How far beyond WALLJUMP_BROLLY_RAY_LENGTH the wall in front is, if within MaxTooFar.
    private static float? WallDistanceBeyondRange(HeroController hero) {
        var range = hero.WALLJUMP_BROLLY_RAY_LENGTH;
        for (var extra = TooFarStep; extra <= MaxTooFar; extra += TooFarStep) {
            if (hero.IsFacingNearWall(hero.cState.facingRight, range + extra, Color.green)) return extra;
        }

        return null;
    }

    private static float HeldDirection(InputHandler? input) =>
        input != null ? input.inputActions.MoveVector.Vector.x : 0f;

    private static void HeroToast(HeroController hero, string message, Color color) {
        WorldToastManager.Show(message,
            hero.transform.position + Vector3.up * 1.5f + Vector3.right * (hero.cState.facingRight ? -1 : 1),
            color, moveUp: false);
    }
}

// ReSharper disable InconsistentNaming
[HarmonyPatch]
internal static class WallJumpPatches {
    [HarmonyPrefix]
    [HarmonyPatch(typeof(HeroController), "Update")]
#pragma warning disable HARMONIZE001
    private static void Update(HeroController __instance) {
#pragma warning restore HARMONIZE001
        try {
            WallJumpDetector.BeforeHeroUpdate(__instance);
        } catch (Exception e) {
            Log.Error(e);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(HeroController), "TryQueueWallJumpInterrupt")]
#pragma warning disable HARMONIZE001
    private static void TryQueuePrefix(HeroController __instance, out bool __state) {
#pragma warning restore HARMONIZE001
        __state = false;
        try {
            __state = WallJumpDetector.QueuedWallJumpInterrupt(__instance);
        } catch (Exception e) {
            Log.Error(e);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HeroController), "TryQueueWallJumpInterrupt")]
#pragma warning disable HARMONIZE001
    private static void TryQueuePostfix(HeroController __instance, bool __state) {
#pragma warning restore HARMONIZE001
        try {
            WallJumpDetector.OnTryQueue(__instance, __state);
        } catch (Exception e) {
            Log.Error(e);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(HeroController), "TryDoWallJumpFromMove")]
#pragma warning disable HARMONIZE001
    private static void TryDoWallJumpFromMovePrefix() => WallJumpDetector.OnTryDoWallJumpFromMove(true);
#pragma warning restore HARMONIZE001

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HeroController), "TryDoWallJumpFromMove")]
#pragma warning disable HARMONIZE001
    private static void TryDoWallJumpFromMovePostfix() => WallJumpDetector.OnTryDoWallJumpFromMove(false);
#pragma warning restore HARMONIZE001

    [HarmonyPrefix]
    [HarmonyPatch(typeof(HeroController), "DoWallJump")]
#pragma warning disable HARMONIZE001
    private static void DoWallJump(HeroController __instance) {
#pragma warning restore HARMONIZE001
        try {
            WallJumpDetector.OnWallJump(__instance);
        } catch (Exception e) {
            Log.Error(e);
        }
    }
}
