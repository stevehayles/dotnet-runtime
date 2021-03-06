// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Net.Quic.Implementations.Managed.Internal.Crypto;
using System.Net.Quic.Implementations.Managed.Internal.Streams;

namespace System.Net.Quic.Implementations.Managed.Internal
{
    /// <summary>
    ///     Class for aggregating all connection data for a single packet number space.
    /// </summary>
    internal class PacketNumberSpace
    {
        public PacketNumberSpace(PacketType packetType, PacketSpace packetSpace)
        {
            PacketType = packetType;
            PacketSpace = packetSpace;
        }

        /// <summary>
        ///     Type of the packets belonging to this connection.
        /// </summary>
        internal PacketType PacketType { get; }

        /// <summary>
        ///     Largest packet number received from the peer.
        /// </summary>
        internal long LargestReceivedPacketNumber { get; set; } = -1;

        /// <summary>
        ///     Timestamp when packet with <see cref="LargestReceivedPacketNumber"/> was received.
        /// </summary>
        internal long LargestReceivedPacketTimestamp { get; set; }

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
        internal SendStream CryptoSendStream { get; } = new SendStream(long.MaxValue);

        /// <summary>
        ///     Inbound messages from CRYPTO frames.
        /// </summary>
        internal ReceiveStream CryptoReceiveStream { get; } = new ReceiveStream(long.MaxValue);

        /// <summary>
        ///     Gets packet number and it's minimum safe encoding length for the next packet sent.
        /// </summary>
        /// <returns>Truncated packet number and it's length.</returns>
        internal (int truncatedPn, int pnLength) GetNextPacketNumber(long largestAcked)
        {
            return QuicPrimitives.EncodePacketNumber(largestAcked, NextPacketNumber);
        }

        /// <summary>
        ///     Timestamp when last ack frame was sent.
        /// </summary>
        internal long LastAckSentTimestamp { get; set; }

        /// <summary>
        ///     Timestamp when the next ACK frame should be sent.
        /// </summary>
        internal long NextAckTimer { get; set; } = long.MaxValue;

        /// <summary>
        ///     Packet space of this instace.
        /// </summary>
        public PacketSpace PacketSpace { get; }
    }
}
