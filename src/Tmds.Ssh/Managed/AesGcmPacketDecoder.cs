using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Tmds.Ssh.Managed
{
    sealed class AesGcmPacketDecoder : IPacketDecoder
    {
        private const int AesBlockSize = 16;

        private readonly AesGcm _aesGcm;
        private readonly byte[] _iv;
        private readonly SequencePool _sequencePool;
        private readonly int _tagLength;

        public AesGcmPacketDecoder(SequencePool sequencePool, byte[] key, byte[] iv, int tagLength)
        {
            _iv = iv;
            _tagLength = tagLength;
            _aesGcm = new AesGcm(key, tagLength);
            _sequencePool = sequencePool;
        }

        public void Dispose()
        {
            _aesGcm.Dispose();
        }

        public bool TryDecodePacket(Sequence receiveBuffer, uint sequenceNumber, int maxLength, out Packet packet)
        {
            packet = new Packet(null);

            // Wait for the unencrypted length.
            if (receiveBuffer.Length < 4)
            {
                return false;
            }

            // Verify the packet length isn't too long and
            // the ciphertext is a multiple of the cipher block size.
            uint packet_length = new SequenceReader(receiveBuffer).ReadUInt32();
            if (packet_length > maxLength || (packet_length % AesBlockSize) != 0)
            {
                ThrowHelper.ThrowProtocolPacketTooLong();
            }
            int packetLength = (int)packet_length;

            // Wait for the full encrypted packet.
            int tagLength = _tagLength;
            int total_length = packetLength + tagLength + 4;
            if (receiveBuffer.Length < total_length)
            {
                return false;
            }

            ReadOnlySpan<byte> nonce = _iv;
            ReadOnlySequence<byte> receiveBufferROSequence = receiveBuffer.AsReadOnlySequence().Slice(0, total_length);
            ReadOnlySpan<byte> received = receiveBufferROSequence.IsSingleSegment ? receiveBufferROSequence.FirstSpan :
                                                                                    receiveBufferROSequence.ToArray(); // TODO: avoid allocation.
            ReadOnlySpan<byte> associatedData = received.Slice(0, 4);
            ReadOnlySpan<byte> ciphertext = received.Slice(4, packetLength);
            ReadOnlySpan<byte> tag = received.Slice(4 + packetLength, tagLength);

            Sequence decoded = _sequencePool.RentSequence();
            int decodedLength = total_length - tagLength;
            Span<byte> span = decoded.AllocGetSpan(decodedLength);
            Span<byte> plaintext = span.Slice(4, packetLength);

            associatedData.CopyTo(span); // packet length
            _aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, associatedData); // decrypted text
            decoded.AppendAlloced(decodedLength);
            receiveBuffer.Remove(total_length);

            IncrementIV();

            packet = new Packet(decoded);
            return true;
        }

        private void IncrementIV()
        {
            // With AES-GCM, the 12-octet IV is broken into two fields: a 4-octet
            // fixed field and an 8-octet invocation counter field.  The invocation
            // field is treated as a 64-bit integer and is incremented after each
            // invocation of AES-GCM to process a binary packet.
            Span<byte> invocationCounter = _iv.AsSpan(4, 8);
            ulong count = BinaryPrimitives.ReadUInt64BigEndian(invocationCounter);
            BinaryPrimitives.WriteUInt64BigEndian(invocationCounter, count + 1);
        }
    }
}