using System.Globalization;

namespace Pomp.Internal
{
    /// <summary>The order of a list people pick from by name, the same as the page's <c>byName</c> (ui.tsx): case-insensitive, numbers by value ("item 2" before "item 10").</summary>
    internal static class NameOrder
    {
        public static int Compare(string? left, string? right)
        {
            left ??= ""; right ??= "";
            int i = 0, j = 0;
            while (i < left.Length && j < right.Length)
            {
                var digits = char.IsDigit(left[i]);
                if (digits != char.IsDigit(right[j])) return string.Compare(left.Substring(i), right.Substring(j), CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
                int si = i, sj = j;
                while (i < left.Length && char.IsDigit(left[i]) == digits) i++;
                while (j < right.Length && char.IsDigit(right[j]) == digits) j++;
                string a = left.Substring(si, i - si), b = right.Substring(sj, j - sj);
                int order;
                if (digits)
                {
                    a = a.TrimStart('0'); b = b.TrimStart('0');
                    order = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                }
                else order = string.Compare(a, b, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
                if (order != 0) return order;
            }
            return (left.Length - i).CompareTo(right.Length - j);
        }
    }
}
