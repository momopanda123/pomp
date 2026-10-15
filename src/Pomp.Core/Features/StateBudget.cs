using System.Collections.Generic;
using Pomp.Json;

namespace Pomp.Features
{
    /// <summary>
    /// How big a list state message may grow. Lists have no count limit: every row is sent while the whole message stays under
    /// <see cref="Limit"/> (a message above 2 MiB is dropped by the UI hub, and the page's whole queue with it). Rows past it are left out
    /// and the list's diagnostics say how many; a list that needs more room sends summaries of its rows (see WorkflowCatalog).
    /// </summary>
    internal static class StateBudget
    {
        /// <summary>The largest list state message; the margin below 2 MiB takes the diagnostics and the message envelope.</summary>
        internal const int Limit = 1792 * 1024;

        /// <summary>The bytes of a payload (or a whole UI message) as the UI hub counts them: its JSON in UTF-8.</summary>
        internal static int Bytes(object payload) => System.Text.Encoding.UTF8.GetByteCount(PompJson.Serialize(payload));

        /// <summary>
        /// Keeps the front rows of <paramref name="rows"/> that fit in <paramref name="limit"/> bytes together with what the message holds
        /// besides them (<paramref name="others"/>, e.g. the message with an empty list); returns how many rows were left out.
        /// </summary>
        internal static int Fit<T>(List<T> rows, long others, long limit = Limit) where T : class
        {
            long total = others; var count = 0;
            while (count < rows.Count)
            {
                var size = Bytes(rows[count]) + 1; // and its comma
                if (total + size > limit) break;
                total += size; count++;
            }
            var left = rows.Count - count;
            if (left > 0) rows.RemoveRange(count, left);
            return left;
        }
    }
}
