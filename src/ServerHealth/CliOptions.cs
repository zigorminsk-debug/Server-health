using System.Globalization;

namespace ServerHealth;

/// <summary>Разбор аргументов командной строки.</summary>
public sealed class CliOptions
{
    public double DurationMin = 10;
    public int IntervalSec = 5;
    public string OutDir = "";
    public int EventsHours = 24;
    public bool Quiet;
    public bool NoJson;
    public bool ShowHelp;
    public bool ShowVersion;
    /// <summary>Запустить GUI (окно мониторинга). По умолчанию — если аргументов нет.</summary>
    public bool Gui;
    /// <summary>Режим постоянного мониторинга: отчёт каждые WatchCycleMin минут.</summary>
    public bool Watch;
    public double WatchCycleMin = 10;
    /// <summary>Сколько последних отчётов хранить (0 = все).</summary>
    public int KeepReports = 0;
    /// <summary>Не открывать report.html автоматически после разового замера.</summary>
    public bool NoOpen;

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        // без аргументов — графический интерфейс
        o.Gui = args.Length == 0;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string la = a.ToLowerInvariant();
            switch (la)
            {
                case "-h": case "--help": case "/?": case "/h": o.ShowHelp = true; break;
                case "--version": case "-v": o.ShowVersion = true; break;
                case "-g": case "--gui": o.Gui = true; break;
                case "-d": case "--duration": o.DurationMin = ReadNum(args, ref i, a); break;
                case "-i": case "--interval": o.IntervalSec = (int)Math.Max(1, Math.Round(ReadNum(args, ref i, a))); break;
                case "-o": case "--out": o.OutDir = ReadStr(args, ref i, a); break;
                case "--events-hours": o.EventsHours = (int)Math.Max(0, Math.Round(ReadNum(args, ref i, a))); break;
                case "-q": case "--quiet": o.Quiet = true; break;
                case "--no-json": o.NoJson = true; break;
                case "--no-open": o.NoOpen = true; break;
                case "--keep": o.KeepReports = (int)Math.Max(0, Math.Round(ReadNum(args, ref i, a))); break;
                case "--quick": o.DurationMin = 1; o.IntervalSec = 2; break;
                case "-w": case "--watch":
                    o.Watch = true;
                    // необязательное значение — длительность цикла в минутах
                    if (i + 1 < args.Length &&
                        (double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double wv) ||
                         double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.CurrentCulture, out wv)))
                    { o.WatchCycleMin = wv; i++; }
                    break;
                default:
                    ConsoleUi.Warn("Неизвестный параметр: " + a);
                    o.ShowHelp = true;
                    break;
            }
        }
        if (o.DurationMin < 0.03) o.DurationMin = 0.03;
        if (o.DurationMin > 1440) o.DurationMin = 1440;
        if (o.WatchCycleMin < 0.1) o.WatchCycleMin = 0.1;
        if (o.WatchCycleMin > 1440) o.WatchCycleMin = 1440;
        if (o.IntervalSec < 1) o.IntervalSec = 1;
        if (o.IntervalSec > 3600) o.IntervalSec = 3600;
        return o;
    }

    /// <summary>Каталог отчёта для разового запуска (с меткой времени).</summary>
    public string DefaultSingleOutDir()
    {
        return string.IsNullOrEmpty(OutDir)
            ? "ServerHealth_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
            : OutDir;
    }

    private static double ReadNum(string[] args, ref int i, string opt)
    {
        if (i + 1 >= args.Length) throw new ArgumentException("После " + opt + " должно быть число");
        string s = args[++i];
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) &&
            !double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
            throw new ArgumentException("Ожидалось число после " + opt + ": " + s);
        return v;
    }

    private static string ReadStr(string[] args, ref int i, string opt)
    {
        if (i + 1 >= args.Length) throw new ArgumentException("После " + opt + " должен быть путь");
        return args[++i];
    }
}
