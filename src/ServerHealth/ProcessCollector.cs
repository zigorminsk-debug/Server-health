using System.Diagnostics;
using ServerHealth.Interop;

namespace ServerHealth;

/// <summary>
/// Сборщик данных по процессам: CPU, память, файловый/сетевой ввод-вывод,
/// дескрипторы, зависшие окна. Не использует локализованные счётчики
/// производительности, поэтому одинаково работает на RU/EN Windows.
/// </summary>
internal sealed class ProcessCollector
{
    private readonly Stopwatch _sw = new Stopwatch();
    private double _lastSec = -1;
    private Dictionary<int, TimeSpan> _prevCpu = new Dictionary<int, TimeSpan>();
    private Dictionary<int, IoCounters> _prevIo = new Dictionary<int, IoCounters>();

    /// <summary>Собрать срез по всем процессам. dt между вызовами должен быть &gt; 0.</summary>
    public List<ProcessTick> Collect(HashSet<int> hungPids, out double elapsedSec)
    {
        if (!_sw.IsRunning) _sw.Start();
        double now = _sw.Elapsed.TotalSeconds;
        double dt = _lastSec < 0 ? 0 : now - _lastSec;
        elapsedSec = dt;
        if (dt < 0.001) dt = 0.001;
        _lastSec = now;

        int cores = Math.Max(1, Environment.ProcessorCount);
        var list = new List<ProcessTick>(256);
        var newCpu = new Dictionary<int, TimeSpan>(512);
        var newIo = new Dictionary<int, IoCounters>(512);

        Process[] procs = Process.GetProcesses();
        foreach (var p in procs)
        {
            var t = new ProcessTick();
            try { t.Pid = p.Id; } catch { continue; }
            try { t.Name = p.ProcessName; } catch { t.Name = "?"; }

            // CPU
            try
            {
                TimeSpan cpu = p.TotalProcessorTime;
                newCpu[t.Pid] = cpu;
                TimeSpan prev;
                if (_prevCpu.TryGetValue(t.Pid, out prev))
                    t.CpuPct = (cpu - prev).TotalSeconds / dt / cores * 100.0;
            }
            catch { }

            // Память
            try { t.Ws = p.WorkingSet64; } catch { }
            try { t.Private = p.PrivateMemorySize64; } catch { }
            try { t.Handles = p.HandleCount; } catch { }

            // Ввод-вывод (файлы + сеть)
            try
            {
                IoCounters? io = Native.GetIoCounters(t.Pid);
                if (io.HasValue)
                {
                    newIo[t.Pid] = io.Value;
                    IoCounters prev;
                    if (_prevIo.TryGetValue(t.Pid, out prev))
                    {
                        t.ReadMbps = Math.Max(0, (double)(io.Value.ReadTransferCount - prev.ReadTransferCount)) / dt * 8 / 1000000.0;
                        t.WriteMbps = Math.Max(0, (double)(io.Value.WriteTransferCount - prev.WriteTransferCount)) / dt * 8 / 1000000.0;
                        t.ReadOps = Math.Max(0, (double)(io.Value.ReadOperationCount - prev.ReadOperationCount)) / dt;
                        t.WriteOps = Math.Max(0, (double)(io.Value.WriteOperationCount - prev.WriteOperationCount)) / dt;
                    }
                }
            }
            catch { }

            if (hungPids != null && hungPids.Contains(t.Pid)) t.Hung = true;

            list.Add(t);
        }

        _prevCpu = newCpu;
        _prevIo = newIo;
        return list;
    }

    /// <summary>PID процессов с зависшими окнами (не обрабатывают сообщения).</summary>
    public HashSet<int> GetHungPids()
    {
        return Native.GetHungWindowPids();
    }
}

/// <summary>Накопитель агрегатов по процессам.</summary>
public sealed class ProcAccumulator
{
    private readonly Dictionary<int, ProcAgg> _byPid = new Dictionary<int, ProcAgg>();

    public void Add(IEnumerable<ProcessTick> ticks, double seconds)
    {
        foreach (var t in ticks)
        {
            if (!_byPid.TryGetValue(t.Pid, out var a))
            {
                a = new ProcAgg { Pid = t.Pid, Name = t.Name, PrivFirst = t.Private };
                _byPid[t.Pid] = a;
            }
            if (a.PrivFirst == 0) a.PrivFirst = t.Private;
            a.Name = t.Name;
            a.Samples++;
            a.CpuSum += t.CpuPct;
            if (t.CpuPct > a.CpuMax) a.CpuMax = t.CpuPct;
            a.CpuSeconds += t.CpuPct / 100.0 * seconds; // доля от всех ядер
            a.PrivSum += t.Private;
            if (t.Private > a.PrivMax) a.PrivMax = t.Private;
            a.PrivLast = t.Private;
            a.WsSum += t.Ws;
            if (t.Ws > a.WsMax) a.WsMax = t.Ws;
            if (t.Handles > a.HandlesMax) a.HandlesMax = t.Handles;
            a.ReadMbpsSum += t.ReadMbps;
            a.WriteMbpsSum += t.WriteMbps;
            if (t.ReadMbps > a.ReadMbpsMax) a.ReadMbpsMax = t.ReadMbps;
            if (t.WriteMbps > a.WriteMbpsMax) a.WriteMbpsMax = t.WriteMbps;
            a.ReadOpsSum += t.ReadOps;
            a.WriteOpsSum += t.WriteOps;
            if (t.TcpEst > a.TcpEstMax) a.TcpEstMax = t.TcpEst;
            if (t.TcpTotal > a.TcpTotalMax) a.TcpTotalMax = t.TcpTotal;
            if (t.Hung) a.HungCount++;
        }
    }

    public void MarkLeaders(IEnumerable<ProcessTick> ticks)
    {
        ProcessTick topCpu = null, topMem = null, topIo = null;
        foreach (var t in ticks)
        {
            if (topCpu == null || t.CpuPct > topCpu.CpuPct) topCpu = t;
            if (topMem == null || t.Private > topMem.Private) topMem = t;
            if (topIo == null || t.ReadMbps + t.WriteMbps > topIo.ReadMbps + topIo.WriteMbps) topIo = t;
        }
        if (topCpu != null && _byPid.TryGetValue(topCpu.Pid, out var a1)) a1.TopCpuTimes++;
        if (topMem != null && _byPid.TryGetValue(topMem.Pid, out var a2)) a2.TopMemTimes++;
        if (topIo != null && _byPid.TryGetValue(topIo.Pid, out var a3)) a3.TopIoTimes++;
    }

    public void MergeTcp(ICollection<TcpConnPid> perPid)
    {
        foreach (var c in perPid)
        {
            if (!_byPid.TryGetValue(c.Pid, out var a))
            {
                a = new ProcAgg { Pid = c.Pid, Name = c.Name };
                _byPid[c.Pid] = a;
            }
            if (c.Est > a.TcpEstMax) a.TcpEstMax = c.Est;
            if (c.Total > a.TcpTotalMax) a.TcpTotalMax = c.Total;
        }
    }

    public List<ProcAgg> Snapshot()
    {
        return _byPid.Values.OrderByDescending(x => x.CpuAvg).ToList();
    }
}

public sealed class TcpConnPid
{
    public int Pid;
    public string Name = "";
    public int Est, Total;
}
