using System.Diagnostics;
using System.Runtime.InteropServices;
using Gac.Shared.ClassMode;

namespace Gac.Modules.ClassMode;

/// <summary>Real class-mode actions. Runs inside the tray app with the teacher's own rights.</summary>
public sealed class WindowsPresetActionExecutor : IPresetActionExecutor
{
    public async Task ExecuteAsync(PresetAction a, bool sharedPreset, CancellationToken ct)
    {
        switch (a.Type)
        {
            case PresetActionTypes.SetVolume:
                AudioEndpoint.SetVolume(Math.Clamp(a.Value ?? 0, 0, 100) / 100f);
                break;
            case PresetActionTypes.Mute:
                AudioEndpoint.SetMute(true);
                break;
            case PresetActionTypes.Unmute:
                AudioEndpoint.SetMute(false);
                break;
            case PresetActionTypes.DisplayMode:
                if (!PresetActionTypes.DisplayModes.Contains(a.Mode)) throw new InvalidOperationException("화면 모드 값이 올바르지 않습니다.");
                using (var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "DisplaySwitch.exe"), "/" + a.Mode) { UseShellExecute = false }))
                    if (p != null) await p.WaitForExitAsync(ct);
                break;
            case PresetActionTypes.SetResolution:
                DisplaySettings.SetResolution(a.Width ?? 0, a.Height ?? 0);
                break;
            case PresetActionTypes.OpenUrl:
                if (!PresetValidator.IsHttpUrl(a.Url)) throw new InvalidOperationException("http/https 주소만 열 수 있습니다.");
                Process.Start(new ProcessStartInfo(a.Url!) { UseShellExecute = true })?.Dispose();
                break;
            case PresetActionTypes.LaunchApp:
                if (!File.Exists(a.Path)) throw new FileNotFoundException("프로그램을 찾을 수 없습니다: " + a.Path);
                Process.Start(new ProcessStartInfo(a.Path!, a.Arguments ?? "") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(a.Path)! })?.Dispose();
                break;
            case PresetActionTypes.CloseApp:
                await CloseAppAsync(a.ProcessName!, ct);
                break;
            case PresetActionTypes.Wait:
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(a.Seconds ?? 1, 1, 30)), ct);
                break;
            case PresetActionTypes.RunCommand:
                if (!sharedPreset) throw new UnauthorizedAccessException("'명령 실행'은 관리자가 배포한 프리셋에서만 사용할 수 있습니다.");
                using (var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c " + a.Command) { UseShellExecute = false, CreateNoWindow = true }))
                    if (p != null) await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
                break;
            default:
                throw new InvalidOperationException("알 수 없는 동작입니다: " + a.Type);
        }
    }

    /// <summary>Closes only processes in the teacher's own session: polite close first, then kill.</summary>
    private static async Task CloseAppAsync(string name, CancellationToken ct)
    {
        name = Path.GetFileNameWithoutExtension(name);
        var session = Process.GetCurrentProcess().SessionId;
        var procs = Process.GetProcessesByName(name).Where(p => p.SessionId == session).ToList();
        if (procs.Count == 0) return;
        foreach (var p in procs) { try { p.CloseMainWindow(); } catch (InvalidOperationException) { } }
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        foreach (var p in procs)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { p.Dispose(); }
        }
    }
}

/// <summary>Minimal Core Audio (IAudioEndpointVolume) interop for the default render device.</summary>
internal static class AudioEndpoint
{
    public static void SetVolume(float level) => WithEndpoint(v => Marshal.ThrowExceptionForHR(v.SetMasterVolumeLevelScalar(level, Guid.Empty)));
    public static void SetMute(bool mute) => WithEndpoint(v => Marshal.ThrowExceptionForHR(v.SetMute(mute, Guid.Empty)));

    private static void WithEndpoint(Action<IAudioEndpointVolume> act)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device));
            var iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj));
            var vol = (IAudioEndpointVolume)obj;
            try { act(vol); }
            finally { Marshal.ReleaseComObject(vol); Marshal.ReleaseComObject(device); }
        }
        catch (COMException e) when ((uint)e.HResult == 0x80070490) { throw new InvalidOperationException("스피커(출력 장치)를 찾을 수 없습니다."); }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}

/// <summary>Changes the primary display resolution only after validating it against the supported modes (CDS_TEST).</summary>
internal static class DisplaySettings
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int CDS_UPDATEREGISTRY = 0x01, CDS_TEST = 0x02;
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000;

    public static void SetResolution(int width, int height)
    {
        var supported = new HashSet<(int, int)>();
        var mode = NewMode();
        for (var i = 0; EnumDisplaySettings(null, i, ref mode); i++) supported.Add(((int)mode.dmPelsWidth, (int)mode.dmPelsHeight));
        if (!supported.Contains((width, height)))
            throw new InvalidOperationException($"이 모니터는 {width}×{height} 해상도를 지원하지 않습니다.");

        var current = NewMode();
        if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref current)) throw new InvalidOperationException("현재 화면 설정을 읽지 못했습니다.");
        if (current.dmPelsWidth == width && current.dmPelsHeight == height) return;
        current.dmPelsWidth = (uint)width;
        current.dmPelsHeight = (uint)height;
        current.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
        if (ChangeDisplaySettings(ref current, CDS_TEST) != DISP_CHANGE_SUCCESSFUL) throw new InvalidOperationException("이 해상도로 바꿀 수 없습니다.");
        if (ChangeDisplaySettings(ref current, CDS_UPDATEREGISTRY) != DISP_CHANGE_SUCCESSFUL) throw new InvalidOperationException("해상도를 바꾸지 못했습니다.");
    }

    private static DEVMODE NewMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettings(ref DEVMODE devMode, int flags);
}
