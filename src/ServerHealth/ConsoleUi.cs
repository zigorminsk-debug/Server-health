using System.Text;

namespace ServerHealth;

/// <summary>Консольный вывод: баннер, справка, живая строка прогресса, итоговая сводка.</summary>
public static class ConsoleUi
{
    public static void Banner(string version)
    {
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  ServerHealth — диагностика медленной работы терминального   ║");
        Console.WriteLine("║  сервера: CPU / RAM / Диски / Сеть / Процессы / Сессии       ║");
        Console.WriteLine("║  v" + version.PadRight(53) + "║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
    }

    public static void Help()
    {
        Console.WriteLine("Использование:");
        Console.WriteLine("  ServerHealth.exe [параметры]");
        Console.WriteLine();
        Console.WriteLine("Параметры:");
        Console.WriteLine("  -d, --duration  <мин>   длительность наблюдения в минутах (по умолчанию 10, дробные допустимы)");
        Console.WriteLine("  -i, --interval  <сек>   интервал замера в секундах (по умолчанию 5, минимум 1)");
        Console.WriteLine("  -o, --out       <путь>  каталог для отчёта (по умолчанию .\\ServerHealth_ГГГГММДД_ЧЧММСС)");
        Console.WriteLine("      --events-hours <ч>  сколько часов журнала событий анализировать (по умолчанию 24, 0 = не читать)");
        Console.WriteLine("      --quick             быстрый замер: 1 минута с интервалом 2 с");
        Console.WriteLine("      --no-json           не создавать report.json");
        Console.WriteLine("  -q, --quiet             без живого вывода прогресса");
        Console.WriteLine("  -h, --help              эта справка");
        Console.WriteLine("  --version               версия");
        Console.WriteLine();
        Console.WriteLine("Примеры:");
        Console.WriteLine("  ServerHealth.exe                       — 10 минут мониторинга, отчёт в текущем каталоге");
        Console.WriteLine("  ServerHealth.exe -d 30 -i 10 -o D:\\logs — 30 минут, замер раз в 10 с, в D:\\logs");
        Console.WriteLine("  ServerHealth.exe --quick               — быстрая проверка на 1 минуту");
        Console.WriteLine();
        Console.WriteLine("Запускать ПРАВОЙ кнопкой -> «Запуск от имени администратора» — иначе часть данных");
        Console.WriteLine("(соединения по процессам, открытые файлы, часть событий) будет недоступна.");
        Console.WriteLine();
        Console.WriteLine("Во время работы: Ctrl+C — завершить досрочно и всё равно сформировать отчёт.");
        Console.WriteLine("Коды возврата: 0 — ок; 2 — найдены критические проблемы; 1 — ошибка запуска.");
    }

    public static void Warn(string msg)
    {
        var c = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[!] " + msg);
        }
        finally { Console.ForegroundColor = c; }
    }

    public static void Error(string msg)
    {
        var c = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[ОШИБКА] " + msg);
        }
        finally { Console.ForegroundColor = c; }
    }

    public static void Live(SystemSample s, int done, int total)
    {
        string tcp = string.Format("TCP est {0}/tw {1}", s.TcpEstablished, s.TcpTimeWait);
        string disk = "";
        var worst = s.Physical.Where(d => !double.IsNaN(d.LatReadMs) || !double.IsNaN(d.LatWriteMs))
            .OrderByDescending(d => Math.Max(double.IsNaN(d.LatReadMs) ? 0 : d.LatReadMs, double.IsNaN(d.LatWriteMs) ? 0 : d.LatWriteMs)).FirstOrDefault();
        if (worst != null)
            disk = string.Format("| Диск {0}: з{1:F1} {2:F0}мс ", Trunc(worst.Name, 8), double.IsNaN(worst.QueueCur) ? 0 : worst.QueueCur, Math.Max(double.IsNaN(worst.LatReadMs) ? 0 : worst.LatReadMs, double.IsNaN(worst.LatWriteMs) ? 0 : worst.LatWriteMs));
        double netMbps = s.Nets.Sum(n => (double.IsNaN(n.RxMbps) ? 0 : n.RxMbps) + (double.IsNaN(n.TxMbps) ? 0 : n.TxMbps));
        string ram = double.IsNaN(s.AvailMb) ? "н/д" : (s.AvailMb >= 1024 ? string.Format("{0:F1}ГБ", s.AvailMb / 1024) : string.Format("{0:F0}МБ", s.AvailMb));
        Console.Write(string.Format("\r[{0,4}/{1}] CPU {2,5:F1}%  RAM своб. {3,-8} {4}| Сеть {5,6:F0} Мбит/с | {6}   ",
            done, total,
            double.IsNaN(s.CpuTotal) ? 0 : s.CpuTotal,
            ram, disk, netMbps, tcp));
    }

    public static void Summary(AnalysisResult r, ReportWriter.Paths paths, SysInfo sys, List<ProcAgg> procs, TimeSpan duration)
    {
        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine("════════════════ ИТОГИ ════════════════");
        foreach (var s in r.Scores)
        {
            var c = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = s.Value >= 70 ? ConsoleColor.Green : (s.Value >= 40 ? ConsoleColor.Yellow : ConsoleColor.Red);
                Console.WriteLine(string.Format("  {0,-18} [{1}] {2,3}/100", s.Key, new string('#', s.Value / 10) + new string('.', 10 - s.Value / 10), s.Value));
            }
            finally { Console.ForegroundColor = c; }
        }
        Console.WriteLine();
        var c2 = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = r.HasCritical ? ConsoleColor.Red : ConsoleColor.White;
            Console.WriteLine("  ВЕРДИКТ: " + r.VerdictTitle);
        }
        finally { Console.ForegroundColor = c2; }
        foreach (var l in r.VerdictText.Split('\n'))
            Console.WriteLine("  " + l.TrimEnd());

        var top = procs.Where(p => p.Samples >= 2 && p.Pid > 4).OrderByDescending(p => p.CpuAvg).Take(3).ToList();
        if (top.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Топ-3 по CPU: " + string.Join(", ", top.Select(p => string.Format("{0} ({1:F0}%)", p.Name, p.CpuAvg))));
        }
        Console.WriteLine();
        Console.WriteLine("  Отчёты сохранены в: " + paths.Dir);
        Console.WriteLine("    " + Path.GetFileName(paths.Txt) + "        — полный отчёт с инструкциями (открыть первым)");
        if (!string.IsNullOrEmpty(paths.Json)) Console.WriteLine("    " + Path.GetFileName(paths.Json) + "      — данные для автоматизации");
        Console.WriteLine("    samples.csv / processes.csv / events.csv — исходные данные");
        Console.WriteLine();
    }

    private static string Trunc(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= n ? s : s.Substring(0, n);
    }
}
