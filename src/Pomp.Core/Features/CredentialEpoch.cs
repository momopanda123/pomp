using System;
using System.Threading;

namespace Pomp.Features
{
    /// <summary>
    /// Counts the changes to what every engine of one chat window starts with: saved API keys, local and shared model servers,
    /// sign-ins and sign-outs (docs/architecture.md "자격 증명 변경과 엔진 재시작"). Every chat tab runs its own engine; one launched
    /// before the latest change keeps the old credentials and model list until it restarts.
    /// </summary>
    internal sealed class CredentialEpoch
    {
        private int _value;

        /// <summary>Raised after every change, on the thread that made it.</summary>
        public event EventHandler? Advanced;

        /// <summary>
        /// Raised, with the setup that made it as sender, after a change every running engine sees without a restart (a sign-in stored
        /// in the shared omp profile): the other tabs' cached model lists are out of date, not their engines.
        /// </summary>
        public event EventHandler? ModelListsChanged;

        public int Current => Volatile.Read(ref _value);

        public void Advance()
        {
            Interlocked.Increment(ref _value);
            Advanced?.Invoke(this, EventArgs.Empty);
        }

        public void NotifyModelListsChanged(object origin) => ModelListsChanged?.Invoke(origin, EventArgs.Empty);
    }
}
