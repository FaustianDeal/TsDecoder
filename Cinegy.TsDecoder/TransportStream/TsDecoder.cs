// Unity-compatible TsDecoder.cs
// - Removed null-coalescing assignments (C# 8+)
// - Converted to C# 7.3 syntax (no '?.', no '??=', no target-typed 'new')
// - Removed Encoding.RegisterProvider for Unity compatibility

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Cinegy.TsDecoder.Descriptors;
using Cinegy.TsDecoder.Tables;

namespace Cinegy.TsDecoder.TransportStream
{
    public class TsDecoder
    {
        public ProgramAssociationTable ProgramAssociationTable { get { return _patFactory.ProgramAssociationTable; } }
        public ServiceDescriptionTable ServiceDescriptionTable { get { return _sdtFactory.ServiceDescriptionTable; } }
        public ServiceDescriptionTable OtherServiceDescriptionTable { get { return _otherSdtFactory.ServiceDescriptionTable; } }
        public NetworkInformationTable NetworkInformationTable { get { return _nitFactory.NetworkInformationTable; } }
        public EventInformationTable EventInformationTable { get { return _eitFactory.EventInformationTable; } }
        public SpliceInfoTable SpliceInfoTable { get { return _sitFactory.SpliceInfoTable; } }

        public List<ProgramMapTable> ProgramMapTables { get; private set; }

        private ProgramAssociationTableFactory _patFactory;
        private ServiceDescriptionTableFactory _sdtFactory;
        private ServiceDescriptionTableFactory _otherSdtFactory;
        private List<ProgramMapTableFactory> _pmtFactories;

        private EventInformationTableFactory _eitFactory;
        private NetworkInformationTableFactory _nitFactory;
        private SpliceInfoTableFactory _sitFactory;

        private TsPacketFactory _packetFactory;

        public delegate void TableChangeEventHandler(object sender, TableChangedEventArgs args);

        public int CorruptedTablePackets()
        {
            int corruptedPkts = 0;
            corruptedPkts += _patFactory.CorruptedPackets;
            corruptedPkts += _eitFactory.CorruptedPackets;
            corruptedPkts += _nitFactory.CorruptedPackets;
            corruptedPkts += _otherSdtFactory.CorruptedPackets;
            corruptedPkts += _sdtFactory.CorruptedPackets;
            corruptedPkts += _sitFactory.CorruptedPackets;

            foreach (var pmt in _pmtFactories)
                corruptedPkts += pmt.CorruptedPackets;

            return corruptedPkts;
        }

        public TsDecoder()
        {
            SetupFactories();
        }

        public void AddData(byte[] data, bool rentPackets = true)
        {
            if (_packetFactory == null)
                _packetFactory = new TsPacketFactory();

            if (rentPackets)
            {
                int pktCount;
                var tsPackets = _packetFactory.GetRentedTsPacketsFromData(data, out pktCount);

                if (tsPackets == null)
                    throw new InvalidDataException("Provided data buffer did not contain any TS packets");

                var subset = new TsPacket[pktCount];
                Array.Copy(tsPackets, subset, pktCount);
                AddPackets(subset);
                // ReturnTsPackets not implemented in Unity-safe version; no-op
            }
            else
            {
                var tsPackets = _packetFactory.GetTsPacketsFromData(data);
                if (tsPackets == null)
                    throw new InvalidDataException("Provided data buffer did not contain any TS packets");
                AddPackets(tsPackets);
            }
        }

        public void AddPackets(IEnumerable<TsPacket> newPackets)
        {
            if (newPackets == null) return;
            foreach (var pkt in newPackets)
                AddPacket(pkt);
        }

        public void AddPacket(TsPacket pkt)
        {
            try
            {
                if (pkt.TransportErrorIndicator) return;

                switch (pkt.Pid)
                {
                    case (ushort)PidType.PatPid:
                        _patFactory.AddPacket(pkt);
                        break;
                    case (ushort)PidType.SdtBatPid:
                        _sdtFactory.AddPacket(pkt);
                        _otherSdtFactory.AddPacket(pkt);
                        break;
                    case (ushort)PidType.EitPid:
                        _eitFactory.AddPacket(pkt);
                        break;
                    case 2048:
                        _sitFactory.AddPacket(pkt);
                        break;
                    default:
                        CheckPmt(pkt);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Exception in AddPacket: " + ex.Message);
            }
        }

        private void SetupFactories()
        {
            _patFactory = new ProgramAssociationTableFactory();
            _patFactory.TableChangeDetected += _patFactory_TableChangeDetected;
            _pmtFactories = new List<ProgramMapTableFactory>(16);
            ProgramMapTables = new List<ProgramMapTable>(16);

            _sdtFactory = new ServiceDescriptionTableFactory();
            _sdtFactory.TableChangeDetected += _sdtFactory_TableChangeDetected;

            _otherSdtFactory = new ServiceDescriptionTableFactory();
            _otherSdtFactory.CurrentMux = false;
            _otherSdtFactory.TableChangeDetected += _otherSdtFactory_TableChangeDetected;

            _eitFactory = new EventInformationTableFactory();
            _eitFactory.TableChangeDetected += _eitFactory_TableChangeDetected;

            _nitFactory = new NetworkInformationTableFactory();
            _nitFactory.TableChangeDetected += _nitFactory_TableChangeDetected;

            _sitFactory = new SpliceInfoTableFactory();
        }

        private void _patFactory_TableChangeDetected(object sender, TransportStreamEventArgs e)
        {
            OnTableChangeDetected(sender, new TableChangedEventArgs { Message = "PAT changed", TablePid = e.TsPid });
        }

        private void _sdtFactory_TableChangeDetected(object sender, TransportStreamEventArgs e)
        {
            OnTableChangeDetected(sender, new TableChangedEventArgs { Message = "SDT changed", TablePid = e.TsPid });
        }

        private void _otherSdtFactory_TableChangeDetected(object sender, TransportStreamEventArgs e)
        {
            OnTableChangeDetected(sender, new TableChangedEventArgs { Message = "SDT (other) changed", TablePid = e.TsPid });
        }

        private void _eitFactory_TableChangeDetected(object sender, TransportStreamEventArgs e)
        {
            OnTableChangeDetected(sender, new TableChangedEventArgs { Message = "EIT changed", TablePid = e.TsPid });
        }

        private void _nitFactory_TableChangeDetected(object sender, TransportStreamEventArgs e)
        {
            OnTableChangeDetected(sender, new TableChangedEventArgs { Message = "NIT changed", TablePid = e.TsPid });
        }

        private void CheckPmt(TsPacket pkt)
        {
            if (ProgramAssociationTable == null) return;
            if (pkt.Pid == (ushort)PidType.NitPid)
            {
                _nitFactory.AddPacket(pkt);
                return;
            }

            bool contains = false;
            foreach (var pid in ProgramAssociationTable.Pids)
            {
                if (pid == pkt.Pid)
                {
                    contains = true;
                    break;
                }
            }
            if (!contains) return;

            ProgramMapTableFactory selected = null;
            foreach (var f in _pmtFactories)
            {
                if (f.TablePid == pkt.Pid)
                {
                    selected = f;
                    break;
                }
            }

            if (selected == null)
            {
                selected = new ProgramMapTableFactory();
                selected.TableChangeDetected += _pmtFactory_TableChangeDetected;
                _pmtFactories.Add(selected);
            }

            selected.AddPacket(pkt);
        }

        private void _pmtFactory_TableChangeDetected(object sender, TransportStreamEventArgs e)
        {
            OnTableChangeDetected(sender, new TableChangedEventArgs { Message = "PMT changed", TablePid = e.TsPid });
        }

        public event TableChangeEventHandler TableChangeDetected;

        private void OnTableChangeDetected(object sender, TableChangedEventArgs args)
        {
            var handler = TableChangeDetected;
            if (handler != null)
                handler(sender, args);
        }
    }
}
