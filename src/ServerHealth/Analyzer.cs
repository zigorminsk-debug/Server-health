using ServerHealth.Interop;

namespace ServerHealth;

/// <summary>
/// Анализ собранных данных: правила выявления узких мест, оценка подсистем,
/// вердикт и конкретные инструкции по устранению причин зависаний.
/// </summary>
public static class Analyzer
{
    // ---------------------------------------------------------------- ввод
    public sealed class Input
    {
        public SysInfo Sys;
        public List<SystemSample> Samples = new List<SystemSample>();
        public List<ProcAgg> Procs = new List<ProcAgg>();
        public List<EventGroup> Events = new List<EventGroup>();
        public TcpSummary Tcp = new TcpSummary();
        public OpenFilesSummary OpenFiles = new OpenFilesSummary();
        public List<SessionInfo> Sessions = new List<SessionInfo>();
        public double SrvRejected, SrvShort;
        public TimeSpan Duration;
        public int SampleCount;
    }

    // ------------------------------------------------------------ утилиты
    private struct M
    {
        public double Avg, P95, Max;
        public double ShareAbove; // доля замеров, где значение >= порога (задаётся при вызове)
    }

    private static M Metric(List<SystemSample> s, Func<SystemSample, double> f, double? thr = null)
    {
        var vals = new List<double>(s.Count);
        int above = 0;
        foreach (var x in s)
        {
            double v = f(x);
            if (double.IsNaN(v)) continue;
            vals.Add(v);
            if (thr.HasValue && v >= thr.Value) above++;
        }
        var m = new M();
        if (vals.Count == 0) return m;
        m.Avg = vals.Average();
        m.Max = vals.Max();
        var sorted = vals.OrderBy(v => v).ToList();
        int idx = (int)Math.Ceiling(0.95 * (sorted.Count - 1));
        if (idx < 0) idx = 0;
        m.P95 = sorted[idx];
        if (thr.HasValue) m.ShareAbove = (double)above / vals.Count;
        return m;
    }

    private static int ClampScore(double v)
    {
        int i = (int)Math.Round(v);
        if (i < 0) i = 0;
        if (i > 100) i = 100;
        return i;
    }

    private static string Bar(int score, int width = 10)
    {
        int full = (int)Math.Round(score / 100.0 * width);
        if (full < 0) full = 0;
        if (full > width) full = width;
        return new string('#', full) + new string('.', width - full);
    }

    /// <summary>База знаний: известные «тяжёлые» процессы и что с ними делать.</summary>
    public static Dictionary<string, string[]> KnownProcessAdvice = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["MsMpEng"] = new[] {
            "Антивирус Microsoft Defender: сканирование в реальном времени нагружает CPU/диск.",
            "Настроить исключения для рабочих каталогов и процессов терминальных приложений:",
            "  Add-MpPreference -ExclusionPath \"C:\\1C\\bases\" (пример; указать свои каталоги баз и профилей)",
            "  Add-MpPreference -ExclusionProcess \"1cv8.exe\"",
            "Для RDS включить оптимизацию: Set-MpPreference -DisableCpuThrottleOnIdleScans $true",
            "Проверить расписание полной проверки (должно выполняться ночью): Get-MpPreference | fl ScanSchedule*" },
        ["wmiprvse"] = new[] {
            "Хост WMI перегружен запросами (часто — инвентаризация/агенты мониторинга).",
            "Найти инициатора: в реестре HKLM\\SOFTWARE\\Microsoft\\WBEM\\CIMOM установить Logging=2, смотреть %SystemRoot%\\System32\\Wbem\\Logs,",
            "либо: wmic process where \"name='WmiPrvSE.exe'\" get commandline — временно отключить агенты инвентаризации (SCCM/антивирусные консоли).",
            "Перезапуск службы WMI (осторожно на продуктиве): Restart-Service winmgmt -Force" },
        ["splwow64"] = new[] {
            "Терминальный брокер печати (32-битные приложения в RDS). Типичная причина зависаний печати и утечек памяти.",
            "Обновить драйверы печати, использовать Easy Print: GPO \"Использовать драйвер принтера Easy Print\" = Включено.",
            "Перезапустить очередь печати: net stop spooler & net start spooler",
            "Очистить застрявшие задания: del /Q %SystemRoot%\\System32\\spool\\PRINTERS\\*.* (при остановленной очереди)." },
        ["spoolsv"] = new[] {
            "Служба очереди печати перегружена/зависла (застрявшие задания или кривые драйверы).",
            "net stop spooler & del /Q %SystemRoot%\\System32\\spool\\PRINTERS\\*.* & net start spooler",
            "Удалить проблемные драйверы принтеров, перейти на Easy Print / Model-specific Type 4 драйверы." },
        ["dwm"] = new[] {
            "Менеджер окон рабочего стола в RDS-сессиях. Нагрузка растёт от обоев/анимации/плохих видеодрайверов.",
            "GPO: отключить обои и сглаживание в сессиях (Computer Config\\Policies\\Admin Templates\\Windows Components\\Remote Desktop Services\\Remote Session Environment).",
            "Обновить видеодрайвер хоста; на Hyper-V/VMware — актуальные интеграционные компоненты/VMware Tools." },
        ["svchost"] = new[] {
            "Процесс-хост служб: нужно определить, какая именно служба нагружает.",
            "tasklist /svc /fi \"PID eq <PID>\" — увидеть службы внутри svchost.",
            "Далее анализировать конкретную службу (wuauserv — обновления,BITS, WinHttpAutoProxySvc — прокси и т.п.)." },
        ["System"] = new[] {
            "Ядро ОС (PID 4): нагрузка обычно от драйверов (сетевые фильтры, антивирусные перехватчики, СКЗИ) или пулов памяти.",
            "Проверить пул ядра: poolmon.exe (WDK). Наибольшие теги — найти драйвер.",
            "Проверить сторонние сетевые драйверы/фильтры (антивирус, DLP, VPN): временно отключить и сравнить." },
        ["lsass"] = new[] {
            "Служба аутентификации: высокая нагрузка = много логинов/групповых политик/проблема с контроллером домена.",
            "Проверить доступность контроллера домена (nltest /dsgetdc:домен), задержки GP: gpresult /h report.html",
            "НЕ ПЕРЕЗАПУСКАТЬ принудительно! Обратиться к администратору домена, включить трассировку при необходимости." },
        ["w3wp"] = new[] {
            "Рабочий процесс IIS. Определить пул приложений: %SystemRoot%\\System32\\inetsrv\\appcmd list wp",
            "Перезапустить проблемный пул: appcmd stop apppool /apppool.name:Имя && appcmd start apppool /apppool.name:Имя",
            "Проверить приложение на утечки/бесконечные циклы, включить лимиты перезапуска пула." },
        ["sqlservr"] = new[] {
            "SQL Server: по умолчанию берёт почти всю память сервера.",
            "Ограничить: sp_configure 'max server memory', <МБ> — оставить ОС и другим процессам 4-8 ГБ.",
            "Проверить отсутствие «раздутых» планов/фрагментации: update usage, перестроение индексов." },
        ["1cv8"] = new[] {
            "Клиент 1С:Предприятие: типичные причины — раздутый локальный кэш и конфигурации с тяжёлыми формами.",
            "Очистить кэш пользователя: удалить %LocalAppData%\\1C\\1cv8\\*guid* (каталоги с длинными GUID).",
            "Проверить версии платформы (обновить до актуальной), включить клиентское сжатие, ограничить число сеансов." },
        ["1cv8c"] = new[] {
            "Тонкий клиент 1С: очистить кэш %LocalAppData%\\1C\\1cv8, обновить платформу.",
            "Проверить сетевую связь с сервером 1С (задержки по сети напрямую бьют в отзывчивость)." },
        ["rphost"] = new[] {
            "Рабочий процесс сервера 1С:Предприятие. Утечки/перегрузка сеансов.",
            "Настроить в консоли кластера 1С лимиты и принудительный перезапуски процессов (интервал, объём памяти, число сеансов).",
            "Перезапустить сбойный rphost через консоль кластера (не kill сессии пользователей без предупреждения)." },
        ["java"] = new[] {
            "Приложение на Java: проверить настройки -Xmx (не больше 50-70% RAM сервера), логи GC.",
            "Если память растёт — собрать heap dump: jmap -dump:live,format=b,file=heap.hprof <PID>" },
        ["chrome"] = new[] {
            "Браузер в терминальных сессиях потребляет много памяти/CPU на вкладки.",
            "Ввести политики: лимит фоновых вкладок, отключить аппаратное ускорение при проблемах с GPU (GPO Chrome)." },
        ["msedge"] = new[] {
            "Браузер Edge в сессиях: настроить Sleeping Tabs (GPO Edge), запретить фоновые процессы.",
            "При проблемах GPU — отключить аппаратное ускорение политикой." },
        ["explorer"] = new[] {
            "Проводник в терминальных сессиях: часто «раздувают» открытые окна сетевых папок и эскизы.",
            "GPO: отключить кэш эскизов, ограничить автозапуск сетевых папок; перезапуск: taskkill /f /im explorer.exe & start explorer.exe" },
        ["outlook"] = new[] {
            "Outlook: проверить размер OST (<50 ГБ), включить режим кэширования Exchange с ограничением загружаемого периода." },
        ["teams"] = new[] {
            "Teams в RDS: использовать версию для VDI/AVD либо веб-клиент; отключить аппаратное ускорение (settings.json: disableGpu)." },
    };

    // ------------------------------------------------------------ анализ
    public static AnalysisResult Analyze(Input x)
    {
        var r = new AnalysisResult();
        if (x.Samples.Count == 0)
        {
            r.VerdictTitle = "Недостаточно данных";
            r.VerdictText = "Период наблюдения слишком короткий. Запустите мониторинг минимум на 5 минут.";
            return r;
        }

        int cores = Math.Max(1, Environment.ProcessorCount);
        double ramGb = x.Sys.RamGb;

        // ---------- метрики ----------
        var cpu = Metric(x.Samples, s => s.CpuTotal, 85);
        var cpuPriv = Metric(x.Samples, s => s.CpuPriv, 80);
        var dpc = Metric(x.Samples, s => s.CpuDpc, 15);
        var intr = Metric(x.Samples, s => s.CpuIntr, 10);
        var queue = Metric(x.Samples, s => s.ProcQueueLen, 2 * cores);
        var ctx = Metric(x.Samples, s => s.CtxSwitch);
        var availMb = Metric(x.Samples, s => s.AvailMb);
        var commit = Metric(x.Samples, s => s.CommitPct, 90);
        var pfPct = Metric(x.Samples, s => s.PagingFilePct, 80);
        var hard = Metric(x.Samples, s => s.HardFaults, 200);
        var pagesIn = Metric(x.Samples, s => s.PagesIn);
        var poolNp = Metric(x.Samples, s => s.PoolNonPaged);
        double availPctAvg = ramGb > 0 ? (availMb.Avg / 1024.0) / ramGb * 100.0 : 100;

        // диски: усредняем по дискам
        var latR = Metric(x.Samples, s => s.Physical.Count == 0 ? double.NaN : s.Physical.Average(d => double.IsNaN(d.LatReadMs) ? 0 : d.LatReadMs), 25);
        var latW = Metric(x.Samples, s => s.Physical.Count == 0 ? double.NaN : s.Physical.Average(d => double.IsNaN(d.LatWriteMs) ? 0 : d.LatWriteMs), 25);
        var latRMax = Metric(x.Samples, s => s.Physical.Count == 0 ? double.NaN : s.Physical.Max(d => double.IsNaN(d.LatReadMs) ? 0 : d.LatReadMs), 50);
        var latWMax = Metric(x.Samples, s => s.Physical.Count == 0 ? double.NaN : s.Physical.Max(d => double.IsNaN(d.LatWriteMs) ? 0 : d.LatWriteMs), 50);
        var dQueueAvg = Metric(x.Samples, s => s.Physical.Count == 0 ? double.NaN : s.Physical.Average(d => double.IsNaN(d.QueueCur) ? 0 : d.QueueCur), 2);
        var dQueueMax = Metric(x.Samples, s => s.Physical.Count == 0 ? double.NaN : s.Physical.Max(d => double.IsNaN(d.QueueCur) ? 0 : d.QueueCur), 4);

        // сеть: суммарная утилизация по интерфейсам
        double NetUtilP95 = 0;
        double NetUtilAvg = 0;
        string NetUtilTop = "";
        double NetErrors = 0;
        foreach (var ifName in x.Samples.SelectMany(s => s.Nets).Select(n => n.Name).Distinct())
        {
            var u95 = Metric(x.Samples, s => { var n = s.Nets.FirstOrDefault(v => v.Name == ifName); return n == null || n.BandwidthMbps <= 0 ? double.NaN : Math.Min(100, (n.RxMbps + n.TxMbps) / n.BandwidthMbps * 100); }, 70);
            var uavg = Metric(x.Samples, s => { var n = s.Nets.FirstOrDefault(v => v.Name == ifName); return n == null || n.BandwidthMbps <= 0 ? double.NaN : Math.Min(100, (n.RxMbps + n.TxMbps) / n.BandwidthMbps * 100); });
            var errs = Metric(x.Samples, s => { var n = s.Nets.FirstOrDefault(v => v.Name == ifName); return n == null ? double.NaN : (double.IsNaN(n.RxEps) ? 0 : n.RxEps) + (double.IsNaN(n.TxEps) ? 0 : n.TxEps); });
            NetErrors += errs.Max;
            if (!double.IsNaN(u95.P95) && u95.P95 > NetUtilP95)
            {
                NetUtilP95 = u95.P95;
                NetUtilAvg = uavg.Avg;
                NetUtilTop = ifName;
            }
        }
        int twMax = x.Samples.Max(s => s.TcpTimeWait);

        // ---------- процессы ----------
        var procs = x.Procs.Where(p => p.Samples >= 2).ToList();
        var topCpu = procs.Where(p => p.Pid > 4).OrderByDescending(p => p.CpuAvg).Take(10).ToList();
        var topMem = procs.OrderByDescending(p => p.PrivMaxMb).Take(10).ToList();
        var topGrow = procs.Where(p => p.PrivMaxMb > 300 && p.GrowthMbPerHour(x.Duration) > 100)
                           .OrderByDescending(p => p.GrowthMbPerHour(x.Duration)).Take(5).ToList();
        var topIo = procs.OrderByDescending(p => p.ReadMbpsAvg + p.WriteMbpsAvg).Take(10).ToList();
        var hung = procs.Where(p => p.HungCount > 0).OrderByDescending(p => p.HungCount).ToList();

        // ---------- оценки подсистем ----------
        double cpuScore = 100
            - Math.Max(0, (cpu.Avg - 75)) * 2.2
            - (queue.ShareAbove > 0.15 ? 20 + Math.Min(20, queue.P95) : 0)
            - (dpc.P95 > 15 ? 20 : 0)
            - (ctx.Avg > 30000 * cores ? 10 : 0);
        double ramScore = 100
            - Math.Max(0, (12 - availPctAvg)) * 4
            - Math.Max(0, (commit.Avg - 85)) * 3
            - (pfPct.P95 > 85 ? 10 : 0)
            - (hard.Avg > 100 ? 15 : 0)
            - (poolNp.Max > 2L * 1024 * 1024 * 1024 ? 15 : 0);
        double diskScore = 100
            - Math.Max(0, (latR.P95 - 20)) * 1.5
            - Math.Max(0, (latW.P95 - 20)) * 1.5
            - (dQueueAvg.ShareAbove > 0.15 ? 20 : 0)
            - (MinFreePct(x) < 10 ? 15 : 0);
        double netScore = 100
            - Math.Max(0, (NetUtilP95 - 65)) * 2
            - (NetErrors > 10 ? 15 : 0)
            - (twMax > 8000 ? 10 : 0)
            - (x.SrvRejected > 0 ? 10 : 0);
        cpuScore = ClampScore(cpuScore); ramScore = ClampScore(ramScore);
        diskScore = ClampScore(diskScore); netScore = ClampScore(netScore);

        r.Scores.Add(new KeyValuePair<string, int>("CPU (процессор)", (int)cpuScore));
        r.Scores.Add(new KeyValuePair<string, int>("RAM (память)", (int)ramScore));
        r.Scores.Add(new KeyValuePair<string, int>("Disk (диски)", (int)diskScore));
        r.Scores.Add(new KeyValuePair<string, int>("Net (сеть)", (int)netScore));

        // ================================================================ ПРАВИЛА
        // ---------------- CPU ----------------
        if (cpu.Avg >= 80 || cpu.P95 >= 90 || cpu.ShareAbove >= 0.25)
        {
            bool crit = cpu.Avg >= 90 || cpu.ShareAbove >= 0.4;
            var f = new Finding
            {
                Category = "CPU",
                Severity = crit ? "CRITICAL" : "WARNING",
                Title = crit
                    ? string.Format("Процессор перегружен: средняя загрузка {0:F0}%, максимум {1:F0}%", cpu.Avg, cpu.Max)
                    : string.Format("Высокая загрузка процессора: средняя {0:F0}%, P95 {1:F0}%", cpu.Avg, cpu.P95),
                Symptom = string.Format("CPU выше 85% в {0:F0}% замеров. Очередь процессора: средняя {1:F1}, максимум {2:F0} (норма < 2 на ядро, всего ядер: {3}).",
                    cpu.ShareAbove * 100, queue.Avg, queue.Max, cores),
                Cause = "Клиентов больше, чем позволяет CPU, либо отдельный процесс «съедает» процессор. Для терминального сервера это прямая причина медленной реакции всех приложений."
            };
            if (topCpu.Count > 0)
            {
                f.Symptom += " Лидеры по CPU: " + string.Join(", ", topCpu.Take(5).Select(p => string.Format("{0} (PID {1}, {2:F0}% ср., макс {3:F0}%)", p.Name, p.Pid, p.CpuAvg, p.CpuMax))) + ".";
            }
            f.Actions.Add("Определить главных потребителей: в разделе 4 отчёта взять топ по CPU, для каждого выполнить: tasklist /svc /fi \"PID eq <PID>\" (покажет службы внутри svchost).");
            foreach (var p in topCpu.Take(3))
            {
                var adv = AdviceFor(p.Name, p.Pid);
                if (adv != null) f.Actions.AddRange(adv);
            }
            f.Actions.Add("Если лидер — прикладной процесс без известной службы: снять дамп и/или перезапустить приложение в согласованное окно: taskkill /pid <PID> (без /f — мягко, /f — принудительно).");
            f.Actions.Add("Краткосрочно: перенести часть пользователей/задач на второй сервер, снизить приоритет фоновых задач: start /b /below normal app.exe.");
            f.Actions.Add("Стратегически: увеличить число vCPU (проверить соотношение: для RDS обычно 4-8 пользователей на ядро при офисной нагрузке) либо разнести нагрузку по серверам.");
            f.Verify.Add("Повторить мониторинг после изменений; средняя загрузка CPU должна опуститься ниже 70%, очередь процессора — ниже 2 на ядро.");
            r.Findings.Add(f);
        }
        if (dpc.P95 > 15 || intr.P95 > 15 || dpc.Max > 30)
        {
            r.Findings.Add(new Finding
            {
                Category = "CPU",
                Severity = dpc.Max > 50 ? "CRITICAL" : "WARNING",
                Title = string.Format("Высокая доля ядра/DPC: DPC до {0:F0}%, прерывания до {1:F0}%", dpc.Max, intr.Max),
                Symptom = string.Format("Процессор занят отложенными вызовами драйверов: DPC сред. {0:F1}%, P95 {1:F1}%. Часть мощности CPU недоступна приложениям.", dpc.Avg, dpc.P95),
                Cause = "Проблемный или устаревший драйвер (чаще всего сетевой адаптер, накопитель, антивирусный перехватчик).",
                Actions =
                {
                    "Обновить драйверы сетевого адаптера и контроллера дисков до актуальных с сайта производителя (не через Центр обновлений).",
                    "Отключить устаревшие сетевые фильтры (старые антивирусы/файрволы/VPN) и проверить, уходит ли DPC.",
                    "Диагностика: xperf -on PROC_THREAD+LOADER -stackwalk DPC; xperf -d dpc.etl (Windows Performance Toolkit) — покажет драйвер-виновник.",
                    "Включить на сетевом адаптере RSS: Get-NetAdapterRss | Enable-NetAdapterRss"
                },
                Verify = { "Повторный замер: % DPC Time должен быть ниже 10%." }
            });
        }
        if (ctx.Avg > 25000 * cores)
        {
            r.Findings.Add(new Finding
            {
                Category = "CPU",
                Severity = "WARNING",
                Title = string.Format("Чрезмерное число переключений контекста: {0:F0} тыс./с", ctx.Avg / 1000),
                Symptom = string.Format("Context Switches/sec в среднем {0:F0} при {1} ядрах — слишком много активных потоков.", ctx.Avg, cores),
                Cause = "Большое число потоков у приложений (часто: антивирус, агенты, приложения с «закрученными» циклами) — процессор тратит время на переключения, а не на работу.",
                Actions = { "Найти процесс с аномальным числом потоков (Process Explorer: колонка Threads).", "Проверить настройки пула потоков у агентов (антивирус, резервное копирование, мониторинг).", "Если источник — прикладной софт, обратиться к его вендору." }
            });
        }

        // ---------------- RAM ----------------
        if (availPctAvg < 10 || availMb.P95 < 700)
        {
            bool crit = availPctAvg < 5 || availMb.P95 < 400;
            var f = new Finding
            {
                Category = "RAM",
                Severity = crit ? "CRITICAL" : "WARNING",
                Title = crit
                    ? string.Format("Критическая нехватка памяти: свободно в среднем {0:F0} МБ из {1:F0} ГБ", availMb.Avg, ramGb)
                    : string.Format("Мало свободной памяти: в среднем {0:F0} МБ ({1:F0}% от {2:F0} ГБ)", availMb.Avg, availPctAvg, ramGb),
                Symptom = string.Format("Доступно ниже 1 ГБ в пиковые моменты (P95 = {0:F0} МБ). Commit: средний {1:F0}% от лимита, максимум {2:F0}%.",
                    availMb.P95, commit.Avg, commit.Max),
                Cause = "Суммарные запросы приложений превышают физическую память: система вытесняет страницы на диск — все приложения начинают «тормозить», возможны зависания."
            };
            f.Actions.Add("Раздел 4 отчёта: топ процессов по памяти (частная память). Проверить процесс с ростом > 100 МБ/час — вероятна утечка; перезапустить его в согласованное окно.");
            foreach (var p in topMem.Take(3))
            {
                var adv = AdviceFor(p.Name, p.Pid);
                if (adv != null) f.Actions.AddRange(adv);
            }
            if (topGrow.Count > 0)
            {
                f.Actions.Add("Признаки утечки памяти (рост частной памяти за период наблюдения): " +
                    string.Join("; ", topGrow.Select(p => string.Format("{0} (PID {1}): +{2:F0} МБ/час, сейчас {3:F0} МБ", p.Name, p.Pid, p.GrowthMbPerHour(x.Duration), p.PrivLastMb))) + ".");
                f.Actions.Add("Для процессов с утечкой: настроить регулярный плановый перезапуск (Диспетчер задач -> Планировщик заданий) до устранения причины, собрать дамп: procdump -ma <имя_процесса>.");
            }
            f.Actions.Add(ramGb <= 8
                ? "ОЗУ недостаточно для терминального сервера: добавить память (минимум 16 ГБ на 5-10 офисных пользователей, 32+ ГБ на 20+)."
                : "Если добавление памяти невозможно — сократить число одновременных сессий/перенести часть на другой сервер.");
            f.Actions.Add("Проверить, что файл подкачки достаточен (раздел «Конфигурация»): для сервера с 16-64 ГБ ОЗУ — задать фикс. размер 1.5xRAM на быстром диске или оставить автовыбор, но следить за % Usage.");
            f.Verify.Add("После изменений: Available MBytes не должен опускаться ниже 10% ОЗУ в часы пик; Commit < 85%.");
            r.Findings.Add(f);
        }
        if (hard.Avg > 100 || hard.ShareAbove >= 0.15)
        {
            r.Findings.Add(new Finding
            {
                Category = "RAM",
                Severity = hard.Avg > 300 ? "CRITICAL" : "WARNING",
                Title = string.Format("Активное свопирование: Pages/sec в среднем {0:F0}", hard.Avg),
                Symptom = string.Format("Жёсткие страничные ошибки (чтение/запись в файл подкачки): среднее {0:F0}/с, P95 {1:F0}/с, пик {2:F0}/с. Чтений с диска (Pages Input): {3:F0}/с.",
                    hard.Avg, hard.P95, hard.Max, pagesIn.Avg),
                Cause = "Памяти не хватает, система постоянно обращается к файлу подкачки — это одновременно нагружает диск и замедляет ВСЕ приложения.",
                Actions = { "Комбинация с низкой доступной памятью — приоритет: добавить ОЗУ или выселить часть процессов/сессий.", "Проверить процессы с большим Pages Input: колонки «Ошибки страниц» в мониторе ресурсов (resmon.exe -> Память).", "Убедиться, что файл подкачки не отключён полностью (это ломает работу при пиках)." },
                Verify = { "Pages/sec < 50 в среднем после изменений." }
            });
        }
        if (poolNp.Max > 1.5 * 1073741824L)
        {
            r.Findings.Add(new Finding
            {
                Category = "RAM",
                Severity = poolNp.Max > 3L * 1024 * 1024 * 1024 ? "CRITICAL" : "WARNING",
                Title = string.Format("Большой невыгружаемый пул ядра: до {0:F1} ГБ", poolNp.Max / 1073741824.0),
                Symptom = string.Format("Pool Nonpaged Bytes: среднее {0:F0} МБ, максимум {1:F0} МБ. Невыгружаемый пул нельзя сбросить в файл подкачки — он держит физическую память.", poolNp.Avg / 1048576.0, poolNp.Max / 1048576.0),
                Cause = "Утечка в пуле ядра: обычно драйвер (сетевой фильтр, антивирус, СКЗИ, драйвер принтера) либо деградация при большом числе SMB-клиентов.",
                Actions = { "Найти «толстый» тег пула: poolmon.exe (из WDK) — колонка Nonp Bytes по тегам.", "По тегу определить драйвер (поиск тега в базе или findstr в .sys файлах драйверов).", "Обновить/заменить виновный драйвер; временно — перезапустить соответствующую службу.", "Для файловых серверов проверить параметры LanmanServer (Size/NonPagedPoolLimit) и событие 2019 'srv' в журнале." },
                Verify = { "Повторный замер: невыгружаемый пул стабилен (не растёт) и < 1 ГБ." }
            });
        }

        // ---------------- DISK ----------------
        if (latR.P95 > 20 || latW.P95 > 20 || latRMax.P95 > 50 || latWMax.P95 > 50)
        {
            bool crit = latR.P95 > 50 || latW.P95 > 50;
            var f = new Finding
            {
                Category = "Disk",
                Severity = crit ? "CRITICAL" : "WARNING",
                Title = crit
                    ? string.Format("Дисковая подсистема слишком медленная: задержки до {0:F0} мс", Math.Max(latRMax.Max, latWMax.Max))
                    : string.Format("Повышенные задержки диска: чтение P95 {0:F1} мс, запись P95 {1:F1} мс", latR.P95, latW.P95),
                Symptom = string.Format("Средние задержки: чтение {0:F1} мс, запись {1:F1} мс (P95: {2:F1}/{3:F1} мс). Очередь диска: средняя {4:F1}, максимум {5:F0}. Норма для HDD — до 15-20 мс, для SSD — до 5 мс.",
                    latR.Avg, latW.Avg, latR.P95, latW.P95, dQueueAvg.Avg, dQueueMax.Max),
                Cause = "Диск не справляется с потоком запросов: перегруженный/изношенный HDD, RAID в деградации, виртуальный диск на перегруженном хранилище, либо его «заливают» антивирус/резервное копирование/индексация."
            };
            foreach (var d in TopWorstDisks(x)) f.Symptom += " " + d;
            f.Actions.Add("Проверить журналы (раздел 7): ошибки disk/ntfs/stor — при их наличии сначала проверить оборудование (см. соответствующую находку).");
            f.Actions.Add("Найти, кто нагружает диск: раздел 4 (топ процессов по вводу-выводу) или resmon.exe -> Диск.");
            foreach (var p in topIo.Take(3))
            {
                var adv = AdviceFor(p.Name, p.Pid);
                if (adv != null) f.Actions.AddRange(adv);
            }
            f.Actions.Add("Исключить наложенные нагрузки: перенести антивирусное сканирование/бэкапы на ночные окна; отключить индексацию Windows Search на рабочих томах.");
            f.Actions.Add("Виртуальная машина: проверить, что диски не на перегруженном хранилище (метрики хоста/СХД), увеличить лимиты IOPS у хостинг-провайдера.");
            f.Actions.Add("Стратегически: перевести сервер на SSD/NVMe — для терминального сервера это самое эффективное вложение.");
            f.Verify.Add("Повторный замер: Avg. Disk sec/Transfer < 20 мс (HDD) или < 5 мс (SSD), Current Disk Queue Length < 2 на диск.");
            r.Findings.Add(f);
        }
        if (dQueueAvg.ShareAbove >= 0.15 && (latR.P95 <= 20 && latW.P95 <= 20))
        {
            r.Findings.Add(new Finding
            {
                Category = "Disk",
                Severity = "WARNING",
                Title = string.Format("Очереди к диску в {0:F0}% времени", dQueueAvg.ShareAbove * 100),
                Symptom = string.Format("Текущая очередь к дискам превышала 2 в {0:F0}% замеров (средняя {1:F1}, максимум {2:F0}), при этом задержки пока в норме.", dQueueAvg.ShareAbove * 100, dQueueAvg.Avg, dQueueMax.Max),
                Cause = "Диск работает на пределе, всплески параллельных запросов (старт приложений, массовое открытие файлов).",
                Actions = { "Профилировать пики: когда возникают (начало рабочего дня, запуск 1С/Outlook всеми сразу) — разнести нагрузку.", "Вынести файл подкачки/профили на отдельный быстрый диск.", "Рассмотреть SSD при апгрейде." }
            });
        }
        double minFree = MinFreePct(x);
        if (minFree < 10)
        {
            var bad = x.Samples.SelectMany(s => s.Logical).GroupBy(l => l.Name)
                .Select(g => new { Name = g.Key, Min = g.Min(l => double.IsNaN(l.FreePct) ? 100 : l.FreePct), MinMb = g.Min(l => double.IsNaN(l.FreeMb) ? 0 : l.FreeMb) })
                .Where(v => v.Min < 10).OrderBy(v => v.Min).ToList();
            bool crit = bad.Any(v => v.Min < 5);
            r.Findings.Add(new Finding
            {
                Category = "Disk",
                Severity = crit ? "CRITICAL" : "WARNING",
                Title = crit ? "Критически мало места на диске (<5%)" : "Мало свободного места на диске (<10%)",
                Symptom = "Диски: " + string.Join("; ", bad.Select(v => string.Format("{0} — свободно {1:F1}% ({2:F0} ГБ)", v.Name, v.Min, v.MinMb / 1024.0))) + ".",
                Cause = "Заполненный системный диск вызывает фрагментацию, отказ записи журналов/временных файлов и падение производительности, вплоть до зависания ОС.",
                Actions = { "Очистить: cleanmgr /sagerun:1, удалить старые профили (Свойства системы -> Дополнительно -> Профили), очистить C:\\Windows\\Temp и %TEMP%.", "Проверить C:\\Windows\\SoftwareDistribution\\Download (кэш обновлений), tmp-каталоги RDS-сессий.", "Перенести файл подкачки/данные на другой том; расширить диск (для ВМ — на лету).", "Настроить мониторинг места и оповещения на пороге 15%." },
                Verify = { "Свободно не менее 15% на каждом томе." }
            });
        }

        // ---------------- NET ----------------
        if (NetUtilP95 > 70)
        {
            bool crit = NetUtilP95 > 90;
            r.Findings.Add(new Finding
            {
                Category = "Net",
                Severity = crit ? "CRITICAL" : "WARNING",
                Title = crit
                    ? string.Format("Сетевой канал насыщен: {0:F0}% утилизации", NetUtilP95)
                    : string.Format("Высокая утилизация сети: до {0:F0}%", NetUtilP95),
                Symptom = string.Format("Интерфейс \"{0}\": средняя загрузка {1:F0}%, P95 {2:F0}% от пропускной способности.", NetUtilTop, NetUtilAvg, NetUtilP95),
                Cause = "Канал сервера перегружен (копирования, обновления, медиа в сессиях) — растут задержки для всех RDP-клиентов, экраны «подвисают».",
                Actions = { "Найти потребителей: раздел 6 (топ процессов по соединениям) + netstat -bno | more, Монитор ресурсов -> Сеть.", "Запретить массовые загрузки в рабочих сессиях (GPO/прокси), перенести обновления/бэкапы на ночные окна.", "Проверить дуплекс/скорость порта коммутатора (ethtool/порт) и ошибки на интерфейсе (раздел 6).", "Увеличить полосу (1 Гбит/с минимум для RDS 20+ пользователей) или добавить второй адаптер/teaming." },
                Verify = { "Утилизация канала в часах пик < 60%." }
            });
        }
        if (NetErrors > 10)
        {
            r.Findings.Add(new Finding
            {
                Category = "Net",
                Severity = "WARNING",
                Title = string.Format("Ошибки пакетов на сетевом интерфейсе: {0:F0} суммарно", NetErrors),
                Symptom = "Обнаружены счётчики Packets Received/Outbound Errors > 0 — пакеты отбрасываются или приходят повреждёнными.",
                Cause = "Физические проблемы: кабель, порт коммутатора, драйвер NIC, либо перегрузка буферов при высоком трафике.",
                Actions = { "Проверить порт коммутатора: счётчики ошибок/CRC, заменить патч-корд/порт.", "Обновить драйвер NIC; включить RSS и проверить offload'ы: Get-NetAdapterAdvancedProperty.", "Если ошибки растут при умеренном трафике — подозрение на оборудование (NIC/коммутатор)." },
                Verify = { "Счётчики ошибок не увеличиваются." }
            });
        }
        if (twMax > 8000)
        {
            r.Findings.Add(new Finding
            {
                Category = "Net",
                Severity = "WARNING",
                Title = string.Format("Много TCP-соединений в TIME_WAIT: до {0:F0}", twMax),
                Symptom = "Большое число закрывающихся соединений занимают порты и память стека — возможны отказы новых подключений.",
                Cause = "Интенсивные короткие соединения от приложений (без keep-alive), часто — интеграционные шины, 1С-клиенты к серверу, мониторинговые агенты.",
                Actions = { "Определить источник: раздел 6 (топ процессов по соединениям) и топ удалённых адресов.", "Включить keep-alive в приложении-источнике (правильное решение).", "Если не помогает — сократить TcpTimedWaitDelay до 60 сек (HKLM\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters, DWORD TcpTimedWaitDelay=60, перезагрузка)." },
                Verify = { "TIME_WAIT < 5000 в любой момент." }
            });
        }
        if (x.SrvRejected > 0 || x.SrvShort > 0)
        {
            r.Findings.Add(new Finding
            {
                Category = "Net",
                Severity = "WARNING",
                Title = "Серверная служба SMB отбрасывала запросы (Server work items)",
                Symptom = string.Format("Blocking Requests Rejected: {0:F0}, Work Item Shortages: {1:F0} за период.", x.SrvRejected, x.SrvShort),
                Cause = "Файловый сервис (общие папки терминального сервера) не успевает обрабатывать запросы — клиенты ждут, файловые операции «зависают».",
                Actions = { "Проверить задержки дисков в момент всплесков (раздел 5) — обычно первопричина в дисках/антивирусе на файловых операциях.", "Исключить сетевые папки сервера из сканирования в реальном времени (антивирус).", "При подтверждении нехватки — увеличить MaxWorkItems/Size (реестр LanmanServer\\Parameters), но только после проверки дисков." },
                Verify = { "Счётчики равны 0 за период мониторинга." }
            });
        }

        // ---------------- HANG ----------------
        if (hung.Count > 0)
        {
            var f = new Finding
            {
                Category = "Hang",
                Severity = "CRITICAL",
                Title = string.Format("Обнаружены зависшие приложения ({0})", hung.Count),
                Symptom = "Процессы с окнами, не обрабатывающими сообщения: " +
                    string.Join("; ", hung.Take(8).Select(p => string.Format("{0} (PID {1}, зависал в {2} замерах)", p.Name, p.Pid, p.HungCount))) + ".",
                Cause = "Окно перестало обрабатывать сообщения: процесс ждёт блокировку (файл/БД/принтер/сеть) или исчерпал ресурсы. Для пользователей это выглядит как «программа не отвечает»."
            };
            foreach (var p in hung.Take(3))
            {
                var adv = AdviceFor(p.Name, p.Pid);
                if (adv != null) f.Actions.AddRange(adv);
                f.Actions.Add(string.Format("{0} (PID {1}): снять дамп перед перезапуском: procdump -ma {1} hang.dmp, затем перезапустить процесс; сверить, что он ждал (IO/сеть/блокировки) — см. его показатели ввода-вывода в разделе 4.", p.Name, p.Pid));
            }
            f.Actions.Add("Если зависания массовые и в момент пиков диск/память — устранять первопричину по разделам выше (это симптом, а не болезнь).");
            f.Verify.Add("Повторный мониторинг: список зависших окон пуст.");
            r.Findings.Add(f);
        }

        // ---------------- EVENTS ----------------
        var diskErr = x.Events.Where(e => e.Level <= 2 && e.IsDiskRelated).ToList();
        if (diskErr.Count > 0)
        {
            var f = new Finding
            {
                Category = "Events",
                Severity = "CRITICAL",
                Title = "Ошибки дисковой подсистемы в журнале событий",
                Symptom = string.Join("; ", diskErr.Take(5).Select(e => string.Format("{0} #{1}: {2} раз (последнее {3:dd.MM HH:mm})", e.Provider, e.Id, e.Count, e.Last))) + ".",
                Cause = "ОС сообщает о проблемах чтения/записи: изношенный или сбойный диск, деградировавший RAID, проблемы контроллера/питания. Это первопричина медленного и «зависающего» сервера.",
                Actions = { "ПРИОРИТЕТ: сделать/проверить резервную копию.", "Проверить состояние: chkdsk C: /scan (без перезагрузки), для RAID — утилита контроллера (проверить статус массива, горячий резерв).", "S.M.A.R.T.: Get-PhysicalDisk | Get-StorageReliabilityCounter или утилита производителя (CrystalDiskInfo на месте).", "Заменить сбойный диск/блок; при виртуализации — проверить диск на хосте гипервизора и путь к СХД.", "Если ошибки на логическом уровне (NTFS) — запланировать chkdsk /f /r в окно обслуживания." },
                Verify = { "В журнале после обслуживания нет новых ошибок disk/ntfs/stor." }
            };
            r.Findings.Add(f);
        }
        var hwErr = x.Events.Where(e => e.Level <= 2 && e.IsHardwareRelated).ToList();
        if (hwErr.Count > 0)
        {
            r.Findings.Add(new Finding
            {
                Category = "Events",
                Severity = "CRITICAL",
                Title = "События аппаратных сбоев/перезагрузок (WHEA/Kernel-Power)",
                Symptom = string.Join("; ", hwErr.Take(5).Select(e => string.Format("{0} #{1}: {2} раз", e.Provider, e.Id, e.Count))) + ".",
                Cause = "Аппаратные ошибки (WHEA), неожиданные выключения (Kernel-Power 41), сбои питания — возможная причина «непонятных» зависаний и перезагрузок.",
                Actions = { "Проверить журналы гипервизора / iLO / iDRAC / IPMI на события железа.", "Обновить BIOS/прошивки; проверить RAM (memtest), питание, температуры.", "Если событие Kernel-Power 41 — сервер просто выключался по питанию/зависанию ядра: включить полный дамп памяти для анализа следующего случая." }
            });
        }
        var rdpErr = x.Events.Where(e => e.Level <= 2 && e.IsRdpRelated).ToList();
        if (rdpErr.Count > 0)
        {
            r.Findings.Add(new Finding
            {
                Category = "Events",
                Severity = "WARNING",
                Title = "Ошибки служб удалённых рабочих столов",
                Symptom = string.Join("; ", rdpErr.Take(5).Select(e => string.Format("{0} #{1}: {2} раз", e.Provider, e.Id, e.Count))) + ".",
                Cause = "Проблемы брокера/лицензирования RDS/переподключений сессий — часть пользователей может испытывать «подвисания» при переподключении.",
                Actions = { "Проверить статус лицензий RDS: события Microsoft-Windows-TerminalServices-Licensing, утилита lsdiag.msc (Server 2016+).", "Проверить лимиты сессий и таймауты: GPO 'Ограничить время отключённых сессий' — отключённые сессии держат память.", "Выгонять «мёртвые» сессии: query user / reset session <ID>." }
            });
        }
        var appErr = x.Events.Where(e => e.Level <= 2 && e.Log == "Application").ToList();
        if (appErr.Count > 0)
        {
            r.Findings.Add(new Finding
            {
                Category = "Events",
                Severity = "WARNING",
                Title = string.Format("Ошибки приложений в журнале: {0} групп", appErr.Count),
                Symptom = string.Join("; ", appErr.Take(5).Select(e => string.Format("{0} #{1}: {2} раз", e.Provider, e.Id, e.Count))) + ".",
                Cause = "Падения/сбои приложений могут быть следствием нехватки ресурсов или причиной зависаний конкретных программ.",
                Actions = { "Соотнести имена сбойных приложений с топ-процессами раздела 4.", "Для повторяющихся падений включить сбор дампов: WER или procdump -i.", "Проверить .NET-исключения в Application журнале (.NET Runtime) — передать разработчикам." }
            });
        }
        var duringEvents = x.Events.Where(e => e.DuringCollection && e.Level <= 2).ToList();
        if (duringEvents.Count > 0)
        {
            r.Findings.Add(new Finding
            {
                Category = "Events",
                Severity = "WARNING",
                Title = "Ошибки возникали ПРЯМО во время наблюдения",
                Symptom = string.Join("; ", duringEvents.Take(6).Select(e => string.Format("{0} #{1} x{2}", e.Provider, e.Id, e.Count))) + ".",
                Cause = "Эти события совпали с медленной работой — высока вероятность связи с проблемой.",
                Actions = { "Разобрать перечисленные события первыми: их описание обычно прямо указывает на сбойный компонент." }
            });
        }

        // ---------------- CONFIG ----------------
        if (x.Sys.Uptime.TotalDays > 180)
        {
            r.Findings.Add(new Finding
            {
                Category = "Config",
                Severity = "INFO",
                Title = string.Format("Сервер не перезагружался {0:F0} дней", x.Sys.Uptime.TotalDays),
                Symptom = "Длительный аптайм: накапливаются утечки памяти/дескрипторов, фрагментация, неустановленные обновления, требующие перезагрузки.",
                Actions = { "Запланировать окно обслуживания: установить обновления и перезагрузить (для RDS — с уведомлением пользователей).", "Перед перезагрузкой сохранить отчёт ServerHealth для сравнения «до/после»." }
            });
        }
        string pp = x.Sys.PowerPlan.ToLowerInvariant();
        if (pp.Contains("сбалансирован") || pp.Contains("balanced") || pp.Contains("эконом"))
        {
            r.Findings.Add(new Finding
            {
                Category = "Config",
                Severity = "WARNING",
                Title = "Схема электропитания не «Высокая производительность»",
                Symptom = "Текущая схема: " + (string.IsNullOrEmpty(x.Sys.PowerPlan) ? "не определена (по умолчанию — Сбалансированная)" : x.Sys.PowerPlan.Split('\n').Last().Trim()),
                Cause = "Сбалансированная схема сбрасывает частоту CPU в простое — на RDS это заметные «подлагивания» при первом открытии окон и скачки производительности.",
                Actions = { "powercfg /list — посмотреть схемы.", "Включить: powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c (High performance).", "Для ВМ убедиться, что хост-гипервизор не троттлит vCPU." },
                Verify = { "powercfg /getactivescheme показывает High performance." }
            });
        }
        if (ramGb <= 8 && x.Sessions.Count(s => s.State == "Active") >= 3)
        {
            r.Findings.Add(new Finding
            {
                Category = "Config",
                Severity = "WARNING",
                Title = "Мало ОЗУ для терминального сервера",
                Symptom = string.Format("Установлено {0:F0} ГБ ОЗУ при {1} активных RDS-сессиях.", ramGb, x.Sessions.Count(s => s.State == "Active")),
                Cause = "Для комфортной работы 5-10 офисных пользователей в RDS требуется 16+ ГБ (8-16 ГБ на каждые 10 сессий в зависимости от приложений).",
                Actions = { "Запланировать расширение ОЗУ (или перенос пользователей на второй сервер)." }
            });
        }

        // если ничего критичного не нашлось — позитивное резюме
        if (!r.Findings.Any(f => f.Severity == "CRITICAL"))
        {
            r.Findings.Insert(0, new Finding
            {
                Category = "OK",
                Severity = "INFO",
                Title = "Критичных узких мест за период наблюдения не зафиксировано",
                Symptom = "Все подсистемы в пределах нормы (см. оценки и разделы 2-6).",
                Cause = "Если пользователи всё равно жалуются на медленную работу: увеличить период наблюдения (-d 30) на проблемный час, собрать отчёт в момент жалобы и сравнить с этим.",
                Actions = { "Повторить замер в период жалоб — причинно-следственная связь видна только в момент проблемы." }
            });
        }

        // ---------------- вердикт ----------------
        var worst = r.Scores.OrderBy(s => s.Value).First();
        string worstKey = worst.Key.Split(' ')[0];
        r.HasCritical = r.Findings.Any(f => f.Severity == "CRITICAL");
        r.VerdictTitle = worst.Value >= 70 && !r.HasCritical
            ? "Выраженного узкого места не выявлено"
            : "Наиболее вероятная причина: " + VerdictName(worstKey);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(worst.Value >= 70 && !r.HasCritical
            ? "Все подсистемы показали удовлетворительные значения за период наблюдения. Если проблема ощущается в другие часы — повторите замер в момент жалобы."
            : BuildVerdictEvidence(worstKey, cpu, queue, cores, availMb, availPctAvg, commit, hard, latR, latW, dQueueAvg, NetUtilTop, NetUtilP95, twMax, hung.Count));
        if (topCpu.Count > 0)
            sb.AppendLine("Главный потребитель CPU: " + topCpu[0].Name + " (PID " + topCpu[0].Pid + ", " + string.Format("{0:F0}%", topCpu[0].CpuAvg) + " в среднем).");
        if (topMem.Count > 0)
            sb.AppendLine("Главный потребитель памяти: " + topMem[0].Name + " (PID " + topMem[0].Pid + ", до " + string.Format("{0:F0}", topMem[0].PrivMaxMb) + " МБ частной памяти).");
        if (hung.Count > 0)
            sb.AppendLine("Зафиксированы зависшие приложения: " + string.Join(", ", hung.Take(5).Select(p => p.Name)) + " — см. находку Hang.");
        r.VerdictText = sb.ToString().TrimEnd();

        return r;
    }

    private static string VerdictName(string key)
    {
        switch (key)
        {
            case "CPU": return "недостаточная производительность процессора (перегрузка CPU)";
            case "RAM": return "нехватка оперативной памяти / проблемы с памятью";
            case "Disk": return "медленная дисковая подсистема";
            case "Net": return "перегрузка/проблемы локальной сети";
            default: return "комбинированная нагрузка";
        }
    }

    private static string BuildVerdictEvidence(string key, M cpu, M queue, int cores, M availMb, double availPct,
        M commit, M hard, M latR, M latW, M dq, string netIf, double netUtil, int tw, int hungCount)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Доказательная база (за период наблюдения):");
        if (key == "CPU")
            sb.AppendLine(string.Format("  • CPU: средняя {0:F0}%, P95 {1:F0}%, очередь процессора до {2:F0} при {3} ядрах.", cpu.Avg, cpu.P95, queue.Max, cores));
        if (key == "RAM")
            sb.AppendLine(string.Format("  • RAM: свободно в среднем {0:F0} МБ ({1:F0}%), commit до {2:F0}%, жёсткие страничные ошибки в среднем {3:F0}/с.", availMb.Avg, availPct, commit.Max, hard.Avg));
        if (key == "Disk")
            sb.AppendLine(string.Format("  • Диск: задержки чтения P95 {0:F1} мс, записи P95 {1:F1} мс, средняя очередь {2:F1}.", latR.P95, latW.P95, dq.Avg));
        if (key == "Net")
            sb.AppendLine(string.Format("  • Сеть: интерфейс \"{0}\" утилизирован до {1:F0}%, TIME_WAIT до {2}.", netIf, netUtil, tw));
        if (hungCount > 0)
            sb.AppendLine(string.Format("  • Зависшие окна приложений: {0} процесс(ов).", hungCount));
        return sb.ToString().TrimEnd();
    }

    private static double MinFreePct(Input x)
    {
        double min = 100;
        foreach (var l in x.Samples.SelectMany(s => s.Logical))
        {
            if (double.IsNaN(l.FreePct)) continue;
            if (l.FreePct < min) min = l.FreePct;
        }
        return min;
    }

    private static List<string> TopWorstDisks(Input x)
    {
        var res = new List<string>();
        var groups = x.Samples.SelectMany(s => s.Physical).GroupBy(d => d.Name);
        foreach (var g in groups)
        {
            double latR95 = Percentile(g.Select(d => double.IsNaN(d.LatReadMs) ? 0 : d.LatReadMs), 0.95);
            double latW95 = Percentile(g.Select(d => double.IsNaN(d.LatWriteMs) ? 0 : d.LatWriteMs), 0.95);
            double qMax = g.Max(d => double.IsNaN(d.QueueCur) ? 0 : d.QueueCur);
            if (latR95 > 20 || latW95 > 20 || qMax >= 4)
                res.Add(string.Format("[{0}] P95 чтение {1:F0} мс, запись {2:F0} мс, очередь до {3:F0}.", g.Key, latR95, latW95, qMax));
        }
        return res.Take(6).ToList();
    }

    internal static double Percentile(IEnumerable<double> vals, double p)
    {
        var s = vals.OrderBy(v => v).ToList();
        if (s.Count == 0) return 0;
        int idx = (int)Math.Ceiling(p * (s.Count - 1));
        if (idx < 0) idx = 0;
        return s[idx];
    }

    private static string[] AdviceFor(string name, int pid)
    {
        if (name == null) return null;
        if (KnownProcessAdvice.TryGetValue(name, out var adv))
        {
            var res = new List<string>(adv);
            res[0] = res[0] + string.Format(" [процесс: {0}, PID {1}]", name, pid);
            return res.ToArray();
        }
        if (name != null && name.StartsWith("1cv8")) return KnownProcessAdvice["1cv8"];
        if (name != null && name.StartsWith("rphost")) return KnownProcessAdvice["rphost"];
        return null;
    }
}
