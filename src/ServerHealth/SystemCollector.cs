using System.Diagnostics;
using ServerHealth.Interop;

namespace ServerHealth;

/// <summary>
/// Сборщик системных метрик через PDH (англоязычные пути счётчиков — не зависят
/// от локализации Windows) и системных API.
/// </summary>
internal sealed class SystemCollector : IDisposable
{
    private readonly PdhQuery _q = new PdhQuery();

    private readonly List<string> _physInstances = new List<string>();
    private readonly List<string> _logInstances = new List<string>();
    private readonly List<string> _netInstances = new List<string>();
    private readonly List<string> _pfInstances = new List<string>();
    private readonly List<string> _coreInstances = new List<string>();

    /// <summary>Метрики нагрузки брать с LogicalDisk (PhysicalDisk недоступен — характерно для ВМ).</summary>
    private bool _useLogicalPerf = false;
    /// <summary>Источник метрик нагрузки диска: "PhysicalDisk" / "LogicalDisk" / "" (нет).</summary>
    public string DiskSource = "";

    private static readonly string[] PhysCounters =
    {
        "% Idle Time", "Avg. Disk sec/Read", "Avg. Disk sec/Write",
        "Avg. Disk Queue Length", "Disk Read Bytes/sec", "Disk Write Bytes/sec", "Disk Transfers/sec"
    };
    private const string PHYS_CURQ = "Current Disk Queue Length";

    private static readonly string[] LogCounters = { "% Free Space", "Free Megabytes" };

    private static readonly string[] NetCounters =
    {
        "Bytes Received/sec", "Bytes Sent/sec", "Packets Received/sec", "Packets Sent/sec",
        "Packets Received Errors", "Packets Outbound Errors", "Current Bandwidth"
    };

    private const string C_CPU_TOTAL = @"\Processor(_Total)\% Processor Time";
    private const string C_CPU_PRIV = @"\Processor(_Total)\% Privileged Time";
    private const string C_CPU_USER = @"\Processor(_Total)\% User Time";
    private const string C_CPU_DPC = @"\Processor(_Total)\% DPC Time";
    private const string C_CPU_INT = @"\Processor(_Total)\% Interrupt Time";
    private const string C_QUEUE = @"\System\Processor Queue Length";
    private const string C_CTX = @"\System\Context Switches/sec";
    private const string C_AVAIL = @"\Memory\Available MBytes";
    private const string C_COMMIT = @"\Memory\Committed Bytes";
    private const string C_COMMIT_LIM = @"\Memory\Commit Limit";
    private const string C_HARD = @"\Memory\Pages/sec";
    private const string C_PF = @"\Memory\Page Faults/sec";
    private const string C_PIN = @"\Memory\Pages Input/sec";
    private const string C_POUT = @"\Memory\Pages Output/sec";
    private const string C_POOL_NP = @"\Memory\Pool Nonpaged Bytes";
    private const string C_POOL_P = @"\Memory\Pool Paged Bytes";
    private const string C_TS_ACT = @"\Terminal Services\Active Sessions";
    private const string C_TS_INACT = @"\Terminal Services\Inactive Sessions";
    private const string C_SRV_REJ = @"\Server\Blocking Requests Rejected";
    private const string C_SRV_SHORT = @"\Server\Work Item Shortages";

    /// <summary>Счётчики, недоступные на этой системе (для отчёта).</summary>
    public List<string> Skipped { get { return _q.SkippedCounters; } }

    public string LastError = "";

    public SystemCollector()
    {
    }

    /// <summary>Открыть запрос, добавить счётчики, сделать базовый замер.</summary>
    public void Initialize()
    {
        // разворачиваем шаблоны экземпляров
        foreach (var p in PdhNative.ExpandWildcard(@"\PhysicalDisk(*)\Avg. Disk sec/Read"))
        {
            string inst = PdhNative.InstanceOf(p);
            if (inst != "_Total") _physInstances.Add(inst);   // _Total считаем сами, иначе усреднения задваиваются
        }
        foreach (var p in PdhNative.ExpandWildcard(@"\LogicalDisk(*)\% Free Space"))
            _logInstances.Add(PdhNative.InstanceOf(p));

        // Виртуальные среды: объект PhysicalDisk иногда отсутствует (гостевые драйверы
        // ESXi PVSCSI / Hyper-V storvsc), но LogicalDisk с полными метриками нагрузки есть
        // всегда. Тогда собираем нагрузку и очереди по томам — иначе раздел дисков пустой.
        if (_physInstances.Count == 0)
        {
            foreach (var p in PdhNative.ExpandWildcard(@"\LogicalDisk(*)\Avg. Disk sec/Read"))
            {
                string inst = PdhNative.InstanceOf(p);
                if (inst != "_Total" && !_physInstances.Contains(inst)) _physInstances.Add(inst);
            }
            if (_physInstances.Count > 0)
            {
                _useLogicalPerf = true;
                DiskSource = "LogicalDisk";
            }
        }
        else DiskSource = "PhysicalDisk";
        foreach (var p in PdhNative.ExpandWildcard(@"\Network Interface(*)\Bytes Received/sec"))
            _netInstances.Add(PdhNative.InstanceOf(p));
        foreach (var p in PdhNative.ExpandWildcard(@"\Paging File(*)\% Usage"))
            _pfInstances.Add(PdhNative.InstanceOf(p));
        foreach (var p in PdhNative.ExpandWildcard(@"\Processor(*)\% Processor Time"))
        {
            string inst = PdhNative.InstanceOf(p);
            if (inst != "_Total") _coreInstances.Add(inst);
        }

        // простые (безэкземплярные) счётчики
        foreach (var c in new[] { C_CPU_TOTAL, C_CPU_PRIV, C_CPU_USER, C_CPU_DPC, C_CPU_INT, C_QUEUE, C_CTX,
                                  C_AVAIL, C_COMMIT, C_COMMIT_LIM, C_HARD, C_PF, C_PIN, C_POUT, C_POOL_NP, C_POOL_P,
                                  C_TS_ACT, C_TS_INACT, C_SRV_REJ, C_SRV_SHORT })
            _q.AddEnglish(c);

        // метрики нагрузки дисков (PhysicalDisk или LogicalDisk — см. выше)
        string diskObj = _useLogicalPerf ? "LogicalDisk" : "PhysicalDisk";
        foreach (var inst in _physInstances)
        {
            string b = @"\" + diskObj + "(" + inst + @")\";
            foreach (var c in PhysCounters)
                _q.AddEnglish(b + c);
            _q.AddEnglish(b + PHYS_CURQ);
        }
        // логические диски
        foreach (var inst in _logInstances)
            foreach (var c in LogCounters)
                _q.AddEnglish(@"\LogicalDisk(" + inst + @")\" + c);
        // сеть
        foreach (var inst in _netInstances)
            foreach (var c in NetCounters)
                _q.AddEnglish(@"\Network Interface(" + inst + @")\" + c);
        // файл подкачки
        foreach (var inst in _pfInstances)
            _q.AddEnglish(@"\Paging File(" + inst + @")\% Usage");
        // ядра CPU
        foreach (var inst in _coreInstances)
            _q.AddEnglish(@"\Processor(" + inst + @")\% Processor Time");

        // базовый замер: следующий Collect даст корректные значения rate-счётчиков
        _q.Collect();
    }

    public void Collect() { _q.Collect(); }

    public SystemSample ReadSample()
    {
        var s = new SystemSample { Ts = DateTime.Now };

        s.CpuTotal = _q.ReadDouble(C_CPU_TOTAL);
        s.CpuPriv = _q.ReadDouble(C_CPU_PRIV);
        s.CpuUser = _q.ReadDouble(C_CPU_USER);
        s.CpuDpc = _q.ReadDouble(C_CPU_DPC);
        s.CpuIntr = _q.ReadDouble(C_CPU_INT);
        s.ProcQueueLen = _q.ReadDouble(C_QUEUE);
        s.CtxSwitch = _q.ReadDouble(C_CTX);

        s.AvailMb = _q.ReadDouble(C_AVAIL);
        double commit = _q.ReadDouble(C_COMMIT);
        double limit = _q.ReadDouble(C_COMMIT_LIM);
        s.CommitBytes = commit;
        s.CommitLimitBytes = limit;
        if (!double.IsNaN(limit) && limit > 0 && !double.IsNaN(commit)) s.CommitPct = commit / limit * 100.0;

        s.HardFaults = _q.ReadDouble(C_HARD);
        s.PageFaults = _q.ReadDouble(C_PF);
        s.PagesIn = _q.ReadDouble(C_PIN);
        s.PagesOut = _q.ReadDouble(C_POUT);
        s.PoolNonPaged = _q.ReadDouble(C_POOL_NP);
        s.PoolPaged = _q.ReadDouble(C_POOL_P);

        double pf = double.NaN;
        foreach (var inst in _pfInstances)
        {
            double v = _q.ReadDouble(@"\Paging File(" + inst + @")\% Usage");
            if (!double.IsNaN(v) && (double.IsNaN(pf) || v > pf)) pf = v;
        }
        s.PagingFilePct = pf;

        double act = _q.ReadDouble(C_TS_ACT);
        double inact = _q.ReadDouble(C_TS_INACT);
        LastSrvRejected = _q.ReadDouble(C_SRV_REJ);
        LastSrvShort = _q.ReadDouble(C_SRV_SHORT);
        if (!double.IsNaN(act)) s.SessionsActiveHint = (int)act;
        if (!double.IsNaN(inact)) s.SessionsInactiveHint = (int)inact;

        // ядра
        foreach (var inst in _coreInstances)
        {
            double v = _q.ReadDouble(@"\Processor(" + inst + @")\% Processor Time");
            if (!double.IsNaN(v)) s.CoreCpu.Add(new KeyValuePair<string, double>(inst, v));
        }

        // метрики нагрузки дисков
        string dObj = _useLogicalPerf ? "LogicalDisk" : "PhysicalDisk";
        s.DiskSource = DiskSource;
        foreach (var inst in _physInstances)
        {
            string b = @"\" + dObj + "(" + inst + @")\";
            double idle = _q.ReadDouble(b + "% Idle Time");
            var d = new DiskSample
            {
                Name = inst,
                Source = _useLogicalPerf ? 'L' : 'P',
                LatReadMs = _q.ReadDouble(b + "Avg. Disk sec/Read") * 1000.0,
                LatWriteMs = _q.ReadDouble(b + "Avg. Disk sec/Write") * 1000.0,
                ReadMbps = _q.ReadDouble(b + "Disk Read Bytes/sec") / 131072.0,   // байт/с -> Мбит/с
                WriteMbps = _q.ReadDouble(b + "Disk Write Bytes/sec") / 131072.0,
                Iops = _q.ReadDouble(b + "Disk Transfers/sec"),
                QueueCur = _q.ReadDouble(b + PHYS_CURQ),
                QueueAvg = _q.ReadDouble(b + "Avg. Disk Queue Length"),
                BusyPct = double.IsNaN(idle) ? double.NaN : Math.Clamp(100.0 - idle, 0, 100)
            };
            s.Physical.Add(d);
        }

        // логические диски
        foreach (var inst in _logInstances)
        {
            string b = @"\LogicalDisk(" + inst + @")\";
            s.Logical.Add(new LogicDiskSample
            {
                Name = inst,
                FreePct = _q.ReadDouble(b + "% Free Space"),
                FreeMb = _q.ReadDouble(b + "Free Megabytes")
            });
        }

        // сеть
        foreach (var inst in _netInstances)
        {
            string b = @"\Network Interface(" + inst + @")\";
            double bw = _q.ReadDouble(b + "Current Bandwidth"); // бит/с
            var n = new NetSample
            {
                Name = inst,
                BandwidthMbps = double.IsNaN(bw) ? 0 : bw / 1000000.0,
                RxMbps = _q.ReadDouble(b + "Bytes Received/sec") * 8 / 1000000.0,
                TxMbps = _q.ReadDouble(b + "Bytes Sent/sec") * 8 / 1000000.0,
                RxPps = _q.ReadDouble(b + "Packets Received/sec"),
                TxPps = _q.ReadDouble(b + "Packets Sent/sec"),
                RxEps = _q.ReadDouble(b + "Packets Received Errors"),
                TxEps = _q.ReadDouble(b + "Packets Outbound Errors")
            };
            s.Nets.Add(n);
        }

        // системные ресурсы
        var pi = Native.GetPerfInfo();
        if (pi != null)
        {
            s.SysHandles = pi.HandleCount;
            s.SysThreads = pi.ThreadCount;
            s.SysProcesses = pi.ProcessCount;
        }

        return s;
    }

    public double LastSrvRejected;
    public double LastSrvShort;

    public void Dispose()
    {
        try { _q.Dispose(); } catch { }
    }
}
