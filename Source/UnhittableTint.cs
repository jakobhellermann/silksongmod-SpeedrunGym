using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SpeedrunGym.Source;

internal static class UnhittableTint {
    internal static ConfigEntry<bool> Enabled = null!;
    private static ConfigEntry<Color> color = null!;

    private static int attackLayer;

    private static readonly HashSet<HealthManager> warnedNoFlash = [];
    private static readonly List<Collider2D> colliderBuffer = [];

    internal static void BindConfig(ConfigFile config) {
        attackLayer = LayerMask.NameToLayer("Attack");

        Enabled = config.Bind("Unhittable Tint",
            "Enabled",
            false,
            "Tint enemies while they can't be hit");
        color = config.Bind("Unhittable Tint",
            "Color",
            new Color(0.3f, 0.3f, 1f, 0.5f),
            "Tint color");
    }

    internal static void OnSceneLoaded() {
        warnedNoFlash.Clear();
    }

    internal static void LateUpdate() {
        var tint = color.Value;
        foreach (var hm in HealthManager.EnumerateActiveEnemies()) {
            bool unhittable = Enabled.Value && !hm.isDead && !IsHittable(hm);

            if (!hm.TryGetComponent<SpriteFlash>(out var flash)) {
                if (unhittable && warnedNoFlash.Add(hm)) Log.Warning($"Unhittable Tint: no SpriteFlash on {hm.name}");
                continue;
            }

            if (unhittable) Apply(flash, tint);
            else Clear(flash, tint);
        }
    }

    internal static void Dispose() {
        var tint = color.Value;
        foreach (var hm in HealthManager.EnumerateActiveEnemies()) {
            if (hm.TryGetComponent<SpriteFlash>(out var flash)) Clear(flash, tint);
        }
    }

    private static bool IsHittable(HealthManager hm) {
        if (hm.IsInvincible) return false;

        hm.GetComponents(colliderBuffer);
        foreach (var collider in colliderBuffer) {
            if (collider.enabled && !Physics2D.GetIgnoreLayerCollision(collider.gameObject.layer, attackLayer)) {
                return true;
            }
        }

        return false;
    }

    private static void Apply(SpriteFlash flash, Color tint) {
        var opaque = tint with {
            a = 1f,
        };
        if (flash.ExtraMixColor == opaque && Mathf.Approximately(flash.ExtraMixAmount, tint.a)) return;

        flash.ExtraMixColor = opaque;
        flash.ExtraMixAmount = tint.a; // the setter refreshes the material
    }

    private static void Clear(SpriteFlash flash, Color tint) {
        if (flash.ExtraMixColor
            != tint with {
                a = 1f,
            }
            || flash.ExtraMixAmount == 0f) return;

        flash.ExtraMixAmount = 0f;
    }
}
