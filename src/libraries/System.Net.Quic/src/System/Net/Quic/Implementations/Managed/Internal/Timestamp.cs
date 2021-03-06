// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace System.Net.Quic.Implementations.Managed.Internal
{
    /// <summary>
    ///     Helper class for managing timestamps.
    /// </summary>
    internal static class Timestamp
    {
        private static readonly long TicksPerMillisecond = Stopwatch.Frequency / 1000;
        public static long Now => Stopwatch.GetTimestamp();

        public static long FromMilliseconds(long milliseconds) => TicksPerMillisecond * milliseconds;
        public static long FromMicroseconds(long microseconds) => TicksPerMillisecond * microseconds / 1000;

        public static long GetMilliseconds(long ticks) => ticks / TicksPerMillisecond;
        public static long GetMicroseconds(long ticks) => ticks * 1000 / TicksPerMillisecond;

        public static double GetMicrosecondsDouble(long ticks) => ticks * 1000.0 / TicksPerMillisecond;

        public static double GetMillisecondsDouble(long ticks) => ticks / (double) TicksPerMillisecond;
    }
}
