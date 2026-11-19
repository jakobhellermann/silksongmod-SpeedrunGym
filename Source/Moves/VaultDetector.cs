using System;
using BepInEx.Configuration;
using HutongGames.PlayMaker;
using SpeedrunGym.Source.WorldToasts;
using UnityEngine;

namespace SpeedrunGym.Source.Moves;

// Dash vault popups. The Sprint FSM's Dashed state fires VAULT once its BoolTestMulti sees
// [Hit Bump=false, Hit Wall=true, On Conveyor=false, High Wall=false, Is Clamber Blocked=false], where
//   Hit Wall / Hit Bump / Hit High Wall come from HeroCheckForBumpV2 → CharacterBumpCheck: rays at feet+0.2 ("vault
//     ray"), above the head and below the feet, reaching 0.03 + |vx|*Time.deltaTime past the collider's leading edge.
//     It runs on entering Dashed and then only in FixedUpdate, the first time before HeroController.Dash() sets the
//     dash speed, so at the previous (running) speed.
//   High Wall is a 1u ray from the hero's pivot (RayCast2dV2) OR Hit High Wall.
// The Mantle FSM grabs a wall the hero touches, checked every frame; it cancels the sprint, so a ledge face reached by a
// physics step without a vault check catching it first gets mantled.
// Each check is classified by the ledge geometry at that moment; the popup names what the player has to change:
//   Vault +Xu                     — after Vault!'s fixed 1.1u lift the feet are X above the ledge top.
//   Vault Xu too low              — the lift ends below the ledge top: runs into it and drops.
// Without a vault, only the reason:
//   Xu too low                    — the ledge top is between pivot and head, so the 1u ray blocks the vault.
//   Xu too low (overhang)         — the body reaches the ledge before the vault ray does.
//   Xu too high                   — the vault ray passes above the ledge top.
// A ledge top above the head is a wall, not a near miss, and isn't reported.
//   Xu too far                    — the dash ended before the vault ray reached the ledge face.
//   dash timing (Xu)              — the first check after the press, still at the previous speed, missed by X, and the
//                                   next physics step pushed the hero into the face, so the mantle won.
internal static class VaultDetector {
    private const string Section = "Vault";
    private const string DashedState = "Dashed";
    private const string DashToVaultState = "Dash To Vault";
    private const string VaultState = "Vault!";
    private const string MantleIdleState = "Idle";

    private const int WallLayerMask = 8448; // Terrain + layer 13, as in CharacterBumpCheck and the High Wall ray
    private const float HighWallRayLength = 1f;
    private const float LookAhead = 3f;
    private const float ProfileStep = 0.05f;
    private const float Epsilon = 0.01f;

    internal static ConfigEntry<bool> enabled = null!;

    private static readonly Color GoodColor = new(0.4f, 1f, 0.4f);
    private static readonly Color FailColor = new(1f, 0.3f, 0.4f);

    private abstract class Miss {
        public sealed class OutOfReach(float distance, bool preDash) : Miss {
            public readonly float Distance = distance;
            public readonly bool PreDash = preDash;
        }

        public sealed class TooLow(float rise, bool overhang) : Miss {
            public readonly float Rise = rise;
            public readonly bool Overhang = overhang;
        }

        public sealed class TooHigh(float drop) : Miss {
            public readonly float Drop = drop;
        }

        public sealed class Wall : Miss;

        public sealed class Blocked(string reason) : Miss {
            public readonly string Reason = reason;
        }

        public override string ToString() => Describe(this);
    }

    private static bool dashing;
    private static Miss? lastMiss; // why the last check BoolTestMulti saw didn't vault
    private static (bool HitWall, Miss? Miss)? uncheckedBump; // latest bump check, not yet seen by BoolTestMulti
    // Set when VAULT fires, measured once Vault! has lifted the hero
    private static (Vector2 LedgeFace, float FeetBelowPivot)? pendingVault;

    internal static void BindConfig(ConfigFile config) {
        enabled = config.Bind(Section, "Enabled", false, "Show dash vault popups");
    }

    internal static void Reset() {
        dashing = false;
        lastMiss = null;
        uncheckedBump = null;
        pendingVault = null;
    }

    // Postfix of the Dashed state's HeroCheckForBumpV2, so the geometry is the hero's at the moment of the check.
    internal static void OnBumpCheck(HeroController hero, bool hitWall) {
        if (!enabled.Value || !dashing) return;

        var miss = Analyze(hero, hitWall);
        uncheckedBump = (hitWall, miss);
    }

    internal static void OnEnterState(Fsm fsm, FsmState state) {
        if (!enabled.Value) return;

        var hero = HeroController.UnsafeInstance;
        if (!hero || hero.sprintFSM.Fsm != fsm) return;

        // ActiveState already is the entered state here
        bool wasDashing = dashing;
        if (state.Name == DashedState) {
            Reset();
            dashing = true;
        }
        else if (wasDashing && state.Name == DashToVaultState) {
            Reset();
            pendingVault = (FindLedgeFace(hero), hero.transform.position.y - hero.Bounds.min.y);
        }
        else if (wasDashing) {
            bool mantled = hero.mantleFSM.ActiveStateName != MantleIdleState;
            if (mantled && lastMiss is Miss.OutOfReach { PreDash: true, Distance: var distance }) {
                HeroToast(hero, $"dash timing ({distance:0.00}u)", FailColor);
            }
            else if (lastMiss is not (null or Miss.Wall)) {
                HeroToast(hero, Describe(lastMiss), FailColor);
            }
            Reset();
        }
    }

    // After the FSM's Update, i.e. with the values this frame's BoolTestMulti saw (a VAULT already left Dashed).
    internal static void LateUpdate() {
        if (!enabled.Value) return;

        var hero = HeroController.UnsafeInstance;
        if (!hero) return;

        if (pendingVault is var (ledgeFace, feetBelowPivot)) {
            pendingVault = null;
            if (hero.sprintFSM.ActiveStateName != VaultState) {
                return;
            }

            // Collider bounds lag behind Vault!'s Translate until the next physics sync
            float feet = hero.transform.position.y - feetBelowPivot;
            float margin = feet - LedgeTop(ledgeFace, hero.cState.facingRight)
                ?? throw new InvalidOperationException($"No ledge top above the vaulted face at {ledgeFace}");
            if (margin >= 0) HeroToast(hero, $"Vault +{margin:0.00}u", GoodColor);
            else HeroToast(hero, $"Vault {-margin:0.00}u too low", FailColor);
            return;
        }

        if (hero.sprintFSM.ActiveStateName != DashedState || uncheckedBump is not var (hitWall, miss)) return;
        uncheckedBump = null;

        var vars = hero.sprintFSM.FsmVariables;
        if (!hitWall) {
            lastMiss = miss;
        }
        else if (Bool(vars, "Is Clamber Blocked")) {
            lastMiss = new Miss.Blocked("vaulting blocked here");
        }
        else if (Bool(vars, "On Conveyor")) {
            lastMiss = new Miss.Blocked("on a conveyor");
        }
        else if (Bool(vars, "High Wall")) {
            lastMiss = miss ?? new Miss.Blocked("something ahead at center height");
        }
    }

    // Why this check doesn't vault, as what the player would have to change; null if it vaults or there's no ledge ahead.
    private static Miss? Analyze(HeroController hero, bool hitWall) {
        bool right = hero.cState.facingRight;
        var dir = right ? Vector2.right : Vector2.left;
        var bounds = hero.Bounds;
        float originX = right ? bounds.max.x - 0.1f : bounds.min.x + 0.1f;
        float reach = 0.1f + 0.03f + Mathf.Abs(hero.rb2d.linearVelocity.x * Time.deltaTime);
        float vaultRayY = bounds.min.y + 0.2f;
        float pivotY = hero.transform.position.y;

        // The closest point ahead over the body's height
        (float Distance, float Y)? front = null;
        for (float y = bounds.min.y + Epsilon; y <= bounds.max.y - Epsilon; y += ProfileStep) {
            if (!Helper.IsRayHittingNoTriggers(new Vector2(originX, y), dir, reach + LookAhead, WallLayerMask, out var hit)) continue;
            if (front is not { } f || hit.distance < f.Distance - Epsilon) front = (hit.distance, y);
        }
        if (front is not var (frontDistance, frontY)) return null;

        if (LedgeTop(new Vector2(originX + dir.x * frontDistance, frontY), right) is not { } top || top > bounds.max.y) {
            return new Miss.Wall();
        }
        if (top > pivotY) return new Miss.TooLow(top - pivotY, overhang: false);
        if (hitWall) return null;
        if (top < vaultRayY) return new Miss.TooHigh(vaultRayY - top);

        bool vaultRayHits = Helper.IsRayHittingNoTriggers(new Vector2(originX, vaultRayY), dir, reach + LookAhead, WallLayerMask, out var vaultHit);
        if (!vaultRayHits || vaultHit.distance > frontDistance + Epsilon) {
            // The lowest height at which the face is as close as its front
            float lowestFrontY = frontY;
            for (float y = vaultRayY; y < frontY; y += ProfileStep) {
                if (Helper.IsRayHittingNoTriggers(new Vector2(originX, y), dir, reach + LookAhead, WallLayerMask, out var hit)
                    && hit.distance <= frontDistance + Epsilon) {
                    lowestFrontY = y;
                    break;
                }
            }
            return new Miss.TooLow(lowestFrontY - vaultRayY, overhang: true);
        }

        bool preDash = Mathf.Abs(hero.rb2d.linearVelocity.x) < hero.DASH_SPEED - Epsilon;
        return new Miss.OutOfReach(vaultHit.distance - reach, preDash);
    }

    private static string Describe(Miss miss) => miss switch {
        Miss.OutOfReach { Distance: var d } => $"{d:0.00}u too far",
        Miss.TooLow { Rise: var rise, Overhang: false } => $"{rise:0.00}u too low",
        Miss.TooLow { Rise: var rise, Overhang: true } => $"{rise:0.00}u too low (overhang)",
        Miss.TooHigh { Drop: var drop } => $"{drop:0.00}u too high",
        Miss.Wall => "wall",
        Miss.Blocked { Reason: var reason } => reason,
        _ => throw new ArgumentOutOfRangeException(nameof(miss), miss, null),
    };

    // Where the vault ray, which just fired VAULT, hits the ledge face.
    private static Vector2 FindLedgeFace(HeroController hero) {
        bool right = hero.cState.facingRight;
        var bounds = hero.Bounds;
        var vaultRayOrigin = new Vector2(right ? bounds.max.x - 0.1f : bounds.min.x + 0.1f, bounds.min.y + 0.2f);
        if (!Helper.IsRayHittingNoTriggers(vaultRayOrigin, right ? Vector2.right : Vector2.left, LookAhead, WallLayerMask, out var hit)) {
            throw new InvalidOperationException($"VAULT fired, but no ledge face within {LookAhead}u of {vaultRayOrigin}");
        }
        return hit.point;
    }

    private static readonly RaycastHit2D[] topHits = new RaycastHit2D[16];

    // The lowest upward-facing surface above a face point, just behind it; null if there's none within reach (a wall).
    // Taking every hit instead of the first copes with a probe starting inside a ceiling.
    private static float? LedgeTop(Vector2 facePoint, bool facingRight) {
        var probe = new Vector2(facePoint.x + (facingRight ? 0.05f : -0.05f), facePoint.y + 2 * LookAhead);
        var filter = new ContactFilter2D { useTriggers = false, useLayerMask = true, layerMask = WallLayerMask };
        int count = Physics2D.Raycast(probe, Vector2.down, filter, topHits, 2 * LookAhead + Epsilon);

        float? top = null;
        for (int i = 0; i < count; i++) {
            var hit = topHits[i];
            if (hit.distance <= 0f || hit.normal.y <= 0.5f || hit.point.y < facePoint.y - Epsilon) continue;
            if (top is not { } t || hit.point.y < t) top = hit.point.y;
        }
        return top;
    }

    private static bool Bool(FsmVariables vars, string name) =>
        (vars.FindFsmBool(name) ?? throw new InvalidOperationException($"Sprint FSM has no bool '{name}'")).Value;

    private static void HeroToast(HeroController hero, string message, Color color) {
        WorldToastManager.Show(message,
            hero.transform.position + Vector3.up * 1.5f + Vector3.right * (hero.cState.facingRight ? -1 : 1),
            color,
            moveUp: false);
    }
}

// ReSharper disable InconsistentNaming
[HarmonyLib.HarmonyPatch]
internal static class VaultPatches {
    // HeroController.Dash() runs its own CheckForBump, but only this action's result reaches the Sprint FSM
    [HarmonyLib.HarmonyPostfix]
    [HarmonyLib.HarmonyPatch(typeof(HutongGames.PlayMaker.Actions.HeroCheckForBumpV2), "DoAction")]
#pragma warning disable HARMONIZE001
    private static void HeroCheckForBump(HutongGames.PlayMaker.Actions.HeroCheckForBumpV2 __instance) {
#pragma warning restore HARMONIZE001
        try {
            var hero = HeroController.UnsafeInstance;
            if (!hero || __instance.Fsm != hero.sprintFSM.Fsm || __instance.State.Name != "Dashed") return;
            VaultDetector.OnBumpCheck(hero, __instance.StoreHitWall.Value);
        } catch (Exception e) {
            Log.Error(e);
        }
    }
}
