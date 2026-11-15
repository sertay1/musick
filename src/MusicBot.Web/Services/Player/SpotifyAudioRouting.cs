using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MusicBot.Services.Player;

/// <summary>Per-application endpoint routing using the Windows audio policy ABI.
/// ABI reference: EarTrumpet's IAudioPolicyConfigFactory variants (21H2 / downlevel).
/// https://github.com/File-New-Project/EarTrumpet/tree/master/EarTrumpet/Interop/MMDeviceAPI
/// Only Spotify process preferences are changed; the system default is untouched.</summary>
public static class SpotifyAudioRouting
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Id; public UIntPtr Heap; public uint Module, Threads, Parent; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }
    public static int[] AppAudioProcesses()
    {
        var result = new HashSet<int> { Environment.ProcessId };
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return result.ToArray();
        try
        {
            var all = new List<(int Id, int Parent)>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Exe = "" };
            if (Process32FirstW(snapshot, ref entry)) do { all.Add(((int)entry.Id, (int)entry.Parent)); } while (Process32NextW(snapshot, ref entry));
            bool changed;
            do { changed = false; foreach (var item in all) if (result.Contains(item.Parent)) changed |= result.Add(item.Id); } while (changed);
            return result.ToArray();
        }
        finally { CloseHandle(snapshot); }
    }
    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, uint length, out IntPtr result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetEndpoint(IntPtr self, uint pid, int flow, int role, IntPtr endpoint);

    public static string PackEndpoint(string endpoint) => @"\\?\SWD#MMDEVAPI#" + endpoint + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    public static int[] SpotifyProcesses()
    {
        var ids = new List<int>();
        foreach (var process in Process.GetProcessesByName("Spotify"))
            using (process) { try { ids.Add(process.Id); } catch (InvalidOperationException) { } }
        return ids.ToArray();
    }

    public static IDisposable MuteForTransition()
    {
        var muted = new List<NAudio.CoreAudioApi.AudioSessionControl>();
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        try
        {
            var ids = SpotifyProcesses().ToHashSet();
            var found = false;
            foreach (var device in enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
            using (device)
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (var i=0;i<sessions.Count;i++)
                {
                    var session = sessions[i];
                    if (!ids.Contains((int)session.GetProcessID)) { session.Dispose(); continue; }
                    found = true;
                    if (session.SimpleAudioVolume.Mute) { session.Dispose(); continue; }
                    muted.Add(session);
                    session.SimpleAudioVolume.Mute = true;
                    if (!session.SimpleAudioVolume.Mute) throw new InvalidOperationException("Spotify sesi geçiş için susturulamadı.");
                }
            }
            if (!found) throw new InvalidOperationException("Bu bilgisayardaki Spotify ses oturumu bulunamadı; başka şarkılar atlanmadı.");
            return new RestoreMute(muted);
        }
        catch { new RestoreMute(muted).Dispose(); throw; }
    }
    private sealed class RestoreMute(List<NAudio.CoreAudioApi.AudioSessionControl> sessions) : IDisposable
    {
        public void Dispose()
        {
            foreach (var session in sessions)
            {
                try { session.SimpleAudioVolume.Mute = false; }
                catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Spotify session mute restore failed: " + ex.Message); }
                finally { session.Dispose(); }
            }
            sessions.Clear();
        }
    }

    public static bool IsSupported()
    {
        var initialized = RoInitialize(1);
        IntPtr className = IntPtr.Zero, factory = IntPtr.Zero;
        try
        {
            const string name = "Windows.Media.Internal.AudioPolicyConfig";
            Marshal.ThrowExceptionForHR(WindowsCreateString(name, (uint)name.Length, out className));
            var iid = new Guid("ab3d4648-e242-459f-b02f-541c70306324");
            if (RoGetActivationFactory(className, ref iid, out factory) >= 0) return true;
            iid = new Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258");
            return RoGetActivationFactory(className, ref iid, out factory) >= 0;
        }
        finally
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (className != IntPtr.Zero) WindowsDeleteString(className);
            if (initialized >= 0) RoUninitialize();
        }
    }

    public static void Apply(string? endpoint, IEnumerable<int> processIds)
    {
        var ids = processIds.ToArray();
        if (ids.Length == 0) return;
        var initialization = RoInitialize(1);
        IntPtr className = IntPtr.Zero, deviceName = IntPtr.Zero, factory = IntPtr.Zero;
        try
        {
            const string runtimeClass = "Windows.Media.Internal.AudioPolicyConfig";
            Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClass, (uint)runtimeClass.Length, out className));
            var iid = new Guid("ab3d4648-e242-459f-b02f-541c70306324");
            var hr = RoGetActivationFactory(className, ref iid, out factory);
            if (hr < 0)
            {
                iid = new Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258");
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, ref iid, out factory));
            }
            if (!string.IsNullOrEmpty(endpoint))
            {
                var packed = PackEndpoint(endpoint);
                Marshal.ThrowExceptionForHR(WindowsCreateString(packed, (uint)packed.Length, out deviceName));
            }
            // IInspectable has 6 slots, followed by 19 audio-policy methods.
            var table = Marshal.ReadIntPtr(factory);
            var set = Marshal.GetDelegateForFunctionPointer<SetEndpoint>(Marshal.ReadIntPtr(table, 25 * IntPtr.Size));
            var succeeded = false;
            var lastError = 0;
            foreach (var pid in ids)
            {
                // Chromium helper processes can disappear or have no routable app identity.
                // Their failure must not discard a successful route for the audio process.
                var multimedia = set(factory, (uint)pid, 0, 1, deviceName);
                var console = set(factory, (uint)pid, 0, 0, deviceName);
                succeeded |= multimedia >= 0 || console >= 0;
                if (multimedia < 0) lastError = multimedia;
                else if (console < 0) lastError = console;
            }
            if (!succeeded) Marshal.ThrowExceptionForHR(lastError);
        }
        finally
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (deviceName != IntPtr.Zero) WindowsDeleteString(deviceName);
            if (className != IntPtr.Zero) WindowsDeleteString(className);
            if (initialization >= 0) RoUninitialize();
        }
    }
}
