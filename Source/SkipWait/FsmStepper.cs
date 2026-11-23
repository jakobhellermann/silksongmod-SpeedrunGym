using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SpeedrunGym.Source.SkipWait;

/// The execution engine: runs an fsm forward within the frame, as far as its current waits allow. It never decides
/// which fsms are worth skipping, it just keeps stepping a state until it gets stuck.
internal static class FsmStepper {
    /// bounds fsm chains that keep progressing forever
    private const int MaxSkipSteps = 100;

    /// timers compare with < or >=, so a step lands just past the end
    private const float StepEpsilon = 0.001f;

    internal static string Name(PlayMakerFSM fsm) => $"{fsm.gameObject.name}/{fsm.FsmName}";

    internal static string Name(Fsm fsm) => $"{fsm.GameObject.name}/{fsm.Name}";

    internal static string Awaiting(Fsm fsm) =>
        fsm.ActiveState?.Transitions.Select(t => t.EventName).Distinct().ToList() is { Count: > 0 } events
            ? $"waiting on {string.Join(", ", events)}"
            : "nothing to skip";

    internal static string Transition(Fsm fsm, FsmState from) =>
        fsm.ActiveState == from ? "" : $" → {fsm.ActiveStateName}";

    /// keeps skipping until the fsm reaches something that can't be skipped, or until `reached` returns true
    internal static SkipResult Chain(SkipRequest req, Fsm fsm, string header, Func<bool>? reached = null) {
        using SkipLog.ChainIndent indent = req.Log.Chain(header);
        bool progressed = false;
        bool startedInRoar = SkipRules.IsStunningRoar(fsm.ActiveState);
        for (int steps = 0; reached?.Invoke() != true; steps++) {
            if (steps == MaxSkipSteps) {
                req.Log.Line($"{fsm.ActiveStateName}: stopped after {steps} steps, it seems to loop");
                break;
            }

            SkipResult result = Step(req, fsm);
            RoutineTracker.FinishStarted(req.Log);
            if (result == SkipResult.Nothing) {
                if (progressed) req.Log.Line($"{fsm.ActiveStateName}: end of chain, {Awaiting(fsm)}");
                return progressed ? SkipResult.Done : SkipResult.Nothing;
            }
            if (result == SkipResult.Refused) return progressed ? SkipResult.Done : SkipResult.Refused;
            if (result == SkipResult.Break) return result;
            progressed = true;

            if (!startedInRoar && SkipRules.IsStunningRoar(fsm.ActiveState)) {
                req.Log.Line($"{fsm.ActiveStateName}: stunning roar, stopping");
                return SkipResult.Break;
            }
        }
        return SkipResult.Done;
    }

    /// one round of progress: advance time past the next timer that runs out, or resolve an event the state waits on
    private static SkipResult Step(SkipRequest req, Fsm fsm) {
        ApplyPendingSwitches(req, fsm);
        FsmState? state = fsm.ActiveState;
        if (state == null) return SkipResult.Nothing;
        string name = state.Name;
        if (EventDelivery.RedeliverEarlySends(req, fsm, state)) return SkipResult.Break;

        // a sequence only has entered the actions up to its current one
        IEnumerable<FsmStateAction> entered = (state.IsSequence ? state.Actions.Take(state.ActiveActionIndex + 1) : state.Actions)
            .Where(a => a.Enabled);
        List<StateWait> waits = [];
        List<Action<float>> sideEffects = [];
        List<string> unhandled = [];
        foreach (FsmStateAction action in entered) {
            if (!action.Finished && ActionCatalog.Wait(req, fsm, action, name) is { } wait) {
                waits.Add(wait);
            } else if (ActionCatalog.SideEffect(action) is { } advance) {
                sideEffects.Add(advance);
            } else if (!action.Finished) {
                unhandled.Add(action.GetType().Name);
            }
        }
        waits.AddRange(fsm.DelayedEvents
            .Where(e => !e.Finished)
            .Select(e => new TimedWait($"delayed {e.FsmEvent?.Name} → {ActionCatalog.DescribeTarget(e)}", e.Timer, dt => e.timer -= dt)));
        ActionCatalog.AddAnimatorClocks(fsm, entered, waits, sideEffects);
        waits.AddRange(EventDelivery.TransitionWaits(req, fsm, state, name));

        List<TimedWait> timed = waits.OfType<TimedWait>().ToList();
        // timers that ran out act on their next update, so they can be slightly overdue
        float step = Mathf.Max(0f, timed.Select(w => w.Remaining).DefaultIfEmpty(float.PositiveInfinity).Min());
        if (!float.IsPositiveInfinity(step) && unhandled.Count == 0) {
            string due = string.Join(", ", timed.Where(w => w.Remaining <= step).Select(w => w.Name).Distinct());
            AdvanceState(req, fsm, state, step + StepEpsilon, timed, sideEffects);
            req.Log.Line($"{name}: advanced {step:0.###}s ({due}){Transition(fsm, state)}");
            return SkipResult.Done;
        }

        // a state entered this frame hasn't run its actions' first update yet, so its actions haven't started
        if (timed.Count == 0 && unhandled.Count > 0 && !state.finished && state.StateTime == 0f) {
            fsm.Update();
            req.Log.Line($"{name}: started{Transition(fsm, state)}");
            return SkipResult.Done;
        }

        // an action finished from outside an update, e.g. by a message's callback, only ends the state on its next update
        if (timed.Count == 0 && unhandled.Count == 0 && !state.finished && entered.Any() && entered.All(a => a.Finished)) {
            fsm.Update();
            req.Log.Line($"{name}: actions finished{Transition(fsm, state)}");
            if (fsm.ActiveState != state) return SkipResult.Done;
        }

        SkipResult result = SkipResult.Nothing;
        foreach (EventWait wait in waits.OfType<EventWait>()) {
            SkipResult resolved = wait.Resolve();
            if (resolved is SkipResult.Done or SkipResult.Break) return resolved;
            if (resolved == SkipResult.Refused) result = resolved;
        }
        if (result != SkipResult.Nothing) return result;

        if (!float.IsPositiveInfinity(step)) {
            req.Log.Line($"{name}: stuck, {string.Join(", ", timed.Select(w => w.Name))} blocked by unhandled {string.Join(", ", unhandled)}");
            return SkipResult.Refused;
        }
        if (timed.Count > 0) {
            req.Log.Line($"{name}: stuck, waiting on {string.Join(", ", timed.Select(w => w.Name))} which never end");
            return SkipResult.Refused;
        }
        if (unhandled.Count > 0) req.Log.Line($"{name}: unhandled {string.Join(", ", unhandled)}");
        return SkipResult.Nothing;
    }

    /// moves every clock in the state forward, then runs a regular fsm update so the actions themselves react
    private static void AdvanceState(SkipRequest req, Fsm fsm, FsmState state, float dt, List<TimedWait> timed, List<Action<float>> sideEffects) {
        foreach (Action<float> advance in sideEffects) advance(dt);
        foreach (TimedWait wait in timed) wait.Advance(dt);
        AdvanceJitterChanges(req, dt);
        if (fsm.ActiveState != state) return;

        state.StateTime += dt;
        fsm.Update();
    }

    /// an event sent from inside another fsm's update only queues the switch for the receiver's next update, and further
    /// events are dropped until then; a skip delivers events faster than updates run, so apply them before every step
    private static void ApplyPendingSwitches(SkipRequest req, Fsm current) {
        // sub fsms run by RunFSM have no component of their own
        foreach (Fsm fsm in PlayMakerFSM.FsmList.Where(c => c).Select(c => c.Fsm).Append(current).Where(f => f != null).Distinct().ToList()) {
            if (!fsm.IsSwitchingState) continue;
            FsmState state = fsm.ActiveState;
            fsm.UpdateStateChanges();
            req.Log.Line($"{Name(fsm)}: applied pending switch{Transition(fsm, state)}");
        }
    }

    /// jitters start and stop after a delay in game time, e.g. a choir, which a stop before its start would miss
    private static void AdvanceJitterChanges(SkipRequest req, float dt) {
        foreach (JitterSelf jitter in req.Jitters) {
            if (!jitter.isChangeQueued) continue;
            jitter.queueEndTime -= dt;
            if (jitter.queueEndTime <= Time.timeAsDouble) jitter.BatchedUpdate();
        }
    }
}
