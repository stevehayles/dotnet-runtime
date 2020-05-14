using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net.Quic.Implementations.Managed.Internal;
using System.Net.Quic.Implementations.Managed.Internal.Crypto;
using System.Net.Quic.Implementations.Managed.Internal.Headers;

namespace System.Net.Quic.Implementations.Managed
{
    internal sealed partial class ManagedQuicConnection
    {
        /// <summary>
        ///     Current value of the key phase bit for key update detection.
        /// </summary>
        private bool _currentKeyPhase;

        private CryptoSeal? _nextSendSeal;

        private CryptoSeal? _nextRecvSeal;

        private bool _doKeyUpdate;

        internal void ReceiveData(QuicReader reader, IPEndPoint sender, QuicSocketContext.RecvContext ctx)
        {
            if (_isDraining)
            {
                // discard any incoming data
                return;
            }

            var buffer = reader.Buffer;

            while (reader.BytesLeft > 0)
            {
                var status = ReceiveOne(reader, ctx);

                switch (status)
                {
                    case ProcessPacketResult.DropPacket:
                        if (NetEventSource.IsEnabled) NetEventSource.PacketDropped(this, reader.Buffer.Length);
                        break;
                    case ProcessPacketResult.Ok:
                        // An endpoint restarts its idle timer when a packet from its peer is
                        // received and processed successfully.
                        _ackElicitingSentSinceLastReceive = false;
                        RestartIdleTimer(ctx.Timestamp);
                        break;
                }

                // ReceiveOne will adjust the buffer length once it is known, thus the length here skips the
                // just processed coalesced packet
                buffer = buffer.Slice(reader.Buffer.Length);
                reader.Reset(buffer);
            }
        }

        private ProcessPacketResult ReceiveOne(QuicReader reader, QuicSocketContext.RecvContext context)
        {
            byte first = reader.Peek();

            if (HeaderHelpers.IsLongHeader(first))
            {
                // first, just parse the header without validation, we will validate after lifting header protection
                if (!LongPacketHeader.Read(reader, out var header))
                {
                    return ProcessPacketResult.DropPacket;
                }

                if (HeaderHelpers.HasPacketTypeEncryption(header.PacketType))
                {
                    var pnSpace = GetPacketNumberSpace(GetEncryptionLevel(header.PacketType));

                    if (!UnprotectLongHeaderPacket(reader, ref header, out var headerData, pnSpace))
                    {
                        return ProcessPacketResult.DropPacket;
                    }

                    switch (header.PacketType)
                    {
                        case PacketType.Initial:
                            if (_isServer)
                            {
                                // check UDP datagram size, by now the reader's buffer end is aligned with the UDP datagram end.
                                // TODO-RZ: in rare cases when initial is not the first of the coalesced packets this can falsely close the connection.
                                // as the QUIC does only recommend, not mandate order of the coalesced packets
                                if (reader.Buffer.Length < QuicConstants.MinimumClientInitialDatagramSize)
                                {
                                    return CloseConnection(TransportErrorCode.ProtocolViolation,
                                        QuicError.InitialPacketTooShort);
                                }
                            }

                            // Servers may not send Token in Initial packets
                            if (!_isServer && !headerData.Token.IsEmpty)
                            {
                                return CloseConnection(
                                    TransportErrorCode.ProtocolViolation,
                                    QuicError.UnexpectedToken);
                            }

                            // after client receives the first packet (which is either initial or retry), it must
                            // use the connection id supplied by the server, but should ignore any further changes to CID,
                            // see [TRANSPORT] Section 7.2
                            if (!_isServer &&
                                GetPacketNumberSpace(EncryptionLevel.Initial).LargestReceivedPacketNumber >= 0)
                            {
                                // protection keys are not affected by this change
                                DestinationConnectionId = new ConnectionId(
                                    header.SourceConnectionId.ToArray(),
                                    DestinationConnectionId!.SequenceNumber,
                                    DestinationConnectionId.StatelessResetToken);
                            }

                            // continue processing
                            goto case PacketType.Handshake;
                        case PacketType.Handshake:
                        case PacketType.ZeroRtt:
                            // TODO-RZ: validate reserved bits etc.
                            if (headerData.Length > reader.BytesLeft)
                            {
                                return ProcessPacketResult.DropPacket;
                            }

                            // total length of the packet is known and checked during header parsing.
                            // Adjust the buffer to the range belonging to the current packet.
                            reader.Reset(reader.Buffer.Slice(0, reader.BytesRead + (int)headerData.Length),
                                reader.BytesRead);
                            ProcessPacketResult result = ReceiveCommon(reader, header, headerData, pnSpace, context);

                            if (result == ProcessPacketResult.Ok && _isServer && header.PacketType == PacketType.Handshake)
                            {
                                // RFC: A server stops sending and processing Initial packets when it receives its first
                                // Handshake packet
                                DropPacketNumberSpace(PacketSpace.Initial);
                            }

                            return result;

                        // other types handled elsewhere
                        default:
                            throw new InvalidOperationException("Unreachable");
                    }
                }

                // clients SHOULD ignore fixed bit when receiving version negotiation
                if (!header.FixedBit && _isServer && header.PacketType == PacketType.VersionNegotiation ||
                    // TODO-RZ: following checks should be moved into SocketContext
                    SourceConnectionId != null &&
                    !header.DestinationConnectionId.SequenceEqual(SourceConnectionId!.Data) ||
                    header.Version != QuicVersion.Draft27)
                {
                    return ProcessPacketResult.DropPacket;
                }

                switch (header.PacketType)
                {
                    case PacketType.Retry:
                        return ReceiveRetry(reader, header, context);
                    case PacketType.VersionNegotiation:
                        return ReceiveVersionNegotiation(reader, header, context);
                    // other types handled elsewhere
                    default:
                        throw new InvalidOperationException("Unreachable");
                }
            }
            else // short header
            {
                var pnSpace = GetPacketNumberSpace(EncryptionLevel.Application);
                if (pnSpace.RecvCryptoSeal == null)
                {
                    // Decryption keys are not available yet
                    return ProcessPacketResult.DropPacket;
                }

                // read first without validation
                if (!ShortPacketHeader.Read(reader, _localConnectionIdCollection, out var header))
                {
                    return ProcessPacketResult.DropPacket;
                }

                // remove header protection
                int pnOffset = reader.BytesRead;
                pnSpace.RecvCryptoSeal.UnprotectHeader(reader.Buffer.Span, pnOffset);

                // refresh the first byte
                header = new ShortPacketHeader(reader.Buffer.Span[0], header.DestinationConnectionId);

                // TODO-RZ: validate reserved bits etc.
                if (!header.FixedBit)
                {
                    return ProcessPacketResult.DropPacket;
                }

                int pnOffset1 = reader.BytesRead;
                PacketType packetType = PacketType.OneRtt;
                int payloadLength = reader.BytesLeft;

                CryptoSeal recvSeal = pnSpace.RecvCryptoSeal;

                // the peer MUST not initiate key update before handshake is done,
                // so the seals should already exist, but we should still check against an attack.
                if (header.KeyPhaseBit != _currentKeyPhase && HandshakeConfirmed)
                {
                    // An endpoint SHOULD retain old keys so that packets sent by its peer
                    // prior to receiving the key update can be processed.  Discarding old
                    // keys too early can cause delayed packets to be discarded.  Discarding
                    // packets will be interpreted as packet loss by the peer and could
                    // adversely affect performance.

                    // keys will be updated next time a packet is sent
                    _doKeyUpdate = true;
                    if (_nextRecvSeal == null)
                    {
                        // create updated keys

                        _nextSendSeal = CryptoSeal.UpdateSeal(pnSpace.SendCryptoSeal!);
                        _nextRecvSeal = CryptoSeal.UpdateSeal(pnSpace.RecvCryptoSeal!);
                    }

                    recvSeal = _nextRecvSeal;
                }

                return ReceiveProtectedFrames(reader, pnSpace, pnOffset1, payloadLength, packetType, recvSeal,
                    context);
            }
        }

        private bool UnprotectLongHeaderPacket(QuicReader reader, ref LongPacketHeader header, out SharedPacketData headerData, PacketNumberSpace pnSpace)
        {
            // initialize protection keys if necessary
            if (_isServer && header.PacketType == PacketType.Initial &&
                pnSpace.RecvCryptoSeal == null)
            {
                // clients destination connection Id is ours source connection Id
                SourceConnectionId = new ConnectionId(header.DestinationConnectionId.ToArray(), 0,
                    StatelessResetToken.Random());
                DestinationConnectionId = new ConnectionId(header.SourceConnectionId.ToArray(), 0,
                    StatelessResetToken.Random());

                _localConnectionIdCollection.Add(SourceConnectionId);
                DeriveInitialProtectionKeys(SourceConnectionId.Data);
            }

            if (pnSpace.RecvCryptoSeal == null || // Decryption keys are not available yet
                // skip additional data so the reader position is on packet number offset
                !SharedPacketData.Read(reader, header.FirstByte, out headerData))
            {
                headerData = default;
                return false;
            }

            // remove header protection
            int pnOffset = reader.BytesRead;
            pnSpace.RecvCryptoSeal!.UnprotectHeader(reader.Buffer.Span, pnOffset);

            byte unprotectedFirst = reader.Buffer.Span[0];

            // update headers with the new unprotected first byte data
            header = new LongPacketHeader(unprotectedFirst,
                header.Version, header.DestinationConnectionId, header.SourceConnectionId);
            headerData = new SharedPacketData(unprotectedFirst, headerData.Token, headerData.Length);
            return true;
        }

        private ProcessPacketResult ReceiveRetry(QuicReader reader, in LongPacketHeader header,
            QuicSocketContext.RecvContext context)
        {
            // TODO-RZ: Retry not supported
            throw new NotImplementedException();
        }

        private ProcessPacketResult ReceiveVersionNegotiation(QuicReader reader, in LongPacketHeader header,
            QuicSocketContext.RecvContext context)
        {
            // TODO-RZ: Version negotiation not supported
            throw new NotImplementedException();
        }

        private ProcessPacketResult ReceiveCommon(QuicReader reader, in LongPacketHeader header,
            in SharedPacketData headerData, PacketNumberSpace pnSpace, QuicSocketContext.RecvContext context)
        {
            int pnOffset = reader.BytesRead;
            int payloadLength = (int)headerData.Length;
            PacketType packetType = header.PacketType;

            return ReceiveProtectedFrames(reader, pnSpace, pnOffset, payloadLength, packetType, pnSpace.RecvCryptoSeal!,
                context);
        }

        private ProcessPacketResult ReceiveProtectedFrames(QuicReader reader, PacketNumberSpace pnSpace, int pnOffset,
            int payloadLength,
            PacketType packetType, CryptoSeal seal, QuicSocketContext.RecvContext context)
        {
            if (!seal.DecryptPacket(reader.Buffer.Span, pnOffset, payloadLength,
                pnSpace.LargestReceivedPacketNumber))
            {
                // decryption failed, drop the packet.
                reader.Advance(payloadLength);
                return ProcessPacketResult.DropPacket;
            }

            // TODO-RZ: read in a better way
            int pnLength = HeaderHelpers.GetPacketNumberLength(reader.Buffer.Span[0]);
            reader.TryReadTruncatedPacketNumber(pnLength, out int truncatedPn);

            long packetNumber = QuicPrimitives.DecodePacketNumber(pnSpace.LargestReceivedPacketNumber,
                truncatedPn, pnLength);

            if (pnSpace.ReceivedPacketNumbers.Contains(packetNumber))
            {
                // already processed or outside congestion window
                // Note: there may be false positives if the packet number is 64 lesser than largest received, but that
                // should not occur often due to flow control / congestion window. Besides the data can be retransmitted
                // in following packet.
                return ProcessPacketResult.Ok;
            }

            if (pnSpace.LargestReceivedPacketNumber < packetNumber)
            {
                pnSpace.LargestReceivedPacketNumber = packetNumber;
                pnSpace.LargestReceivedPacketTimestamp = context.Timestamp;
            }

            pnSpace.UnackedPacketNumbers.Add(packetNumber);
            pnSpace.ReceivedPacketNumbers.Add(packetNumber);

            return ProcessFramesWithoutTag(reader, packetType, context);
        }

        private ProcessPacketResult ProcessFramesWithoutTag(QuicReader reader, PacketType packetType,
            QuicSocketContext.RecvContext context)
        {
            // HACK: we do not want to try processing the AEAD integrity tag as if it were frames.
            var originalSegment = reader.Buffer;
            int originalBytesRead = reader.BytesRead;
            int tagLength = GetPacketNumberSpace(GetEncryptionLevel(packetType)).RecvCryptoSeal!.TagLength;
            int length = reader.BytesLeft - tagLength;
            reader.Reset(originalSegment.Slice(originalBytesRead, length));
            var retval = ProcessFrames(reader, packetType, context);
            reader.Reset(originalSegment);
            return retval;
        }

        internal void SendData(QuicWriter writer, out IPEndPoint? receiver, QuicSocketContext.SendContext ctx)
        {
            receiver = _remoteEndpoint;

            if (ctx.Timestamp > _closingPeriodEnd)
            {
                SignalConnectionClose();
                return;
            }

            if (ctx.Timestamp > _idleTimeout)
            {
                // TODO-RZ: Force close the connection with error
                SignalConnectionClose();
            }

            if (_isDraining)
            {
                // While otherwise identical to the closing state, an endpoint in the draining state MUST NOT
                // send any packets
                return;
            }

            if (ctx.Timestamp >= Recovery.LossRecoveryTimer)
            {
                Recovery.OnLossDetectionTimeout(_tls.IsHandshakeComplete, ctx.Timestamp);
            }

            var level = GetWriteLevel();
            var origMemory = writer.Buffer;
            int written = 0;

            while (true)
            {
                if (GetPacketNumberSpace(level).SendCryptoSeal == null)
                {
                    // Secrets have not been derived yet, can't send anything
                    break;
                }

                if (SendOne(writer, level, ctx))
                {
                    ctx.StartNextPacket();
                }
                else
                {
                    // no more data to send.
                    ctx.SentPacket.Reset();
                    break;
                }

                written += writer.BytesWritten;

                // 0-RTT packets do not have Length, so they may not be coalesced
                if (level == EncryptionLevel.Application)
                    break;

                var nextLevel = GetWriteLevel();

                // only coalesce packets in ascending encryption level
                if (nextLevel <= level)
                    break;

                level = nextLevel;
                writer.Reset(writer.Buffer.Slice(writer.BytesWritten));
            }

            writer.Reset(origMemory, written);
        }

        private bool SendOne(QuicWriter writer, EncryptionLevel level, QuicSocketContext.SendContext context)
        {
            (PacketType packetType, PacketSpace packetSpace) = level switch
            {
                EncryptionLevel.Initial => (PacketType.Initial, PacketSpace.Initial),
                EncryptionLevel.EarlyData => (PacketType.ZeroRtt, PacketSpace.Application),
                EncryptionLevel.Handshake => (PacketType.Handshake, PacketSpace.Handshake),
                EncryptionLevel.Application => (PacketType.OneRtt, PacketSpace.Application),
                _ => throw new InvalidOperationException()
            };

            var pnSpace = GetPacketNumberSpace(level);
            var recoverySpace = Recovery.GetPacketNumberSpace(packetSpace);
            var seal = pnSpace.SendCryptoSeal!;

            // process lost packets
            var lostPackets = recoverySpace.LostPackets;
            while (lostPackets.TryDequeue(out var lostPacket))
            {
                OnPacketLost(lostPacket, pnSpace);
                context.ReturnPacket(lostPacket);
            }

            int maxPacketLength = (int)(_tls.IsHandshakeComplete
                // Limit maximum size so that it can be always encoded into the reserved 2 bytes of `payloadLengthSpan`
                ? Math.Min((1 << 14) - 1, _peerTransportParameters.MaxPacketSize)
                // use minimum size for packets during handshake
                : QuicConstants.MinimumClientInitialDatagramSize);

            bool isProbePacket = recoverySpace.RemainingLossProbes > 0;

            // make sure we send something if a probe is wanted
            _pingWanted |= isProbePacket;

            // TODO-RZ: Although ping should always work, the actual algorithm for probe packet is following
            // if (!isServer && GetPacketNumberSpace(EncryptionLevel.Application).RecvCryptoSeal == null)
            // {
            // TODO-RZ: Client needs to send an anti-deadlock packet:
            // }
            // else
            // {
            // TODO-RZ: PTO. Send new data if available, else retransmit old data.
            // If neither is available, send single PING frame.
            // }

            // limit outbound packet by available congestion window
            // probe packets are not limited by congestion window
            if (!isProbePacket)
            {
                maxPacketLength = Math.Min(maxPacketLength, Recovery.GetAvailableCongestionWindowBytes());
            }

            if (maxPacketLength <= seal.TagLength)
            {
                // unable to send any useful data anyway.
                return false;
            }

            (int truncatedPn, int pnLength) = pnSpace.GetNextPacketNumber(recoverySpace.LargestAckedPacketNumber);
            WritePacketHeader(writer, packetType, pnLength);

            // for non 1-RTT packets, we reserve 2 bytes which we will overwrite once total payload length is known
            var payloadLengthSpan = writer.Buffer.Span.Slice(writer.BytesWritten - 2, 2);

            int pnOffset = writer.BytesWritten;
            writer.WriteTruncatedPacketNumber(pnLength, truncatedPn);

            int written = writer.BytesWritten;
            var origBuffer = writer.Buffer;

            writer.Reset(origBuffer.Slice(0, Math.Min(origBuffer.Length, maxPacketLength - seal.TagLength)), written);
            WriteFrames(writer, packetType, level, context);
            writer.Reset(origBuffer, writer.BytesWritten);

            if (writer.BytesWritten == written)
            {
                // no data to send
                // TODO-RZ: we might be able to detect this sooner
                writer.Reset(writer.Buffer);
                Debug.Assert(!_pingWanted);
                return false;
            }

            // after this point it is certain that the packet will be sent, commit pending key update
            if (_doKeyUpdate)
            {
                pnSpace.SendCryptoSeal = _nextSendSeal;
                pnSpace.RecvCryptoSeal = _nextRecvSeal;
                _nextRecvSeal = null;
                _nextSendSeal = null;
                _currentKeyPhase = !_currentKeyPhase;
                _doKeyUpdate = false;
            }

            if (!_isServer && packetType == PacketType.Initial)
            {
                // TODO-RZ: It would be more efficient to add padding only to the last packet sent when coalescing packets.

                // Pad client initial packets to the minimum size
                int paddingLength = QuicConstants.MinimumClientInitialDatagramSize - seal.TagLength -
                                    writer.BytesWritten;
                if (paddingLength > 0)
                    // zero bytes are equivalent to PADDING frames
                    writer.GetWritableSpan(paddingLength).Clear();

                context.SentPacket.InFlight = true; // padding implies InFlight
            }

            // pad the packet payload so that it can always be sampled for header protection
            if (writer.BytesWritten - pnOffset < seal.PayloadSampleLength + 4)
            {
                writer.GetWritableSpan(seal.PayloadSampleLength + 4 - writer.BytesWritten + pnOffset).Clear();
                context.SentPacket.InFlight = true; // padding implies InFlight
            }

            // reserve space for AEAD integrity tag
            writer.GetWritableSpan(seal.TagLength);
            int payloadLength = writer.BytesWritten - pnOffset;

            // fill in the payload length retrospectively
            if (packetType != PacketType.OneRtt)
            {
                QuicPrimitives.WriteVarInt(payloadLengthSpan, payloadLength, 2);
            }

            seal.EncryptPacket(writer.Buffer.Span, pnOffset, payloadLength, truncatedPn);
            seal.ProtectHeader(writer.Buffer.Span, pnOffset);

            // remember what we sent in this packet
            context.SentPacket.PacketNumber = pnSpace.NextPacketNumber;
            context.SentPacket.BytesSent = writer.BytesWritten;
            context.SentPacket.TimeSent = context.Timestamp;

            if (isProbePacket)
            {
                recoverySpace.RemainingLossProbes--;
            }

            Recovery.OnPacketSent(GetPacketSpace(packetType), context.SentPacket, _tls.IsHandshakeComplete);
            pnSpace.NextPacketNumber++;
            NetEventSource.PacketSent(this, context.SentPacket.BytesSent);

            if (context.SentPacket.AckEliciting && !_ackElicitingSentSinceLastReceive)
            {
                RestartIdleTimer(context.Timestamp);
                _ackElicitingSentSinceLastReceive = true;
            }

            if (!_isServer && packetType == PacketType.Handshake)
            {
                // RFC: A client stops sending and processing Initial packets when it sends its first Handshake packet
                DropPacketNumberSpace(PacketSpace.Initial);
            }

            return true;
        }

        private void WritePacketHeader(QuicWriter writer, PacketType packetType, int pnLength)
        {
            if (packetType == PacketType.OneRtt)
            {
                // 1-RTT packets are the only ones using short header
                // TODO-RZ: implement spin
                // TODO-RZ: implement key update fully
                bool keyPhase = _doKeyUpdate
                    ? !_currentKeyPhase
                    : _currentKeyPhase;
                ShortPacketHeader.Write(writer,
                    new ShortPacketHeader(false, keyPhase, pnLength, DestinationConnectionId!));
            }
            else
            {
                LongPacketHeader.Write(writer, new LongPacketHeader(
                    packetType,
                    pnLength,
                    version,
                    DestinationConnectionId!.Data,
                    SourceConnectionId!.Data));

                // HACK: reserve 2 bytes for payload length and overwrite it later
                SharedPacketData.Write(writer, new SharedPacketData(
                    writer.Buffer.Span[0],
                    ReadOnlySpan<byte>.Empty,
                    1000 /*arbitrary number with 2-byte encoding*/));
            }
        }
    }
}
