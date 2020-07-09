using System.Collections.Generic;

namespace DNET_Publisher
{
    class UploadConfig
    {
        public string Host { get; set; }
        public string User { get; set; }
        public string Pass { get; set; }
        public string Key { get; set; }
        public string Destination { get; set; }
        public string[] Execute { get; set; }

        public string getHost()
        {
            return Host.Contains(':') ? Host.Substring(0, Host.IndexOf(':')) : Host;
        }

        public int getPort()
        {
            int pos = Host.IndexOf(':');
            return pos != -1 ? int.Parse(Host.Substring(pos+1)) : 22;
        }
    }
}
