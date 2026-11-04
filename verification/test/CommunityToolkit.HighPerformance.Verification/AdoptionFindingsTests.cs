using CommunityToolkit.HighPerformance.Helpers;
using Xunit;

namespace CommunityToolkit.HighPerformance.Verification;

public class AdoptionFindingsTests
{
    private struct CountingAction : IAction
    {
        private readonly int[] _counts;

        public CountingAction(int[] counts) => _counts = counts;

        public void Invoke(int i) => System.Threading.Interlocked.Increment(ref _counts[0]);
    }

    [Fact]
    public void For_covers_every_index_in_a_range_that_ends_at_int_MaxValue()
    {
        // 1001 iterations is enough to split across batches on any realistic core count.
        // The last batch's `low + batchSize` can overflow even though `end` is int.MaxValue.
        const int count = 1001;
        int start = int.MaxValue - count;
        int end = int.MaxValue;
        int[] counts = new int[1];

        ParallelHelper.For(start, end, new CountingAction(counts), 1);

        Assert.Equal(count, counts[0]);
    }

    [Fact]
    public void For_covers_every_index_when_a_batch_starts_below_zero()
    {
        const int count = 1001;
        int[] counts = new int[1];

        ParallelHelper.For(-count, 0, new CountingAction(counts), 1);

        Assert.Equal(count, counts[0]);
    }

    [Fact]
    public void For_covers_every_index_in_a_range_that_starts_at_int_MinValue()
    {
        const int count = 1001;
        int start = int.MinValue;
        int end = int.MinValue + count;
        int[] counts = new int[1];

        ParallelHelper.For(start, end, new CountingAction(counts), 1);

        Assert.Equal(count, counts[0]);
    }

    [Fact]
    public void Target_declares_four_proven_symbolic_claims()
    {
        var targetSource = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.AppContext.BaseDirectory,
            "../../../../../../src/CommunityToolkit.HighPerformance"));

        var results = SymbolicMethodChecker.CheckAll(targetSource);

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.IsType<SymbolicCheckResult.Verified>(r));
    }
}
