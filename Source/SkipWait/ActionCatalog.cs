using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace SpeedrunGym.Source.SkipWait;

/// Knowledge about how each PlayMaker action type spends its time: what a state waits on and what keeps running
/// over the skipped time.
internal static class ActionCatalog {
    private static readonly Dictionary<(Type, Type), FieldInfo[]> fieldsByType = [];

    internal static StateWait? Wait(SkipRequest req, Fsm fsm, FsmStateAction action, string stateName) => action switch {
        Wait w => new TimedWait("Wait", w.time.Value - w.timer, dt => {
            if (w.realTime) w.startTime -= dt;
            else w.timer += dt;
        }),
        WaitRandom w => new TimedWait("WaitRandom", w.time - w.timer, dt => {
            if (w.realTime) w.startTime -= dt;
            else w.timer += dt;
        }),
        WaitBool w => new TimedWait("WaitBool", w.time.Value - w.timer, dt => {
            if (w.realTime) w.startTime -= dt;
            else w.timer += dt;
        }),
        SendEventToRegisterDelay s => new TimedWait($"send {s.EventName.Value}", s.timer, dt => s.timer -= dt),
        ActivateGameObjectDelay a => new TimedWait("ActivateGameObjectDelay", a.timer, dt => a.timer -= dt),
        AudioPlayerOneShotSingle a => new TimedWait("audio", a.delay.Value - a.timer, dt => a.timer += dt),
        AudioPlayerOneShot a => new TimedWait("audio", a.delay.Value - a.timer, dt => a.timer += dt),
        StartRoarEmitter r => new TimedWait("roar", r.delay.Value - r.timer, dt => r.timer += dt),
        StartRoarEmitterV2 r => new TimedWait("roar", r.delay.Value - r.timer, dt => r.timer += dt),
        CameraBlurPlaneFade b => new TimedWait("blur fade", b.Duration.Value - b.State.StateTime, _ => { }),
        PlayTimeline p => new TimedWait($"timeline {p.timeline.name}", (float)(p.timeline.duration - p.timeline.time), dt => {
            p.timeline.time += dt;
            p.timeline.Evaluate();
        }),
        ListenForPromptContinue p => new EventWait("prompt", () => {
            FsmState state = fsm.ActiveState;
            fsm.Event(p.ContinuePressed);
            req.Log.Line($"{stateName}: continued prompt{FsmStepper.Transition(fsm, state)}");
            return SkipResult.Done;
        }),
        NextFrameEvent n => new EventWait("next frame", () => {
            FsmState state = fsm.ActiveState;
            n.Finish();
            fsm.Event(n.sendEvent);
            req.Log.Line($"{stateName}: skipped a frame{FsmStepper.Transition(fsm, state)}");
            return SkipResult.Done;
        }),
        RunFSMAction r when r.runFsm != null => SubFsmWait(req, fsm, r.runFsm, stateName),
        ListenForMenuActions l => PressWait(req, fsm, stateName, "menu cancel", l.cancelPressed),
        // its OnUpdate only sends when the delay crosses zero, which a step past it would skip
        CheckHeroPerformanceRegionBase c when c.delay > 0f => new TimedWait("performance reaction", c.delay, dt => {
            c.delay -= dt;
            if (c.delay <= 0f) c.SendEvents(c.affectedState);
        }),
        SpawnSkillGetMsg or SpawnPowerUpGetMsg => new EventWait("message", () => req.SkipUIMsg() ? SkipResult.Done : SkipResult.Nothing),
        ListenForAnimationEvent l => AnimationEventWait(fsm.GetOwnerDefaultTarget(l.Target)),
        ListenForCast l => PressWait(req, fsm, stateName, "cast", l.wasPressed),
        ListenForJump l => PressWait(req, fsm, stateName, "jump", l.wasPressed),
        ListenForAttack l => PressWait(req, fsm, stateName, "attack", l.wasPressed),
        Tk2dWatchAnimationEvents a => AnimationWait(fsm, a.gameObject, a.animationTriggerEvent, a.animationCompleteEvent != null),
        Tk2dWatchAnimationEventsV3 a => AnimationWait(fsm, a.gameObject, a.animationTriggerEvent, a.animationCompleteEvent != null),
        Tk2dPlayAnimationWithEvents a => AnimationWait(fsm, a.gameObject, a.animationTriggerEvent, a.animationCompleteEvent != null),
        Tk2dPlayAnimationWithEventsV3 a => AnimationWait(fsm, a.gameObject, a.animationTriggerEvent, a.animationCompleteEvent != null),
        Tk2dPlayAnimationWait a => AnimationWait(fsm, a.Target, null, true),
        EaseFsmAction e => EaseWait(e),
        iTweenMoveTo m => TweenMoveWait(fsm, m),
        FadeAudio f => FadeAudioWait(f),
        DoCameraShakeV4 c => new TimedWait("camera shake", c.delayLeft, dt => {
            if (c.delayLeft > 0f && (c.delayLeft -= dt) <= 0f) c.DoShake();
        }),
        AnimatorPlayStateWait a => AnimatorStateWait(a),
        CheckYPosition c => HeightWait(fsm, c),
        _ => DelayedEventWait(action),
    };

    /// effects that keep running over the state's time without ever ending it
    internal static Action<float>? SideEffect(FsmStateAction action) => action switch {
        DecelerateXY d => dt => Decelerate(d, dt),
        // reapplied by the regular update, nothing builds up over time
        SetScale { everyFrame: true } or SetRotation { everyFrame: true } or AudioPlayInState or ObjectJitter => _ => { },
        // per-frame computation (camera following a position), re-run by the regular update
        GetPosition2D or Vector2Add or Vector2Subtract or SetVector2Value or FollowTargetPositionV2 => _ => { },
        // conditions on the current world, re-checked by the regular update
        CheckHeroPerformanceRegionBase or CheckAlertRangeByName => _ => { },
        ScreenFader f => dt => AdvanceScreenFade(f.State.StateTime, dt, f.duration.Value, f.endColour.Value),
        ScreenFaderAlpha f => dt => AdvanceScreenFade(f.State.StateTime, dt, f.Duration.Value, ScreenFaderUtils.GetColour() with { a = f.EndAlpha.Value }),
        _ => null,
    };

    private static StateWait SubFsmWait(SkipRequest req, Fsm fsm, Fsm sub, string stateName) => new EventWait($"sub fsm {sub.Name}", () => {
        // RunFSM leaves the state once its sub fsm stops, which it notices on the next update
        FsmState state = fsm.ActiveState;
        if (FsmStepper.Chain(req, sub, $"{stateName}: running sub fsm {FsmStepper.Name(sub)}", () => sub.Finished) == SkipResult.Break) {
            return SkipResult.Break;
        }
        if (!sub.Finished) return SkipResult.Refused;

        fsm.Update();
        req.Log.Line($"{stateName}: sub fsm finished{FsmStepper.Transition(fsm, state)}");
        return SkipResult.Done;
    });

    private static StateWait? PressWait(SkipRequest req, Fsm fsm, string stateName, string button, FsmEvent? wasPressed) {
        // all input listeners lead to one event, while controls map buttons to different ones
        if (wasPressed == null || !IsButtonPrompt(fsm.ActiveState)) return null;
        return new EventWait($"{button} press", () => {
            FsmState state = fsm.ActiveState;
            fsm.Event(wasPressed);
            req.Log.Line($"{stateName}: pressed {button}{FsmStepper.Transition(fsm, state)}");
            return SkipResult.Done;
        });
    }

    internal static bool IsButtonPrompt(FsmState? state) => state != null && state.Actions
        .Where(a => a.Enabled && a.GetType().Name.StartsWith("ListenFor"))
        .SelectMany(a => FieldsOfType(a.GetType(), typeof(FsmEvent)).Select(f => (FsmEvent?)f.GetValue(a)))
        .Where(e => !string.IsNullOrEmpty(e?.Name))
        .Distinct()
        .Count() == 1;

    /// an action whose pending work is a delayed event of its fsm, which is advanced as its own wait
    private static TimedWait? DelayedEventWait(FsmStateAction action) {
        DelayedEvent? pending = FieldsOfType(action.GetType(), typeof(DelayedEvent))
            .Select(f => (DelayedEvent?)f.GetValue(action))
            .SingleOrDefault(e => e != null);
        if (pending == null) return null;
        // once sent, the action finishes on its next update
        return new TimedWait($"{action.GetType().Name} {pending.FsmEvent?.Name}", pending.Finished ? 0f : pending.Timer, _ => { });
    }

    /// animators the state works with keep running over the skipped time and fire their animation events
    internal static void AddAnimatorClocks(Fsm fsm, IEnumerable<FsmStateAction> entered, List<StateWait> waits, List<Action<float>> sideEffects) {
        IEnumerable<Animator> animators = entered
            .SelectMany(a => ReferencedObjects(fsm, a))
            .Where(go => go)
            .Select(go => go.GetComponent<Animator>())
            .Where(a => a && a.isActiveAndEnabled)
            .Distinct();
        foreach (Animator animator in animators) {
            float remaining = NextAnimationEvent(animator, out string? eventName);
            if (float.IsPositiveInfinity(remaining)) sideEffects.Add(dt => animator.Update(dt));
            else waits.Add(new TimedWait($"{animator.name} animation event {eventName}", remaining, dt => animator.Update(dt)));
        }
    }

    /// fired by the target's animator, whose clock advances it
    private static TimedWait? AnimationEventWait(GameObject? target) {
        Animator? animator = target ? target.GetComponent<Animator>() : null;
        if (!animator || !animator.isActiveAndEnabled) return null;

        // a state played this frame only becomes the current one with the animator's next update
        animator.Update(0f);
        float remaining = NextAnimationEvent(animator, out string? eventName);
        return float.IsPositiveInfinity(remaining) ? null : new TimedWait($"{animator.name} animation event {eventName}", remaining, _ => { });
    }

    /// seconds until the next animation event of a clip that plays once
    private static float NextAnimationEvent(Animator animator, out string? eventName) {
        eventName = null;
        AnimatorClipInfo[] clips = animator.GetCurrentAnimatorClipInfo(0);
        if (clips.Length == 0 || clips[0].clip.isLooping) return float.PositiveInfinity;

        AnimationClip clip = clips[0].clip;
        AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
        float time = info.normalizedTime * clip.length;
        AnimationEvent? next = clip.events.Where(e => e.time > time).OrderBy(e => e.time).FirstOrDefault();
        if (next == null) return float.PositiveInfinity;

        eventName = next.functionName;
        return (next.time - time) / clip.length * info.length;
    }

    /// resumes once game time passes the length of the animator state, which the action records on its second update
    private static TimedWait AnimatorStateWait(AnimatorPlayStateWait wait) =>
        wait.resumeTime is { } resume
            ? new TimedWait($"{wait.animator.name} state", (float)(resume - Time.timeAsDouble), dt => wait.resumeTime -= dt)
            : new TimedWait("AnimatorPlayStateWait starting", 0f, _ => { });

    private static StateWait? TweenMoveWait(Fsm fsm, iTweenMoveTo move) {
        GameObject? target = fsm.GetOwnerDefaultTarget(move.gameObject);
        if (!target) return null;

        iTween? tween = null;
        foreach (iTween t in target.GetComponents<iTween>()) {
            if (t.isRunning && t.type == "move") tween = t;
        }
        if (tween == null) {
            // a tween started this frame hasn't run its start setup yet, so do what its start coroutine would
            foreach (iTween t in target.GetComponents<iTween>()) {
                if (t.isRunning || !iTween.tweens.Any(a => (string)a["id"] == t.id && (string)a["type"] == "move")) continue;
                Tween.ThisTransform.SetValue(t, t.transform);
                Tween.RetrieveArgs.Invoke(t, null);
                Tween.IsValid.SetValue(t, true, null);
                Tween.TweenStart.Invoke(t, null);
                tween = t;
                break;
            }
        }
        if (tween == null || tween.type != "move" || (bool)Tween.Physics.GetValue(tween)
            || tween.loopType != iTween.LoopType.none || (bool)Tween.Reverse.GetValue(tween)) return null;

        return new TimedWait($"iTween {target.name}", Mathf.Max(0f, tween.time - (float)Tween.RunningTime.GetValue(tween)), dt => {
            if (!tween.isRunning) return;
            float running = (float)Tween.RunningTime.GetValue(tween) + dt;
            Tween.RunningTime.SetValue(tween, running);
            float percentage = tween.time > 0f ? running / tween.time : 1f;
            Tween.Percentage.SetValue(tween, percentage);
            if (percentage >= 1f) Tween.TweenComplete.Invoke(tween, null);
            else if (Tween.Apply.GetValue(tween) is Delegate apply) apply.DynamicInvoke();
        });
    }

    /// itween's assembly is not publicized, so its private members are reached by reflection
    private static class Tween {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static readonly FieldInfo RunningTime = Field("runningTime");
        internal static readonly FieldInfo Percentage = Field("percentage");
        internal static readonly FieldInfo Reverse = Field("reverse");
        internal static readonly FieldInfo Physics = Field("physics");
        internal static readonly FieldInfo ThisTransform = Field("thisTransform");
        internal static readonly FieldInfo Apply = Field("apply");
        internal static readonly MethodInfo RetrieveArgs = Method("RetrieveArgs");
        internal static readonly MethodInfo TweenStart = Method("TweenStart");
        internal static readonly MethodInfo TweenComplete = Method("TweenComplete");
        internal static readonly PropertyInfo IsValid = Property("IsValid");

        private static FieldInfo Field(string name)
            => typeof(iTween).GetField(name, Flags) ?? throw new MissingFieldException(nameof(iTween), name);
        private static MethodInfo Method(string name)
            => typeof(iTween).GetMethod(name, Flags) ?? throw new MissingMethodException(nameof(iTween), name);
        private static PropertyInfo Property(string name)
            => typeof(iTween).GetProperty(name, Flags) ?? throw new MissingFieldException(nameof(iTween), name);
    }

    internal static string DescribeTarget(DelayedEvent delayed) => delayed.eventTarget is not { } target
        ? "self"
        : target.target switch {
            FsmEventTarget.EventTarget.GameObject or FsmEventTarget.EventTarget.GameObjectFSM =>
                delayed.fsm.GetOwnerDefaultTarget(target.gameObject) is { } go ? go.name : "nothing",
            FsmEventTarget.EventTarget.FSMComponent => target.fsmComponent ? FsmStepper.Name(target.fsmComponent) : "nothing",
            _ => target.target.ToString(),
        };

    /// the ease's delay, then its running time scaled by speed
    private static TimedWait EaseWait(EaseFsmAction ease) {
        string name = ease.GetType().Name;
        if (ease.realTime) return new TimedWait($"{name} in real time", float.PositiveInfinity, _ => { });

        float rate = ease.speed.IsNone ? 1f : ease.speed.Value;
        float delay = ease.start && !ease.isRunning ? Mathf.Max(0f, ease.delayTime) : 0f;
        // a finished ease still needs an update to finish the action
        float remaining = ease.finishAction ? 0f : delay + (ease.time.Value - ease.runningTime) / rate;
        return new TimedWait(name, remaining, dt => {
            if (ease.start && !ease.isRunning) {
                ease.delayTime -= dt;
                if (ease.delayTime >= 0f) return;

                // what OnUpdate does once the delay is over, with the overshoot as running time
                dt = -ease.delayTime;
                ease.isRunning = true;
                ease.start = false;
            }
            if (ease.isRunning) ease.runningTime += dt * rate;
        });
    }

    /// the volume moves linearly from start to end over time
    private static TimedWait FadeAudioWait(FadeAudio fade) {
        if (!fade.audio) return new TimedWait("FadeAudio without audio", float.PositiveInfinity, _ => { });

        float span = fade.endVolume.Value - fade.startVolume.Value;
        float remaining = Mathf.Approximately(span, 0f)
            ? 0f
            : (fade.endVolume.Value - fade.GetVolume()) / span * fade.time.Value;
        return new TimedWait("FadeAudio", remaining, dt => fade.SetVolume(fade.GetVolume() + span * dt / fade.time.Value));
    }

    /// the object crossing the compared height at its current vertical speed, ignoring gravity and collisions
    private static TimedWait HeightWait(Fsm fsm, CheckYPosition check) {
        GameObject? go = fsm.GetOwnerDefaultTarget(check.gameObject);
        string name = $"{(go ? go.name : "nothing")} reaching y";
        Rigidbody2D? body = go ? go.GetComponent<Rigidbody2D>() : null;
        if (!go || !body || check.space != Space.World || (!check.activeBool.IsNone && !check.activeBool.Value)) {
            return new TimedWait(name, float.PositiveInfinity, _ => { });
        }

        GameObject moving = go;
        Rigidbody2D rigid = body;
        float distance = check.compareTo.Value + check.compareToOffset.Value - moving.transform.position.y;
        float speed = rigid.linearVelocity.y;
        FsmEvent? crossing = speed < 0 ? check.lessThan : check.greaterThan;
        float remaining = distance * speed > 0 && crossing != null
            ? Mathf.Max(0f, (Mathf.Abs(distance) - check.tolerance.Value) / Mathf.Abs(speed))
            : float.PositiveInfinity;
        return new TimedWait(name, remaining, dt => moving.transform.position += (Vector3)(rigid.linearVelocity * dt));
    }

    /// the watched clip's next trigger frame, or its end
    private static TimedWait AnimationWait(Fsm fsm, FsmOwnerDefault target, FsmEvent? triggerEvent, bool untilEnd) {
        tk2dSpriteAnimator? animator = fsm.GetOwnerDefaultTarget(target)?.GetComponent<tk2dSpriteAnimator>();
        if (!animator || !animator.Playing) return new TimedWait("stopped animation", float.PositiveInfinity, _ => { });

        tk2dSpriteAnimationClip clip = animator.CurrentClip;
        int current = (int)animator.clipTime;
        int? frame = triggerEvent == null ? null : NextTriggerFrame(clip, current);
        if (frame == null && untilEnd && clip.wrapMode == tk2dSpriteAnimationClip.WrapMode.Once) {
            frame = clip.frames.Length;
        }

        float remaining = frame == null ? float.PositiveInfinity : (frame.Value - animator.clipTime) / animator.clipFps;
        return new TimedWait($"animation {clip.name}", remaining, dt => animator.UpdateAnimation(dt));
    }

    private static int? NextTriggerFrame(tk2dSpriteAnimationClip clip, int current) {
        for (int i = current + 1; i < clip.frames.Length; i++) {
            if (clip.frames[i].triggerEvent) return i;
        }
        return null;
    }

    /// the per-physics-step deceleration compounded over dt, without the movement it would cause
    private static void Decelerate(DecelerateXY decelerate, float dt) {
        if (!decelerate.rb2d) return;
        float steps = dt / Time.fixedDeltaTime;
        Vector2 velocity = decelerate.rb2d.linearVelocity;
        if (!decelerate.decelerationX.IsNone) velocity.x *= Mathf.Pow(decelerate.decelerationX.Value, steps);
        if (!decelerate.decelerationY.IsNone) velocity.y *= Mathf.Pow(decelerate.decelerationY.Value, steps);
        decelerate.rb2d.linearVelocity = velocity;
    }

    /// the fade started on state enter and eases linearly, so its colour after dt can be interpolated from now
    private static void AdvanceScreenFade(float now, float dt, float duration, Color target) {
        if (now >= duration) return;
        if (now + dt >= duration) {
            ScreenFaderUtils.SetColour(target);
            return;
        }

        Color atEnd = Color.Lerp(ScreenFaderUtils.GetColour(), target, dt / (duration - now));
        ScreenFaderUtils.Fade(atEnd, target, duration - now - dt);
    }

    private static IEnumerable<GameObject> ReferencedObjects(Fsm fsm, FsmStateAction action) =>
        FieldsOfType(action.GetType(), typeof(FsmGameObject)).Select(f => ((FsmGameObject?)f.GetValue(action))?.Value)
            .Concat(FieldsOfType(action.GetType(), typeof(FsmOwnerDefault)).Select(f => fsm.GetOwnerDefaultTarget((FsmOwnerDefault?)f.GetValue(action))))
            .Where(go => go != null)!;

    private static FieldInfo[] FieldsOfType(Type type, Type fieldType) {
        if (!fieldsByType.TryGetValue((type, fieldType), out FieldInfo[]? fields)) {
            fieldsByType[(type, fieldType)] = fields = type
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => f.FieldType == fieldType)
                .ToArray();
        }
        return fields;
    }
}
