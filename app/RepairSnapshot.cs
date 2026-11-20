using System.Collections.Generic;

namespace ZapretLauncher
{
    sealed class RepairSnapshot
    {
        public readonly string Strategy, Dns;
        readonly List<string> blocked;
        readonly Dictionary<string, NetProfile> profiles;

        public RepairSnapshot(Config config)
        {
            Strategy = config.Strategy;
            Dns = config.SecureDns;
            blocked = new List<string>(config.BlockedSites);
            profiles = new Dictionary<string, NetProfile>(config.NetworkProfiles);
        }

        public void Restore(Config config)
        {
            config.Strategy = Strategy;
            config.BlockedSites = new List<string>(blocked);
            config.NetworkProfiles = new Dictionary<string, NetProfile>(profiles);
        }
    }
}
