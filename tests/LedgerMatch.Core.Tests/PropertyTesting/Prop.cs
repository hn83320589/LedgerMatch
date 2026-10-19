namespace LedgerMatch.Core.Tests.PropertyTesting;

/// <summary>
/// Tiny property-test runner: each case gets its own <see cref="Random"/> derived from a fixed seed,
/// so a failure message names the exact case to replay.
/// </summary>
public static class Prop
{
    public static void ForAll(int seed, int cases, Action<Random> property)
    {
        for (var i = 0; i < cases; i++)
        {
            // Not HashCode.Combine: it is randomised per process and would make failures unreproducible.
            var caseSeed = unchecked((seed * 7919) + i);
            try
            {
                property(new Random(caseSeed));
            }
            catch (Exception ex)
            {
                throw new PropertyFailedException($"Property failed at case {i} (seed {seed}, case seed {caseSeed}): {ex.Message}", ex);
            }
        }
    }

    /// <summary>Fisher–Yates shuffle into a new list.</summary>
    public static List<T> Shuffle<T>(this Random random, IEnumerable<T> source)
    {
        var list = source.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }
}

public sealed class PropertyFailedException(string message, Exception inner) : Exception(message, inner);
