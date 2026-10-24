// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Diffs;

public enum EditKind { Same, Removed, Added }

/// <summary>One element of an edit script: which item, from which side.</summary>
public readonly record struct Edit<T>(EditKind Kind, T Item);

/// <summary>
/// The shortest edit script between two sequences (Eugene Myers, 1986), the algorithm behind git diff.
/// Time O((n+m)·d) for d differences, so similar texts diff fast however long they are.
/// </summary>
public static class Myers
{
    public static IReadOnlyList<Edit<T>> Diff<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;

        // Common prefix and suffix cost nothing and make the search much smaller.
        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && comparer.Equals(a[prefix], b[prefix])) prefix++;
        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix
               && comparer.Equals(a[a.Count - 1 - suffix], b[b.Count - 1 - suffix])) suffix++;

        var result = new List<Edit<T>>(a.Count + b.Count);
        for (var i = 0; i < prefix; i++) result.Add(new(EditKind.Same, a[i]));
        result.AddRange(Middle(a, b, prefix, a.Count - suffix, prefix, b.Count - suffix, comparer));
        for (var i = a.Count - suffix; i < a.Count; i++) result.Add(new(EditKind.Same, a[i]));
        return result;
    }

    private static List<Edit<T>> Middle<T>(
        IReadOnlyList<T> a, IReadOnlyList<T> b, int aStart, int aEnd, int bStart, int bEnd, IEqualityComparer<T> comparer)
    {
        int n = aEnd - aStart, m = bEnd - bStart, max = n + m;
        var edits = new List<Edit<T>>(max);
        if (max == 0) return edits;

        // v[k] = furthest x on diagonal k; one copy per step d to walk back the path.
        var v = new int[2 * max + 2];
        var trace = new List<int[]>();
        var found = false;
        for (var d = 0; d <= max && !found; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                var down = k == -d || (k != d && v[max + k - 1] < v[max + k + 1]);
                var x = down ? v[max + k + 1] : v[max + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && comparer.Equals(a[aStart + x], b[bStart + y])) { x++; y++; }
                v[max + k] = x;
                if (x >= n && y >= m) { found = true; break; }
            }
        }

        // Walk back from (n, m) through the saved states.
        int cx = n, cy = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var vd = trace[d];
            var k = cx - cy;
            var down = k == -d || (k != d && vd[max + k - 1] < vd[max + k + 1]);
            var prevK = down ? k + 1 : k - 1;
            var prevX = vd[max + prevK];
            var prevY = prevX - prevK;
            while (cx > prevX && cy > prevY)
            {
                edits.Add(new(EditKind.Same, a[aStart + cx - 1]));
                cx--; cy--;
            }

            if (d > 0)
            {
                if (down) edits.Add(new(EditKind.Added, b[bStart + cy - 1]));
                else edits.Add(new(EditKind.Removed, a[aStart + cx - 1]));
            }

            cx = prevX;
            cy = prevY;
        }

        edits.Reverse();
        return edits;
    }
}
