using System.Collections.Generic;

namespace DNET_Publisher
{
    class PublishConfig
    {
        public string PublishRuntime { get; set; } = "";
        public string PublishConfiguration { get; set; } = "Release";
        public string OutputDir { get; set; } = "";
        public Dictionary<string, string> Minify { get; set; } = new Dictionary<string, string>();
        public bool SelfContained { get; set; } = false;
        public UploadConfig Upload { get; set; }
        public string[] Exclude { get; set; }
    }
}
