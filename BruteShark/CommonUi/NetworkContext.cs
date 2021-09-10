﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CommonUi
{
    public class NetworkContext
    {
        private Dictionary<string, HashSet<int>> _openPorts;
        private HashSet<PcapAnalyzer.DnsNameMapping> _dnsMappings;
        private HashSet<PcapAnalyzer.NetworkConnection> _connections;

        public NetworkContext()
        {
            _openPorts = new Dictionary<string, HashSet<int>>();
            _dnsMappings = new HashSet<PcapAnalyzer.DnsNameMapping>();
            _connections = new HashSet<PcapAnalyzer.NetworkConnection>();
        }

        public void HandleDnsNameMapping(PcapAnalyzer.DnsNameMapping dnsNameMapping)
        {
            _dnsMappings.Add(dnsNameMapping);
        }

        public void HandleNetworkConection(PcapAnalyzer.NetworkConnection networkConnection)
        {
            // Create network nodes if needed.
            if (_connections.Add(networkConnection))
            {
                if (!_openPorts.ContainsKey(networkConnection.Source))
                {
                    _openPorts[networkConnection.Source] = new HashSet<int>();
                }
                if (!_openPorts.ContainsKey(networkConnection.Destination))
                {
                    _openPorts[networkConnection.Destination] = new HashSet<int>();
                }
            }

            // Update open ports.
            _openPorts[networkConnection.Source].Add(networkConnection.SrcPort);
            _openPorts[networkConnection.Destination].Add(networkConnection.DestPort);
        }

        public string GetNodeData(string ipAddress)
        {
            return JsonConvert.SerializeObject(new NetworkNode()
            {
                IpAddress = ipAddress,
                OpenPorts = _openPorts[ipAddress],
                DnsMappings = _dnsMappings.Where(d => d.Destination == ipAddress).Select(d => d.Query).ToHashSet()
            });
        }

    }

    public static class Extensions
    {
        public static HashSet<T> ToHashSet<T>(
            this IEnumerable<T> source,
            IEqualityComparer<T> comparer = null)
        {
            return new HashSet<T>(source, comparer);
        }
    }

}
