using System;
using System.Net;
using System.Net.Sockets;

internal static class VpnFirewallProbe
{
    static int Main(string[] args)
    {
        try
        {
            IPAddress address = IPAddress.Parse(args[0]);
            using (var client = new TcpClient(address.AddressFamily))
            {
                var connection = client.ConnectAsync(address, int.Parse(args[1]));
                return connection.Wait(TimeSpan.FromSeconds(4)) && client.Connected ? 0 : 1;
            }
        }
        catch { return 1; }
    }
}
