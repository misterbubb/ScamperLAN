using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ScamperLAN;

internal readonly record struct LanInterface(IPAddress Address, int PrefixLength, int Index, bool Radmin)
{
    internal bool Contains(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var left = Address.GetAddressBytes();
        var right = address.GetAddressBytes();
        int remaining = PrefixLength;
        for (int i = 0; i < left.Length && remaining > 0; i++, remaining -= 8)
        {
            int mask = 0xff << (8 - Math.Min(remaining, 8)) & 0xff;
            if ((left[i] & mask) != (right[i] & mask))
            {
                return false;
            }
        }
        return true;
    }

    internal IPAddress Broadcast()
    {
        var bytes = Address.GetAddressBytes();
        for (int i = 0; i < bytes.Length; i++)
        {
            int networkBits = Math.Clamp(PrefixLength - i * 8, 0, 8);
            bytes[i] |= (byte)(0xff >> networkBits);
        }
        return new IPAddress(bytes);
    }
}

internal static class LanNetworks
{
    internal static LanInterface[] FindInterfaces()
    {
        var found = new List<LanInterface>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
                !adapter.Supports(NetworkInterfaceComponent.IPv4))
            {
                continue;
            }

            bool radmin = adapter.Name.Contains("Radmin", StringComparison.OrdinalIgnoreCase) ||
                adapter.Description.Contains("Radmin", StringComparison.OrdinalIgnoreCase);
            var properties = adapter.GetIPProperties();
            int index = properties.GetIPv4Properties()?.Index ?? 0;
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var bytes = unicast.Address.GetAddressBytes();
                if (radmin ? bytes[0] != 26 : !IsPrivate(bytes)) continue;
                // Radmin peers use the virtual 26/8 network, including adapters reporting /32.
                int prefix = radmin ? 8 : unicast.PrefixLength;
                if (prefix is > 0 and <= 32)
                {
                    found.Add(new LanInterface(unicast.Address, prefix, index, radmin));
                }
            }
        }
        return found.DistinctBy(network => network.Address).ToArray();
    }

    private static bool IsPrivate(byte[] bytes) =>
        bytes[0] == 10 ||
        (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
        (bytes[0] == 192 && bytes[1] == 168) ||
        (bytes[0] == 169 && bytes[1] == 254);

    internal static bool Allows(LanInterface network, IPAddress peer) =>
        network.Contains(peer) && !peer.Equals(network.Address) && !peer.Equals(network.Broadcast());

    internal static bool HasLocalRoute(IReadOnlyList<LanInterface> networks, IPAddress peer)
    {
        if (!networks.Any(network => Allows(network, peer)) || networks.Any(network => network.Address.Equals(peer)))
        {
            return false;
        }

        try
        {
            using var route = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // UDP Connect here selects the OS route without sending a datagram.
            route.Connect(new IPEndPoint(peer, LanDiscovery.GamePort));
            return route.LocalEndPoint is IPEndPoint source &&
                networks.Any(network => network.Address.Equals(source.Address) && Allows(network, peer));
        }
        catch (SocketException)
        {
            return false;
        }
    }

    internal static IPAddress[] KnownRadminPeers(LanInterface network)
    {
        if (!network.Radmin || !OperatingSystem.IsWindows()) return Array.Empty<IPAddress>();
        // Windows already knows these peers. This supplements broadcast without scanning the VPN subnet.
        int size = 0;
        if (GetIpNetTable(IntPtr.Zero, ref size, false) != 122 || size is < 4 or > 1_048_576)
        {
            return Array.Empty<IPAddress>();
        }
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, false) != 0) return Array.Empty<IPAddress>();
            var peers = new List<IPAddress>();
            int count = Math.Min(Marshal.ReadInt32(buffer), (size - 4) / 24);
            for (int i = 0; i < count && peers.Count < 64; i++)
            {
                int row = 4 + i * 24;
                if (Marshal.ReadInt32(buffer, row) != network.Index ||
                    Marshal.ReadInt32(buffer, row + 20) is not (3 or 4)) continue;
                var peer = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(buffer, row + 16)));
                if (Allows(network, peer)) peers.Add(peer);
            }
            return peers.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIpNetTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool ordered);
}
