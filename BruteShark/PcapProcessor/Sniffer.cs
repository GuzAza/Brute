﻿using PcapProcessor.Objects;
using SharpPcap;
using SharpPcap.LibPcap;
using SharpPcap.Npcap;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PcapProcessor
{
    public class Sniffer
    {
        public delegate void UdpPacketArivedEventHandler(object sender, UdpPacketArivedEventArgs e);
        public event UdpPacketArivedEventHandler UdpPacketArived;
        public delegate void UdpSessionArrivedEventHandler(object sender, UdpSessionArrivedEventArgs e);
        public event UdpSessionArrivedEventHandler UdpSessionArrived;
        public delegate void TcpPacketArivedEventHandler(object sender, TcpPacketArivedEventArgs e);
        public event TcpPacketArivedEventHandler TcpPacketArived;
        public delegate void TcpSessionArivedEventHandler(object sender, TcpSessionArivedEventArgs e);
        public event TcpSessionArivedEventHandler TcpSessionArrived;

        private TcpSessionsBuilder _tcpSessionsBuilder;
        private UdpStreamBuilder _udpStreamBuilder;

        private Queue<PacketDotNet.Packet> _packets { get; set; }
        private object _packetsQueueLock { get; set; }
        private Task _packetProcessingTask;
        private CancellationTokenSource _cts;

        public bool BuildTcpSessions { get; set; }
        public bool BuildUdpSessions { get; set; }
        public bool PromisciousMode { get; set; }
        public string Filter { get; set; }
        public string SelectedInterface { get; set; }

        public List<string> AvailiableDevicesNames => CaptureDeviceList.Instance.Select(d => (PcapDevice)d).Select(d => d.Interface.FriendlyName).ToList();


        public Sniffer()
        {
            Filter = string.Empty;
            BuildTcpSessions = false;
            BuildUdpSessions = false;
            _tcpSessionsBuilder = new TcpSessionsBuilder();
            _tcpSessionsBuilder.IsLiveCapture = true;
            _udpStreamBuilder = new UdpStreamBuilder();
            _packets = new Queue<PacketDotNet.Packet>();
            _packetsQueueLock = new object();
            _cts = new CancellationTokenSource();
        }

        public void StartSniffing(CancellationToken ct)
        {
            var readTimeoutMilliseconds = 1000;
            _udpStreamBuilder.Clear();
            _tcpSessionsBuilder.Clear();
            _packets.Clear();
            
            var availiableDevices = CaptureDeviceList.Instance;

            if (!AvailiableDevicesNames.Contains(SelectedInterface))
            {
                throw new Exception($"No such device {SelectedInterface}");
            }

            ICaptureDevice selectedDevice = availiableDevices[AvailiableDevicesNames.IndexOf(SelectedInterface)];

            if (selectedDevice is NpcapDevice)
            {
                var nPcap = selectedDevice as NpcapDevice;
                if (PromisciousMode)
                {
                    nPcap.Open(SharpPcap.Npcap.OpenFlags.Promiscuous, readTimeoutMilliseconds);
                }
                else
                {
                    nPcap.Open();
                }

                nPcap.Mode = CaptureMode.Packets;
            }
            else if (selectedDevice is LibPcapLiveDevice)
            {
                var livePcapDevice = selectedDevice as LibPcapLiveDevice;
                livePcapDevice.Open(PromisciousMode ? DeviceMode.Promiscuous : DeviceMode.Normal);
            }
            else
            {
                throw new InvalidOperationException("Unknown device type of " + SelectedInterface.GetType().ToString());
            }

            // Setup capture filter.
            if (Filter != string.Empty)
            {
                selectedDevice.Filter = this.Filter;
            }

            // Register our handler function to the 'packet arrival' event.
            selectedDevice.OnPacketArrival += InsertPacketToQueue;

            // Start the packet procesing thread.
            StartPacketProcessingThread();

            // Start the capturing process
            selectedDevice.StartCapture();

            // Wait for sniffing to be stoped by user.
            WaitForStopSniffingSignal(ct);

            // Stop the capturing process.
            selectedDevice.StopCapture();

            // Waiting on the packet procesing thread to finish.
            StopPacketProcessingThread();

            // Close the pcap device
            selectedDevice.Close();

            /*
            _tcpSessionsBuilder.Sessions.AsParallel().ForAll(session => TcpSessionArrived?.Invoke(this, new TcpSessionArivedEventArgs()
            {
                TcpSession = session
            }));

            _udpStreamBuilder.Sessions.AsParallel().ForAll(session => UdpSessionArrived?.Invoke(this, new UdpSessionArrivedEventArgs()
            {
                UdpSession = session
            }));
            */
        }

        private void StartPacketProcessingThread()
        {
            _cts.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _packetProcessingTask = new Task(() => ProcessPacketsQueue(ct), ct);
            _packetProcessingTask.Start();
        }

        private void StopPacketProcessingThread()
        {
            _cts.Cancel();
            _packetProcessingTask.ConfigureAwait(false);
        }

        private void WaitForStopSniffingSignal(CancellationToken ct)
        {
            while (true)
            {
                Thread.Sleep(1000);

                if (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        private void ProcessPacket(PacketDotNet.Packet packet)
        {
            try
            {
                var tcpPacket = packet.Extract<PacketDotNet.TcpPacket>();
                var udpPacket = packet.Extract<PacketDotNet.UdpPacket>();

                if (udpPacket != null)
                {
                    var ipPacket = (PacketDotNet.IPPacket)udpPacket.ParentPacket;

                    UdpPacketArived?.Invoke(this, new UdpPacketArivedEventArgs
                    {
                        Packet = new UdpPacket
                        {
                            SourcePort = udpPacket.SourcePort,
                            DestinationPort = udpPacket.DestinationPort,
                            SourceIp = ipPacket.SourceAddress.ToString(),
                            DestinationIp = ipPacket.DestinationAddress.ToString(),
                            Data = udpPacket.PayloadData ?? new byte[] { }
                        }
                    });

                    if (this.BuildUdpSessions)
                    {
                        this._udpStreamBuilder.HandlePacket(udpPacket);
                    }
                    
                }
                else if (tcpPacket != null)
                {
                    var ipPacket = (PacketDotNet.IPPacket)tcpPacket.ParentPacket;

                    // Raise event Tcp packet arived event.
                    TcpPacketArived?.Invoke(this, new TcpPacketArivedEventArgs
                    {
                        Packet = new TcpPacket
                        {
                            SourcePort = tcpPacket.SourcePort,
                            DestinationPort = tcpPacket.DestinationPort,
                            SourceIp = ipPacket.SourceAddress.ToString(),
                            DestinationIp = ipPacket.DestinationAddress.ToString(),
                            Data = tcpPacket.PayloadData ?? new byte[] { }
                        }
                    });

                    if (this.BuildTcpSessions)
                    {
                        this._tcpSessionsBuilder.HandlePacket(tcpPacket);
                        _tcpSessionsBuilder.completedSessions.AsParallel().ForAll((session) =>
                        {
                            TcpSessionArrived?.Invoke(this, new TcpSessionArivedEventArgs()
                            {
                                TcpSession = session
                            });
                            _tcpSessionsBuilder.completedSessions.Remove(session);
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                // TODO: handle or throw this
                //Console.WriteLine(ex);
            }
        }

        public static bool CheckCaptureFilter(string filter)
        {
            return PcapDevice.CheckFilter(filter, out string outString);
        }

        private void InsertPacketToQueue(object sender, CaptureEventArgs e)
        {
            var packet = PacketDotNet.Packet.ParsePacket(e.Packet.LinkLayerType, e.Packet.Data);

            lock (_packetsQueueLock)
            {
                _packets.Enqueue(packet);
            }
        }

        private void ProcessPacketsQueue(CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_packetsQueueLock)
                {
                    while (_packets.Count > 0)
                    {
                        ProcessPacket(_packets.Dequeue());

                        if (cancellationToken.IsCancellationRequested) 
                        {
                            return;
                        }
                    }
                }

                Thread.Sleep(1000);
            }
        }

    }
}


