using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class VpnFirewallProbe
{
    static void Record(string log, string token)
    {
        lock (typeof(VpnFirewallProbe)) File.AppendAllText(log, token + Environment.NewLine);
    }

    static void Listen(int port, string log, string ready)
    {
        var tcp = new TcpListener(IPAddress.Loopback, port);
        tcp.Start();
        var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        Task.Run(() =>
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                byte[] data = udp.Receive(ref from);
                Record(log, Encoding.ASCII.GetString(data));
                udp.Send(data, data.Length, from);
            }
        });
        File.WriteAllText(ready, "");
        while (true)
        {
            var client = tcp.AcceptTcpClient();
            Task.Run(() =>
            {
                try
                {
                    using (client)
                    using (var stream = client.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.ASCII))
                    using (var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true })
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            Record(log, line);
                            writer.WriteLine(line);
                        }
                    }
                }
                catch (IOException) { }
            });
        }
    }

    static int Main(string[] args)
    {
        if (args[0] == "listen")
        {
            Listen(int.Parse(args[1]), args[2], args[3]);
            return 0;
        }
        try
        {
            IPAddress address = IPAddress.Parse(args[0]);
            int port = int.Parse(args[1]);
            if (args.Length > 2 && args[2] == "udp")
            {
                using (var udp = new UdpClient(address.AddressFamily))
                {
                    udp.Client.ReceiveTimeout = 1200;
                    udp.Connect(address, port);
                    byte[] data = Encoding.ASCII.GetBytes(args[3]);
                    for (int i = 0; i < 3; i++)
                    {
                        udp.Send(data, data.Length);
                        Thread.Sleep(75);
                    }
                    var from = new IPEndPoint(address, 0);
                    return Encoding.ASCII.GetString(udp.Receive(ref from)) == args[3] ? 0 : 1;
                }
            }
            using (var client = new TcpClient(address.AddressFamily))
            {
                if (!client.ConnectAsync(address, port).Wait(2000) || !client.Connected) return 1;
                if (args.Length == 2) return 0;
                client.ReceiveTimeout = 2000;
                client.SendTimeout = 2000;
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII))
                using (var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true })
                {
                    writer.WriteLine(args[3]);
                    if (reader.ReadLine() != args[3]) return 1;
                    if (args[2] != "hold") return 0;
                    File.WriteAllText(args[4], "");
                    for (int i = 0; i < 400 && !File.Exists(args[5]); i++) Thread.Sleep(50);
                    if (!File.Exists(args[5])) return 2;
                    writer.WriteLine(args[6]);
                    return reader.ReadLine() == args[6] ? 0 : 1;
                }
            }
        }
        catch (AggregateException) { return 1; }
        catch (SocketException) { return 1; }
        catch (IOException) { return 1; }
    }
}
