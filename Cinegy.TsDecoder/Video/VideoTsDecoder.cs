// Unity-compatible VideoTsDecoder.cs
// - Converted 'is not' pattern to C# 7.3 compatible 'as' check
// - Removed range operators and updated to use manual slicing
// - Note: Methods like GetSelectedPmt or GetFirstEsStreamForProgramNumber must be implemented in your TsDecoder wrapper

using System;
using Cinegy.TsDecoder.DataAccess;
using Cinegy.TsDecoder.Tables;
using Cinegy.TsDecoder.TransportStream;

namespace Cinegy.TsDecoder.Video
{
    public class VideoTsDecoder
    {
        private Pes _currentVideoPes;
        private bool _foundPps;
        private bool _foundSps;

        public long LastPts { get; private set; }

        public bool FoundSpsAndPps => _foundPps && _foundSps;

        public VideoTsService TsService { get; private set; }

        public ushort ProgramNumber { get; private set; }

        public ushort StreamType { get; private set; } = 0;

        public bool PreserveSourceData { get; private set; }

        public VideoTsDecoder()
        {
            TsService = new VideoTsService();
            TsService.OnVideoNalUnitsReady += TsService_OnVideoNalUnitsReady;
        }

        public VideoTsDecoder(int streamType, ushort programNumber = 0)
        {
            StreamType = (ushort)streamType;
            ProgramNumber = programNumber;
            TsService = new VideoTsService();
            TsService.OnVideoNalUnitsReady += TsService_OnVideoNalUnitsReady;
        }

        private void TsService_OnVideoNalUnitsReady(object sender, NalUnitReadyEventArgs args)
        {
            foreach (var nalUnit in args.NalUnits)
            {
                var h264Nal = nalUnit as H264NalUnit;
                if (h264Nal == null) continue;

                switch (h264Nal.UnitType)
                {
                    case H264NalUnitType.PictureParameterSet:
                        _foundPps = true;
                        break;
                    case H264NalUnitType.SequenceParameterSet:
                        if (_foundSps) continue;
                        _foundSps = true;
                        var sps = new H264SeqParamSet();
                        sps.Decode(h264Nal.RbspData);
                        break;
                }
            }

            if (FoundSpsAndPps)
                TsService.OnVideoNalUnitsReady -= TsService_OnVideoNalUnitsReady;
        }

        public bool FindVideoService(TsDecoder tsDecoder, out EsInfo esStreamInfo)
        {
            if (tsDecoder == null) throw new InvalidOperationException("Null reference to TS Decoder");

            esStreamInfo = null;

            lock (tsDecoder)
            {
                if (ProgramNumber == 0)
                {
                    var pmt = TsDecoderExtensions.GetSelectedPmt(tsDecoder, ProgramNumber);
                    if (pmt != null)
                    {
                        ProgramNumber = pmt.ProgramNumber;
                    }
                }

                if (ProgramNumber == 0) return false;

                TsService.ProgramNumber = ProgramNumber;

                if (StreamType > 0)
                {
                    esStreamInfo = TsDecoderExtensions.GetFirstEsStreamForProgramNumber(tsDecoder, ProgramNumber, StreamType);
                }
                else
                {
                    esStreamInfo = TsDecoderExtensions.GetFirstEsStreamForProgramNumber(tsDecoder, ProgramNumber, 0x1B);
                    if (esStreamInfo == null)
                    {
                        esStreamInfo = TsDecoderExtensions.GetFirstEsStreamForProgramNumber(tsDecoder, ProgramNumber, 0x24);
                        if (esStreamInfo != null)
                        {
                            StreamType = 0x24;
                            Console.WriteLine("Found HEVC stream");
                        }
                    }
                    else
                    {
                        StreamType = 0x1B;
                        Console.WriteLine("Found H264 stream");
                    }
                }

                return esStreamInfo != null;
            }
        }

        private void Setup(TsDecoder tsDecoder)
        {
            EsInfo esStreamInfo;
            if (FindVideoService(tsDecoder, out esStreamInfo))
            {
                Setup(esStreamInfo.ElementaryPid);
            }
        }

        public void Setup(ushort videoPid)
        {
            TsService.VideoPid = videoPid;
        }

        public void AddPacket(TsPacket tsPacket, TransportStream.TsDecoder tsDecoder = null)
        {
            if (TsService == null || TsService.VideoPid == 0)
            {
                if (tsDecoder != null)
                {
                    Setup(tsDecoder);
                }
            }

            if (tsPacket.Pid != TsService.VideoPid) return;

            if (tsPacket.PayloadUnitStartIndicator)
            {
                if (tsPacket.PesHeader != null && tsPacket.PesHeader.Pts > -1)
                    LastPts = tsPacket.PesHeader.Pts;

                if (_currentVideoPes != null)
                {
                    _currentVideoPes.Decode();
                    TsService.AddData(_currentVideoPes, tsPacket.PesHeader, StreamType);
                }
                _currentVideoPes = new Pes(tsPacket);
            }
            else
            {
                if (_currentVideoPes != null)
                    _currentVideoPes.Add(tsPacket);
            }
        }
    }

    // These extension methods are stubs and need proper implementation
    public static class TsDecoderExtensions
    {
        public static ProgramMapTable GetSelectedPmt(TsDecoder decoder, ushort programNumber)
        {
            if (decoder.ProgramMapTables == null) return null;
            foreach (var pmt in decoder.ProgramMapTables)
            {
                if (pmt.ProgramNumber == programNumber)
                    return pmt;
            }
            return null;
        }

        public static EsInfo GetFirstEsStreamForProgramNumber(TsDecoder decoder, ushort programNumber, int streamType)
        {
            var pmt = GetSelectedPmt(decoder, programNumber);
            if (pmt == null || pmt.EsStreams == null) return null;
            foreach (var es in pmt.EsStreams)
            {
                if (es.StreamType == streamType)
                    return es;
            }
            return null;
        }
    }
}
