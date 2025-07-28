// Unity-compatible Pes.cs
// - Removed System.Buffers.ArrayPool
// - Replaced with direct byte[] allocation
// - Removed C# 9+ features (like 'is not' and 'is null')

using System;
using System.Collections.Generic;

namespace Cinegy.TsDecoder.TransportStream
{
    public class Pes
    {
        public const uint DefaultPacketStartCodePrefix = 0x000001;

        public uint PacketStartCodePrefix { get; set; }
        public byte StreamId { get; set; }
        public ushort PesPacketLength { get; set; }
        public OptionalPes OptionalPesHeader { get; set; }
        public byte[] Data { get; set; }

        public static readonly IList<PesStreamTypes> SimplePesTypes = new[]
        {
            PesStreamTypes.ProgramStreamMap,
            PesStreamTypes.PaddingStream,
            PesStreamTypes.PrivateStream2,
            PesStreamTypes.ECMStream,
            PesStreamTypes.EMMStream,
            PesStreamTypes.ProgramStreamDirectory,
            PesStreamTypes.DSMCCStream,
            PesStreamTypes.H2221TypeEStream
        };

        private ushort _pesBytes;
        private byte[] _data;

        public Pes(PesStreamTypes type, byte[] payload, OptionalPes optionalPesHeader = null)
        {
            PacketStartCodePrefix = DefaultPacketStartCodePrefix;
            StreamId = (byte)type;
            PesPacketLength = (ushort)payload.Length;

            switch (type)
            {
                case PesStreamTypes.PaddingStream:
                    Data = new byte[payload.Length];
                    for (int i = 0; i < Data.Length; i++) Data[i] = 0xFF;
                    break;

                case PesStreamTypes.ProgramStreamMap:
                case PesStreamTypes.PrivateStream2:
                case PesStreamTypes.ECMStream:
                case PesStreamTypes.EMMStream:
                case PesStreamTypes.ProgramStreamDirectory:
                case PesStreamTypes.DSMCCStream:
                case PesStreamTypes.H2221TypeEStream:
                    Data = new byte[payload.Length];
                    Buffer.BlockCopy(payload, 0, Data, 0, payload.Length);
                    break;

                default:
                    if (optionalPesHeader == null)
                        throw new ArgumentException("This stream type requires an OptionalPesHeader");

                    OptionalPesHeader = optionalPesHeader;
                    PesPacketLength = (ushort)(payload.Length + optionalPesHeader.PesHeaderLength + 3);
                    Data = new byte[payload.Length];
                    Buffer.BlockCopy(payload, 0, Data, 0, payload.Length);
                    break;
            }
        }

        public Pes(TsPacket packet)
        {
            PacketStartCodePrefix = (uint)((packet.Payload[0] << 16) + (packet.Payload[1] << 8) + packet.Payload[2]);
            StreamId = packet.Payload[3];
            PesPacketLength = (ushort)((packet.Payload[4] << 8) + packet.Payload[5]);

            int size = (PesPacketLength > 0) ? PesPacketLength + 6 : 1024 * 1024 * 10;
            _data = new byte[size];
            Buffer.BlockCopy(packet.Payload, 0, _data, 0, packet.PayloadLen);
            _pesBytes += (ushort)packet.PayloadLen;
        }

        public bool HasAllBytes()
        {
            if (PesPacketLength == 0) return true;
            return _pesBytes >= PesPacketLength + 6 && PesPacketLength > 0;
        }

        public bool Add(TsPacket packet)
        {
            if (packet.PayloadUnitStartIndicator) return false;
            if (packet.Payload == null || packet.PayloadLen <= 0) return false;

            int copyLen = Math.Min(packet.PayloadLen, _data.Length - _pesBytes);
            Buffer.BlockCopy(packet.Payload, 0, _data, _pesBytes, copyLen);
            _pesBytes += (ushort)copyLen;
            return true;
        }

        public byte[] GetDataFromPes()
        {
            var data = new byte[6 + PesPacketLength];
            data[0] = 0x0; data[1] = 0x0; data[2] = 0x1;
            data[3] = StreamId;
            data[4] = (byte)(PesPacketLength >> 8);
            data[5] = (byte)(PesPacketLength & 0xFF);

            if (SimplePesTypes.Contains((PesStreamTypes)StreamId) || StreamId == (byte)PesStreamTypes.PaddingStream)
            {
                Buffer.BlockCopy(Data, 0, data, 6, PesPacketLength);
                return data;
            }

            data[6] = 0b10000000;
            data[6] += (byte)(OptionalPesHeader.ScramblingControl << 4);
            data[6] += (byte)(OptionalPesHeader.Priority ? 0x08 : 0);
            data[6] += (byte)(OptionalPesHeader.DataAlignmentIndicator ? 0x04 : 0);
            data[6] += (byte)(OptionalPesHeader.Copyright ? 0x02 : 0);
            data[6] += (byte)(OptionalPesHeader.OriginalOrCopy ? 0x01 : 0);

            data[8] += OptionalPesHeader.PesHeaderLength;

            var pos = 9;
            if (OptionalPesHeader.OptionalFields != null && OptionalPesHeader.OptionalFields.Length > 0)
            {
                Buffer.BlockCopy(OptionalPesHeader.OptionalFields, 0, data, pos, OptionalPesHeader.OptionalFields.Length);
                pos += OptionalPesHeader.OptionalFields.Length;
            }

            Buffer.BlockCopy(Data, 0, data, pos, PesPacketLength - 3);
            return data;
        }

        public bool Decode()
        {
            if (_data == null || !HasAllBytes()) return false;

            if (!SimplePesTypes.Contains((PesStreamTypes)StreamId))
            {
                OptionalPesHeader = new OptionalPes
                {
                    MarkerBits = (byte)((_data[6] >> 6) & 0x03),
                    ScramblingControl = (byte)((_data[6] >> 4) & 0x03),
                    Priority = (_data[6] & 0x08) == 0x08,
                    DataAlignmentIndicator = (_data[6] & 0x04) == 0x04,
                    Copyright = (_data[6] & 0x02) == 0x02,
                    OriginalOrCopy = (_data[6] & 0x01) == 0x01,
                    PtsdtsIndicator = (byte)((_data[7] >> 6) & 0x03),
                    EscrFlag = (_data[7] & 0x20) == 0x20,
                    EsRateFlag = (_data[7] & 0x10) == 0x10,
                    DsmTrickModeFlag = (_data[7] & 0x08) == 0x08,
                    AdditionalCopyInfoFlag = (_data[7] & 0x04) == 0x04,
                    CrcFlag = (_data[7] & 0x02) == 0x02,
                    ExtensionFlag = (_data[7] & 0x01) == 0x01,
                    PesHeaderLength = _data[8]
                };
            }

            Data = new byte[_pesBytes];
            Buffer.BlockCopy(_data, 0, Data, 0, _pesBytes);
            _data = null; // was using ArrayPool before

            return true;
        }
    }
}