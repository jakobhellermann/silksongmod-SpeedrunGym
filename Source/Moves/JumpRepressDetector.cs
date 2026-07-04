using System;
using BepInEx.Configuration;
using HarmonyLib;
using SpeedrunGym.Source.WorldToasts;
using UnityEngine;
using FrameTime = SpeedrunGym.Source.Moves.PogoEndlagDetector.FrameTime;
using FrameTimeDelta = SpeedrunGym.Source.Moves.PogoEndlagDetector.FrameTimeDelta;
using Vector3 = UnityEngine.Vector3;

namespace SpeedrunGym.Source.Moves;

// Two separate popups for air jumps: on release, the vertical velocity at that moment (yvel ≈ 0 =
// released at the apex; + = still rising, − = already falling); on the following re-press, how long the
// repress took. State resets on the ground, and a pending release is dropped if no repress arrives in
// time.
internal static class JumpRepressDetector {
    private const string Section = "Jump Repress";
    private static ConfigEntry<bool> enabled = null!;

    private static readonly Color Color = new(0.4f, 1f, 0.4f);
    private const float ResetMs = 250f; // drop a pending release if no repress arrives within this window

    private static bool prevPressed;
    private static FrameTime? released; // when the button was let go, awaiting a repress
    private static bool shuttlecockActive; // a dashjump is in flight; gates the release-yvel popup

    internal static void BindConfig(ConfigFile config) {
        enabled = config.Bind(Section, "Enabled", false,
            "Report each air jump repress (release → re-press) with its timing and vertical velocity.");
    }

    // HeroController clears the jump input state on every jump (HeroJump → ClearJumpInputState), which
    // drops IsPressed for one frame while the button is physically held. Resync so the re-registration
    // reads as a fresh press, not a spurious release → repress. The real double-jump repress goes
    // through DoDoubleJump (no clear) and is unaffected.
    internal static void OnInputCleared() {
        released = null;
        prevPressed = false;
    }

    // A dashjump (shuttlecock) just started — arm the release-yvel popup for the upcoming release.
    internal static void OnShuttleCockJump() {
        shuttlecockActive = true;
    }

    internal static void LateUpdate() {
        if (!enabled.Value) return;

        var input = InputHandler.SilentInstance;
        if (input == null) return;

        var pressed = input.inputActions.Jump.IsPressed;

        // Reset while grounded or dashing: only air represses matter, a ground release must not pair with
        // a later press, and a jump out of a dash (shuttlecock → release → dash → jump) is not a repress.
        var hero = HeroController.SilentInstance;
        if (hero && (hero.cState.onGround || hero.cState.dashing)) {
            released = null;
            shuttlecockActive = false;
            prevPressed = pressed;
            return;
        }

        var now = FrameTime.Now;

        if (prevPressed && !pressed) {
            released = now; // let go
            // The release-yvel readout only makes sense out of a dashjump. Sign is flipped from Unity's
            // rb.velocity.y convention on request: shown − = rising, + = falling.
            if (shuttlecockActive) {
                HeroToast(hero, $"release yvel={-YVel(hero):+0.0;-0.0}", Color, heightOffset: 1.9f);
                shuttlecockActive = false;
            }
        } else if (!prevPressed && pressed && released is { } r) {
            var delta = now - r;
            released = null;
            if (delta.Ms <= ResetMs) HeroToast(hero, $"repress +{Discount(delta)}", Color, heightOffset: 1.2f);
        } else if (released is { } stale && (now - stale).Ms > ResetMs) {
            released = null; // repress window elapsed
        }

        prevPressed = pressed;
    }

    private static float YVel(HeroController? hero) {
        if (!hero) return 0f;
        var rb = hero!.GetComponent<Rigidbody2D>();
        return rb != null ? rb.linearVelocity.y : 0f;
    }

    // Drop the one mandatory release frame (you have to let go before you can re-press) without assuming
    // any particular framerate: scale the elapsed time by the frames that remain after removing one.
    private static FrameTimeDelta Discount(FrameTimeDelta delta) {
        var frames = Math.Max(0, delta.Frames - 1);
        var ms = delta.Frames > 0 ? delta.Ms * frames / delta.Frames : 0f;
        return new FrameTimeDelta { Frames = frames, Ms = ms };
    }

    private static void HeroToast(HeroController? hero, string message, Color color, float heightOffset) {
        if (!hero) return;
        WorldToastManager.Show(message,
            hero!.transform.position + Vector3.up * heightOffset + Vector3.right * (hero.cState.facingRight ? 1 : -1),
            color, moveUp: false);
    }
}

// ReSharper disable InconsistentNaming
[HarmonyPatch]
internal static class JumpRepressPatches {
    [HarmonyPostfix]
    [HarmonyPatch(typeof(HeroController), nameof(HeroController.ClearJumpInputState))]
    private static void ClearJumpInputState() {
        try {
            JumpRepressDetector.OnInputCleared();
        } catch (Exception e) {
            Log.Error(e);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HeroController), "OnShuttleCockJump")]
#pragma warning disable HARMONIZE001
    private static void OnShuttleCockJump() {
#pragma warning restore HARMONIZE001
        try {
            JumpRepressDetector.OnShuttleCockJump();
        } catch (Exception e) {
            Log.Error(e);
        }
    }
}
