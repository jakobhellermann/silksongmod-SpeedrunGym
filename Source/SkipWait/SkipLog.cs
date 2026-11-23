using System;
using System.Collections.Generic;

namespace SpeedrunGym.Source.SkipWait;

/// Skip output, indented by chain nesting. Chain headers are only logged once something in the chain is, so idle
/// fsms stay silent.
internal sealed class SkipLog {
    private int depth;

    /// headers of chains that haven't logged anything yet, outermost first
    private readonly List<string> pendingHeaders = [];

    internal void Line(string message) {
        foreach (string header in pendingHeaders) Log.Info(header);
        pendingHeaders.Clear();
        Log.Info(new string(' ', 2 * depth) + message);
    }

    internal ChainIndent Chain(string header) {
        pendingHeaders.Add(new string(' ', 2 * depth) + header);
        depth++;
        return new ChainIndent(this, pendingHeaders.Count - 1);
    }

    internal readonly struct ChainIndent(SkipLog log, int headerIndex) : IDisposable {
        public void Dispose() {
            log.depth--;
            if (log.pendingHeaders.Count > headerIndex) {
                log.pendingHeaders.RemoveRange(headerIndex, log.pendingHeaders.Count - headerIndex);
            }
        }
    }
}
