using System;
using System.Net;
using System.Net.Sockets;

internal static class VpnFirewallProbe
{
    static int Main(string[] args)
    {
        try
        {
            using (var client = new TcpClient(AddressFamily.InterNetwork))
            {
                var connection = client.ConnectAsync(IPAddress.Parse(args[0]), int.Parse(args[1]));
                return connection.Wait(TimeSpan.FromSeconds(4)) && client.Connected ? 0 : 1;
            }
        }
        catch { return 1; }
    }
}
