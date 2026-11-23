using System;

namespace SpeedrunGym.Source.SkipWait;

internal enum SkipResult {
    Nothing,
    Refused,
    Done,
    /// progressed up to a point that ends the whole request, e.g. a roar that takes a request of its own
    Break,
}

/// something a state waits on: a timed wait ends after `Remaining` seconds of `Advance`, an event wait when `Resolve`
/// makes its event happen
internal abstract class StateWait(string name) {
    public readonly string Name = name;
}

internal sealed class TimedWait(string name, float remaining, Action<float> advance) : StateWait(name) {
    public readonly float Remaining = remaining;
    public readonly Action<float> Advance = advance;
}

internal sealed class EventWait(string name, Func<SkipResult> resolve) : StateWait(name) {
    public readonly Func<SkipResult> Resolve = resolve;
}
