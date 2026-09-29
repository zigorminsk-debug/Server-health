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

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string la = a.ToLowerInvariant();
            switch (la)
            {
                case "-h": case "--help": case "/?": case "/h": o.ShowHelp = true; break;
                case "--version": case "-v": o.ShowVersion = true; break;
                case "-d": case "--duration": o.DurationMin = ReadNum(args, ref i, a); break;
                case "-i": case "--interval": o.IntervalSec = (int)Math.Max(1, Math.Round(ReadNum(args, ref i, a))); break;
                case "-o": case "--out": o.OutDir = ReadStr(args, ref i, a); break;
                case "--events-hours": o.EventsHours = (int)Math.Max(0, Math.Round(ReadNum(args, ref i, a))); break;
                case "-q": case "--quiet": o.Quiet = true; break;
                case "--no-json": o.NoJson = true; break;
                case "--quick": o.DurationMin = 1; o.IntervalSec = 2; break;
                default:
                    ConsoleUi.Warn("Неизвестный параметр: " + a);
                    o.ShowHelp = true;
                    break;
            }
        }
        if (o.DurationMin < 0.03) o.DurationMin = 0.03;
        if (o.DurationMin > 1440) o.DurationMin = 1440;
        if (o.IntervalSec < 1) o.IntervalSec = 1;
        if (o.IntervalSec > 3600) o.IntervalSec = 3600;
        if (string.IsNullOrEmpty(o.OutDir))
            o.OutDir = "ServerHealth_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return o;
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
