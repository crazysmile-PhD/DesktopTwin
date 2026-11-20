using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using DesktopTwin.Launching;

namespace DesktopTwin.Tests;

public sealed class ProcessImageIdentityTests : IClassFixture<SessionLaunchFixture>
{
    private readonly SessionLaunchFixture _fixture;
    public ProcessImageIdentityTests(SessionLaunchFixture fixture) => _fixture = fixture;

    [Fact]
    public void ImageIdentityIsAvailableBeforeTheTargetLoaderStarts()
    {
        // A suspended, owned fixture has no initialized CLR or UI. Module enumeration cannot be a launch precondition.
        var target = Path.Combine(_fixture.Directory, "ImageIdentityFixture.exe");
        File.Copy(_fixture.Target, target);
        var startup = new byte[IntPtr.Size == 8 ? 104 : 68];
        BitConverter.GetBytes(startup.Length).CopyTo(startup, 0);
        if (!CreateProcess(target, 0, 0, 0, false, 0x08000004, 0, _fixture.Directory, startup, out var process))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            Assert.Equal(target, ProcessCreationIdentity.ReadImagePath((int)process.Id), ignoreCase: true);
            var creation = ProcessCreationIdentity.Read((int)process.Id);
            Assert.True(creation > 0);
            Assert.Equal(creation, ProcessCreationIdentity.Read((int)process.Id));
        }
        finally { StopOwnedProcess(process); }
    }

    private static void StopOwnedProcess(ProcessInformation process)
    {
        try
        {
            if (!TerminateProcess(process.Process, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            Assert.Equal(0u, WaitForSingleObject(process.Process, 5000));
        }
        finally { CloseHandle(process.Thread); CloseHandle(process.Process); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { internal nint Process, Thread; internal uint Id, ThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, nint command, nint processAttributes, nint threadAttributes,
        bool inherit, uint flags, nint environment, string directory, [In] byte[] startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint process, uint exitCode);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
