using System.Diagnostics.Eventing.Reader;

namespace ServerHealth;

/// <summary>
/// Чтение журналов Windows (System + Application): критические, ошибки и
/// отдельные предупреждения за заданное число часов.
/// </summary>
internal sealed class EventLogCollector
{
    /// <summary>Поставщики, чьи WARNING тоже включаем (остальные warnings слишком шумные).</summary>
    private static readonly string[] WarnProviders =
    {
        "disk", "ntfs", "stor", "volmgr", "volsnap", "lanman", "srv2", "srvnet", "netbt", "tcpip",
        "termservice", "termdd", "remoteconnection", "terminalservices", "whea", "kernel-power",
        "lsasrv", "lsa", "netlogon", "group policy", "dfs", "mrxsmb", "rdp", "msiexec", "windows error"
    };

    public List<EventGroup> Collect(int hours, DateTime collectStart)
    {
        var res = new List<EventGroup>();
        var since = DateTime.Now.AddHours(-hours);
        ScanLog("System", since, collectStart, 3, 400, res, true);
        ScanLog("Application", since, collectStart, 2, 200, res, false);
        res.Sort((a, b) => b.Count.CompareTo(a.Count));
        return res;
    }

    private void ScanLog(string logName, DateTime since, DateTime collectStart, int maxLevel, int maxRecords,
                         List<EventGroup> res, bool includeWarnings)
    {
        try
        {
            long ms = (long)(DateTime.Now - since).TotalMilliseconds;
            string xpath = "*[System[(Level<=" + maxLevel + ") and TimeCreated[timediff(@SystemTime) <= " + ms + "]]]";
            var q = new EventLogQuery(logName, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            int taken = 0;
            while (taken < maxRecords)
            {
                EventRecord rec;
                try { rec = reader.ReadEvent(); }
                catch (EventLogException) { break; }
                if (rec == null) break;
                taken++;
                using (rec)
                {
                    try
                    {
                        int level = rec.Level ?? 4;
                        string provider = rec.ProviderName ?? "";
                        if (level == 3 && !IsWarnProviderWanted(provider)) continue;

                        string msg;
                        try { msg = rec.FormatDescription() ?? ""; }
                        catch { msg = ""; }
                        msg = (msg ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                        if (msg.Length > 240) msg = msg.Substring(0, 240) + "…";
                        if (msg.Length == 0) msg = "(описание недоступно)";

                        var dt = rec.TimeCreated ?? DateTime.Now;
                        bool during = dt >= collectStart;
                        EventGroup g;

                        if (during)
                            g = res.FirstOrDefault(x => x.Log == logName && x.Provider == provider && x.Id == rec.Id && x.Level == level && x.DuringCollection);
                        else
                            g = res.FirstOrDefault(x => x.Log == logName && x.Provider == provider && x.Id == rec.Id && x.Level == level && !x.DuringCollection);

                        if (g == null)
                        {
                            g = new EventGroup
                            {
                                Log = logName,
                                Provider = provider,
                                Id = rec.Id,
                                Level = level,
                                LevelName = LevelName(level),
                                Count = 0,
                                First = dt,
                                Last = dt,
                                Sample = msg,
                                DuringCollection = during
                            };
                            res.Add(g);
                        }
                        g.Count++;
                        if (dt < g.First) g.First = dt;
                        if (dt > g.Last) g.Last = dt;
                    }
                    catch { }
                }
            }
        }
        catch
        {
            // журнал недоступен (нет прав/нет журнала) — не критично
        }
    }

    private static bool IsWarnProviderWanted(string provider)
    {
        if (string.IsNullOrEmpty(provider)) return false;
        string p = provider.ToLowerInvariant();
        foreach (var w in WarnProviders)
            if (p.Contains(w)) return true;
        return false;
    }

    private static string LevelName(int l)
    {
        switch (l)
        {
            case 1: return "КРИТИЧНО";
            case 2: return "Ошибка";
            case 3: return "Предупреждение";
            default: return "Инфо";
        }
    }
}
