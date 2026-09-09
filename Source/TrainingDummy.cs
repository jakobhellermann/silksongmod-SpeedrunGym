using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Silksong.AssetHelper.ManagedAssets;
using UnityEngine;
using Object = UnityEngine.Object;
using Vector3 = UnityEngine.Vector3;

namespace SpeedrunGym.Source;

public enum DummyContactDamage {
    Off,
    EffectOnly,
    Damage,
}

[RequireComponent(typeof(HealthManager))]
public class TrainingDummyBehaviour : MonoBehaviour {
    private const int Hp = 10000;
    private const int RefillBelowHp = 1000;

    private HealthManager healthManager = null!;
    private Recoil recoil = null!;
    private float defaultRecoilSpeed;

    private DamageHero[] damageHeroes = null!;

    private void Awake() {
        healthManager = GetComponent<HealthManager>();
        healthManager.AddHP(Hp, Hp);

        // cached before SetTakesKnockback can zero it via SetRecoilSpeed
        recoil = GetComponent<Recoil>();
        defaultRecoilSpeed = recoil.RecoilSpeedBase;

        gameObject.name = "Training Dummy";

        GetComponent<ConstrainPosition>().enabled = false;

        var control = GetComponents<PlayMakerFSM>().First(x => x.Fsm.Name == "Control");
        control.SetState("Idle");
        foreach (var fsm in GetComponents<PlayMakerFSM>()) {
            fsm.enabled = false;
        }

        GetComponent<tk2dSpriteAnimator>().enabled = false;

        foreach (var persistent in GetComponentsInChildren<PersistentBoolItem>(true)) {
            persistent.enabled = false;
        }

        damageHeroes = GetComponentsInChildren<DamageHero>(true);
    }

    private void Update() {
        if (healthManager.hp < RefillBelowHp) {
            healthManager.RefillHP();
        }
    }

    public void SetContactDamage(DummyContactDamage mode) {
        foreach (var damageHero in damageHeroes) {
            damageHero.enabled = mode != DummyContactDamage.Off;
        }
    }

    public void SetTakesKnockback(bool value) {
        recoil.SetRecoilSpeed(value ? defaultRecoilSpeed : 0f);
    }
}

public class TrainingDummy : IDisposable {
    private const string Section = "Training Dummy";
    private const int SpawnOffset = 4;

    private readonly ManagedAsset<GameObject> trainingDummyAsset = ManagedAsset<GameObject>.FromSceneAsset(
        sceneName: "Song_25",
        objPath: "Black Thread States/Normal World/Song Reed Grand (2)");

    private static ConfigEntry<KeyboardShortcut> spawnDummyShortcut = null!;
    private static ConfigEntry<KeyboardShortcut> despawnDummiesShortcut = null!;
    private static ConfigEntry<DummyContactDamage> contactDamage = null!;
    private static ConfigEntry<bool> takeKnockback = null!;

    internal static DummyContactDamage ContactDamageMode => contactDamage.Value;

    private readonly List<TrainingDummyBehaviour> spawnedDummies = [];

    internal void BindConfig(ConfigFile config) {
        spawnDummyShortcut = config.Bind(Section, "Spawn training dummy", new KeyboardShortcut(),
            "Spawn a grand reed as target practice.");
        despawnDummiesShortcut = config.Bind(Section, "Despawn training dummies", new KeyboardShortcut(),
            "Despawn all training dummies.");
        contactDamage = config.Bind(Section, "Contact damage", DummyContactDamage.EffectOnly,
            "Off: No damage. EffectOnly: knockback etc. Damage: regular contact damage.");
        takeKnockback = config.Bind(Section, "Take knockback", false, "Whether the training dummy takes knockback.");

        contactDamage.SettingChanged += (_, _) => ToggleContactDamage(contactDamage.Value);
        takeKnockback.SettingChanged += (_, _) => ToggleTakeKnockback(takeKnockback.Value);
    }

    private void ToggleContactDamage(DummyContactDamage mode) {
        PruneDestroyed();
        foreach (var dummy in spawnedDummies) {
            dummy.SetContactDamage(mode);
        }
    }

    private void ToggleTakeKnockback(bool value) {
        PruneDestroyed();
        foreach (var dummy in spawnedDummies) {
            dummy.SetTakesKnockback(value);
        }
    }

    private IEnumerator InstantiateTrainingDummy() {
        var hero = HeroController.SilentInstance;
        if (!hero) yield break;

        trainingDummyAsset.Load();
        yield return trainingDummyAsset.Handle;
        var go = trainingDummyAsset.InstantiateAsset();

        var dummy = go.AddComponent<TrainingDummyBehaviour>();
        spawnedDummies.Add(dummy);

        dummy.SetContactDamage(contactDamage.Value);
        dummy.SetTakesKnockback(takeKnockback.Value);

        go.transform.position = hero.transform.position +
                                Vector3.right * ((hero.cState.facingRight ? 1f : -1f) * SpawnOffset);

        Log.Info("Spawned training dummy");
    }

    private void PruneDestroyed() {
        spawnedDummies.RemoveAll(d => !d);
    }

    private void DespawnTrainingDummies() {
        PruneDestroyed();
        foreach (var dummy in spawnedDummies) {
            Object.Destroy(dummy.gameObject);
        }
        spawnedDummies.Clear();
    }

    internal void LateUpdate() {
        if (spawnDummyShortcut.Value.IsDown()) {
            SpeedrunGymPlugin.StartCoroutine(InstantiateTrainingDummy());
        }
        if (despawnDummiesShortcut.Value.IsDown()) {
            DespawnTrainingDummies();
        }
    }

    public void Dispose() {
        GC.SuppressFinalize(this);
        trainingDummyAsset.Unload();
        DespawnTrainingDummies();
    }
}

[HarmonyPatch]
public class PreventTrainingDummyDamage {
    [HarmonyPrefix]
    [HarmonyPatch(typeof(HeroController), nameof(HeroController.TakeDamage))]
    private static void PreventTakeDamage(HeroController __instance, GameObject go, ref bool __state) {
        if (TrainingDummy.ContactDamageMode != DummyContactDamage.EffectOnly) return;
        if (!go || !go.GetComponentInParent<TrainingDummyBehaviour>()) return;
        __state = true;
        __instance.SetTakeNoDamage();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HeroController), nameof(HeroController.TakeDamage))]
    private static void RestoreTakeNoDamage(HeroController __instance, bool __state) {
        if (__state) __instance.EndTakeNoDamage();
    }
}
