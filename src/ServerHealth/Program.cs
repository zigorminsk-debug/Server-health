using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using ServerHealth.Interop;

namespace ServerHealth;

internal static class Program
{
    private static volatile bool _stop;

    [STAThread]
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        CliOptions opt;
        try { opt = CliOptions.Parse(args); }
        catch (Exception ex)
        {
            ConsoleUi.Error(ex.Message);
            ConsoleUi.Help();
            return 1;
        }

        string version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        if (opt.ShowVersion) { Console.WriteLine("ServerHealth " + version); return 0; }
        if (opt.ShowHelp && !opt.Gui) { ConsoleUi.Banner(version); ConsoleUi.Help(); return 0; }

        // графический интерфейс (по умолчанию без аргументов, либо -g/--gui)
        if (opt.Gui)
        {
            if (!OperatingSystem.IsWindows())
            {
                ConsoleUi.Error("GUI доступен только в Windows.");
                return 1;
            }
            FreeAutoConsole(); // при запуске двойным кликом убираем лишнее консольное окно
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }
            Application.Run(new Gui.MainForm(opt));
            return 0;
        }

        ConsoleUi.Banner(version);

        if (!OperatingSystem.IsWindows())
        {
            ConsoleUi.Error("Эта программа предназначена для Windows. Запустите собранный ServerHealth.exe на Windows Server.");
            return 1;
        }

        // постоянный мониторинг: отчёт каждые N минут
        if (opt.Watch)
            return RunWatch(opt);

        return RunSingle(opt);
    }

    // ------------------------------------------------------ консоль/GUI
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    /// <summary>
    /// Если консоль была автоматически создана для нас (запуск двойным кликом) — освободить её,
    /// чтобы не висело пустое окно рядом с GUI. Терминал пользователя не трогаем.
    /// </summary>
    private static void FreeAutoConsole()
    {
        try
        {
            IntPtr h = GetConsoleWindow();
            if (h == IntPtr.Zero) return;
            var buf = new uint[1];
            uint n = GetConsoleProcessList(buf, 1);
            if (n == 1) FreeConsole();
        }
        catch { }
    }

    // ------------------------------------------------------------ watch
    private static int RunWatch(CliOptions opt)
    {
        if (!Native.IsElevated())
            ConsoleUi.Warn("Запуск без прав администратора: соединения по процессам, открытые SMB-файлы и часть событий будут недоступны.");

        var engine = new MonitoringEngine(new MonitoringOptions
        {
            IntervalSec = opt.IntervalSec,
            CycleMinutes = opt.WatchCycleMin,
            EventsHours = opt.EventsHours,
            OutRoot = opt.OutDir ?? "",
            KeepReports = opt.KeepReports,
            HtmlRefreshSec = 30
        });

        engine.Log += m => Console.WriteLine("  " + m);
        engine.CycleCompleted += r =>
        {
            Console.WriteLine();
            Console.WriteLine("════════ ОТЧЁТ #" + r.CycleNumber + " ════════");
            Console.WriteLine("  Каталог : " + r.Dir);
            Console.WriteLine("  Веб     : " + Path.Combine(r.Dir, "report.html"));
            Console.WriteLine("  Вердикт : " + r.VerdictTitle);
            if (r.Analysis != null)
                foreach (var s in r.Analysis.Scores)
                    Console.WriteLine(string.Format("  {0,-18} {1,3}/100", s.Key, s.Value));
            Console.WriteLine();
        };

        Console.CancelKeyPress += (s, e) => { e.Cancel = true; engine.Stop(); };
        engine.Start();
        while (engine.State != MonitorState.Idle)
            Thread.Sleep(400);
        return 0;
    }

    // ---------------------------------------------------------- single run
    private static int RunSingle(CliOptions opt)
    {
        string version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        bool elevated = Native.IsElevated();
        if (!elevated)
            ConsoleUi.Warn("Запуск без прав администратора: не будут доступны соединения по процессам, открытые SMB-файлы и часть событий. Рекомендуется «Запуск от имени администратора».");

        // ---------- каталог отчёта ----------
        string outDir = opt.DefaultSingleOutDir();
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex) { ConsoleUi.Error("Не удалось создать каталог отчёта " + outDir + ": " + ex.Message); return 1; }

        // ---------- информация об узле ----------
        var sys = new SysInfo
        {
            Machine = Environment.MachineName,
            User = Environment.UserName,
            Domain = Environment.UserDomainName,
            Os = Environment.OSVersion.VersionString + " (" + (Environment.Is64BitOperatingSystem ? "x64" : "x86") + ")",
            CpuName = Native.RegGetString(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString").Trim(),
            Cores = Environment.ProcessorCount,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            Elevated = elevated,
            PowerPlan = Native.GetActivePowerScheme(),
            AppVersion = version,
            CollectedStart = DateTime.Now
        };
        var mem = Native.GlobalMemory();
        sys.RamGb = mem.total / 1073741824.0;
        var pfCfg = Native.RegGetMultiString(@"SYSTEM\CurrentControlSet\Session Manager\Memory Management", "PagingFiles");
        sys.PageFileConfig = pfCfg.Count == 0 ? "не задан (только системный по умолчанию?)" : string.Join("; ", pfCfg);
        if (sys.CpuName.Length == 0) sys.CpuName = "не определён";

        int totalSamples = Math.Max(1, (int)Math.Ceiling(opt.DurationMin * 60.0 / opt.IntervalSec));

        Console.WriteLine("Узел: " + sys.Machine + "  |  " + sys.CpuName + " x" + sys.Cores + "  |  ОЗУ " + sys.RamGb.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " ГБ  |  Аптайм " + (int)sys.Uptime.TotalDays + " д " + sys.Uptime.Hours + " ч");
        Console.WriteLine(string.Format("Наблюдение: {0:F1} мин, интервал {1} с, замеров: {2}. Отчёт: {3}",
            opt.DurationMin, opt.IntervalSec, totalSamples, Path.GetFullPath(outDir)));
        Console.WriteLine("Ctrl+C — завершить досрочно (отчёт всё равно будет сформирован).");
        Console.WriteLine();

        // ---------- системный сборщик ----------
        SystemCollector sysCol;
        try
        {
            sysCol = new SystemCollector();
            sysCol.Initialize();
        }
        catch (Exception ex)
        {
            ConsoleUi.Error("Счётчики производительности недоступны: " + ex.Message);
            ConsoleUi.Error("Возможно, повреждены счётчики: выполните 'lodctr /r' и повторите, либо запустите от администратора.");
            return 1;
        }
        foreach (var skipped in sysCol.Skipped)
            ConsoleUi.Warn("Счётчик недоступен на этой системе: " + skipped);

        var procCol = new ProcessCollector();
        var acc = new ProcAccumulator();
        var samples = new List<SystemSample>(totalSamples);
        var tcpSummary = new TcpSummary();
        var remoteSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pidNames = new Dictionary<int, string>();
        var openFilesSummary = new OpenFilesSummary();
        var openFilePaths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var openFileUsers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int openFilesTotalMax = 0;
        double srvRejectedTotal = 0, srvShortTotal = 0;

        Console.CancelKeyPress += (s, e) => { e.Cancel = true; _stop = true; };

        var runSw = Stopwatch.StartNew();
        int done = 0;
        var tickSw = Stopwatch.StartNew();

        try
        {
            while (!_stop && done < totalSamples)
            {
                tickSw.Restart();

                // --- сбор ---
                sysCol.Collect();
                var sample = sysCol.ReadSample();

                var hung = procCol.GetHungPids();
                List<ProcessTick> ticks = procCol.Collect(hung, out double dt);

                // TCP/UDP по процессам
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
                    if (c.State == 5) tp.Est++;
                    if (c.State == 5 && !c.IsV6)
                    {
                        string rem = c.Remote;
                        int ci = rem.LastIndexOf(':');
                        string ip = ci > 0 ? rem.Substring(0, ci) : rem;
                        if (remoteSeen.ContainsKey(ip)) remoteSeen[ip]++;
                        else remoteSeen[ip] = 1;
                    }
                }
                sample.TcpEstablished = est; sample.TcpTimeWait = tw; sample.TcpCloseWait = cw;
                sample.TcpListen = lst; sample.TcpSynSent = syns; sample.TcpSynRecv = synr;
                sample.TcpFin = fin; sample.TcpOther = other; sample.TcpTotal = conns.Count;
                sample.UdpEndpoints = udp.Count;

                foreach (var t in ticks)
                {
                    if (perPid.TryGetValue(t.Pid, out var tp)) { t.TcpEst = tp.Est; t.TcpTotal = tp.Total; }
                    pidNames[t.Pid] = t.Name;
                }

                acc.Add(ticks, Math.Max(0.2, dt));
                acc.MarkLeaders(ticks);
                acc.MergeTcp(perPid.Values.ToList());
                samples.Add(sample);
                srvRejectedTotal += sysCol.LastSrvRejected;
                srvShortTotal += sysCol.LastSrvShort;

                // открытые SMB-файлы — раз в 5 замеров
                if (done % 5 == 0 || done == totalSamples - 1)
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

                done++;

                // --- вывод ---
                if (!opt.Quiet) ConsoleUi.Live(sample, done, totalSamples);

                // пауза до конца интервала
                int remain = opt.IntervalSec * 1000 - (int)tickSw.ElapsedMilliseconds;
                if (remain > 0)
                {
                    // спим короткими отрезками, чтобы быстро реагировать на Ctrl+C
                    int slept = 0;
                    while (slept < remain && !_stop)
                    {
                        int chunk = Math.Min(250, remain - slept);
                        Thread.Sleep(chunk);
                        slept += chunk;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ConsoleUi.Error("Во время наблюдения: " + ex.Message);
        }

        sys.CollectedEnd = DateTime.Now;
        var duration = runSw.Elapsed;

        // ---------- TCP-сводка ----------
        foreach (var kv in remoteSeen.OrderByDescending(v => v.Value).Take(15))
            tcpSummary.TopRemote.Add(new KeyValuePair<string, int>(kv.Key, kv.Value));
        var agg = acc.Snapshot();
        foreach (var p in agg.Where(p => p.TcpEstMax > 0).OrderByDescending(p => p.TcpEstMax).Take(15))
            tcpSummary.TopPidEst.Add(new KeyValuePair<int, int>(p.Pid, p.TcpEstMax));
        openFilesSummary.TotalMax = openFilesTotalMax;
        openFilesSummary.TopPaths = openFilePaths.OrderByDescending(v => v.Value).Take(15).ToList();
        openFilesSummary.TopUsers = openFileUsers.OrderByDescending(v => v.Value).Take(10).ToList();

        if (!opt.Quiet) Console.WriteLine();

        // ---------- события ----------
        Console.WriteLine("Чтение журналов событий…");
        var eventGroups = opt.EventsHours > 0
            ? new EventLogCollector().Collect(opt.EventsHours, sys.CollectedStart)
            : new List<EventGroup>();

        // ---------- сессии ----------
        var sessions = Native.GetSessions();

        // ---------- анализ ----------
        Console.WriteLine("Анализ и формирование отчёта…");
        var input = new Analyzer.Input
        {
            Sys = sys,
            Samples = samples,
            Procs = agg,
            Events = eventGroups,
            Tcp = tcpSummary,
            OpenFiles = openFilesSummary,
            Sessions = sessions,
            SrvRejected = srvRejectedTotal,
            SrvShort = srvShortTotal,
            Duration = duration,
            SampleCount = samples.Count
        };
        var analysis = Analyzer.Analyze(input);

        // известные процессы-«тяжеловесы» в топе
        var knownNote = new StringBuilder();
        foreach (var p in agg.Where(p => p.Samples >= 2 && (p.CpuAvg > 15 || p.PrivMaxMb > 1024)).Take(30))
        {
            if (Analyzer.KnownProcessAdvice.ContainsKey(p.Name))
                knownNote.Append(p.Name + " (PID " + p.Pid + "); ");
        }
        string knownNoteStr = knownNote.Length > 0
            ? "в топе замечены известные «тяжеловесы», для них в разделе 8 даны адресные рекомендации: " + knownNote.ToString().TrimEnd(' ', ';')
            : "";

        var paths = ReportWriter.Write(outDir, sys, samples, agg,
            eventGroups, sessions, tcpSummary, openFilesSummary, analysis, duration, knownNoteStr, !opt.NoJson);

        ConsoleUi.Summary(analysis, paths, sys, agg, duration);

        return analysis.HasCritical ? 2 : 0;
    }
}
