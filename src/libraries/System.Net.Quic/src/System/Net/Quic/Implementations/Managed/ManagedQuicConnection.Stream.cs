// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Net.Quic.Implementations.Managed.Internal;
using System.Net.Quic.Implementations.Managed.Internal.Frames;
using System.Net.Quic.Implementations.Managed.Internal.Streams;
using System.Threading;

namespace System.Net.Quic.Implementations.Managed
{
    internal partial class ManagedQuicConnection
    {
        private struct ConnectionFlowControlLimits
        {
            private long _maxData;

            /// <summary>
            ///     Maximum amount of data the endpoint is allowed to send.
            /// </summary>
            internal long MaxData => _maxData;

            internal void UpdateMaxData(long value)
            {
                _maxData = Math.Max(_maxData, value);
            }

            internal void AddMaxData(long value)
            {
                Interlocked.Add(ref _maxData, value);
            }

            /// <summary>
            ///     Maximum number of bidirectional streams the endpoint is allowed to open.
            /// </summary>
            internal long MaxStreamsBidi { get; private set; }

            internal void UpdateMaxStreamsBidi(long value)
            {
                MaxStreamsBidi = Math.Max(MaxStreamsBidi, value);
            }

            /// <summary>
            ///     Maximum number of unidirectional streams the endpoint is allowed to open.
            /// </summary>
            internal long MaxStreamsUni { get; private set; }

            internal void UpdateMaxStreamsUni(long value)
            {
                MaxStreamsUni = Math.Max(MaxStreamsUni, value);
            }
        }

        /// <summary>
        ///     True if packet with <see cref="MaxDataFrame"/> is waiting for acknowledgement.
        /// </summary>
        private bool MaxDataFrameSent { get; set; }

        /// <summary>
        ///     Sum of maximum offsets of data sent across all streams.
        /// </summary>
        private long SentData { get; set; }

        /// <summary>
        ///     Sum of maximum offsets of data received across all streams.
        /// </summary>
        private long ReceivedData { get; set; }

        /// <summary>
        ///     Number of bidirectional streams explicitly opened by this endpoint.
        /// </summary>
        private int _bidirStreamsOpened;

        /// <summary>
        ///     Number of unidirectional streams explicitly opened by this endpoint.
        /// </summary>
        private int _uniStreamsOpened;

        /// <summary>
        ///     Opens a new outbound stream with lowest available stream id.
        /// </summary>
        /// <param name="unidirectional">True if the stream should be unidirectional.</param>
        /// <returns></returns>
        internal ManagedQuicStream OpenStream(bool unidirectional)
        {
            var type = StreamHelpers.GetLocallyInitiatedType(IsServer, unidirectional);
            ref int counter = ref (unidirectional ? ref _uniStreamsOpened : ref _bidirStreamsOpened);
            long limit = unidirectional ? _sendLimits.MaxStreamsUni : _sendLimits.MaxStreamsBidi;

            // atomically increment the respective counter
            int priorCounter = Volatile.Read(ref counter);
            bool success;
            do
            {
                if (priorCounter == limit)
                {
                    // TODO-RZ: use messages from string resources
                    throw new QuicException("Cannot open stream");
                }

                int interlockedResult = Interlocked.CompareExchange(ref counter, priorCounter + 1, priorCounter);
                success = interlockedResult == priorCounter;
                priorCounter = interlockedResult;
            } while (!success);

            // priorCounter now holds the index of the newly opened stream
            return _streams.GetOrCreateStream(StreamHelpers.ComposeStreamId(type, priorCounter), _localTransportParameters, _peerTransportParameters, true, this);
        }

        /// <summary>
        ///     Gets a stream with given id. Use in cases where you are sure the stream exists.
        /// </summary>
        /// <param name="streamId">Id of the stream.</param>
        /// <returns>The stream associated with provided id.</returns>
        private ManagedQuicStream GetStream(long streamId)
        {
            return _streams[streamId];
        }

        /// <summary>
        ///     Tries to get the stream with given id. Creates also all streams of the same type with lower id. Returns
        ///     false if creating the remote initiated stream would violate stream limits imposed by this endpoint.
        /// </summary>
        /// <param name="streamId">Id of the stream to get or create.</param>
        /// <param name="stream">Contains the result stream, or null if operation failed.</param>
        /// <returns></returns>
        private bool TryGetOrCreateStream(long streamId, out ManagedQuicStream? stream)
        {
            // check whether the stream can be opened based on local limits

            long index = StreamHelpers.GetStreamIndex(streamId);
            bool local = StreamHelpers.IsLocallyInitiated(IsServer, streamId);
            var param = local
                ? _sendLimits
                : _receiveLimits;

            long limit = StreamHelpers.IsBidirectional(streamId)
                ? param.MaxStreamsBidi
                : param.MaxStreamsUni;

            if (index >= limit)
            {
                stream = null;
                return false;
            }

            stream = _streams.GetOrCreateStream(streamId, _localTransportParameters, _peerTransportParameters, local, this);
            return true;
        }

        internal ManagedQuicStream? AcceptStream()
        {
            _streams.IncomingStreams.Reader.TryRead(out var stream);
            return stream;
        }

        internal void OnStreamDataWritten(ManagedQuicStream stream)
        {
            // no need to ping if the thread is already spinning
            if (stream.SendStream!.IsFlushable)
            {
                var doPing = _streams.MarkFlushable(stream);

                if (doPing)
                {
                    _socketContext!.WakeUp();
                }
            }
        }

        internal void OnStreamDataRead(ManagedQuicStream stream, int bytesRead)
        {
            _receiveLimits.AddMaxData(bytesRead);
            if (stream.ReceiveStream!.ShouldUpdateMaxData())
            {
                OnStreamStateUpdated(stream);
            }
        }

        internal void OnStreamStateUpdated(ManagedQuicStream stream)
        {
            _streams.MarkForUpdate(stream);
            _socketContext!.WakeUp();
        }
    }
}
