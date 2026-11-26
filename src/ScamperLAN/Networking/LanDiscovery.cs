using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ScamperLAN;

internal readonly record struct LanRoom(Guid Id, string Code, byte Players, byte Capacity, byte Intensity, byte FrequencyBand)
{
    internal bool HasSpace => Players < Capacity;
}
internal readonly record struct LanOffer(LanRoom Room, IPAddress Address, bool Radmin);

internal sealed class LanDiscovery : IDisposable
{
    internal const ushort GamePort = 7777;
    internal const ushort DiscoveryPort = 7778;
    private const long LifetimeMs = 4_500;
    private const long QueryIntervalMs = 900;
    private const long AdapterIntervalMs = 2_500;
    private const int MaxOffers = 256;
    private const int HeaderLength = 14;
    private const int OfferLength = 40;
    private static readonly byte[] Header = { 0x53, 0x4c, 0x41, 0x4e, 1 };

    private readonly Dictionary<LanInterface, Socket> probes = new();
    private readonly Dictionary<(Guid, IPAddress), (LanOffer Offer, long Expires)> offers = new();
    private readonly byte[] receive = new byte[256];
    private readonly byte[] request = new byte[HeaderLength];
    private readonly byte[] response = new byte[OfferLength];
    private readonly Func<LanInterface[]> enumerate;
    private readonly ushort discoveryPort;
    private long nextAdapters, nextQuery, nextListener;
    private Socket? host;
    private bool reportedHostError;
    private LanInterface[] networks = Array.Empty<LanInterface>();

    internal LanDiscovery() : this(LanNetworks.FindInterfaces, DiscoveryPort) { }

    internal LanDiscovery(Func<LanInterface[]> enumerate, ushort discoveryPort)
    {
        this.enumerate = enumerate;
        this.discoveryPort = discoveryPort;
        Header.CopyTo(request, 0);
        request[5] = 1;
        RandomNumberGenerator.Fill(request.AsSpan(6));
        Header.CopyTo(response, 0);
        response[5] = 2;
    }

    internal IReadOnlyList<LanInterface> Networks => networks;

    internal void Tick(bool browse, LanRoom? room, Action<string> log)
    {
        long now = Environment.TickCount64;
        if (now >= nextAdapters)
        {
            networks = enumerate();
            nextAdapters = now + AdapterIntervalMs;
        }

        if (room.HasValue && networks.Length > 0)
        {
            if (host == null && now >= nextListener)
            {
                nextListener = now + AdapterIntervalMs;
                try
                {
                    host = OpenSocket(IPAddress.Any, discoveryPort);
                    reportedHostError = false;
                }
                catch (SocketException ex)
                {
                    if (!reportedHostError)
                    {
                        reportedHostError = true;
                        log("Could not open the LAN room discovery port: " + ex.SocketErrorCode);
                    }
                }
            }
            if (host != null) ReplyToQueries(room.Value);
        }
        else
        {
            StopHosting();
        }

        if (!browse)
        {
            if (probes.Count > 0)
            {
                foreach (var socket in probes.Values) socket.Dispose();
                probes.Clear();
                nextQuery = 0;
            }
            return;
        }

        if (now >= nextQuery)
        {
            SyncProbes();
            foreach (var (network, socket) in probes)
            {
                if (network.PrefixLength < 31)
                {
                    Send(socket, request, new IPEndPoint(network.Broadcast(), discoveryPort));
                }
                if (network.Radmin || network.PrefixLength >= 31)
                {
                    Send(socket, request, new IPEndPoint(IPAddress.Broadcast, discoveryPort));
                }
                if (network.Radmin)
                {
                    foreach (var peer in LanNetworks.KnownRadminPeers(network))
                    {
                        Send(socket, request, new IPEndPoint(peer, discoveryPort));
                    }
                }
            }
            nextQuery = now + QueryIntervalMs;
        }

        foreach (var (network, socket) in probes)
        {
            ReadOffers(network, socket, now);
        }
    }

    internal LanOffer[] Rooms()
    {
        long now = Environment.TickCount64;
        foreach (var key in offers.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray())
        {
            offers.Remove(key);
        }

        return offers.Values
            .Select(entry => entry.Offer)
            .GroupBy(offer => offer.Room.Id)
            .Select(group => group.OrderBy(offer => offer.Radmin).ThenBy(offer => offer.Address.ToString(), StringComparer.Ordinal).First())
            .OrderBy(offer => offer.Room.Code, StringComparer.Ordinal)
            .ThenBy(offer => offer.Room.Id)
            .ToArray();
    }

    internal LanOffer[] Endpoints(Guid roomId) => offers.Values
        .Where(entry => entry.Expires > Environment.TickCount64 && entry.Offer.Room.Id == roomId)
        .Select(entry => entry.Offer)
        .OrderBy(offer => offer.Radmin)
        .ToArray();

    internal static bool ValidCode(string? code) => code != null && code.Length == 6 &&
        code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9');

    internal static string NewCode()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        Span<char> code = stackalloc char[6];
        for (int i = 0; i < code.Length; i++)
        {
            code[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }
        return new string(code);
    }

    private void SyncProbes()
    {
        foreach (var removed in probes.Keys.Where(network => !networks.Contains(network)).ToArray())
        {
            probes[removed].Dispose();
            probes.Remove(removed);
        }

        foreach (var network in networks.Where(network => !probes.ContainsKey(network)))
        {
            try
            {
                probes.Add(network, OpenSocket(network.Address, 0));
            }
            catch (SocketException)
            {
                // Another adapter can still find rooms when this one goes down mid-refresh.
            }
        }
    }

    private void ReplyToQueries(LanRoom room)
    {
        for (int i = 0; i < 32 && TryReceive(host!, out var peer, out int count); i++)
        {
            if (count != HeaderLength || !receive.AsSpan(0, Header.Length).SequenceEqual(Header) ||
                receive[5] != 1 || !LanNetworks.HasLocalRoute(networks, peer.Address))
            {
                continue;
            }

            Buffer.BlockCopy(receive, 6, response, 6, 8);
            room.Id.TryWriteBytes(response.AsSpan(14, 16));
            for (int c = 0; c < room.Code.Length; c++) response[30 + c] = (byte)room.Code[c];
            response[36] = room.Players;
            response[37] = room.Capacity;
            response[38] = room.Intensity;
            response[39] = room.FrequencyBand;
            Send(host!, response, peer);
        }
    }

    private void ReadOffers(LanInterface network, Socket socket, long now)
    {
        for (int i = 0; i < 32 && TryReceive(socket, out var peer, out int count); i++)
        {
            if (count != OfferLength || peer.Port != discoveryPort || !LanNetworks.Allows(network, peer.Address) ||
                !receive.AsSpan(0, Header.Length).SequenceEqual(Header) || receive[5] != 2 ||
                !receive.AsSpan(6, 8).SequenceEqual(request.AsSpan(6, 8)))
            {
                continue;
            }

            var id = new Guid(receive.AsSpan(14, 16));
            var code = System.Text.Encoding.ASCII.GetString(receive, 30, 6);
            byte players = receive[36], capacity = receive[37];
            if (id == Guid.Empty || !ValidCode(code) || capacity is < 2 or > 64 || players == 0 ||
                players > capacity || receive[38] > 10 || receive[39] > 3)
            {
                continue;
            }

            var key = (id, peer.Address);
            if (offers.Count >= MaxOffers && !offers.ContainsKey(key))
            {
                // Expired entries never prevent an otherwise valid later reply.
                _ = Rooms();
                if (offers.Count >= MaxOffers) continue;
            }
            var room = new LanRoom(id, code, players, capacity, receive[38], receive[39]);
            offers[key] = (new LanOffer(room, peer.Address, network.Radmin), now + LifetimeMs);
        }
    }

    private bool TryReceive(Socket socket, out IPEndPoint peer, out int count)
    {
        peer = new IPEndPoint(IPAddress.Any, 0);
        count = 0;
        try
        {
            EndPoint sender = peer;
            count = socket.ReceiveFrom(receive, ref sender);
            peer = (IPEndPoint)sender;
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static void Send(Socket socket, byte[] payload, IPEndPoint peer)
    {
        try { socket.SendTo(payload, peer); }
        catch (SocketException) { }
    }

    private static Socket OpenSocket(IPAddress address, ushort port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.EnableBroadcast = true;
            socket.Blocking = false;
            socket.Bind(new IPEndPoint(address, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal void StopHosting()
    {
        host?.Dispose();
        host = null;
    }

    public void Dispose()
    {
        StopHosting();
        foreach (var socket in probes.Values) socket.Dispose();
        probes.Clear();
        offers.Clear();
    }
}
