using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;

namespace System.Net.Quic.Implementations.Managed.Internal.Crypto
{
    internal static class HeaderHelpers
    {
        public static bool IsLongHeader(byte firstByte)
        {
            return (firstByte & 0x80) != 0;
        }
    }
}
