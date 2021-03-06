// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.Quic
{
    public static class QuicImplementationProviders
    {
        public static Implementations.QuicImplementationProvider Mock { get; } = new Implementations.Mock.MockImplementationProvider();
        public static Implementations.QuicImplementationProvider MsQuic { get; } = new Implementations.MsQuic.MsQuicImplementationProvider();
        public static Implementations.QuicImplementationProvider Managed { get; } = new Implementations.Managed.ManagedQuicImplementationProvider();
        public static Implementations.QuicImplementationProvider Default => GetDefaultProvider();

        private static Implementations.QuicImplementationProvider GetDefaultProvider()
        {
            if (Environment.GetEnvironmentVariable("USE_MSQUIC") != null)
            {
                return MsQuic;
            }

            return Managed;
        }
    }
}
