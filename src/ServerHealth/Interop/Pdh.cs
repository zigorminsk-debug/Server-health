using System.Runtime.InteropServices;
using System.Text;

namespace ServerHealth.Interop;

/// <summary>
/// Обёртка над PDH (Performance Data Helper).
/// Используются ТОЛЬКО англоязычные пути счётчиков (PdhAddEnglishCounterW),
/// поэтому работает на любой локализации Windows (RU/EN и т.д.).
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new Dictionary<string, IntPtr>(StringComparer.Ordinal);

    /// <summary>Счётчики, которых нет на данной системе (добавлены не были).</summary>
    public List<string> SkippedCounters { get; } = new List<string>();

    public PdhQuery()
    {
        int st = PdhNative.PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (st != 0)
            throw new InvalidOperationException("PDH: не удалось открыть запрос, код 0x" + st.ToString("X"));
    }

    public bool AddEnglish(string englishPath)
    {
        int st = PdhNative.PdhAddEnglishCounterW(_query, englishPath, IntPtr.Zero, out IntPtr h);
        if (st == 0 && h != IntPtr.Zero)
        {
            _counters[englishPath] = h;
            return true;
        }
        if (!SkippedCounters.Contains(englishPath)) SkippedCounters.Add(englishPath);
        return false;
    }

    public void Collect()
    {
        int st = PdhNative.PdhCollectQueryData(_query);
        if (st != 0)
            throw new InvalidOperationException("PDH: ошибка сбора данных, код 0x" + st.ToString("X"));
    }

    public double ReadDouble(string englishPath)
    {
        if (!_counters.TryGetValue(englishPath, out IntPtr h)) return double.NaN;
        int st = PdhNative.PdhGetFormattedCounterValue(h, PdhNative.PDH_FMT_DOUBLE, IntPtr.Zero, out PdhNative.PdhFmtCounterValue v);
        if (st != 0 || v.CStatus != 0) return double.NaN;
        return v.DoubleValue;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            try { PdhNative.PdhCloseQuery(_query); } catch { }
            _query = IntPtr.Zero;
        }
    }
}

internal static class PdhNative
{
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern int PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern int PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    public static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr counterType, out PdhFmtCounterValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern int PdhExpandWildCardPathW(IntPtr dataSource, string wildCardPath, IntPtr expandedPathList, ref uint pcchPathList, uint flags);

    [DllImport("pdh.dll")]
    public static extern int PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Explicit)]
    public struct PdhFmtCounterValue
    {
        [FieldOffset(0)] public int CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    /// <summary>
    /// Разворачивает шаблон вида "\PhysicalDisk(*)\Avg. Disk sec/Read" в список полных путей.
    /// </summary>
    public static List<string> ExpandWildcard(string wildcardPath)
    {
        var result = new List<string>();
        uint size = 2048;
        IntPtr buf = Marshal.AllocHGlobal((int)size * 2);
        try
        {
            int st;
            while (true)
            {
                st = PdhExpandWildCardPathW(IntPtr.Zero, wildcardPath, buf, ref size, 0);
                if (st == PDH_MORE_DATA)
                {
                    buf = Marshal.ReAllocHGlobal(buf, (IntPtr)((long)size * 2));
                    continue;
                }
                break;
            }
            if (st == 0) result = ReadMultiSz(buf);
        }
        catch { }
        finally
        {
            try { Marshal.FreeHGlobal(buf); } catch { }
        }
        return result;
    }

    /// <summary>Имя экземпляра из пути вида "\Object(instance)\Counter".</summary>
    public static string InstanceOf(string fullPath)
    {
        int i1 = fullPath.IndexOf('(');
        int i2 = fullPath.LastIndexOf(')');
        if (i1 >= 0 && i2 > i1) return fullPath.Substring(i1 + 1, i2 - i1 - 1);
        return fullPath;
    }

    private static List<string> ReadMultiSz(IntPtr buffer)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        long offset = 0;
        while (true)
        {
            short ch = Marshal.ReadInt16(buffer, (int)offset);
            if (ch == 0)
            {
                if (sb.Length == 0) break;
                list.Add(sb.ToString());
                sb.Clear();
                offset += 2;
            }
            else
            {
                sb.Append((char)ch);
                offset += 2;
            }
        }
        return list;
    }
}
