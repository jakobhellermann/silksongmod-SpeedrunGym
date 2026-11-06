using System;
using SpeedrunGym.Source.Moves;
using UnityEngine;

namespace SpeedrunGym.Source;

[DefaultExecutionOrder(HeroExecutionOrder - 1)]
internal class PreHeroUpdate : MonoBehaviour {
    private const int HeroExecutionOrder = 208;

    private void Update() {
        try {
            var hero = HeroController.SilentInstance;
            if (!hero || !hero.isActiveAndEnabled) return;

            WallJumpDetector.PreHeroUpdate(hero);
        } catch (Exception e) {
            Log.Error(e);
        }
    }
}
