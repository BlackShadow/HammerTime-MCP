using System;

namespace HammerTime.Mcp.Plugin
{
    /// <summary>
    /// Raises the engine's inactive-viewport frame rate while at least one capture is waiting for a fresh frame.
    /// The rate is process-global, so overlapping captures share one boost: the first raises it, the last
    /// restores the value that was there before any of them (a per-caller save/restore would leave the boost on).
    /// </summary>
    internal sealed class InactiveFpsBoost : IDisposable
    {
        private static readonly object Sync = new object();
        private static int _holders;
        private static int _original;

        private readonly Action<int> _set;
        private bool _released;

        private InactiveFpsBoost(Action<int> set)
        {
            _set = set;
        }

        public static InactiveFpsBoost Acquire(Func<int> get, Action<int> set, int target)
        {
            lock (Sync)
            {
                if (_holders == 0)
                {
                    var original = get();
                    set(Math.Max(original, target));
                    _original = original;
                }
                _holders++; // only once the boost is really applied, so a failing engine call cannot leave a phantom holder
                return new InactiveFpsBoost(set);
            }
        }

        public void Dispose()
        {
            lock (Sync)
            {
                if (_released) return;
                _released = true;
                if (--_holders == 0) _set(_original);
            }
        }
    }
}
