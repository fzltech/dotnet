// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Fmverify.Annotations;

namespace CommunityToolkit.HighPerformance.Memory.Internals;

/// <summary>
/// The pure arithmetic used to map a 2D view onto its backing memory.
/// </summary>
/// <remarks>
/// These are deliberately static and call-free so the fmverify symbolic checker can reason about
/// them. The contracts describe the subset the checker proves: views up to 1024x1024 with a pitch
/// up to 1024. Larger views follow the same expression, but their overflow behaviour is outside the
/// proved scope and is caught by the runtime <see cref="OverflowHelper"/> checks.
/// </remarks>
internal static class SliceArithmetic
{
    /// <summary>
    /// Computes the pitch of a slice that starts at the current view's column and is
    /// <paramref name="sliceWidth"/> columns wide.
    /// </summary>
    [Requires("width > 0 && width <= 1024 && pitch >= 0 && pitch <= 1024 && sliceWidth >= 0 && sliceWidth <= width")]
    [Ensures("result == pitch + (width - sliceWidth) && result >= 0")]
    internal static int GetPitch(int width, int pitch, int sliceWidth)
    {
        return pitch + (width - sliceWidth);
    }

    /// <summary>
    /// Adds a column offset to a row offset without overflowing for the proved range.
    /// </summary>
    [Requires("rowOffset >= 0 && rowOffset <= 1000000 && column >= 0 && column <= 1000000")]
    [Ensures("result == rowOffset + column && result >= rowOffset")]
    internal static int GetColumnOffset(int rowOffset, int column)
    {
        return rowOffset + column;
    }

    /// <summary>
    /// Returns the index of the last element in a zero-based sequence of the given length.
    /// </summary>
    [Requires("length > 0")]
    [Ensures("result == length - 1 && result >= 0")]
    internal static int GetLastIndex(int length)
    {
        return length - 1;
    }
}
