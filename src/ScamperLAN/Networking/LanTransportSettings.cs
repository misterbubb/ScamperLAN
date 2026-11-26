using System;

namespace ScamperLAN;

public sealed partial class Plugin
{
    private static LanTransportSettings? lanTransportSettings;
    private static bool lanTransportCleanupPending, lanTransportCleanupErrorLogged;

    private sealed class LanTransportSettings
    {
        internal readonly object Multipass;
        internal readonly object Tugboat;
        internal readonly bool Server;
        private readonly ushort port;
        private readonly string? address, bindV4, bindV6;
        private readonly object? clientTransport;

        internal LanTransportSettings(object multipass, bool server)
        {
            Multipass = multipass;
            Server = server;
            Tugboat = getTransportMethod!.Invoke(multipass, new object[] { TugboatIndex })
                ?? throw new InvalidOperationException("Tugboat is unavailable.");
            port = (ushort)Require(Tugboat.GetType(), "GetPort").Invoke(Tugboat, null)!;
            if (server)
            {
                var getBind = Require(Tugboat.GetType(), "GetServerBindAddress", addressType!);
                bindV4 = (string?)getBind.Invoke(Tugboat, new[] { Enum.Parse(addressType!, "IPv4") });
                bindV6 = (string?)getBind.Invoke(Tugboat, new[] { Enum.Parse(addressType!, "IPv6") });
            }
            else
            {
                address = (string?)nativeGetAddress!.Invoke(Tugboat, null);
                clientTransport = nativeClientTransport!.GetValue(multipass);
            }
        }

        internal void Restore()
        {
            portMethod!.Invoke(Multipass, new object[] { port, TugboatIndex });
            if (Server)
            {
                bindMethod!.Invoke(Multipass, new[] { bindV4, Enum.Parse(addressType!, "IPv4"), (object)TugboatIndex });
                bindMethod.Invoke(Multipass, new[] { bindV6, Enum.Parse(addressType!, "IPv6"), (object)TugboatIndex });
                return;
            }

            nativeSetAddress!.Invoke(Multipass, new object?[] { address, TugboatIndex });
            var current = nativeClientTransport!.GetValue(Multipass);
            if (current != null && SameNativeInstance(current, Tugboat))
            {
                nativeClientTransport.SetValue(Multipass, clientTransport);
            }
        }
    }

    private static void StopLanTransport(bool server)
    {
        var settings = lanTransportSettings;
        if (settings == null || settings.Server != server || stopping)
        {
            return;
        }

        stopping = true;
        lanTransportCleanupPending = true;
        try
        {
            if (IsUnityObjectAlive(settings.Multipass) &&
                State(settings.Multipass, server, TugboatIndex) is not ("Stopped" or "Stopping"))
            {
                stopMethod!.Invoke(settings.Multipass, new object[] { server, TugboatIndex });
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning("[LAN] Could not stop Tugboat: " + ex.GetBaseException().Message);
        }
        finally
        {
            stopping = false;
        }

    }

    private static void FinishLanTransportCleanup()
    {
        var settings = lanTransportSettings;
        if (!lanTransportCleanupPending || settings == null || stopping)
        {
            return;
        }

        try
        {
            if (IsUnityObjectAlive(settings.Multipass))
            {
                if (State(settings.Multipass, settings.Server, TugboatIndex) != "Stopped")
                {
                    return;
                }

                settings.Restore();
            }

            if (settings.Server)
            {
                testMultipass = null;
            }
            else
            {
                nativeClientMultipass = null;
            }
            lanTransportSettings = null;
            lanTransportCleanupPending = lanTransportCleanupErrorLogged = false;
            logger?.LogInfo("[LAN] LAN transport stopped and its original settings were restored.");
        }
        catch (Exception ex)
        {
            if (!lanTransportCleanupErrorLogged)
            {
                lanTransportCleanupErrorLogged = true;
                logger?.LogWarning("[LAN] Transport cleanup is incomplete: " + ex.GetBaseException().Message);
            }
        }
    }
}
