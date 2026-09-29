using System.Net;
using System.Runtime.InteropServices;

namespace ServerHealth.Interop;

/// <summary>Сводная информация об узле (статическая конфигурация).</summary>
public sealed class SysInfo
{
    public string Machine = "", User = "", Domain = "", Os = "", CpuName = "";
    public int Cores;
    public double RamGb;
    public TimeSpan Uptime;
    public bool Elevated;
    public string PowerPlan = "";
    public string PageFileConfig = "";
    public string AppVersion = "";
    public DateTime CollectedStart, CollectedEnd;
}

/// <summary>Счётчики ввода-вывода процесса (файлы + сеть + устройства).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct IoCounters
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

/// <summary>RDP/TS-сессия.</summary>
public sealed class SessionInfo
{
    public int Id;
    public string Station = "", State = "", User = "", Domain = "", Client = "";
    public override string ToString()
    {
        string who = (Domain + "\\" + User).Trim('\\');
        if (who.Length == 0) who = "—";
        string cl = string.IsNullOrEmpty(Client) ? "" : " (клиент: " + Client + ")";
        return string.Format("  сессия {0,-4} {1,-14} {2,-12} {3}{4}", Id, Station, State, who, cl);
    }
}

/// <summary>Открытый SMB-файл на сервере (NetFileEnum).</summary>
public sealed class OpenFileRec
{
    public string Path = "";
    public string User = "";
    public int Locks;
}

internal static class Native
{
    // ------------------------------------------------------------------ память
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx b);

    public static (ulong total, ulong avail) GlobalMemory()
    {
        var m = new MemoryStatusEx();
        m.dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0);
        return (m.ullTotalPhys, m.ullAvailPhys);
    }

    [StructLayout(LayoutKind.Sequential)]
    public sealed class PerfInfo
    {
        public uint cb;
        public uint CommitTotal, CommitLimit, CommitPeak;
        public UIntPtr PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonPaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("psapi.dll")]
    private static extern bool GetPerformanceInfo([In, Out] PerfInfo p, int cb);

    public static PerfInfo GetPerfInfo()
    {
        var p = new PerfInfo();
        try
        {
            p.cb = (uint)Marshal.SizeOf<PerfInfo>();
            if (GetPerformanceInfo(p, (int)p.cb)) return p;
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------- IO процесса
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr h, out IoCounters io);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    public static IoCounters? GetIoCounters(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (GetProcessIoCounters(h, out IoCounters io)) return io;
            return null;
        }
        finally { try { CloseHandle(h); } catch { } }
    }

    // --------------------------------------------------------------- TCP / UDP
    public sealed class TcpConn
    {
        public int Pid;
        public int State;
        public string Local = "", Remote = "";
        public bool IsV6;
    }

    public sealed class UdpSock
    {
        public int Pid;
        public string Local = "";
    }

    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 1;
    private const int UDP_TABLE_OWNER_PID = 1;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int cls, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int cls, int reserved);

    private static string Ip4(uint a)
    {
        return (a & 0xFF).ToString() + "." + ((a >> 8) & 0xFF) + "." + ((a >> 16) & 0xFF) + "." + ((a >> 24) & 0xFF);
    }

    private static ushort NetPort(uint v)
    {
        return (ushort)(((v & 0xFF) << 8) | ((v >> 8) & 0xFF));
    }

    public static List<TcpConn> GetTcpConnections()
    {
        var res = new List<TcpConn>();
        CollectTcp(AF_INET, res);
        CollectTcp(AF_INET6, res);
        return res;
    }

    private static void CollectTcp(int family, List<TcpConn> res)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, family, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            uint rc = GetExtendedTcpTable(buf, ref size, true, family, TCP_TABLE_OWNER_PID_ALL, 0);
            if (rc != 0) return;
            int n = Marshal.ReadInt32(buf, 0);
            if (n <= 0 || n > 200000) return;
            if (family == AF_INET)
            {
                for (int i = 0; i < n; i++)
                {
                    int o = 4 + i * 24;
                    var c = new TcpConn
                    {
                        Pid = Marshal.ReadInt32(buf, o + 20),
                        State = Marshal.ReadInt32(buf, o),
                        Local = Ip4(Marshal.ReadUInt32(buf, o + 4)) + ":" + NetPort(Marshal.ReadUInt32(buf, o + 8)),
                        Remote = Ip4(Marshal.ReadUInt32(buf, o + 12)) + ":" + NetPort(Marshal.ReadUInt32(buf, o + 16))
                    };
                    res.Add(c);
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    int o = 4 + i * 56;
                    byte[] la = new byte[16], ra = new byte[16];
                    for (int k = 0; k < 16; k++) { la[k] = Marshal.ReadByte(buf, o + k); ra[k] = Marshal.ReadByte(buf, o + 24 + k); }
                    var c = new TcpConn
                    {
                        IsV6 = true,
                        Pid = Marshal.ReadInt32(buf, o + 52),
                        State = Marshal.ReadInt32(buf, o + 48),
                        Local = new IPAddress(la) + ":" + NetPort(Marshal.ReadUInt32(buf, o + 20)),
                        Remote = new IPAddress(ra) + ":" + NetPort(Marshal.ReadUInt32(buf, o + 44))
                    };
                    res.Add(c);
                }
            }
        }
        catch { }
        finally { try { Marshal.FreeHGlobal(buf); } catch { } }
    }

    public static List<UdpSock> GetUdpSockets()
    {
        var res = new List<UdpSock>();
        CollectUdp(AF_INET, res);
        CollectUdp(AF_INET6, res);
        return res;
    }

    private static void CollectUdp(int family, List<UdpSock> res)
    {
        int size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, true, family, UDP_TABLE_OWNER_PID, 0);
        if (size <= 0) return;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            uint rc = GetExtendedUdpTable(buf, ref size, true, family, UDP_TABLE_OWNER_PID, 0);
            if (rc != 0) return;
            int n = Marshal.ReadInt32(buf, 0);
            if (n <= 0 || n > 200000) return;
            if (family == AF_INET)
            {
                for (int i = 0; i < n; i++)
                {
                    int o = 4 + i * 12;
                    res.Add(new UdpSock
                    {
                        Pid = Marshal.ReadInt32(buf, o + 8),
                        Local = Ip4(Marshal.ReadUInt32(buf, o)) + ":" + NetPort(Marshal.ReadUInt32(buf, o + 4))
                    });
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    int o = 4 + i * 28;
                    byte[] la = new byte[16];
                    for (int k = 0; k < 16; k++) la[k] = Marshal.ReadByte(buf, o + k);
                    res.Add(new UdpSock
                    {
                        Pid = Marshal.ReadInt32(buf, o + 24),
                        Local = new IPAddress(la) + ":" + NetPort(Marshal.ReadUInt32(buf, o + 20))
                    });
                }
            }
        }
        catch { }
        finally { try { Marshal.FreeHGlobal(buf); } catch { } }
    }

    public static string TcpStateName(int s)
    {
        switch (s)
        {
            case 1: return "CLOSED";
            case 2: return "LISTEN";
            case 3: return "SYN_SENT";
            case 4: return "SYN_RCVD";
            case 5: return "ESTABLISHED";
            case 6: return "FIN_WAIT1";
            case 7: return "FIN_WAIT2";
            case 8: return "CLOSE_WAIT";
            case 9: return "CLOSING";
            case 10: return "LAST_ACK";
            case 11: return "TIME_WAIT";
            case 12: return "DELETE_TCB";
            default: return "STATE_" + s;
        }
    }

    // ------------------------------------------------------------------- RDP-сессии
    [DllImport("wtsapi32.dll")]
    private static extern int WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr p);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, int infoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public int State;
    }

    private const int WTSUserName = 5;
    private const int WTSDomainName = 7;
    private const int WTSClientName = 10;

    public static List<SessionInfo> GetSessions()
    {
        var res = new List<SessionInfo>();
        IntPtr p = IntPtr.Zero;
        int cnt = 0;
        try
        {
            if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out p, out cnt) == 0) return res;
            int sz = Marshal.SizeOf<WtsSessionInfo>();
            for (int i = 0; i < cnt; i++)
            {
                var si = Marshal.PtrToStructure<WtsSessionInfo>(new IntPtr(p.ToInt64() + (long)i * sz));
                var s = new SessionInfo
                {
                    Id = si.SessionId,
                    Station = si.pWinStationName == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(si.pWinStationName) ?? "",
                    State = SessionStateName(si.State),
                    User = WtsQueryStr(si.SessionId, WTSUserName),
                    Domain = WtsQueryStr(si.SessionId, WTSDomainName),
                    Client = WtsQueryStr(si.SessionId, WTSClientName)
                };
                res.Add(s);
            }
        }
        catch { }
        finally
        {
            if (p != IntPtr.Zero) try { WTSFreeMemory(p); } catch { }
        }
        return res;
    }

    private static string WtsQueryStr(int sessionId, int cls)
    {
        IntPtr b = IntPtr.Zero;
        int n = 0;
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, cls, out b, out n)) return "";
            return Marshal.PtrToStringUni(b) ?? "";
        }
        catch { return ""; }
        finally { if (b != IntPtr.Zero) try { WTSFreeMemory(b); } catch { } }
    }

    private static string SessionStateName(int st)
    {
        switch (st)
        {
            case 0: return "Active";
            case 1: return "Connected";
            case 2: return "ConnectQuery";
            case 3: return "Shadow";
            case 4: return "Disconnected";
            case 5: return "Idle";
            case 6: return "Listen";
            case 7: return "Reset";
            case 8: return "Down";
            case 9: return "Init";
            default: return "State" + st;
        }
    }

    // -------------------------------------------------- зависшие окна приложений
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    public static HashSet<int> GetHungWindowPids()
    {
        var set = new HashSet<int>();
        try
        {
            EnumWindows((h, l) =>
            {
                try
                {
                    if (!IsWindowVisible(h)) return true;
                    if (!IsHungAppWindow(h)) return true;
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (pid > 4) set.Add((int)pid);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return set;
    }

    // ------------------------------------------------------- открытые SMB-файлы
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetFileEnum(string server, string basePath, string user, int level,
        out IntPtr bufPtr, int prefMaxLen, out int entriesRead, out int totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buf);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo3
    {
        public uint fi3_id;
        public uint fi3_permissions;
        public uint fi3_num_locks;
        public IntPtr fi3_path;
        public IntPtr fi3_user;
    }

    /// <summary>Список открытых по сети файлов (требуются права администратора).</summary>
    public static (List<OpenFileRec> files, string error) GetOpenFiles()
    {
        var list = new List<OpenFileRec>();
        IntPtr buf = IntPtr.Zero;
        int read = 0, total = 0;
        try
        {
            int rc = NetFileEnum(null, null, null, 3, out buf, -1, out read, out total, IntPtr.Zero);
            if (rc != 0) return (list, "код " + rc + (rc == 5 ? " (нет прав администратора)" : ""));
            int sz = Marshal.SizeOf<FileInfo3>();
            for (int i = 0; i < read && list.Count < 10000; i++)
            {
                var fi = Marshal.PtrToStructure<FileInfo3>(new IntPtr(buf.ToInt64() + (long)i * sz));
                list.Add(new OpenFileRec
                {
                    Path = fi.fi3_path == IntPtr.Zero ? "" : Marshal.PtrToStringUni(fi.fi3_path) ?? "",
                    User = fi.fi3_user == IntPtr.Zero ? "" : Marshal.PtrToStringUni(fi.fi3_user) ?? "",
                    Locks = (int)fi.fi3_num_locks
                });
            }
            return (list, "");
        }
        catch (Exception ex) { return (list, ex.Message); }
        finally { if (buf != IntPtr.Zero) try { NetApiBufferFree(buf); } catch { } }
    }

    // ------------------------------------------------------------------ реестр
    private static readonly UIntPtr HKEY_LOCAL_MACHINE = new UIntPtr(0x80000002u);
    private const uint RRF_RT_REG_SZ = 0x02;
    private const uint RRF_RT_REG_MULTI_SZ = 0x10;
    private const int ERROR_MORE_DATA = 234;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegGetValueW(UIntPtr hkey, string lpSubKey, string lpValue, uint dwFlags,
        out uint pdwType, IntPtr pvData, ref uint pcbData);

    public static string RegGetString(string subKey, string valueName)
    {
        uint size = 1024;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            int rc;
            while (true)
            {
                rc = RegGetValueW(HKEY_LOCAL_MACHINE, subKey, valueName, RRF_RT_REG_SZ, out uint type, buf, ref size);
                if (rc == ERROR_MORE_DATA && size < 1 << 20) { buf = Marshal.ReAllocHGlobal(buf, (IntPtr)(long)size); continue; }
                break;
            }
            return rc == 0 ? (Marshal.PtrToStringUni(buf) ?? "") : "";
        }
        catch { return ""; }
        finally { try { Marshal.FreeHGlobal(buf); } catch { } }
    }

    public static List<string> RegGetMultiString(string subKey, string valueName)
    {
        var res = new List<string>();
        uint size = 2048;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            int rc;
            while (true)
            {
                rc = RegGetValueW(HKEY_LOCAL_MACHINE, subKey, valueName, RRF_RT_REG_MULTI_SZ, out uint type, buf, ref size);
                if (rc == ERROR_MORE_DATA && size < 1 << 20) { buf = Marshal.ReAllocHGlobal(buf, (IntPtr)(long)size); continue; }
                break;
            }
            if (rc == 0) res = ReadMultiSzU(buf);
        }
        catch { }
        finally { try { Marshal.FreeHGlobal(buf); } catch { } }
        return res;
    }

    private static List<string> ReadMultiSzU(IntPtr buffer)
    {
        var list = new List<string>();
        var sb = new System.Text.StringBuilder();
        long off = 0;
        while (true)
        {
            short ch = Marshal.ReadInt16(buffer, (int)off);
            if (ch == 0)
            {
                if (sb.Length == 0) break;
                list.Add(sb.ToString()); sb.Clear(); off += 2;
            }
            else { sb.Append((char)ch); off += 2; }
        }
        return list;
    }

    // ------------------------------------------------------------- power scheme
    public static string GetActivePowerScheme()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ExpandEnvironmentVariables("%SystemRoot%\\System32\\powercfg.exe"),
                Arguments = "/getactivescheme",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return "";
            string outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } }
            return outp.Trim();
        }
        catch { return ""; }
    }

    // ---------------------------------------------------------------- elevation
    public static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var pr = new System.Security.Principal.WindowsPrincipal(id);
            return pr.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
