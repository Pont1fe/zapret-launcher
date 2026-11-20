using System;
using System.Threading;

public static class FakeVpnCore
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "check") return 0;
        Thread.Sleep(30000); // A running process without a VPN adapter must never count as connected.
        return 0;
    }
}
