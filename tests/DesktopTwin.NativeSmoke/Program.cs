using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var assembly = typeof(DesktopTwin.Models.AppSettings).Assembly;
            var type = assembly.GetType("DesktopTwin.Interop.RdpActiveXHost", throwOnError: true)!;
            using var form = new Form();
            using var host = (AxHost)Activator.CreateInstance(type, nonPublic: true)!;
            ((ISupportInitialize)host).BeginInit();
            form.Controls.Add(host);
            ((ISupportInitialize)host).EndInit();
            form.CreateControl();
            host.CreateControl();
            var client = type.GetMethod("GetRequiredOcx", flags)!.Invoke(host, null)!;
            var advanced = client.GetType().InvokeMember("AdvancedSettings7", BindingFlags.GetProperty,
                null, client, null, CultureInfo.InvariantCulture)!;
            var extended = (IExtendedSettings)client;
            Console.WriteLine("Assembly=" + assembly.GetName().Version);
            Console.WriteLine("Windows=" + Environment.OSVersion.Version);
            Console.WriteLine("MsTscAx=" + FileVersionInfo.GetVersionInfo(Environment.SystemDirectory + "\\mstscax.dll").FileVersion);

            // Exercise the production pre-connection path, including reuse after toggling.
            foreach (var enabled in new[] { false, true, true, false, true, false })
            {
                type.GetMethod("SetRelativeMouseMode", flags)!.Invoke(host, new object[] { enabled });
                type.GetMethod("ApplyRelativeMouseMode", flags)!.Invoke(host, new[] { client, advanced });
                var accepted = (bool)type.GetProperty("RelativeMouseModeSettingsAccepted", flags)!.GetValue(host)!;
                var allow = Convert.ToBoolean(extended.Get("AllowRelativeMouseMode"), CultureInfo.InvariantCulture);
                var relative = Convert.ToBoolean(advanced.GetType().InvokeMember("RelativeMouseMode",
                    BindingFlags.GetProperty, null, advanced, null, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                Console.WriteLine($"Requested={enabled}; Accepted={accepted}; AllowRelativeMouseMode={allow}; RelativeMouseMode={relative}");
                if (!accepted || allow != enabled || relative != enabled)
                    throw new InvalidOperationException("Production configuration and native readback disagree.");
            }

            var connected = Convert.ToInt32(client.GetType().InvokeMember("Connected", BindingFlags.GetProperty,
                null, client, null, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            if (connected != 0) throw new InvalidOperationException("Probe unexpectedly established a connection.");
            Console.WriteLine("PASS: production properties verified; Connected=0; no child session created.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.GetBaseException());
            return 1;
        }
    }
}

[ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExtendedSettings
{
    void Set([MarshalAs(UnmanagedType.BStr)] string name, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
    [return: MarshalAs(UnmanagedType.Struct)] object Get([MarshalAs(UnmanagedType.BStr)] string name);
}
