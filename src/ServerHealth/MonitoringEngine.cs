using System.Diagnostics;
using ServerHealth.Interop;

namespace ServerHealth;

public enum MonitorState { Idle, Running, Stopping }

/// <summary>Точка живого графика (обновляется на каждом замере).</summary>
public sealed class LivePoint
{
    public DateTime Ts;
    public double Cpu;
    public double AvailMb;
    public double NetMbps;
    public double DiskLatMaxMs;
    public double DiskQueueAvg;
    public int TcpEst;
    public double ProcQueue;
}

/// <summary>Результат завершённого цикла мониторинга (отчёт).</summary>
public sealed class CycleResult
{
    public int CycleNumber;
    public DateTime Finished;
    public string Dir = "";
    public ReportWriter.Paths? Files;
    public AnalysisResult? Analysis;
    public int Samples;
    public TimeSpan Duration;
    public List<ProcAgg> TopProcs = new List<ProcAgg>();
    public string VerdictTitle = "";
    public bool HasCritical;
}

/// <summary>Настройки движка мониторинга.</summary>
public sealed class MonitoringOptions
{
    /// <summary>Интервал замера, секунд (1–3600).</summary>
    public int IntervalSec = 5;
    /// <summary>Длительность одного цикла = период формирования отчёта, минут (0.1–1440).</summary>
    public double CycleMinutes = 10;
    /// <summary>Глубина анализа журналов событий, часов (0 = не читать).</summary>
    public int EventsHours = 24;
    /// <summary>Корневой каталог отчётов (пусто = Документы\ServerHealth).</summary>
    public string OutRoot = "";
    /// <summary>Сколько последних отчётов хранить (0 = все).</summary>
    public int KeepReports = 0;
    /// <summary>Автообновление report.html, секунд (0 = не обновлять).</summary>
    public int HtmlRefreshSec = 60;
}

/// <summary>
/// Движок непрерывного мониторинга: бесконечные циклы «сбор → анализ → отчёт».
/// Используется и консольным режимом --watch, и GUI. Все решения о UI — через события.
/// </summary>
public sealed class MonitoringEngine : IDisposable
{
    private readonly MonitoringOptions _o;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly object _liveLock = new();
    private readonly List<LivePoint> _live = new List<LivePoint>(4096);
    private SystemCollector? _sys;
    private ProcessCollector? _proc;

    public MonitorState State { get; private set; } = MonitorState.Idle;
    public SysInfo Sys { get; private set; }
    public int CycleNumber { get; private set; }
    public int ReportsDone { get; private set; }
    public int TickCount { get; private set; }
    public int PlannedTicks { get; private set; }
    public DateTime NextReportAt { get; private set; }
    public CycleResult? Last { get; private set; }
    public string OutRoot { get; private set; }
    /// <summary>Последняя живая точка (обновляется на каждом замере, потокобезопасно).</summary>
    public LivePoint? LastPoint { get; private set; }
    /// <summary>Путь к постоянному дашборду (dashboard.html — всегда последний цикл).</summary>
    public string? DashboardPath { get; private set; }

    public event Action<LivePoint>? Tick;
    public event Action<CycleResult>? CycleCompleted;
    public event Action<string>? Log;
    public event Action<string>? CriticalDetected;

    public MonitoringEngine(MonitoringOptions options)
    {
        _o = options;
        _o.IntervalSec = Math.Clamp(_o.IntervalSec, 1, 3600);
        _o.CycleMinutes = Math.Clamp(_o.CycleMinutes, 0.1, 1440);
        OutRoot = ResolveRoot(_o.OutRoot);
        Sys = BuildSysInfo();
        PlannedTicks = Math.Max(1, (int)Math.Ceiling(_o.CycleMinutes * 60 / _o.IntervalSec));
    }

    private static string ResolveRoot(string root)
    {
        if (!string.IsNullOrWhiteSpace(root)) return root;
        try
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ServerHealth");
        }
        catch { return "ServerHealth"; }
    }

    /// <summary>Собрать статическую информацию об узле.</summary>
    public static SysInfo BuildSysInfo()
    {
        bool elevated = OperatingSystem.IsWindows() && Native.IsElevated();
        var sys = new SysInfo
        {
            Machine = Environment.MachineName,
            User = Environment.UserName,
            Domain = Environment.UserDomainName,
            Os = Environment.OSVersion.VersionString + " (" + (Environment.Is64BitOperatingSystem ? "x64" : "x86") + ")",
            CpuName = OperatingSystem.IsWindows()
                ? Native.RegGetString(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString").Trim()
                : "",
            Cores = Environment.ProcessorCount,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            Elevated = elevated,
            PowerPlan = OperatingSystem.IsWindows() ? Native.GetActivePowerScheme() : "",
            AppVersion = typeof(MonitoringEngine).Assembly.GetName().Version?.ToString(3) ?? "1.0",
            CollectedStart = DateTime.Now
        };
        if (OperatingSystem.IsWindows())
        {
            var mem = Native.GlobalMemory();
            sys.RamGb = mem.total / 1073741824.0;
            var pf = Native.RegGetMultiString(@"SYSTEM\CurrentControlSet\Session Manager\Memory Management", "PagingFiles");
            sys.PageFileConfig = pf.Count == 0 ? "не задан" : string.Join("; ", pf);
        }
        if (sys.CpuName.Length == 0) sys.CpuName = "не определён";
        return sys;
    }

    // -------------------------------------------------------------- управление
    public void Start()
    {
        if (State != MonitorState.Idle) return;
        _stop = false;
        State = MonitorState.Running;
        Log?.Invoke("Мониторинг запущен. Отчёты: " + Path.GetFullPath(OutRoot));
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "ServerHealth.Monitor" };
        _thread.Start();
    }

    public void Stop()
    {
        if (State != MonitorState.Running) return;
        State = MonitorState.Stopping;
        _stop = true;
        Log?.Invoke("Остановка мониторинга…");
        try { _thread?.Join(20000); } catch { }
        State = MonitorState.Idle;
    }

    public bool IsRunning { get { return State == MonitorState.Running; } }

    /// <summary>Снимок живых точек для графика.</summary>
    public List<LivePoint> LiveSnapshot()
    {
        lock (_liveLock) return new List<LivePoint>(_live);
    }

    public void Dispose()
    {
        Stop();
        try { _sys?.Dispose(); } catch { }
    }

    // ------------------------------------------------------------------- цикл
    private void RunLoop()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Log?.Invoke("Ошибка: мониторинг поддерживается только в Windows.");
                State = MonitorState.Idle;
                return;
            }
            _sys = new SystemCollector();
            _sys.Initialize();
            foreach (var skipped in _sys.Skipped)
                Log?.Invoke("Счётчик недоступен: " + skipped);
        }
        catch (Exception ex)
        {
            Log?.Invoke("Ошибка счётчиков производительности: " + ex.Message + " (попробуйте 'lodctr /r' и запуск от администратора)");
            State = MonitorState.Idle;
            return;
        }
        _proc = new ProcessCollector();
        Directory.CreateDirectory(OutRoot);

        while (!_stop)
        {
            CycleResult? cycle = null;
            try { cycle = RunCycle(); }
            catch (Exception ex) { Log?.Invoke("Ошибка цикла: " + ex.Message); }
            if (cycle != null)
            {
                Last = cycle;
                ReportsDone++;
                Log?.Invoke($"Отчёт #{cycle.CycleNumber} готов: {cycle.Dir}");
                if (DashboardPath != null)
                    Log?.Invoke("Дашборд (браузер): " + DashboardPath);
                Log?.Invoke("Вердикт: " + cycle.VerdictTitle);
                try { CycleCompleted?.Invoke(cycle); } catch { }
                if (cycle.HasCritical)
                {
                    var firstCrit = cycle.Analysis?.Findings.FirstOrDefault(f => f.Severity == "CRITICAL");
                    try { CriticalDetected?.Invoke(firstCrit?.Title ?? "Обнаружены критические проблемы"); } catch { }
                }
                PruneOldReports();
            }
        }
        try { _sys?.Dispose(); } catch { }
        _sys = null;
        State = MonitorState.Idle;
        Log?.Invoke("Мониторинг остановлен.");
    }

    private CycleResult? RunCycle()
    {
        var runSw = Stopwatch.StartNew();
        var start = DateTime.Now;
        CycleNumber++;
        TickCount = 0;
        PlannedTicks = Math.Max(1, (int)Math.Ceiling(_o.CycleMinutes * 60 / _o.IntervalSec));
        NextReportAt = start.AddMinutes(_o.CycleMinutes);
        Log?.Invoke($"Цикл #{CycleNumber}: наблюдение {_o.CycleMinutes:F0} мин (замер каждые {_o.IntervalSec} с, ~{PlannedTicks} замеров)");

        var samples = new List<SystemSample>(PlannedTicks);
        var acc = new ProcAccumulator();
        var remoteSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var openFilePaths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var openFileUsers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var openFilesSummary = new OpenFilesSummary();
        var tcpSummary = new TcpSummary();
        int openFilesTotalMax = 0;
        double srvRej = 0, srvShort = 0;

        Sys.CollectedStart = start;
        var tickSw = Stopwatch.StartNew();

        while (!_stop)
        {
            tickSw.Restart();
            var sysCol = _sys!;
            var procCol = _proc!;

            sysCol.Collect();
            var sample = sysCol.ReadSample();

            var hung = procCol.GetHungPids();
            List<ProcessTick> ticks = procCol.Collect(hung, out double dt);

            // TCP/UDP
            var conns = Native.GetTcpConnections();
            var udp = Native.GetUdpSockets();
            int est = 0, tw = 0, cw = 0, lst = 0, syns = 0, synr = 0, fin = 0, other = 0;
            var perPid = new Dictionary<int, TcpConnPid>();
            foreach (var c in conns)
            {
                switch (c.State)
                {
                    case 5: est++; break;
                    case 11: tw++; break;
                    case 8: cw++; break;
                    case 2: lst++; break;
                    case 3: syns++; break;
                    case 4: synr++; break;
                    case 6: case 7: case 9: case 10: fin++; break;
                    default: other++; break;
                }
                if (!perPid.TryGetValue(c.Pid, out var tp)) { tp = new TcpConnPid { Pid = c.Pid }; perPid[c.Pid] = tp; }
                tp.Total++;
                if (c.State == 5)
                {
                    tp.Est++;
                    if (!c.IsV6)
                    {
                        string rem = c.Remote;
                        int ci = rem.LastIndexOf(':');
                        string ip = ci > 0 ? rem.Substring(0, ci) : rem;
                        if (remoteSeen.ContainsKey(ip)) remoteSeen[ip]++;
                        else remoteSeen[ip] = 1;
                    }
                }
            }
            sample.TcpEstablished = est; sample.TcpTimeWait = tw; sample.TcpCloseWait = cw;
            sample.TcpListen = lst; sample.TcpSynSent = syns; sample.TcpSynRecv = synr;
            sample.TcpFin = fin; sample.TcpOther = other; sample.TcpTotal = conns.Count;
            sample.UdpEndpoints = udp.Count;
            foreach (var t in ticks)
                if (perPid.TryGetValue(t.Pid, out var tp)) { t.TcpEst = tp.Est; t.TcpTotal = tp.Total; }

            acc.Add(ticks, Math.Max(0.2, dt));
            acc.MarkLeaders(ticks);
            acc.MergeTcp(perPid.Values.ToList());
            samples.Add(sample);
            srvRej += sysCol.LastSrvRejected;
            srvShort += sysCol.LastSrvShort;

            // SMB-файлы — раз в 5 замеров
            if (TickCount % 5 == 0)
            {
                var (files, err) = Native.GetOpenFiles();
                if (err.Length > 0) openFilesSummary.Error = err;
                else
                {
                    if (files.Count > openFilesTotalMax) openFilesTotalMax = files.Count;
                    foreach (var ofr in files)
                    {
                        string pth = ofr.Path ?? "";
                        string usr = string.IsNullOrEmpty(ofr.User) ? "(неизвестно)" : ofr.User;
                        if (openFilePaths.ContainsKey(pth)) openFilePaths[pth]++;
                        else openFilePaths[pth] = 1;
                        if (openFileUsers.ContainsKey(usr)) openFileUsers[usr]++;
                        else openFileUsers[usr] = 1;
                    }
                }
            }

            TickCount++;
            var livePoint = AddLive(sample);
            try { Tick?.Invoke(livePoint); } catch { }

            int remain = _o.IntervalSec * 1000 - (int)tickSw.ElapsedMilliseconds;
            int slept = 0;
            while (slept < remain && !_stop)
            {
                int chunk = Math.Min(250, remain - slept);
                Thread.Sleep(chunk);
                slept += chunk;
            }
            if (runSw.Elapsed.TotalMinutes >= _o.CycleMinutes) break;
        }

        if (samples.Count < 2)
        {
            Log?.Invoke("Цикл прерван до накопления данных — отчёт не формируется.");
            return null;
        }

        Sys.CollectedEnd = DateTime.Now;
        var duration = runSw.Elapsed;

        foreach (var kv in remoteSeen.OrderByDescending(v => v.Value).Take(15))
            tcpSummary.TopRemote.Add(new KeyValuePair<string, int>(kv.Key, kv.Value));
        var agg = acc.Snapshot();
        openFilesSummary.TotalMax = openFilesTotalMax;
        openFilesSummary.TopPaths = openFilePaths.OrderByDescending(v => v.Value).Take(15).ToList();
        openFilesSummary.TopUsers = openFileUsers.OrderByDescending(v => v.Value).Take(10).ToList();

        Log?.Invoke("Анализ журналов событий и формирование отчёта…");
        var evGroups = _o.EventsHours > 0
            ? new EventLogCollector().Collect(_o.EventsHours, Sys.CollectedStart)
            : new List<EventGroup>();
        var sessions = OperatingSystem.IsWindows() ? Native.GetSessions() : new List<SessionInfo>();

        var analysis = Analyzer.Analyze(new Analyzer.Input
        {
            Sys = Sys,
            Samples = samples,
            Procs = agg,
            Events = evGroups,
            Tcp = tcpSummary,
            OpenFiles = openFilesSummary,
            Sessions = sessions,
            SrvRejected = srvRej,
            SrvShort = srvShort,
            Duration = duration,
            SampleCount = samples.Count
        });

        string dir = Path.Combine(OutRoot, "ServerHealth_" + Sys.CollectedStart.ToString("yyyyMMdd_HHmmss"));
        var written = ReportWriter.Write(dir, Sys, samples, agg, evGroups, sessions, tcpSummary,
            openFilesSummary, analysis, duration, "", writeJson: true, htmlRefreshSec: _o.HtmlRefreshSec);
        if (ReportWriter.LastHtmlError != null)
            Log?.Invoke("report.html: ошибка генерации — " + ReportWriter.LastHtmlError);

        // постоянный дашборд: одна страница в корне отчётов, всегда показывает
        // последний цикл и сама обновляется в браузере (meta refresh)
        try
        {
            DashboardPath = Path.Combine(OutRoot, "dashboard.html");
            ReportHtml.Write(DashboardPath, Sys, samples, agg, evGroups, tcpSummary, openFilesSummary,
                analysis, duration, written, Math.Max(15, _o.HtmlRefreshSec));
        }
        catch (Exception ex)
        {
            Log?.Invoke("dashboard.html: " + ex.Message);
        }

        return new CycleResult
        {
            CycleNumber = CycleNumber,
            Finished = DateTime.Now,
            Dir = dir,
            Files = written,
            Analysis = analysis,
            Samples = samples.Count,
            Duration = duration,
            TopProcs = agg.Where(p => p.Samples >= 2 && p.Pid > 4).OrderByDescending(p => p.CpuAvg).Take(15).ToList(),
            VerdictTitle = analysis.VerdictTitle,
            HasCritical = analysis.HasCritical
        };
    }

    private LivePoint AddLive(SystemSample s)
    {
        var p = new LivePoint
        {
            Ts = s.Ts,
            Cpu = double.IsNaN(s.CpuTotal) ? 0 : s.CpuTotal,
            AvailMb = double.IsNaN(s.AvailMb) ? 0 : s.AvailMb,
            NetMbps = s.Nets.Sum(n => (double.IsNaN(n.RxMbps) ? 0 : n.RxMbps) + (double.IsNaN(n.TxMbps) ? 0 : n.TxMbps)),
            DiskLatMaxMs = s.Physical.Count == 0 ? 0 : s.Physical.Max(d => double.IsNaN(d.LatReadMs) ? 0 : Math.Max(d.LatReadMs, double.IsNaN(d.LatWriteMs) ? 0 : d.LatWriteMs)),
            DiskQueueAvg = s.Physical.Count == 0 ? 0 : s.Physical.Average(d => double.IsNaN(d.QueueCur) ? 0 : d.QueueCur),
            TcpEst = s.TcpEstablished,
            ProcQueue = double.IsNaN(s.ProcQueueLen) ? 0 : s.ProcQueueLen
        };
        lock (_liveLock)
        {
            _live.Add(p);
            if (_live.Count > 20000) _live.RemoveRange(0, 5000);
        }
        LastPoint = p;
        return p;
    }

    private void PruneOldReports()
    {
        if (_o.KeepReports <= 0) return;
        try
        {
            var old = Directory.EnumerateDirectories(OutRoot, "ServerHealth_*")
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Skip(_o.KeepReports);
            foreach (var d in old)
            {
                try { Directory.Delete(d, true); Log?.Invoke("Удалён старый отчёт: " + Path.GetFileName(d)); } catch { }
            }
        }
        catch { }
    }
}
