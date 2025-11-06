namespace FileSyncService;

public class FileSyncConfig
{
    public required ConfigSection Config { get; set; }
    public required FileSection Files { get; set; }

    public class ConfigSection
    {
        public LogSection Log { get; set; } = new();
        public List<AuthEntry> Auth { get; set; } = new();
        public required HttpsSection Https { get; set; }
        public required SyncSection Sync { get; set; }

        public class LogSection
        {
            public string Path { get; set; } = "logs/filesync.log";
            public RotationSection? Rotation { get; set; }

            public class RotationSection
            {
                public int MaxSizeMb { get; set; } = 10;
                public int MaxFiles { get; set; } = 5;
            }
        }

        public class AuthEntry
        {
            public string Username { get; set; } = "";
            public string Password { get; set; } = "";
        }

        public class HttpsSection
        {
            public int Port { get; set; }
            public string CertPathPublic { get; set; } = "";
            public string CertPathPrivate { get; set; } = "";
        }

        public class SyncSection
        {
            public List<string> Schedule { get; set; } = [];
        }
    }

    public class FileSection
    {
        public string Private { get; set; } = "";
        public string Public { get; set; } = "";
        public required MirrorSection Mirror { get; set; }

        public class MirrorSection
        {
            public string BasePath { get; set; } = "";

            public Dictionary<string, Dictionary<string, string>> Data { get; set; } = [];
        }
    }
}
