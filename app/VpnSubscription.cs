using System;
using System.Collections.Generic;
using System.Linq;

namespace ZapretLauncher
{
    public sealed class VpnSubscription
    {
        public string Id { get; set; }
        public string Url { get; set; }
        public string Info { get; set; }
        public string Updated { get; set; }
        public string Warned { get; set; }
        // Older settings did not record which servers came from the subscription.
        public bool Legacy { get; set; }

        internal string Host { get { Uri uri; return Uri.TryCreate(Url, UriKind.Absolute, out uri) ? uri.Host : ""; } }

        internal bool IsStale(DateTime now, double hours)
        {
            DateTime updated;
            return !DateTime.TryParse(Updated, out updated) || (now - updated).TotalHours >= hours;
        }

        internal void ReplaceServers(Config cfg, List<VpnServer> servers)
        {
            VpnServer selected = cfg.VpnServers.ElementAtOrDefault(cfg.VpnSelected);
            var links = new HashSet<string>(servers.Select(s => s.Link), StringComparer.Ordinal);
            var favorites = new HashSet<string>(cfg.VpnServers.Where(s => s.Favorite &&
                (s.SubscriptionId == Id || (Legacy && string.IsNullOrEmpty(s.SubscriptionId)))).Select(s => s.Link), StringComparer.Ordinal);
            int index = cfg.VpnServers.FindIndex(s => s.SubscriptionId == Id);
            cfg.VpnServers.RemoveAll(s => s.SubscriptionId == Id || (Legacy &&
                string.IsNullOrEmpty(s.SubscriptionId) && !Awg.IsAwg(s.Link) && links.Contains(s.Link)));
            foreach (VpnServer server in servers)
            {
                server.SubscriptionId = Id;
                server.Favorite = favorites.Contains(server.Link);
            }
            cfg.VpnServers.InsertRange(index < 0 ? cfg.VpnServers.Count : Math.Min(index, cfg.VpnServers.Count), servers);
            Legacy = false;
            cfg.VpnSelected = Selection(cfg.VpnServers, selected);
        }

        internal static int Selection(List<VpnServer> servers, VpnServer selected)
        {
            if (selected == null) return 0;
            int index = servers.IndexOf(selected);
            if (index < 0) index = servers.FindIndex(s => s.SubscriptionId == selected.SubscriptionId && s.Link == selected.Link);
            if (index < 0) index = servers.FindIndex(s => s.Link == selected.Link);
            return Math.Max(0, index);
        }
    }
}
