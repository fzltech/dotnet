// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Fmverify.Annotations;

namespace CommunityToolkit.HighPerformance.Helpers;

/// <summary>
/// The pure arithmetic used by <see cref="ParallelHelper"/>'s batch loop.
/// </summary>
/// <remarks>
/// Kept static and call-free so fmverify can prove the non-negative branch. The negative branch
/// uses <see cref="long"/> arithmetic because a difference of two <see cref="int"/> values can be
/// larger than <see cref="int.MaxValue"/>.
/// </remarks>
internal static class ParallelArithmetic
{
    /// <summary>
    /// Computes the exclusive stop index of a batch without overflowing when the batch's end
    /// reaches <see cref="int.MaxValue"/>.
    /// </summary>
    [Requires("low >= 0")]
    [Requires("low <= end")]
    [Requires("batchSize > 0")]
    [Ensures("result <= end")]
    [Ensures("result >= low")]
    internal static int GetBatchStop(int low, int batchSize, int end)
    {
        return end - low < batchSize ? end : low + batchSize;
    }
}
