// Unity-compatible TsPacketFactory.cs with GetRentedTsPacketsFromData restored
// Removed ArrayPool, but kept API signature

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Cinegy.TsDecoder.TransportStream
{
    public class TsPacketFactory
    {
        private const byte SyncByte = 0x47;
        private ulong _lastPcr;
        private ulong _lastOpcr;

        private byte[] _residualData;
        private int _residualDataSz;

        public const int TsPacketFixedSize = 188;
        public const int MaxAdaptationFieldSize = 183;

        public long TotalDataProcessed { get; private set; }
        public long TotalCorruptedTsPackets { get; private set; }

        public event TsPacketReadyEventHandler TsPacketReady;
        public delegate void TsPacketReadyEventHandler(object sender, TsPacketReadyEventArgs args);

        public void PushData(byte[] data, int dataSize = 0, bool retainPayload = true, bool preserveSourceData = false)
        {
            if (dataSize == 0) dataSize = data.Length;
            TotalDataProcessed += dataSize;
            var packets = GetTsPacketsFromData(data, dataSize, retainPayload, preserveSourceData);
            foreach (var tsPacket in packets)
                OnTsPacketReadyDetected(tsPacket);
        }

        public TsPacket[] GetTsPacketsFromData(byte[] data, int dataSize = 0, bool retainPayload = true, bool preserveSourceData = false)
        {
            return GetTsPacketsFromData(data, dataSize, retainPayload, preserveSourceData, out _);
        }

        // ✅ Restored method for TsDecoder.cs compatibility
        public TsPacket[] GetRentedTsPacketsFromData(byte[] data, out int packetCount)
        {
            return GetTsPacketsFromData(data, data.Length, true, false, out packetCount);
        }

        protected virtual void OnTsPacketReadyDetected(TsPacket tsPacket)
        {
            var handler = TsPacketReady;
            if (handler != null)
            {
                var args = new TsPacketReadyEventArgs { TsPacket = tsPacket };
                handler(this, args);
            }
        }

        private TsPacket[] GetTsPacketsFromData(byte[] data, int dataSize, bool retainPayload, bool preserveSourceData, out int packetCounter)
        {
            try
            {
                if (dataSize == 0) dataSize = data.Length;
                TotalDataProcessed += dataSize;

                if (_residualData != null)
                {
                    var combinedData = new byte[dataSize + _residualDataSz];
                    Buffer.BlockCopy(_residualData, 0, combinedData, 0, _residualDataSz);
                    Buffer.BlockCopy(data, 0, combinedData, _residualDataSz, dataSize);
                    data = combinedData;
                    dataSize += _residualDataSz;
                    _residualData = null;
                    _residualDataSz = 0;
                }

                var maxPackets = dataSize / TsPacketFixedSize;
                var tsPackets = new TsPacket[maxPackets];
                packetCounter = 0;

                var start = FindSync(data, 0, ref dataSize);
                while (start >= 0 && dataSize - start >= TsPacketFixedSize)
                {
                    var tsPacket = new TsPacket();
                    tsPacket.SourceData = new byte[TsPacketFixedSize];
                    Buffer.BlockCopy(data, start, tsPacket.SourceData, 0, TsPacketFixedSize);

                    if (retainPayload)
                    {
                        tsPacket.Payload = new byte[184];
                        Buffer.BlockCopy(data, start + 4, tsPacket.Payload, 0, 184);
                    }

                    tsPackets[packetCounter++] = tsPacket;
                    start += TsPacketFixedSize;
                    if (start >= dataSize || data[start] != SyncByte) break;
                }

                if (start < dataSize)
                {
                    _residualDataSz = dataSize - start;
                    _residualData = new byte[_residualDataSz];
                    Buffer.BlockCopy(data, start, _residualData, 0, _residualDataSz);
                }

                return tsPackets;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Exception in GetTsPacketsFromData: " + ex.Message);
                packetCounter = 0;
                return new TsPacket[0];
            }
        }

        private static int FindSync(IList<byte> tsData, int offset, ref int dataLength)
        {
            for (int i = offset; i < dataLength; i++)
            {
                if (tsData[i] == SyncByte)
                {
                    return i;
                }
            }
            return -1;
        }
    }

    public class TsPacketReadyEventArgs : EventArgs
    {
        public TsPacket TsPacket { get; set; }
    }
}
