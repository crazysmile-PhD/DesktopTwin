using System;
using System.IO;

namespace DesktopTwin.Launching
{
    // Shared with the C# 5 / .NET Framework worker. No success is inferred from RunEx.
    internal static class SessionLaunchProtocol
    {
        internal const int Version = 2;
        internal const int ObservationMilliseconds = 30000;
        internal const int ConnectionMilliseconds = 15000;

        internal static void WriteRequest(BinaryWriter writer, string path, string arguments,
            string directory, int sessionId, int observationMilliseconds)
        {
            writer.Write(Version);
            writer.Write(path);
            writer.Write(arguments);
            writer.Write(directory);
            writer.Write(sessionId);
            writer.Write(observationMilliseconds);
            writer.Flush();
        }

        internal static void WriteResult(BinaryWriter writer, bool ready, int pid,
            int sessionId, long started, long window, string error)
        {
            writer.Write(Version);
            writer.Write(ready);
            writer.Write(pid);
            writer.Write(sessionId);
            writer.Write(started);
            writer.Write(window);
            writer.Write(error);
            writer.Flush();
        }
    }
}
