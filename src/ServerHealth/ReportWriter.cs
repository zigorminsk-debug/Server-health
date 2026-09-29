using System.Globalization;
using System.Text;
using System.Text.Json;
using ServerHealth.Interop;

namespace ServerHealth;

/// <summary>Формирование файлов отчёта: report.txt, report.json, samples.csv, processes.csv, events.csv.</summary>
public static class ReportWriter
{
    public sealed class Paths
    {
        public string Txt, Html, Json, Samples, Processes, Events, Dir;
    }

    private static string F(double v, string fmt = "0.#")
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "н/д";
        return v.ToString(fmt, CultureInfo.InvariantCulture);
    }

    private static string Gb(double bytes) { return F(bytes / 1073741824.0, "0.##"); }
    private static string Mb(double bytes) { return F(bytes / 1048576.0, "0"); }

    public static Paths Write(
        string dir, SysInfo sys, List<SystemSample> samples, List<ProcAgg> procs,
        List<EventGroup> events, List<SessionInfo> sessions, TcpSummary tcp,
        OpenFilesSummary openFiles, AnalysisResult analysis, TimeSpan duration,
        string knownProcsNote, bool writeJson = true, int htmlRefreshSec = 0)
    {
        Directory.CreateDirectory(dir);
        var paths = new Paths { Dir = dir };
        paths.Txt = Path.Combine(dir, "report.txt");
        paths.Html = Path.Combine(dir, "report.html");
        paths.Json = writeJson ? Path.Combine(dir, "report.json") : "";
        paths.Samples = Path.Combine(dir, "samples.csv");
        paths.Processes = Path.Combine(dir, "processes.csv");
        paths.Events = Path.Combine(dir, "events.csv");

        WriteSamplesCsv(paths.Samples, samples);
        WriteProcessesCsv(paths.Processes, procs, duration);
        WriteEventsCsv(paths.Events, events);
        if (writeJson)
            WriteJson(paths.Json, sys, samples, procs, events, sessions, tcp, openFiles, analysis, duration);
        try
        {
            ReportHtml.Write(paths.Html, sys, samples, procs, events, tcp, openFiles, analysis, duration, paths, htmlRefreshSec);
        }
        catch { /* HTML не критичен: TXT/CSV уже записаны */ }
        WriteTxt(paths.Txt, sys, samples, procs, events, sessions, tcp, openFiles, analysis, duration, knownProcsNote);
        return paths;
    }

    // ============================================================ TXT
    private static void WriteTxt(string path, SysInfo sys, List<SystemSample> samples, List<ProcAgg> procs,
        List<EventGroup> events, List<SessionInfo> sessions, TcpSummary tcp, OpenFilesSummary openFiles,
        AnalysisResult analysis, TimeSpan duration, string knownProcsNote)
    {
        int cores = Math.Max(1, Environment.ProcessorCount);
        var sb = new StringBuilder();
        Action<string> w = t => sb.AppendLine(t);
        string line = new string('=', 100);
        string thin = new string('-', 100);

        w(line);
        w(" ServerHealth — ОТЧЁТ о причинах медленной работы терминального сервера");
        w(line);
        w("Узел: " + sys.Machine + "   Пользователь: " + sys.Domain + "\\" + sys.User +
          "   Версия: " + sys.AppVersion);
        w("ОС: " + sys.Os);
        w("Период: " + sys.CollectedStart.ToString("dd.MM.yyyy HH:mm:ss") + " — " +
          sys.CollectedEnd.ToString("dd.MM.yyyy HH:mm:ss") +
          string.Format(" (длительность {0:F1} мин, замеров: {1})", duration.TotalMinutes, samples.Count));
        w("Права запуска: " + (sys.Elevated ? "Администратор" : "ОБЫЧНЫЙ ПОЛЬЗОВАТЕЛЬ (часть данных недоступна — запустите от администратора!)"));
        w("");

        // ---- 1. Конфигурация
        w("1. КОНФИГУРАЦИЯ УЗЛА");
        w(thin);
        w("  Процессор: " + sys.CpuName + "  [" + cores + " лог. ядер]");
        w(string.Format("  ОЗУ: {0:F1} ГБ   Аптайм: {1} д {2} ч {3} мин",
            sys.RamGb, (int)sys.Uptime.TotalDays, sys.Uptime.Hours, sys.Uptime.Minutes));
        w("  Файл подкачки: " + (string.IsNullOrEmpty(sys.PageFileConfig) ? "не определён" : sys.PageFileConfig));
        w("  Схема питания: " + (string.IsNullOrEmpty(sys.PowerPlan) ? "не определена" : sys.PowerPlan.Replace("\n", "; ")));
        if (sessions.Count > 0)
        {
            int act = sessions.Count(s => s.State == "Active");
            int disc = sessions.Count(s => s.State == "Disconnected");
            w(string.Format("  RDS-сессии: всего {0} (активных {1}, отключённых {2})", sessions.Count, act, disc));
            foreach (var s in sessions) w(s.ToString());
        }
        w("");

        // ---- 2. Сводка метрик
        w("2. СВОДКА ПО ПОДСИСТЕМАМ (среднее / P95 / максимум)");
        w(thin);
        foreach (var kv in Dist(samples))
        {
            w("  " + kv);
        }
        w("");

        // ---- 3. Вердикт
        w("3. ВЕРДИКТ И ОЦЕНКА ПОДСИСТЕМ");
        w(thin);
        foreach (var s in analysis.Scores)
        {
            string mark = s.Value >= 70 ? "НОРМА" : (s.Value >= 40 ? "ПРОБЛЕМА" : "КРИТИЧНО");
            w(string.Format("  {0,-18} [{1}] {2,3}/100  {3}", s.Key, AnalyzerBar(s.Value), s.Value, mark));
        }
        w("");
        w("  ВЕРДИКТ: " + analysis.VerdictTitle);
        foreach (var l in SplitLines(analysis.VerdictText, 98)) w("  " + l);
        w("");

        // ---- 4. Процессы
        w("4. ПРОЦЕССЫ (топ за период наблюдения)");
        w(thin);
        var act2 = procs.Where(p => p.Samples >= 2).ToList();
        var topCpu = act2.Where(p => p.Pid > 4).OrderByDescending(p => p.CpuAvg).Take(15).ToList();
        var topMem = act2.OrderByDescending(p => p.PrivMaxMb).Take(15).ToList();
        var topIo = act2.OrderByDescending(p => p.ReadMbpsAvg + p.WriteMbpsAvg).Take(15).ToList();
        var topConn = act2.Where(p => p.TcpEstMax > 0).OrderByDescending(p => p.TcpEstMax).Take(15).ToList();

        w("  4.1. По загрузке процессора (средний % от ВСЕХ ядер):");
        w(string.Format("  {0,-24} {1,7} {2,7} {3,7} {4,9} {5,10} {6,6}", "Процесс", "PID", "сред.%", "макс.%", "CPU-время", "лидер#", "потоков"));
        foreach (var p in topCpu)
            w(string.Format("  {0,-24} {1,7} {2,7} {3,7} {4,9:F1} {5,7} {6,6}",
                Trunc(p.Name, 24), p.Pid, F(p.CpuAvg), F(p.CpuMax), p.CpuSeconds, p.TopCpuTimes, "-"));
        w("  4.2. По памяти (частная память = «съедено» процессом):");
        w(string.Format("  {0,-24} {1,7} {2,10} {3,10} {4,10} {5,10}", "Процесс", "PID", "ср., МБ", "макс., МБ", "тек., МБ", "рост МБ/ч"));
        foreach (var p in topMem)
            w(string.Format("  {0,-24} {1,7} {2,10} {3,10} {4,10} {5,10}",
                Trunc(p.Name, 24), p.Pid, F(p.PrivAvgMb, "0"), F(p.PrivMaxMb, "0"), F(p.PrivLastMb, "0"), F(p.GrowthMbPerHour(duration), "+0;-0;0")));
        w("  4.3. По вводу-выводу файлов/диска (включая сетевой ввод-вывод процесса):");
        w(string.Format("  {0,-24} {1,7} {2,12} {3,12} {4,10} {5,10}", "Процесс", "PID", "чт. ср.Мбит/с", "зп. ср.Мбит/с", "чт. оп/с", "зп. оп/с"));
        foreach (var p in topIo)
            w(string.Format("  {0,-24} {1,7} {2,12} {3,12} {4,10} {5,10}",
                Trunc(p.Name, 24), p.Pid, F(p.ReadMbpsAvg), F(p.WriteMbpsAvg), F(p.ReadOpsAvg, "0"), F(p.WriteOpsAvg, "0")));
        if (topConn.Count > 0)
        {
            w("  4.4. По числу TCP-соединений:");
            w(string.Format("  {0,-24} {1,7} {2,12} {3,12}", "Процесс", "PID", "est. макс", "всего макс"));
            foreach (var p in topConn)
                w(string.Format("  {0,-24} {1,7} {2,12} {3,12}", Trunc(p.Name, 24), p.Pid, p.TcpEstMax, p.TcpTotalMax));
        }
        if (knownProcsNote.Length > 0) { w(""); w("  Примечание: " + knownProcsNote); }
        w("");

        // ---- 5. Диски
        w("5. ДИСКОВАЯ ПОДСИСТЕМА");
        w(thin);
        var physGroups = samples.SelectMany(s => s.Physical).GroupBy(d => d.Name).ToList();
        if (physGroups.Count == 0) w("  Счётчики PhysicalDisk недоступны на этой системе.");
        foreach (var g in physGroups)
        {
            var latr = g.Select(d => double.IsNaN(d.LatReadMs) ? 0 : d.LatReadMs).ToList();
            var latw = g.Select(d => double.IsNaN(d.LatWriteMs) ? 0 : d.LatWriteMs).ToList();
            var q = g.Select(d => double.IsNaN(d.QueueCur) ? 0 : d.QueueCur).ToList();
            double mbpsAvg = g.Average(d => (double.IsNaN(d.ReadMbps) ? 0 : d.ReadMbps) + (double.IsNaN(d.WriteMbps) ? 0 : d.WriteMbps));
            double mbpsMax = g.Max(d => (double.IsNaN(d.ReadMbps) ? 0 : d.ReadMbps) + (double.IsNaN(d.WriteMbps) ? 0 : d.WriteMbps));
            double iopsMax = g.Max(d => double.IsNaN(d.Iops) ? 0 : d.Iops);
            w(string.Format("  Диск [{0}]: задержка чтения {1}/{2}/{3} мс (ср/P95/макс), записи {4}/{5}/{6} мс;",
                g.Key, F(avg(latr)), F(Analyzer.Percentile(latr, 0.95)), F(latr.Max()),
                F(avg(latw)), F(Analyzer.Percentile(latw, 0.95)), F(latw.Max())));
            w(string.Format("            очередь {0:F1}/{1}/{2}; поток {3}/{4} Мбит/с (ср/макс); {5:F0} IOPS макс",
                avg(q), F(Analyzer.Percentile(q, 0.95), "0.#"), F(q.Max(), "0.#"), F(mbpsAvg, "0"), F(mbpsMax, "0"), iopsMax));
        }
        var logGroups = samples.SelectMany(s => s.Logical).GroupBy(l => l.Name).ToList();
        foreach (var g in logGroups)
        {
            double minFree = g.Min(l => double.IsNaN(l.FreePct) ? 100 : l.FreePct);
            double minMb = g.Min(l => double.IsNaN(l.FreeMb) ? 0 : l.FreeMb);
            w(string.Format("  Том {0}: свободно {1:F1}% ({2:F1} ГБ) минимум за период", g.Key, minFree, minMb / 1024.0));
        }
        w("");

        // ---- 6. Сеть
        w("6. СЕТЬ (LAN) И СОЕДИНЕНИЯ");
        w(thin);
        var netGroups = samples.SelectMany(s => s.Nets).GroupBy(n => n.Name).ToList();
        if (netGroups.Count == 0) w("  Счётчики Network Interface недоступны.");
        foreach (var g in netGroups)
        {
            double bw = g.Where(n => n.BandwidthMbps > 0).Select(n => n.BandwidthMbps).DefaultIfEmpty(0).First();
            var rx = g.Select(n => double.IsNaN(n.RxMbps) ? 0 : n.RxMbps).ToList();
            var tx = g.Select(n => double.IsNaN(n.TxMbps) ? 0 : n.TxMbps).ToList();
            var pps = g.Select(n => (double.IsNaN(n.RxPps) ? 0 : n.RxPps) + (double.IsNaN(n.TxPps) ? 0 : n.TxPps)).ToList();
            double errs = g.Sum(n => (double.IsNaN(n.RxEps) ? 0 : n.RxEps) + (double.IsNaN(n.TxEps) ? 0 : n.TxEps));
            double utilP95 = bw > 0 ? Analyzer.Percentile(rx.Zip(tx, (a, b) => a + b), 0.95) / bw * 100 : double.NaN;
            w(string.Format("  Интерфейс [{0}] полоса {1:F0} Мбит/с:", g.Key, bw));
            w(string.Format("    приём {0}/{1} Мбит/с (ср/макс), передача {2}/{3} Мбит/с; пакетов {4:F0}/с макс; ошибок пакетов {5:F0}",
                F(avg(rx), "0.#"), F(rx.Max(), "0.#"), F(avg(tx), "0.#"), F(tx.Max(), "0.#"), F(pps.Max(), "0"), errs));
            if (!double.IsNaN(utilP95)) w(string.Format("    утилизация P95: {0:F1}%", utilP95));
        }
        if (samples.Count > 0)
        {
            var last = samples[samples.Count - 1];
            w("  Состояния TCP (максимум за период / в конце периода):");
            foreach (var st in new[] { "ESTABLISHED", "TIME_WAIT", "CLOSE_WAIT", "SYN_SENT", "SYN_RCVD", "LISTEN", "FIN_WAIT", "ДРУГИЕ" })
            {
                int max = st switch
                {
                    "ESTABLISHED" => samples.Max(s => s.TcpEstablished),
                    "TIME_WAIT" => samples.Max(s => s.TcpTimeWait),
                    "CLOSE_WAIT" => samples.Max(s => s.TcpCloseWait),
                    "SYN_SENT" => samples.Max(s => s.TcpSynSent),
                    "SYN_RCVD" => samples.Max(s => s.TcpSynRecv),
                    "LISTEN" => samples.Max(s => s.TcpListen),
                    "FIN_WAIT" => samples.Max(s => s.TcpFin),
                    _ => samples.Max(s => s.TcpOther)
                };
                int cur = st switch
                {
                    "ESTABLISHED" => last.TcpEstablished,
                    "TIME_WAIT" => last.TcpTimeWait,
                    "CLOSE_WAIT" => last.TcpCloseWait,
                    "SYN_SENT" => last.TcpSynSent,
                    "SYN_RCVD" => last.TcpSynRecv,
                    "LISTEN" => last.TcpListen,
                    "FIN_WAIT" => last.TcpFin,
                    _ => last.TcpOther
                };
                w(string.Format("    {0,-12} {1,6} / {2,6}", st, max, cur));
            }
            w(string.Format("  UDP-портов (сокетов): {0} в конце периода; TCP-соединений всего: {1} макс.", last.UdpEndpoints, samples.Max(s => s.TcpTotal)));
            if (tcp.TopRemote.Count > 0)
            {
                w("  Топ удалённых адресов (по числу наблюдённых соединений):");
                foreach (var kv in tcp.TopRemote.Take(10))
                    w(string.Format("    {0,-42} {1,6}", kv.Key, kv.Value));
            }
        }
        if (openFiles.TotalMax > 0 || openFiles.Error.Length > 0)
        {
            w("  Открытые по сети файлы (SMB):");
            if (openFiles.Error.Length > 0) w("    недоступно: " + openFiles.Error);
            else
            {
                w(string.Format("    одновременно открыто до {0} файлов.", openFiles.TotalMax));
                if (openFiles.TopPaths.Count > 0)
                {
                    w("    Топ файлов/папок:");
                    foreach (var kv in openFiles.TopPaths.Take(10)) w(string.Format("      {0,5}x  {1}", kv.Value, Trunc(kv.Key, 90)));
                }
                if (openFiles.TopUsers.Count > 0)
                {
                    w("    Топ пользователей (открытые файлы):");
                    foreach (var kv in openFiles.TopUsers.Take(10)) w(string.Format("      {0,5}x  {1}", kv.Value, kv.Key));
                }
            }
        }
        w("");

        // ---- 7. События
        w("7. ЖУРНАЛ СОБЫТИЙ WINDOWS (критические, ошибки и значимые предупреждения)");
        w(thin);
        if (events.Count == 0)
        {
            w("  Подходящих событий не найдено (или нет прав на чтение журналов).");
        }
        else
        {
            w(string.Format("  {0,-14} {1,-32} {2,8} {3,6} {4,-16} {5}", "Журнал", "Поставщик", "Код", "Кол-во", "Последнее", "Уровень"));
            foreach (var e in events.Take(40))
            {
                w(string.Format("  {0,-14} {1,-32} {2,8} {3,6} {4,-16} {5}{6}",
                    Trunc(e.Log, 14), Trunc(e.Provider, 32), e.Id, e.Count, e.Last.ToString("dd.MM HH:mm"), e.LevelName,
                    e.DuringCollection ? "  <-- ВОЗНИКЛО ВО ВРЕМЯ НАБЛЮДЕНИЯ" : ""));
                if (!string.IsNullOrEmpty(e.Sample))
                    foreach (var l in SplitLines(e.Sample, 92).Take(2)) w("      " + l);
            }
        }
        w("");

        // ---- 8. Находки
        w("8. НАХОДКИ И РЕКОМЕНДАЦИИ ПО УСТРАНЕНИЮ");
        w(thin);
        int num = 1;
        foreach (var f in analysis.Findings.OrderByDescending(f => SevOrder(f.Severity)))
        {
            w("");
            w(string.Format("  [{0}] #{1}. {2}", f.Severity, num++, f.Title));
            w("  " + new string('-', 96));
            w("  Симптомы: " + f.Symptom);
            if (!string.IsNullOrEmpty(f.Cause)) w("  Вероятная причина: " + f.Cause);
            if (f.Actions.Count > 0)
            {
                w("  Что делать:");
                int step = 1;
                foreach (var a in f.Actions) w("    " + step++ + ") " + a);
            }
            if (f.Verify.Count > 0)
            {
                w("  Как проверить, что помогло:");
                foreach (var v in f.Verify) w("    - " + v);
            }
        }
        w("");

        // ---- 9. План действий
        w("9. ПЛАН ПЕРВООЧЕРЕДНЫХ ДЕЙСТВИЙ (чек-лист)");
        w(thin);
        w("  Выполняйте по порядку; после каждого блока — контроль по разделу «Как проверить».");
        int pn = 1;
        foreach (var f in analysis.Findings.Where(f => f.Severity == "CRITICAL"))
            foreach (var a in f.Actions.Take(2))
                w("  [ ] " + pn++ + ". (" + f.Category + ") " + a);
        foreach (var f in analysis.Findings.Where(f => f.Severity == "WARNING"))
            foreach (var a in f.Actions.Take(1))
                w("  [ ] " + pn++ + ". (" + f.Category + ") " + a);
        if (pn == 1) w("  [ ] Критичных находок нет — повторите мониторинг в период жалоб (см. находку INFO).");
        w("  [ ] " + pn++ + ". Повторить ServerHealth на 10-15 минут после изменений и сравнить оценки подсистем.");
        w("");

        // ---- 10. Быстрые команды
        w("10. БЫСТРЫЕ КОМАНДЫ РУЧНОЙ ДИАГНОСТИКИ");
        w(thin);
        foreach (var c in new[]
        {
            "tasklist /svc /fi \"PID eq <PID>\"        — какие службы внутри процесса",
            "resmon                                   — интерактивный монитор ресурсов (CPU/диск/сеть/память)",
            "typeperf \"\\PhysicalDisk(_Total)\\Avg. Disk sec/Transfer\" — задержки диска в реальном времени",
            "logman create counter SlowTrace -f csv -si 5 -max 500 -c \"\\Processor(_Total)\\% Processor Time\" \"\\Memory\\Available MBytes\" \"\\PhysicalDisk(_Total)\\Avg. Disk sec/Transfer\" -o C:\\logs\\slow --v",
            "netstat -bno                             — соединения по процессам (cmd от администратора)",
            "query user                               — RDS-сессии; reset session <ID> — сброс зависшей сессии",
            "Get-MpPreference / Get-Process | sort CPU -desc | select -first 15   — PowerShell",
            "powercfg /getactivescheme                — схема питания",
            "chkdsk C: /scan                          — проверка диска без перезагрузки",
            "poolmon.exe (WDK)                        — утечки пула ядра по тегам",
        }) w("  " + c);
        w("");

        // ---- 11. Профилактика
        w("11. ПРОФИЛАКТИКА (чтобы проблема не возвращалась)");
        w(thin);
        foreach (var c in new[]
        {
            "• Запускайте ServerHealth по расписанию в часы пик и храните отчёты (пример планировщика в README.md).",
            "• Держите на RDS запас: CPU < 70%, RAM свободно > 15%, задержки диска < 20 мс, канал < 60%.",
            "• Установите фиксированный файл подкачки и «Высокую производительность» электропитания.",
            "• Настройте антивирусные исключения для рабочих каталогов и баз (1С и т.п.).",
            "• Ведите журнал изменений на сервере: после каждого изменения — контрольный замер ServerHealth.",
            "• Отключённые RDS-сессии: принудительное завершение через 4-8 часов (GPO) — освобождает память.",
        }) w("  " + c);
        w("");

        // ---- 12. Файлы
        w("12. ФАЙЛЫ ЭТОГО ОТЧЁТА");
        w(thin);
        w("  report.txt      — этот отчёт (главный документ)");
        w("  report.json     — все данные в машиночитаемом виде");
        w("  samples.csv     — пометочные данные всех системных метрик (разделитель «;», кодировка UTF-8 BOM)");
        w("  processes.csv   — агрегаты по процессам");
        w("  events.csv      — журнал событий");
        w("");
        w(line);
        w(" Конец отчёта. Сформировано ServerHealth " + sys.AppVersion);
        w(line);

        System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static double avg(List<double> v) { return v.Count == 0 ? double.NaN : v.Average(); }

    private static string AnalyzerBar(int score) { return new string('#', score / 10) + new string('.', 10 - score / 10); }

    private static int SevOrder(string s) { return s == "CRITICAL" ? 0 : s == "WARNING" ? 1 : 2; }

    private static string Trunc(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
    }

    private static List<string> SplitLines(string s, int width)
    {
        var res = new List<string>();
        if (s == null) return res;
        var words = s.Split(' ');
        var cur = new StringBuilder();
        foreach (var wd in words)
        {
            if (cur.Length + wd.Length + 1 > width) { res.Add(cur.ToString()); cur.Clear(); }
            if (cur.Length > 0) cur.Append(' ');
            cur.Append(wd);
        }
        if (cur.Length > 0) res.Add(cur.ToString());
        return res;
    }

    private static List<string> Dist(List<SystemSample> samples)
    {
        var res = new List<string>();
        if (samples.Count == 0) return res;
        void Add(string name, Func<SystemSample, double> f, string unit = "")
        {
            var vals = samples.Select(f).Where(v => !double.IsNaN(v)).ToList();
            if (vals.Count == 0) { res.Add(string.Format("  {0,-44} нет данных", name)); return; }
            double a = vals.Average(), mx = vals.Max(), p95 = Analyzer.Percentile(vals, 0.95);
            res.Add(string.Format("  {0,-44} {1,8} / {2,8} / {3,8} {4}", name, F(a), F(p95), F(mx), unit));
        }
        Add("CPU, суммарная загрузка, %", s => s.CpuTotal, "%");
        Add("CPU, режим ядра (Privileged), %", s => s.CpuPriv, "%");
        Add("CPU, DPC (драйверы), %", s => s.CpuDpc, "%");
        Add("Очередь процессора", s => s.ProcQueueLen, "");
        Add("Переключений контекста/с", s => s.CtxSwitch, "");
        Add("Свободная память, МБ", s => s.AvailMb, "МБ");
        Add("Commit (выделено памяти), % лимита", s => s.CommitPct, "%");
        Add("Файл подкачки занят, %", s => s.PagingFilePct, "%");
        Add("Жёсткие страничные ошибки /с", s => s.HardFaults, "/с");
        Add("Невыгружаемый пул ядра, МБ", s => s.PoolNonPaged, "МБ");
        Add("Выгружаемый пул ядра, МБ", s => s.PoolPaged, "МБ");
        var latr = samples.SelectMany(s => s.Physical).Select(d => double.IsNaN(d.LatReadMs) ? 0 : d.LatReadMs).ToList();
        var latw = samples.SelectMany(s => s.Physical).Select(d => double.IsNaN(d.LatWriteMs) ? 0 : d.LatWriteMs).ToList();
        var qq = samples.SelectMany(s => s.Physical).Select(d => double.IsNaN(d.QueueCur) ? 0 : d.QueueCur).ToList();
        if (latr.Count > 0)
        {
            res.Add(string.Format("  {0,-44} {1,8} / {2,8} / {3,8} {4}", "Задержка диска: чтение, мс", F(avg(latr)), F(Analyzer.Percentile(latr, 0.95)), F(latr.Max()), "мс"));
            res.Add(string.Format("  {0,-44} {1,8} / {2,8} / {3,8} {4}", "Задержка диска: запись, мс", F(avg(latw)), F(Analyzer.Percentile(latw, 0.95)), F(latw.Max()), "мс"));
            res.Add(string.Format("  {0,-44} {1,8} / {2,8} / {3,8} {4}", "Очередь диска (текущая)", F(avg(qq), "0.##"), F(Analyzer.Percentile(qq, 0.95), "0.##"), F(qq.Max(), "0.##"), ""));
        }
        var netMb = samples.SelectMany(s => s.Nets).Select(n => (double.IsNaN(n.RxMbps) ? 0 : n.RxMbps) + (double.IsNaN(n.TxMbps) ? 0 : n.TxMbps)).ToList();
        if (netMb.Count > 0)
            res.Add(string.Format("  {0,-44} {1,8} / {2,8} / {3,8} {4}", "Сеть: весь трафик (приём+передача)", F(avg(netMb), "0.#"), F(Analyzer.Percentile(netMb, 0.95), "0.#"), F(netMb.Max(), "0.#"), "Мбит/с"));
        Add("TCP: соединений ESTABLISHED", s => s.TcpEstablished, "");
        Add("TCP: соединений TIME_WAIT", s => s.TcpTimeWait, "");
        if (samples[0].SysHandles > 0)
        {
            Add("Дескрипторов в системе", s => s.SysHandles, "");
            Add("Потоков в системе", s => s.SysThreads, "");
            Add("Процессов в системе", s => s.SysProcesses, "");
        }
        return res;
    }

    // ============================================================ CSV
    private static void WriteSamplesCsv(string path, List<SystemSample> samples)
    {
        using var sw = new StreamWriter(path, false, new UTF8Encoding(true));
        var header = new List<string>
        {
            "Время", "CPU %", "CPU ядро %", "CPU польз %", "DPC %", "Очередь CPU", "Контекст/с",
            "Свободно МБ", "Commit %", "Подкачка %", "Hard faults/с", "Pages in/с", "Pages out/с",
            "Пул NP МБ", "Пул P МБ", "Дескрипторы", "Потоки", "Процессы",
            "TCP est", "TCP tw", "TCP cw", "TCP всего", "UDP"
        };
        // диски/сеть — по экземплярам первого замера
        var diskNames = samples.Count > 0 ? samples[0].Physical.Select(d => d.Name).ToList() : new List<string>();
        var netNames = samples.Count > 0 ? samples[0].Nets.Select(n => n.Name).ToList() : new List<string>();
        foreach (var d in diskNames) { header.Add("[" + d + "] задержка чт. мс"); header.Add("[" + d + "] задержка зп. мс"); header.Add("[" + d + "] очередь"); header.Add("[" + d + "] Мбит/с"); }
        foreach (var n in netNames) { header.Add("[" + n + "] пр. Мбит/с"); header.Add("[" + n + "] от. Мбит/с"); header.Add("[" + n + "] пакетов/с"); }
        sw.WriteLine(string.Join(";", header));
        foreach (var s in samples)
        {
            var row = new List<string>
            {
                s.Ts.ToString("dd.MM.yyyy HH:mm:ss"),
                F(s.CpuTotal), F(s.CpuPriv), F(s.CpuUser), F(s.CpuDpc), F(s.ProcQueueLen), F(s.CtxSwitch, "0"),
                F(s.AvailMb, "0"), F(s.CommitPct), F(s.PagingFilePct), F(s.HardFaults, "0"), F(s.PagesIn, "0"), F(s.PagesOut, "0"),
                F(s.PoolNonPaged, "0"), F(s.PoolPaged, "0"), s.SysHandles.ToString(), s.SysThreads.ToString(), s.SysProcesses.ToString(),
                s.TcpEstablished.ToString(), s.TcpTimeWait.ToString(), s.TcpCloseWait.ToString(), s.TcpTotal.ToString(), s.UdpEndpoints.ToString()
            };
            foreach (var d in diskNames)
            {
                var dd = s.Physical.FirstOrDefault(x => x.Name == d);
                row.Add(dd == null ? "" : F(dd.LatReadMs));
                row.Add(dd == null ? "" : F(dd.LatWriteMs));
                row.Add(dd == null ? "" : F(dd.QueueCur, "0.##"));
                row.Add(dd == null ? "" : F((double.IsNaN(dd.ReadMbps) ? 0 : dd.ReadMbps) + (double.IsNaN(dd.WriteMbps) ? 0 : dd.WriteMbps)));
            }
            foreach (var n in netNames)
            {
                var nn = s.Nets.FirstOrDefault(x => x.Name == n);
                row.Add(nn == null ? "" : F(nn.RxMbps));
                row.Add(nn == null ? "" : F(nn.TxMbps));
                row.Add(nn == null ? "" : F((double.IsNaN(nn.RxPps) ? 0 : nn.RxPps) + (double.IsNaN(nn.TxPps) ? 0 : nn.TxPps), "0"));
            }
            sw.WriteLine(string.Join(";", row));
        }
    }

    private static void WriteProcessesCsv(string path, List<ProcAgg> procs, TimeSpan duration)
    {
        using var sw = new StreamWriter(path, false, new UTF8Encoding(true));
        sw.WriteLine("Процесс;PID;Замеров;CPU ср %;CPU макс %;CPU время с;Частная пам. ср МБ;Частная пам. макс МБ;Частная пам. тек МБ;Рост МБ/ч;WS макс МБ;Чтение ср Мбит/с;Запись ср Мбит/с;Чтение оп/с;Запись оп/с;TCP est макс;Зависал(замеров);Лидер CPU;Лидер памяти;Лидер IO");
        foreach (var p in procs.Where(p => p.Samples >= 2).OrderByDescending(p => p.CpuAvg))
        {
            sw.WriteLine(string.Join(";", new[]
            {
                p.Name, p.Pid.ToString(), p.Samples.ToString(),
                F(p.CpuAvg), F(p.CpuMax), F(p.CpuSeconds, "0.0"),
                F(p.PrivAvgMb, "0"), F(p.PrivMaxMb, "0"), F(p.PrivLastMb, "0"),
                F(p.GrowthMbPerHour(duration), "+0;-0;0"),
                F(p.WsMaxMb, "0"), F(p.ReadMbpsAvg), F(p.WriteMbpsAvg),
                F(p.ReadOpsAvg, "0.0"), F(p.WriteOpsAvg, "0.0"),
                p.TcpEstMax.ToString(), p.HungCount.ToString(),
                p.TopCpuTimes.ToString(), p.TopMemTimes.ToString(), p.TopIoTimes.ToString()
            }));
        }
    }

    private static void WriteEventsCsv(string path, List<EventGroup> events)
    {
        using var sw = new StreamWriter(path, false, new UTF8Encoding(true));
        sw.WriteLine("Журнал;Поставщик;Код;Уровень;Количество;Первое;Последнее;Во время наблюдения;Пример");
        foreach (var e in events)
        {
            sw.WriteLine(string.Join(";", new[]
            {
                e.Log, e.Provider, e.Id.ToString(), e.LevelName, e.Count.ToString(),
                e.First.ToString("dd.MM.yyyy HH:mm:ss"), e.Last.ToString("dd.MM.yyyy HH:mm:ss"),
                e.DuringCollection ? "ДА" : "",
                (e.Sample ?? "").Replace(";", ",")
            }));
        }
    }

    // ============================================================ JSON
    private static void WriteJson(string path, SysInfo sys, List<SystemSample> samples, List<ProcAgg> procs,
        List<EventGroup> events, List<SessionInfo> sessions, TcpSummary tcp, OpenFilesSummary openFiles,
        AnalysisResult analysis, TimeSpan duration)
    {
        var doc = new
        {
            tool = "ServerHealth",
            version = sys.AppVersion,
            host = new
            {
                sys.Machine, sys.User, sys.Domain, sys.Os, sys.CpuName,
                cores = Environment.ProcessorCount,
                ramGb = sys.RamGb,
                uptimeDays = Math.Round(sys.Uptime.TotalDays, 2),
                elevated = sys.Elevated,
                sys.PowerPlan, sys.PageFileConfig
            },
            period = new { start = sys.CollectedStart, end = sys.CollectedEnd, durationMinutes = Math.Round(duration.TotalMinutes, 1), samples = samples.Count },
            verdict = new { analysis.VerdictTitle, analysis.VerdictText, hasCritical = analysis.HasCritical },
            scores = analysis.Scores.Select(s => new { subsystem = s.Key, score = s.Value }),
            metrics = samples.Select(s => new
            {
                ts = s.Ts,
                cpu = s.CpuTotal, cpuPriv = s.CpuPriv, dpc = s.CpuDpc,
                cpuQueue = s.ProcQueueLen, ctxSwitch = s.CtxSwitch,
                availMb = s.AvailMb, commitPct = s.CommitPct, pagingPct = s.PagingFilePct,
                hardFaults = s.HardFaults, poolNpMb = s.PoolNonPaged / 1048576.0,
                disks = s.Physical.Select(d => new { d.Name, latReadMs = d.LatReadMs, latWriteMs = d.LatWriteMs, queue = d.QueueCur, mbps = d.ReadMbps + d.WriteMbps, iops = d.Iops }),
                nets = s.Nets.Select(n => new { n.Name, n.RxMbps, n.TxMbps, pps = n.RxPps + n.TxPps, n.BandwidthMbps }),
                tcp = new { est = s.TcpEstablished, tw = s.TcpTimeWait, cw = s.TcpCloseWait, total = s.TcpTotal }
            }),
            processes = procs.Where(p => p.Samples >= 2).OrderByDescending(p => p.CpuAvg).Select(p => new
            {
                name = p.Name, pid = p.Pid, samplesN = p.Samples,
                cpuAvg = Math.Round(p.CpuAvg, 1), cpuMax = Math.Round(p.CpuMax, 1),
                privAvgMb = Math.Round(p.PrivAvgMb), privMaxMb = Math.Round(p.PrivMaxMb),
                growthMbPerHour = Math.Round(p.GrowthMbPerHour(duration)),
                ioMbpsAvg = Math.Round(p.ReadMbpsAvg + p.WriteMbpsAvg, 1),
                tcpEstMax = p.TcpEstMax, hungCount = p.HungCount
            }),
            sessions = sessions.Select(s => new { s.Id, s.Station, s.State, s.Domain, s.User, s.Client }),
            events = events.Select(e => new { e.Log, e.Provider, id = e.Id, level = e.LevelName, count = e.Count, first = e.First, last = e.Last, duringCollection = e.DuringCollection, sample = e.Sample }),
            openFiles = new { openFiles.TotalMax, error = openFiles.Error, top = openFiles.TopPaths.Take(15) },
            findings = analysis.Findings.Select(f => new { f.Category, f.Severity, f.Title, f.Symptom, f.Cause, actions = f.Actions, verify = f.Verify })
        };
        var opts = new JsonSerializerOptions { WriteIndented = true };
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(doc, opts), new UTF8Encoding(false));
    }
}
