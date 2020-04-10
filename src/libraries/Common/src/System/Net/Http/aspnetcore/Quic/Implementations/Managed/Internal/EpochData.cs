#nullable enable

using System.Collections.Generic;
using System.Net.Quic.Implementations.Managed.Internal.Buffers;
using System.Net.Quic.Implementations.Managed.Internal.Crypto;
using System.Runtime.CompilerServices;

namespace System.Net.Quic.Implementations.Managed.Internal
{
    /// <summary>
    ///     Class for aggregating all connection data for a single epoch.
    /// </summary>
    internal class EpochData
    {
        public EpochData(PacketEpoch epoch)
        {
            Epoch = epoch;
        }

        /// <summary>
        ///     Encryption level of this epoch
        /// </summary>
        internal PacketEpoch Epoch { get; }

        /// <summary>
        ///     Largest packet number received from the peer.
        /// </summary>
        internal long LargestReceivedPacketNumber { get; set; }

        /// <summary>
        ///     Timestamp when packet with <see cref="LargestReceivedPacketNumber"/> was received.
        /// </summary>
        internal DateTime LargestReceivedPacketTimestamp { get; set; }

        /// <summary>
        ///     Number for the next packet to be send with.
        /// </summary>
        internal long NextPacketNumber { get; set; }

        /// <summary>
        ///     Received packet numbers which an ack frame needs to be sent to the peer.
        /// </summary>
        internal RangeSet UnackedPacketNumbers { get; } = new RangeSet();

        /// <summary>
        ///     Set of all received packet numbers.
        /// </summary>
        internal PacketNumberWindow ReceivedPacketNumbers;

        /// <summary>
        ///     Flag that next time packets for sending are requested, an ack frame should be added, because an ack eliciting frame was received meanwhile.
        /// </summary>
        internal bool AckElicited { get; set; }

        /// <summary>
        ///     CryptoSeal for encryption of the outbound data.
        /// </summary>
        internal CryptoSeal? SendCryptoSeal { get; set; }

        /// <summary>
        ///     CryptoSeal for decryption of inbound data.
        /// </summary>
        internal CryptoSeal? RecvCryptoSeal { get; set; }

        /// <summary>
        ///     Outbound messages to be carried in CRYPTO frames.
        /// </summary>
        internal OutboundBuffer CryptoOutboundStream { get; } = new OutboundBuffer(long.MaxValue);

        /// <summary>
        ///     Inbound messages from CRYPTO frames.
        /// </summary>
        internal InboundBuffer CryptoInboundBuffer { get; } = new InboundBuffer(long.MaxValue);

        /// <summary>
        ///     All sent packets, for which we are still awaiting acknowledgement.
        /// </summary>
        internal SortedList<long, ManagedQuicConnection.SentPacket> PacketsInFlight { get; } = new SortedList<long, ManagedQuicConnection.SentPacket>();

        /// <summary>
        ///     Gets packet number and it's minimum safe encoding length for the next packet sent.
        /// </summary>
        /// <returns>Truncated packet number and it's length.</returns>
        internal (int truncatedPn, int pnLength) GetNextPacketNumber()
        {
            int pnLength = QuicPrimitives.GetPacketNumberByteCount(LargestReceivedPacketNumber, NextPacketNumber);
            return ((int) NextPacketNumber, pnLength);
        }
    }
}
