using System.Linq;
using Xunit;
using RaceLib;

namespace RaceLib.Tests
{
    internal static class RaceNumberingInvariant
    {
        /// <summary>
        /// The contract every round must satisfy: its valid races, ordered by
        /// RaceNumber, are exactly 1, 2, 3 ... N - no gaps, no duplicates. This is
        /// what produces the "Round-Race" labels (e.g. "4-1", "4-2", "4-3") shown in
        /// the UI; a gap shows up as "4-1, 4-2, 4-4".
        /// </summary>
        public static void AssertContiguous(EventManager em, Round round)
        {
            int[] numbers = em.RaceManager.GetRaces(round)
                .Where(r => r.Valid)
                .Select(r => r.RaceNumber)
                .OrderBy(n => n)
                .ToArray();

            int[] expected = Enumerable.Range(1, numbers.Length).ToArray();

            Assert.True(
                numbers.SequenceEqual(expected),
                $"Race numbers in the round should be contiguous 1..{numbers.Length} " +
                $"but were [{string.Join(", ", numbers)}].");
        }
    }
}
