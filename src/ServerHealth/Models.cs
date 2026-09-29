namespace ServerHealth;

/// <summary>Замер системных счётчиков (один тик мониторинга).</summary>
public sealed class SystemSample
{
    public DateTime Ts;

    // CPU
    public double CpuTotal, CpuPriv, CpuUser, CpuDpc, CpuIntr;
    public double ProcQueueLen, CtxSwitch;
    public List<KeyValuePair<string, double>> CoreCpu = new List<KeyValuePair<string, double>>();

    // RAM
    public double AvailMb, CommitPct, CommitBytes, CommitLimitBytes, PagesIn, PagesOut, HardFaults, PageFaults;
    public double PoolNonPaged, PoolPaged, PagingFilePct;

    // Системные ресурсы
    public uint SysHandles, SysThreads, SysProcesses;

    // Диски
    public List<DiskSample> Physical = new List<DiskSample>();
    public List<LogicDiskSample> Logical = new List<LogicDiskSample>();
    /// <summary>Источник метрик нагрузки: "PhysicalDisk" / "LogicalDisk" / "" (нет).</summary>
    public string DiskSource = "";

    // Сеть
    public List<NetSample> Nets = new List<NetSample>();

    // TCP
    public int TcpEstablished, TcpTimeWait, TcpCloseWait, TcpListen, TcpSynSent, TcpSynRecv, TcpFin, TcpOther, TcpTotal, UdpEndpoints;

    // Подсказки из счётчиков Terminal Services (-1 = счётчик недоступен)
    public int SessionsActiveHint = -1;
    public int SessionsInactiveHint = -1;
}

public sealed class DiskSample
{
    public string Name = "";
    /// <summary>'P' — PhysicalDisk, 'L' — LogicalDisk (запасной источник, часто в ВМ).</summary>
    public char Source = 'P';
    public double QueueCur;      // Current Disk Queue Length
    public double QueueAvg;      // Avg. Disk Queue Length (счётчик)
    public double BusyPct;       // 100 - % Idle Time = занятость диска
    public double LatReadMs, LatWriteMs;
    public double ReadMbps, WriteMbps, Iops;
}

public sealed class LogicDiskSample
{
    public string Name = "";
    public double FreePct, FreeMb;
}

public sealed class NetSample
{
    public string Name = "";
    public double RxMbps, TxMbps, RxPps, TxPps, RxEps, TxEps, BandwidthMbps;
}

/// <summary>Мгновенный срез по процессу.</summary>
public sealed class ProcessTick
{
    public int Pid;
    public string Name = "";
    public double CpuPct;
    public long Ws, Private;
    public long Handles;
    public double ReadMbps, WriteMbps, ReadOps, WriteOps;
    public int TcpEst, TcpTotal;
    public bool Hung;
}

/// <summary>Накопитель агрегатов по процессу за весь период наблюдения.</summary>
public sealed class ProcAgg
{
    public int Pid;
    public string Name = "";
    public int Samples;
    public double CpuSum, CpuMax;
    public double CpuSeconds;             // суммарное процессорное время за период
    public double PrivSum; public long PrivFirst, PrivLast, PrivMax;
    public double WsSum; public long WsMax;
    public long HandlesMax;
    public double ReadMbpsSum, WriteMbpsSum, ReadMbpsMax, WriteMbpsMax;
    public double ReadOpsSum, WriteOpsSum;
    public int TcpEstMax, TcpTotalMax;
    public int HungCount;
    public int TopCpuTimes, TopMemTimes, TopIoTimes;

    public double CpuAvg { get { return Samples > 0 ? CpuSum / Samples : 0; } }
    public double PrivAvgMb { get { return Samples > 0 ? PrivSum / Samples / 1048576.0 : 0; } }
    public double PrivMaxMb { get { return PrivMax / 1048576.0; } }
    public double PrivLastMb { get { return PrivLast / 1048576.0; } }
    public double WsAvgMb { get { return Samples > 0 ? WsSum / Samples / 1048576.0 : 0; } }
    public double WsMaxMb { get { return WsMax / 1048576.0; } }
    public double ReadMbpsAvg { get { return Samples > 0 ? ReadMbpsSum / Samples : 0; } }
    public double WriteMbpsAvg { get { return Samples > 0 ? WriteMbpsSum / Samples : 0; } }
    public double ReadOpsAvg { get { return Samples > 0 ? ReadOpsSum / Samples : 0; } }
    public double WriteOpsAvg { get { return Samples > 0 ? WriteOpsSum / Samples : 0; } }
    /// <summary>Рост частной памяти, МБ/час (может быть отрицательным).</summary>
    public double GrowthMbPerHour(TimeSpan dur)
    {
        if (Samples < 2 || dur.TotalHours < 0.02) return 0;
        return (PrivLast - PrivFirst) / 1048576.0 / dur.TotalHours;
    }
}

/// <summary>Группа событий журнала Windows.</summary>
public sealed class EventGroup
{
    public string Log = "";
    public string Provider = "";
    public long Id;
    public int Level;
    public string LevelName = "";
    public int Count;
    public DateTime First, Last;
    public string Sample = "";
    public bool DuringCollection;

    public bool IsDiskRelated
    {
        get
        {
            string p = Provider.ToLowerInvariant();
            return p.Contains("disk") || p.Contains("ntfs") || p.Contains("stor") || p.Contains("volmgr") ||
                   p.Contains("volsnap") || p.Contains("iaStor") || p.Contains("nvme") || p.Contains("megasas") ||
                   p.Contains("percsas") || p.Contains("lsi") || p.Contains("vmd") || p.Contains("viostor");
        }
    }
    public bool IsNetRelated
    {
        get
        {
            string p = Provider.ToLowerInvariant();
            return p.Contains("lanman") || p.Contains("srv2") || p.Contains("srvnet") || p.Contains("netbt") ||
                   p.Contains("tcpip") || p.Contains("ndis") || p.Contains("e1c") || p.Contains("e1d") ||
                   p.Contains("vmxnet") || p.Contains("hnls") || p.Contains("tcp");
        }
    }
    public bool IsRdpRelated
    {
        get
        {
            string p = Provider.ToLowerInvariant();
            return p.Contains("termservice") || p.Contains("termdd") || p.Contains("rdp") ||
                   p.Contains("remoteconnectionmanager") || p.Contains("terminalservices");
        }
    }
    public bool IsHardwareRelated
    {
        get
        {
            string p = Provider.ToLowerInvariant();
            return p.Contains("whea") || p.Contains("bugcheck") || p.Contains("kernel-power") ||
                   p.Contains("kernel-pnp") || p.Contains("hal") || p.Contains("acpi");
        }
    }
}

/// <summary>Сводка по TCP за период.</summary>
public sealed class TcpSummary
{
    public Dictionary<string, int> StatesMax = new Dictionary<string, int>();
    public List<KeyValuePair<string, int>> TopRemote = new List<KeyValuePair<string, int>>();
    public List<KeyValuePair<int, int>> TopPidEst = new List<KeyValuePair<int, int>>(); // pid -> max established
}

/// <summary>Сводка по открытым SMB-файлам.</summary>
public sealed class OpenFilesSummary
{
    public int TotalMax;
    public string Error = "";
    public List<KeyValuePair<string, int>> TopPaths = new List<KeyValuePair<string, int>>();
    public List<KeyValuePair<string, int>> TopUsers = new List<KeyValuePair<string, int>>();
}

/// <summary>Результат анализа.</summary>
public sealed class Finding
{
    public string Category = "";    // CPU / RAM / Disk / Net / Hang / Events / Config
    public string Severity = "";    // CRITICAL / WARNING / INFO
    public string Title = "";
    public string Symptom = "";
    public string Cause = "";
    public List<string> Actions = new List<string>();
    public List<string> Verify = new List<string>();
}

public sealed class AnalysisResult
{
    public List<Finding> Findings = new List<Finding>();
    public List<KeyValuePair<string, int>> Scores = new List<KeyValuePair<string, int>>();
    public string VerdictTitle = "";
    public string VerdictText = "";
    public bool HasCritical;
}
