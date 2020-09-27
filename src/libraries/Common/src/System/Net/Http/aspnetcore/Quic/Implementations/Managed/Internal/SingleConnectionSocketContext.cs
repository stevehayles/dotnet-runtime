using System.Diagnostics;
using System.Net.Quic.Implementations.MsQuic.Internal;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Quic.Implementations.Managed.Internal
{
    internal sealed class SingleConnectionSocketContext : QuicSocketContext
    {
        private readonly IPEndPoint _remoteEndPoint;
        private readonly ManagedQuicConnection _connection;

        internal SingleConnectionSocketContext(IPEndPoint? localEndpoint, IPEndPoint remoteEndPoint, ManagedQuicConnection connection)
            : base(localEndpoint, remoteEndPoint, connection.IsServer)
        {
            _remoteEndPoint = remoteEndPoint;
            _connection = connection;
        }

        protected override ManagedQuicConnection? FindConnection(QuicReader reader, IPEndPoint sender)
        {
            return _connection;
        }

        protected override void OnSignal()
        {
            Update(_connection);
            UpdateTimeout(_connection.GetNextTimerTimestamp());
        }

        protected override void OnTimeout(long now)
        {
            long oldTimeout = _connection.GetNextTimerTimestamp();

            // timout may have changed since have set it
            if (oldTimeout <= now)
            {
                var origState = _connection.ConnectionState;
                _connection.OnTimeout(now);

                // the connection may have data to send
                Update(_connection, origState);

                long newTimeout = _connection.GetNextTimerTimestamp();
                if (newTimeout == oldTimeout)
                {
                    Debug.Assert(newTimeout != oldTimeout);
                }
                UpdateTimeout(newTimeout);
            }
            else
            {
                // set timer to the current value
                UpdateTimeout(oldTimeout);
            }
        }

        protected override bool OnConnectionStateChanged(ManagedQuicConnection connection, QuicConnectionState newState)
        {
            switch (newState)
            {
                case QuicConnectionState.None:
                    break;
                case QuicConnectionState.Connected:
                    break;
                case QuicConnectionState.Closing:
                    break;
                case QuicConnectionState.Draining:
                    if (!connection.IsServer)
                    {
                        // clients can stop earlier because there is no danger of packets being interpreted as belonging
                        // to a new connection.
                        DetachConnection(connection);
                    }
                    break;
                case QuicConnectionState.Closed:
                    // draining timer elapsed, discard the state
                    DetachConnection(connection);
                    return true;
                default:
                    throw new ArgumentOutOfRangeException(nameof(newState), newState, null);
            }

            return false;
        }

        private class AsyncSocketArgs : SocketAsyncEventArgs
        {
            public AsyncSocketArgs()
            {

            }

            public ResettableCompletionSource<SocketReceiveFromResult> CompletionSource { get; } = new ResettableCompletionSource<SocketReceiveFromResult>();

            protected override void OnCompleted(SocketAsyncEventArgs e)
            {
                CompletionSource.Complete(
                    new SocketReceiveFromResult()
                    {
                        ReceivedBytes = e.SocketError == SocketError.Success ? e.BytesTransferred : 0,
                        RemoteEndPoint = e.RemoteEndPoint!
                    });
            }
        }

        private AsyncSocketArgs _socketReceiveEventArgs = new AsyncSocketArgs();


        protected override int ReceiveFrom(byte[] buffer, ref EndPoint sender)
        {
            sender = _remoteEndPoint;
            // use method without explicit address because we use connected socket
            return Socket.Receive(buffer, SocketFlags.None, out _);
        }

        private ValueTask<SocketReceiveFromResult> ReceiveFromAsyncCore(byte[] buffer, EndPoint sender,
            CancellationToken token)
        {
            _socketReceiveEventArgs.SetBuffer(buffer);
            _socketReceiveEventArgs.SocketFlags = SocketFlags.None;
            _socketReceiveEventArgs.RemoteEndPoint = _remoteEndPoint;

            if (Socket.ReceiveAsync(_socketReceiveEventArgs))
            {
                return _socketReceiveEventArgs.CompletionSource.GetValueTask();
            }

            // use method without explicit address because we use connected socket
            return new ValueTask<SocketReceiveFromResult>(
                new SocketReceiveFromResult
                {
                    ReceivedBytes = _socketReceiveEventArgs.BytesTransferred,
                    RemoteEndPoint = _remoteEndPoint
                });
        }

        protected override Task<SocketReceiveFromResult> ReceiveFromAsync(byte[] buffer, EndPoint sender,
            CancellationToken token)
        {
            return ReceiveFromAsyncCore(buffer, sender, token).AsTask();
        }

        protected override void SendTo(byte[] buffer, int size, EndPoint receiver)
            // use method without explicit address because we use connected socket
            => Socket.Send(buffer.AsSpan(0, size), SocketFlags.None, out _);

        protected internal override void DetachConnection(ManagedQuicConnection connection)
        {
            Debug.Assert(connection.IsClosed);
            Debug.Assert(connection == _connection);
            // only one connection, so we can stop the background worker and free resources
            Stop();
        }
    }
}
