using System.Runtime.InteropServices;
using System.Security.Principal;
using DesktopTwin.Services;

namespace DesktopTwin.Tests;

public sealed class SessionTaskExpiryTests
{
    [Fact]
    public async Task WindowsRemovesExpiredBootstrapWithoutHostCleanupOrRelaunch()
    {
        ChildSessionProcessLauncher.EnsureNoUserClassOverride();
        object schedulerObject = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        dynamic scheduler = schedulerObject;
        object? folderObject = null, definitionObject = null, triggerObject = null, registered = null, actionObject = null;
        var name = "DesktopTwin-SessionLaunch-" + Guid.NewGuid().ToString("N");
        try
        {
            scheduler.Connect();
            folderObject = scheduler.GetFolder("\\");
            dynamic folder = folderObject;
            definitionObject = scheduler.NewTask(0);
            dynamic definition = definitionObject;
            definition.Settings.Hidden = true;
            definition.Settings.ExecutionTimeLimit = "PT0S";
            triggerObject = ChildSessionProcessLauncher.ConfigureTaskExpiry(definitionObject,
                DateTime.UtcNow, TimeSpan.FromSeconds(3));
            using var identity = WindowsIdentity.GetCurrent();
            definition.Principal.UserId = identity.Name;
            definition.Principal.LogonType = 3;
            actionObject = definition.Actions.Create(0);
            dynamic action = actionObject;
            action.Path = Environment.ProcessPath!; // disabled trigger, NEVER executed
            registered = folder.RegisterTaskDefinition(name, definition, 2, identity.Name, null, 3, null);
            var deleted = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(50))
            {
                object? found = null;
                try { found = folder.GetTask(name); }
                catch (Exception error) when (error.HResult == unchecked((int)0x80070002))
                { deleted = true; break; }
                finally { Release(found); }
                await Task.Delay(250);
            }
            Assert.True(deleted, "Windows did not remove the expired task independently within the test budget.");
        }
        finally
        {
            // Only an emergency test cleanup, never the evidence for the assertion above.
            if (folderObject is not null) ChildSessionProcessLauncher.DeleteTemporaryTask(name);
            Release(registered); Release(actionObject); Release(triggerObject);
            Release(definitionObject); Release(folderObject); Release(schedulerObject);
        }
    }

    private static void Release(object? value)
    { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
