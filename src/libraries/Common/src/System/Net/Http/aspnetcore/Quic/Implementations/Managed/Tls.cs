#nullable enable

using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Quic.Implementations.Managed.Internal;
using System.Net.Quic.Implementations.Managed.Internal.OpenSsl;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Net.Quic.Implementations.Managed
{
    /// <summary>
    ///     Class encapsulating TLS related logic and interop.
    /// </summary>
    internal class Tls : IDisposable
    {
        private static readonly unsafe OpenSslQuicMethods.AddHandshakeDataFunc AddHandshakeDelegate = AddHandshakeDataImpl;
        private static readonly unsafe OpenSslQuicMethods.SetEncryptionSecretsFunc SetEncryptionSecretsDelegate = SetEncryptionSecretsImpl;
        private static readonly OpenSslQuicMethods.FlushFlightFunc FlushFlightDelegate = FlushFlightImpl;
        private static readonly OpenSslQuicMethods.SendAlertFunc SendAlertDelegate = SendAlertImpl;

        private static readonly IntPtr _callbacksPtr;

        private static readonly int _managedInterfaceIndex =
            Interop.OpenSslQuic.CryptoGetExNewIndex(Interop.OpenSslQuic.CRYPTO_EX_INDEX_SSL, 0, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        static unsafe Tls()
        {
            _callbacksPtr = Marshal.AllocHGlobal(sizeof(OpenSslQuicMethods.NativeCallbacks));
            *(OpenSslQuicMethods.NativeCallbacks*)_callbacksPtr.ToPointer() = new OpenSslQuicMethods.NativeCallbacks
            {
                setEncryptionSecrets =
                    Marshal.GetFunctionPointerForDelegate(SetEncryptionSecretsDelegate),
                addHandshakeData =
                    Marshal.GetFunctionPointerForDelegate(AddHandshakeDelegate),
                flushFlight =
                    Marshal.GetFunctionPointerForDelegate(FlushFlightDelegate),
                sendAlert = Marshal.GetFunctionPointerForDelegate(SendAlertDelegate)
            };
        }

        internal Tls(GCHandle handle, QuicClientConnectionOptions options, TransportParameters localTransportParams)
            : this(handle, false, localTransportParams)
        {
            Interop.OpenSslQuic.SslSetConnectState(_ssl);
            if (options.ClientAuthenticationOptions?.TargetHost != null)
                Interop.OpenSslQuic.SslSetTlsExHostName(_ssl, options.ClientAuthenticationOptions.TargetHost);

            if (options.ClientAuthenticationOptions != null)
            {
                SetAlpn(options.ClientAuthenticationOptions.ApplicationProtocols);
            }
        }

        internal Tls(GCHandle handle, QuicListenerOptions options, TransportParameters localTransportParameters)
            : this(handle, true, localTransportParameters)
        {
            Interop.OpenSslQuic.SslSetAcceptState(_ssl);

            if (options.CertificateFilePath != null)
                Interop.OpenSslQuic.SslUseCertificateFile(_ssl, options.CertificateFilePath!, SslFiletype.Pem);

            if (options.PrivateKeyFilePath != null)
                Interop.OpenSslQuic.SslUsePrivateKeyFile(_ssl, options.PrivateKeyFilePath, SslFiletype.Pem);

            if (options.ServerAuthenticationOptions != null)
            {
                SetAlpn(options.ServerAuthenticationOptions.ApplicationProtocols);
            }
        }

        private unsafe void SetAlpn(List<SslApplicationProtocol> protos)
        {
            Span<byte> buffer = stackalloc byte[protos.Sum(p => p.Protocol.Length + 1)];
            int offset = 0;
            foreach (var protocol in protos)
            {
                buffer[offset] = (byte) protocol.Protocol.Length;
                protocol.Protocol.Span.CopyTo(buffer.Slice(offset + 1));
                offset += 1 + protocol.Protocol.Length;
            }

            int result = Interop.OpenSslQuic.SslSetAlpnProtos(_ssl,
                new IntPtr(Unsafe.AsPointer(ref MemoryMarshal.AsRef<byte>(buffer))), buffer.Length);
            Debug.Assert(result == 0);
        }

        private unsafe Tls(GCHandle handle, bool isServer, TransportParameters localTransportParams)
        {
            _ssl = Interop.OpenSslQuic.SslCreate();
            Debug.Assert(handle.Target is ManagedQuicConnection);
            Interop.OpenSslQuic.SslSetQuicMethod(_ssl, _callbacksPtr);

            // add the callback as contextual data so we can retrieve it inside the callback
            Interop.OpenSslQuic.SslSetExData(_ssl, _managedInterfaceIndex, GCHandle.ToIntPtr(handle));

            Interop.OpenSslQuic.SslCtrl(_ssl, SslCtrlCommand.SetMinProtoVersion, (long)OpenSslTlsVersion.Tls13,
                IntPtr.Zero);
            Interop.OpenSslQuic.SslCtrl(_ssl, SslCtrlCommand.SetMaxProtoVersion, (long)OpenSslTlsVersion.Tls13,
                IntPtr.Zero);

            // explicitly set allowed suites
            var ciphers = new TlsCipherSuite[]
            {
                TlsCipherSuite.TLS_AES_128_GCM_SHA256,
                TlsCipherSuite.TLS_AES_128_CCM_SHA256,
                TlsCipherSuite.TLS_AES_256_GCM_SHA384,
                // not supported yet
                // TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256
            };
            Interop.OpenSslQuic.SslSetCiphersuites(_ssl, string.Join(":", ciphers));

            // init transport parameters
            byte[] buffer = new byte[1024];
            var writer = new QuicWriter(buffer);
            TransportParameters.Write(writer, isServer, localTransportParams);
            fixed (byte* pData = buffer)
            {
                Interop.OpenSslQuic.SslSetQuicTransportParams(_ssl, pData, new IntPtr(writer.BytesWritten));
            }
        }

        private readonly IntPtr _ssl;

        internal bool IsHandshakeComplete => Interop.OpenSslQuic.SslIsInitFinished(_ssl) == 1;
        public EncryptionLevel WriteLevel { get; private set; }

        public void Dispose()
        {
            // call SslSetQuicMethod(ssl, null) to stop callbacks being called
            Interop.OpenSslQuic.SslSetQuicMethod(_ssl, IntPtr.Zero);
            Interop.OpenSslQuic.SslFree(_ssl);
        }

        internal static EncryptionLevel ToManagedEncryptionLevel(OpenSslEncryptionLevel level)
        {
            return level switch
            {
                OpenSslEncryptionLevel.Initial => EncryptionLevel.Initial,
                OpenSslEncryptionLevel.EarlyData => EncryptionLevel.EarlyData,
                OpenSslEncryptionLevel.Handshake => EncryptionLevel.Handshake,
                OpenSslEncryptionLevel.Application => EncryptionLevel.Application,
                _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
            };
        }

        private static OpenSslEncryptionLevel ToOpenSslEncryptionLevel(EncryptionLevel level)
        {
            var osslLevel = level switch
            {
                EncryptionLevel.Initial => OpenSslEncryptionLevel.Initial,
                EncryptionLevel.Handshake => OpenSslEncryptionLevel.Handshake,
                EncryptionLevel.EarlyData => OpenSslEncryptionLevel.EarlyData,
                EncryptionLevel.Application => OpenSslEncryptionLevel.Application,
                _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
            };
            return osslLevel;
        }

        private static ManagedQuicConnection GetCallbackInterface(IntPtr ssl)
        {
            var addr = Interop.OpenSslQuic.SslGetExData(ssl, _managedInterfaceIndex);
            var callback = (ManagedQuicConnection)GCHandle.FromIntPtr(addr).Target!;

            return callback;
        }

        internal SslError OnDataReceived(EncryptionLevel level, ReadOnlySpan<byte> data)
        {
            int status = Interop.OpenSslQuic.SslProvideQuicData(_ssl, ToOpenSslEncryptionLevel(level), data);
            if (status == 1) return SslError.None;
            return (SslError) Interop.OpenSslQuic.SslGetError(_ssl, status);
        }

        internal SslError DoHandshake()
        {
            if (IsHandshakeComplete)
                return SslError.None;

            int status = Interop.OpenSslQuic.SslDoHandshake(_ssl);

            // update also write level
            WriteLevel = ToManagedEncryptionLevel(Interop.OpenSslQuic.SslQuicWriteLevel(_ssl));

            if (status <= 0)
            {
                return (SslError)Interop.OpenSslQuic.SslGetError(_ssl, status);
            }

            return SslError.None;
        }

        internal TlsCipherSuite GetNegotiatedCipher()
        {
            return Interop.OpenSslQuic.SslGetCipherId(_ssl);
        }

        internal unsafe TransportParameters? GetPeerTransportParameters(bool isServer)
        {
            if (Interop.OpenSslQuic.SslGetPeerQuicTransportParams(_ssl, out byte* data, out IntPtr length) == 0 ||
                length.ToInt32() == 0)
            {
                // nothing received yet, use default values
                return TransportParameters.Default;
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(length.ToInt32());

            new Span<byte>(data, length.ToInt32()).CopyTo(buffer);
            var reader = new QuicReader(buffer.AsMemory(0, length.ToInt32()));

            TransportParameters.Read(reader, !isServer, out var parameters);
            ArrayPool<byte>.Shared.Return(buffer);

            return parameters;
        }

        internal unsafe SslApplicationProtocol GetAlpnProtocol()
        {
            int result = Interop.OpenSslQuic.SslGet0AlpnSelected(_ssl, out IntPtr pString, out int length);
            if (pString != IntPtr.Zero)
            {
                return new SslApplicationProtocol(Marshal.PtrToStringAnsi(pString, length));
            }

            return new SslApplicationProtocol();
        }

        private static unsafe int SetEncryptionSecretsImpl(IntPtr ssl, OpenSslEncryptionLevel level, byte* readSecret,
            byte* writeSecret, UIntPtr secretLen)
        {
            var callback = GetCallbackInterface(ssl);

            var readS = new ReadOnlySpan<byte>(readSecret, (int)secretLen.ToUInt32());
            var writeS = new ReadOnlySpan<byte>(writeSecret, (int)secretLen.ToUInt32());

            return callback.HandleSetEncryptionSecrets(ToManagedEncryptionLevel(level), readS, writeS);
        }

        private static unsafe int AddHandshakeDataImpl(IntPtr ssl, OpenSslEncryptionLevel level, byte* data,
            UIntPtr len)
        {
            var callback = GetCallbackInterface(ssl);

            var span = new ReadOnlySpan<byte>(data, (int)len.ToUInt32());

            return callback.HandleAddHandshakeData(ToManagedEncryptionLevel(level), span);
        }

        private static int FlushFlightImpl(IntPtr ssl)
        {
            var callback = GetCallbackInterface(ssl);

            return callback.HandleFlush();
        }

        private static int SendAlertImpl(IntPtr ssl, OpenSslEncryptionLevel level, byte alert)
        {
            var callback = GetCallbackInterface(ssl);

            return callback.HandleSendAlert(ToManagedEncryptionLevel(level), (TlsAlert)alert);
        }
    }
}
