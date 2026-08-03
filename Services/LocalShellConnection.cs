using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MultiSSH.Models;

namespace MultiSSH.Services;

/// <summary>
/// A local PowerShell / cmd.exe session hosted in a Windows pseudoconsole
/// (ConPTY). The child console writes real VT sequences into a pipe, so the
/// same <see cref="MultiSSH.Terminal.TerminalControl"/> renders it exactly as
/// it renders an SSH shell — colours, cursor addressing, full-screen apps.
/// </summary>
public class LocalShellConnection : ITerminalBackend
{
    private readonly SessionConfig _cfg;

    private IntPtr _hPC = IntPtr.Zero;
    private IntPtr _attrList = IntPtr.Zero;
    private IntPtr _hProcess = IntPtr.Zero;
    private IntPtr _hThread = IntPtr.Zero;

    private SafeFileHandle? _inWrite;
    private SafeFileHandle? _outRead;
    private FileStream? _writer;
    private FileStream? _reader;

    private Thread? _readThread;
    private Thread? _waitThread;
    private volatile bool _disposed;
    private volatile bool _running;

    public event Action<byte[]>? DataReceived;
    public event Action<string>? StatusChanged;
    public event Action<string>? Closed;
    public event Action? ShellExited;

    public bool IsConnected => _running;

    public LocalShellConnection(SessionConfig cfg) => _cfg = cfg;

    /// <summary>Full command line for the configured shell.</summary>
    private string CommandLine
    {
        get
        {
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return _cfg.Kind == SessionKind.Cmd
                ? Path.Combine(sys, "cmd.exe")
                : Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe");
        }
    }

    public Task ConnectAsync(int cols, int rows)
    {
        // Everything here is fast, synchronous Win32 — no need for a worker thread.
        StatusChanged?.Invoke($"Starting {_cfg.LocalShellName} …");

        if (cols < 1) cols = 80;
        if (rows < 1) rows = 24;

        // Two pipes: one we write keystrokes into, one we read console output from.
        if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (input) failed");
        if (!CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (output) failed");

        try
        {
            int hr = CreatePseudoConsole(new COORD { X = (short)cols, Y = (short)rows },
                inRead, outWrite, 0, out _hPC);
            if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed");

            StartShell();

            _inWrite = inWrite;
            _outRead = outRead;
            _writer = new FileStream(_inWrite, FileAccess.Write);
            _reader = new FileStream(_outRead, FileAccess.Read);
            _running = true;

            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "conpty-read" };
            _readThread.Start();
            _waitThread = new Thread(WaitLoop) { IsBackground = true, Name = "conpty-wait" };
            _waitThread.Start();

            StatusChanged?.Invoke($"{_cfg.LocalShellName} — local");
        }
        finally
        {
            // The pseudoconsole owns its ends now; the parent must let go of them
            // or the pipes never report EOF.
            inRead.Dispose();
            outWrite.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <summary>Launch the shell attached to the pseudoconsole via STARTUPINFOEX.</summary>
    private void StartShell()
    {
        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);   // sizing call: expected to fail
        _attrList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref size))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");

        if (!UpdateProcThreadAttribute(_attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");

        si.lpAttributeList = _attrList;

        // CreateProcessW may write to the command-line buffer, so pass a mutable one.
        var cmd = new StringBuilder(CommandLine);
        string cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, cwd, ref si, out var pi))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"Could not start {_cfg.LocalShellName}");

        _hProcess = pi.hProcess;
        _hThread = pi.hThread;
    }

    private void ReadLoop()
    {
        var buf = new byte[8192];
        var reader = _reader;
        try
        {
            while (!_disposed && reader != null)
            {
                int n = reader.Read(buf, 0, buf.Length);
                if (n <= 0) break;   // pipe closed — the console is gone
                var slice = new byte[n];
                Array.Copy(buf, slice, n);
                DataReceived?.Invoke(slice);
            }
        }
        catch
        {
            // Stream disposed or pipe broken — same as EOF.
        }
    }

    /// <summary>Report the shell exiting, which is what closes the pane.</summary>
    private void WaitLoop()
    {
        try { WaitForSingleObject(_hProcess, INFINITE); }
        catch { /* handle already gone */ }

        _running = false;
        if (_disposed) return;
        StatusChanged?.Invoke($"{_cfg.LocalShellName} exited");
        ShellExited?.Invoke();
    }

    public void Send(byte[] data)
    {
        var w = _writer;
        if (w == null) return;
        try
        {
            w.Write(data, 0, data.Length);
            w.Flush();
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("Send failed: " + ex.Message);
        }
    }

    public void Send(string text) => Send(Encoding.UTF8.GetBytes(text));

    public void Resize(int cols, int rows)
    {
        if (_hPC == IntPtr.Zero || cols < 1 || rows < 1) return;
        try { ResizePseudoConsole(_hPC, new COORD { X = (short)cols, Y = (short)rows }); }
        catch { /* non-fatal: the console keeps its current size */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;

        try
        {
            // Closing the pseudoconsole asks the shell to exit and unblocks the
            // read loop; terminate anything that ignores it.
            if (_hPC != IntPtr.Zero) { ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; }

            if (_hProcess != IntPtr.Zero)
            {
                if (WaitForSingleObject(_hProcess, 2000) != 0) TerminateProcess(_hProcess, 0);
                CloseHandle(_hProcess);
                _hProcess = IntPtr.Zero;
            }
            if (_hThread != IntPtr.Zero) { CloseHandle(_hThread); _hThread = IntPtr.Zero; }

            _writer?.Dispose();
            _reader?.Dispose();

            if (_attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(_attrList);
                Marshal.FreeHGlobal(_attrList);
                _attrList = IntPtr.Zero;
            }
        }
        catch { /* ignore teardown errors */ }
        finally
        {
            _writer = null;
            _reader = null;
        }

        Closed?.Invoke("Closed");
    }

    // -------------------- Win32 --------------------

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;
    private const uint INFINITE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe,
        IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput,
        uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount,
        int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute,
        IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
