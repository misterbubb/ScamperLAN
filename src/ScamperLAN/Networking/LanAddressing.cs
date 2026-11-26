using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ScamperLAN;

public sealed partial class Plugin
{
    private static bool IsSupportedLanAddress(string text)
    {
        if (!IPAddress.TryParse(text, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            address.ToString() != text)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
            bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
            bytes[0] == 192 && bytes[1] == 168 ||
            bytes[0] == 169 && bytes[1] == 254 ||
            bytes[0] == 25 ||
            bytes[0] == 26 ||
            bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    private static bool IsRadminAddress(string text) =>
        IPAddress.TryParse(text, out var address) &&
        address.AddressFamily == AddressFamily.InterNetwork &&
        address.GetAddressBytes()[0] == 26;

    private static IEnumerable<string> LocalLanAddresses()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address.ToString();
                if (IsSupportedLanAddress(address))
                {
                    yield return address;
                }
            }
        }
    }

}
