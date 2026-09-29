using System.Text;
using System.Text.Json;
using ServerHealth.Interop;

namespace ServerHealth;

/// <summary>
/// Веб-отчёт (report.html): самодостаточная страница — инлайн CSS/JS,
/// графики на canvas, вкладки процессов, аккордеон находок, выгрузка CSV.
/// Не требует интернета и веб-сервера: открывается двойным кликом.
/// </summary>
public static class ReportHtml
{
    private const string Favicon = "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";

    private static string Enc(string? s)
    {
        return System.Net.WebUtility.HtmlEncode(s ?? "");
    }

    private static string F(double v, string fmt = "0.#")
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "н/д";
        return v.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static void Write(string path, SysInfo sys, List<SystemSample> samples, List<ProcAgg> procs,
        List<EventGroup> events, TcpSummary tcp, OpenFilesSummary openFiles, AnalysisResult analysis,
        TimeSpan duration, ReportWriter.Paths files, int refreshSec)
    {
        // ---------------- данные для графиков и таблиц ----------------
        var act2 = procs.Where(p => p.Samples >= 2).ToList();
        var topCpu = act2.Where(p => p.Pid > 4).OrderByDescending(p => p.CpuAvg).Take(15)
            .Select(p => new { n = p.Name, pid = p.Pid, avg = Math.Round(p.CpuAvg, 1), max = Math.Round(p.CpuMax, 1), hung = p.HungCount }).ToList();
        var topMem = act2.OrderByDescending(p => p.PrivMaxMb).Take(15)
            .Select(p => new { n = p.Name, pid = p.Pid, avg = Math.Round(p.PrivAvgMb), max = Math.Round(p.PrivMaxMb), last = Math.Round(p.PrivLastMb), gr = Math.Round(p.GrowthMbPerHour(duration)) }).ToList();
        var topIo = act2.OrderByDescending(p => p.ReadMbpsAvg + p.WriteMbpsAvg).Take(15)
            .Select(p => new { n = p.Name, pid = p.Pid, r = Math.Round(p.ReadMbpsAvg, 1), w = Math.Round(p.WriteMbpsAvg, 1), ro = Math.Round(p.ReadOpsAvg), wo = Math.Round(p.WriteOpsAvg) }).ToList();
        var topConn = act2.Where(p => p.TcpEstMax > 0).OrderByDescending(p => p.TcpEstMax).Take(15)
            .Select(p => new { n = p.Name, pid = p.Pid, est = p.TcpEstMax, tot = p.TcpTotalMax }).ToList();

        var findings = analysis.Findings
            .OrderBy(f => f.Severity == "CRITICAL" ? 0 : f.Severity == "WARNING" ? 1 : 2)
            .Select(f => new { sev = f.Severity, cat = f.Category, title = f.Title, sym = f.Symptom, cause = f.Cause, act = f.Actions, ver = f.Verify }).ToList();

        var charts = new
        {
            t = samples.Select(s => s.Ts.ToString("HH:mm:ss")).ToArray(),
            cpu = samples.Select(s => R(s.CpuTotal)).ToArray(),
            cpuPriv = samples.Select(s => R(s.CpuPriv)).ToArray(),
            ram = samples.Select(s => R(s.AvailMb)).ToArray(),
            ramMax = Math.Round(sys.RamGb * 1024),
            net = samples.Select(s => R(s.Nets.Sum(n => (double.IsNaN(n.RxMbps) ? 0 : n.RxMbps) + (double.IsNaN(n.TxMbps) ? 0 : n.TxMbps)))).ToArray(),
            lat = samples.Select(s => s.Physical.Count == 0 ? 0 : R(s.Physical.Max(d => double.IsNaN(d.LatReadMs) ? 0 : Math.Max(d.LatReadMs, double.IsNaN(d.LatWriteMs) ? 0 : d.LatWriteMs)))).ToArray(),
            q = samples.Select(s => s.Physical.Count == 0 ? 0 : R(s.Physical.Average(d => double.IsNaN(d.QueueCur) ? 0 : d.QueueCur))).ToArray(),
            tcp = samples.Select(s => s.TcpEstablished).ToArray(),
            pql = samples.Select(s => R(s.ProcQueueLen)).ToArray()
        };

        // CSV читаем из уже записанных файлов
        string csvSamples = SafeRead(files.Samples), csvProc = SafeRead(files.Processes), csvEvents = SafeRead(files.Events);

        var payload = new
        {
            meta = new
            {
                host = sys.Machine, os = sys.Os, cpu = sys.CpuName, cores = sys.Cores,
                ram = Math.Round(sys.RamGb, 1), ver = sys.AppVersion, elev = sys.Elevated,
                start = sys.CollectedStart.ToString("dd.MM.yyyy HH:mm:ss"),
                end = sys.CollectedEnd.ToString("dd.MM.yyyy HH:mm:ss"),
                mins = Math.Round(duration.TotalMinutes, 1), n = samples.Count
            },
            scores = analysis.Scores.Select(s => new { k = s.Key, v = s.Value }).ToArray(),
            verdict = new { title = analysis.VerdictTitle, text = analysis.VerdictText, crit = analysis.HasCritical },
            charts, topCpu, topMem, topIo, topConn, findings,
            ev = events.Take(25).Select(e => new { log = e.Log, prov = e.Provider, id = e.Id, lvl = e.LevelName, cnt = e.Count, last = e.Last.ToString("dd.MM HH:mm"), dur = e.DuringCollection, sample = e.Sample }).ToArray()
        };
        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        json = json.Replace("<", "\\u003c");

        string csvJson = JsonSerializer.Serialize(new { s = csvSamples, p = csvProc, e = csvEvents });
        csvJson = csvJson.Replace("<", "\\u003c");

        // ---------------- HTML ----------------
        var sb = new StringBuilder(256 * 1024);
        sb.Append("<!DOCTYPE html>\n<html lang=\"ru\"><head><meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        if (refreshSec > 0) sb.Append("<meta http-equiv=\"refresh\" content=\"").Append(refreshSec).Append("\">\n");
        sb.Append("<link rel=\"icon\" type=\"image/png\" href=\"data:image/png;base64,").Append(Favicon).Append("\">\n");
        sb.Append("<title>ServerHealth — ").Append(Enc(sys.Machine)).Append(" — ").Append(Enc(sys.CollectedStart.ToString("dd.MM HH:mm"))).Append("</title>\n");
        sb.Append("<style>").Append(Css).Append("</style>\n</head><body>\n");

        // шапка
        sb.Append("<header><div class=\"brand\"><img alt=\"\" src=\"data:image/png;base64,").Append(Favicon).Append("\" class=\"logo\">")
          .Append("<div><h1>ServerHealth <span class=\"ver\">v").Append(Enc(sys.AppVersion)).Append("</span></h1>")
          .Append("<div class=\"sub\">").Append(Enc(sys.Machine)).Append(" · ").Append(Enc(sys.CpuName)).Append(" ×").Append(sys.Cores)
          .Append(" · ОЗУ ").Append(F(sys.RamGb, "0.#")).Append(" ГБ · ").Append(Enc(sys.Os)).Append("</div></div></div>")
          .Append("<div class=\"period\">Период: <b>").Append(Enc(sys.CollectedStart.ToString("dd.MM.yyyy HH:mm:ss")))
          .Append(" — ").Append(Enc(sys.CollectedEnd.ToString("HH:mm:ss"))).Append("</b> (")
          .Append(F(duration.TotalMinutes, "0.#")).Append(" мин, замеров: ").Append(samples.Count).Append(")")
          .Append(sys.Elevated ? "" : " · <span class=\"warn\">без прав администратора</span>")
          .Append(refreshSec > 0 ? "<div class=\"autoref\">страница обновляется автоматически</div>" : "")
          .Append("</div></header>\n");

        // оценки + вердикт
        sb.Append("<section class=\"scores\">");
        foreach (var s in analysis.Scores)
        {
            string cls = s.Value >= 70 ? "ok" : s.Value >= 40 ? "mid" : "bad";
            sb.Append("<div class=\"score ").Append(cls).Append("\"><div class=\"sk\">").Append(Enc(s.Key.Split('(')[0].Trim()))
              .Append("</div><div class=\"sv\">").Append(s.Value).Append("<small>/100</small></div>")
              .Append("<div class=\"bar\"><i style=\"width:").Append(s.Value).Append("%\"></i></div>")
              .Append("<div class=\"sl\">").Append(s.Value >= 70 ? "норма" : s.Value >= 40 ? "проблема" : "критично").Append("</div></div>");
        }
        string vcls = analysis.HasCritical ? "bad" : "mid";
        sb.Append("</section>\n<section class=\"verdict ").Append(vcls).Append("\"><div class=\"vt\">")
          .Append(Enc(analysis.VerdictTitle)).Append("</div>");
        foreach (var line in analysis.VerdictText.Split('\n'))
            sb.Append("<div class=\"vl\">").Append(Enc(line.TrimEnd())).Append("</div>");
        sb.Append("</section>\n");

        // графики
        sb.Append("<section class=\"grid\">");
        ChartCard(sb, "c-cpu", "Процессор, % (зелёный штрих — режим ядра)", "#61afef", "#61afef22", "c-cpu2");
        ChartCard(sb, "c-ram", "Свободная память, МБ", "#4cc38a", "#4cc38a22");
        ChartCard(sb, "c-lat", "Макс. задержка диска, мс", "#e5c07b", "#e5c07b22");
        ChartCard(sb, "c-q", "Очередь к дискам (средняя)", "#d19a66", "#d19a6622");
        ChartCard(sb, "c-net", "Сеть: приём+передача, Мбит/с", "#c678dd", "#c678dd22");
        ChartCard(sb, "c-tcp", "TCP-соединений ESTABLISHED", "#56b6c2", "#56b6c222");
        sb.Append("</section>\n");

        // процессы (вкладки)
        sb.Append("<section class=\"card\"><div class=\"tabs\">")
          .Append("<button class=\"tab active\" data-t=\"tp-cpu\">По CPU</button>")
          .Append("<button class=\"tab\" data-t=\"tp-mem\">По памяти</button>")
          .Append("<button class=\"tab\" data-t=\"tp-io\">По вводу-выводу</button>")
          .Append("<button class=\"tab\" data-t=\"tp-net\">По соединениям</button>")
          .Append("<button class=\"tab\" data-t=\"tp-ev\">События Windows</button>")
          .Append("</div>");
        sb.Append("<div id=\"tp-cpu\" class=\"tabv show\"><table class=\"tbl\"><thead><tr><th>Процесс</th><th>PID</th><th>CPU ср., %</th><th>CPU макс., %</th><th>Зависания</th></tr></thead><tbody id=\"b-cpu\"></tbody></table></div>");
        sb.Append("<div id=\"tp-mem\" class=\"tabv\"><table class=\"tbl\"><thead><tr><th>Процесс</th><th>PID</th><th>Частная ср., МБ</th><th>Макс., МБ</th><th>Тек., МБ</th><th>Рост, МБ/ч</th></tr></thead><tbody id=\"b-mem\"></tbody></table></div>");
        sb.Append("<div id=\"tp-io\" class=\"tabv\"><table class=\"tbl\"><thead><tr><th>Процесс</th><th>PID</th><th>Чтение, Мбит/с</th><th>Запись, Мбит/с</th><th>Чтение, оп/с</th><th>Запись, оп/с</th></tr></thead><tbody id=\"b-io\"></tbody></table></div>");
        sb.Append("<div id=\"tp-net\" class=\"tabv\"><table class=\"tbl\"><thead><tr><th>Процесс</th><th>PID</th><th>TCP est. макс</th><th>Всего макс</th></tr></thead><tbody id=\"b-net\"></tbody></table></div>");
        sb.Append("<div id=\"tp-ev\" class=\"tabv\"><table class=\"tbl\"><thead><tr><th>Журнал</th><th>Поставщик</th><th>Код</th><th>Уровень</th><th>Кол-во</th><th>Последнее</th><th>Во время наблюдения</th></tr></thead><tbody id=\"b-ev\"></tbody></table></div>");
        sb.Append("</section>\n");

        // находки
        sb.Append("<section class=\"card\"><h2>Находки и инструкции по устранению</h2><div id=\"findings\"></div></section>\n");

        // подвал
        sb.Append("<footer><div class=\"files\">Файлы отчёта: <code>report.html</code> · <code>report.txt</code> · <code>report.json</code> · <code>samples.csv</code> · <code>processes.csv</code> · <code>events.csv</code></div>")
          .Append("<div class=\"dl\"><button id=\"dl-s\">Скачать samples.csv</button> <button id=\"dl-p\">processes.csv</button> <button id=\"dl-e\">events.csv</button> <button onclick=\"window.print()\">Печать</button></div></footer>\n");

        // данные + скрипты
        sb.Append("<script>\nconst DATA = ").Append(json).Append(";\nconst CSV = ").Append(csvJson).Append(";\n");
        sb.Append(Js).Append("\n</script>\n</body></html>\n");

        System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static double R(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Round(v, 2); }

    private static string SafeRead(string? path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; } catch { return ""; }
    }

    private static void ChartCard(StringBuilder sb, string id, string title, string color, string fill, string? extraCanvas = null)
    {
        sb.Append("<div class=\"card chart\"><h3>").Append(Enc(title)).Append("</h3><canvas id=\"").Append(id).Append("\"></canvas>");
        if (extraCanvas != null)
            sb.Append("<canvas id=\"").Append(extraCanvas).Append("\" class=\"mini\"></canvas>");
        sb.Append("<div class=\"legend\" id=\"lg-").Append(id).Append("\"></div></div>");
    }

    // ------------------------------------------------------------------ CSS
    private const string Css = """
:root { --bg:#14141f; --card:#1e1e2c; --card2:#232335; --tx:#e8e8f2; --mut:#9a9ab0; --line:#2c2c40;
  --ok:#4cc38a; --mid:#e5c07b; --bad:#e06c75; --blue:#61afef; }
* { box-sizing:border-box; }
body { margin:0; background:var(--bg); color:var(--tx); font:14px/1.45 "Segoe UI",system-ui,sans-serif; padding:14px 18px 30px; }
header { display:flex; flex-wrap:wrap; gap:14px; justify-content:space-between; align-items:center; margin-bottom:14px; }
.brand { display:flex; gap:12px; align-items:center; }
.logo { width:44px; height:44px; border-radius:10px; }
h1 { margin:0; font-size:21px; } h1 .ver { color:var(--mut); font-size:13px; font-weight:400; }
.sub { color:var(--mut); font-size:12.5px; margin-top:2px; }
.period { text-align:right; color:var(--mut); font-size:12.5px; }
.period b { color:var(--tx); }
.warn { color:var(--mid); }
.autoref { color:var(--ok); font-size:11.5px; margin-top:3px; }
.scores { display:grid; grid-template-columns:repeat(auto-fit,minmax(150px,1fr)); gap:10px; margin-bottom:12px; }
.score { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:10px 14px; }
.score.ok .sv { color:var(--ok); } .score.mid .sv { color:var(--mid); } .score.bad .sv { color:var(--bad); }
.sk { color:var(--mut); font-size:12px; }
.sv { font-size:30px; font-weight:600; line-height:1.15; } .sv small { font-size:12px; color:var(--mut); }
.bar { height:5px; background:#33334a; border-radius:3px; overflow:hidden; margin:6px 0 3px; }
.bar i { display:block; height:100%; background:currentColor; }
.score.ok .bar i { background:var(--ok);} .score.mid .bar i { background:var(--mid);} .score.bad .bar i { background:var(--bad);}
.sl { color:var(--mut); font-size:11px; }
.verdict { border-radius:10px; padding:12px 16px; margin-bottom:14px; border:1px solid var(--line); background:var(--card); }
.verdict.bad { border-color:var(--bad); box-shadow:0 0 0 1px var(--bad) inset; }
.verdict .vt { font-size:17px; font-weight:600; margin-bottom:6px; }
.verdict.mid .vt { color:var(--mid); } .verdict.bad .vt { color:var(--bad); }
.vl { color:var(--mut); font-size:13px; white-space:pre-wrap; }
.grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(340px,1fr)); gap:12px; margin-bottom:14px; }
.card { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:12px 16px; margin-bottom:0; }
section.card { margin-bottom:14px; }
.card h2 { margin:2px 0 10px; font-size:16px; }
.card h3 { margin:0 0 6px; font-size:13.5px; font-weight:600; color:var(--tx); }
canvas { width:100%; height:150px; display:block; }
canvas.mini { height:44px; margin-top:4px; }
.legend { color:var(--mut); font-size:11.5px; margin-top:4px; min-height:14px; }
.tabs { display:flex; gap:6px; flex-wrap:wrap; margin-bottom:10px; }
.tab { background:var(--card2); color:var(--mut); border:1px solid var(--line); padding:6px 14px; border-radius:8px; cursor:pointer; font-size:13px; }
.tab.active { background:var(--blue); color:#10141f; border-color:var(--blue); font-weight:600; }
.tabv { display:none; overflow-x:auto; }
.tabv.show { display:block; }
.tbl { border-collapse:collapse; width:100%; font-size:13px; }
.tbl th { text-align:left; color:var(--mut); font-weight:600; padding:6px 10px; border-bottom:1px solid var(--line); white-space:nowrap; }
.tbl td { padding:5px 10px; border-bottom:1px solid #23233a; white-space:nowrap; }
.tbl tr:hover td { background:#232335; }
.num { text-align:right; font-variant-numeric:tabular-nums; }
.hung { color:var(--bad); font-weight:600; }
.gr { color:var(--mid); }
.finding { border:1px solid var(--line); border-left-width:4px; border-radius:8px; margin-bottom:10px; background:var(--card2); }
.finding.sev-CRITICAL { border-left-color:var(--bad); }
.finding.sev-WARNING { border-left-color:var(--mid); }
.finding.sev-INFO { border-left-color:var(--blue); }
.fhead { display:flex; gap:10px; align-items:center; padding:10px 14px; cursor:pointer; }
.badge { font-size:10.5px; font-weight:700; padding:2px 8px; border-radius:6px; letter-spacing:.4px; }
.sev-CRITICAL .badge { background:var(--bad); color:#1a0e10; }
.sev-WARNING .badge { background:var(--mid); color:#1c1608; }
.sev-INFO .badge { background:var(--blue); color:#0e1620; }
.cat { color:var(--mut); font-size:11.5px; min-width:52px; }
.ftitle { font-weight:600; }
.fbody { display:none; padding:2px 16px 14px 16px; color:var(--mut); font-size:13px; }
.fbody.show { display:block; }
.fbody .lbl { color:var(--tx); font-weight:600; margin:8px 0 2px; }
.fbody ol { margin:4px 0 4px 18px; padding:0; }
.fbody li { margin:3px 0; }
.fbody code, .files code { background:#12121c; border:1px solid var(--line); padding:1px 6px; border-radius:5px; font-size:12px; color:#9ece6a; word-break:break-all; }
footer { display:flex; flex-wrap:wrap; gap:10px; justify-content:space-between; align-items:center; color:var(--mut); font-size:12.5px; margin-top:14px; }
.dl button { background:var(--card2); color:var(--tx); border:1px solid var(--line); border-radius:8px; padding:7px 12px; cursor:pointer; font-size:12.5px; }
.dl button:hover { border-color:var(--blue); color:var(--blue); }
@media print { body { background:#fff; color:#111; } .card,.score,.verdict,.finding { border-color:#ccc; background:#fff; } .dl,.tabs { display:none; } .tabv{display:block;} .fbody{display:block;} }
""";

    // ------------------------------------------------------------------- JS
    private const string Js = """
function h(s){ const d=document.createElement('div'); d.textContent=s==null?'':String(s); return d.innerHTML; }
function fmt(v){ if(!isFinite(v)) return '—'; if(Math.abs(v)>=100) return v.toFixed(0); if(Math.abs(v)>=10) return v.toFixed(1); return v.toFixed(2); }

// --------------------- графики ---------------------
function chart(id, vals, color, fill, unit, fixedMax, mini){
  const cv = document.getElementById(id); if(!cv || !vals || !vals.length) return;
  const dpr = window.devicePixelRatio||1;
  const W = cv.clientWidth||300, H = cv.clientHeight||150;
  cv.width = W*dpr; cv.height = H*dpr;
  const c = cv.getContext('2d'); c.scale(dpr,dpr); c.clearRect(0,0,W,H);
  const padL=44, padR=10, padT=8, padB=16;
  const w=W-padL-padR, hh=H-padT-padB;
  let max = fixedMax || 1;
  if(!fixedMax){ const mx = Math.max.apply(null, vals.filter(isFinite).concat([1])); max = mx*1.15; }
  c.strokeStyle='#2c2c40'; c.lineWidth=1; c.fillStyle='#8a8aa0'; c.font='10px Segoe UI';
  for(let i=0;i<=4;i++){ const y=padT+hh*i/4;
    c.beginPath(); c.moveTo(padL,y); c.lineTo(W-padR,y); c.stroke();
    c.textAlign='right'; c.fillText(fmt(max*(1-i/4)), padL-5, y+3); }
  c.textAlign='center';
  const n = vals.length;
  const X = i => padL + w*i/Math.max(1,n-1);
  const Y = v => padT + hh*(1-Math.min(1,Math.max(0,(isFinite(v)?v:0)/max)));
  c.beginPath();
  for(let i=0;i<n;i++){ const x=X(i), y=Y(vals[i]); i?c.lineTo(x,y):c.moveTo(x,y); }
  c.strokeStyle=color; c.lineWidth=1.7; c.stroke();
  c.lineTo(X(n-1),padT+hh); c.lineTo(X(0),padT+hh); c.closePath(); c.fillStyle=fill; c.fill();
  // последняя точка
  const lx=X(n-1), ly=Y(vals[n-1]);
  c.beginPath(); c.arc(lx,ly,3,0,7); c.fillStyle=color; c.fill();
  // подписи времени
  if(!mini && DATA.charts.t && DATA.charts.t.length){
    c.fillStyle='#6a6a84'; c.textAlign='left';  c.fillText(DATA.charts.t[0], padL, H-4);
    c.textAlign='center'; c.fillText(DATA.charts.t[Math.floor(n/2)], padL+w/2, H-4);
    c.textAlign='right';  c.fillText(DATA.charts.t[n-1], W-padR, H-4);
  }
  const avg = vals.reduce((a,b)=>a+(isFinite(b)?b:0),0)/n;
  const mx = Math.max.apply(null, vals);
  const lg = document.getElementById('lg-'+id);
  if(lg) lg.innerHTML = 'сейчас: <b style="color:'+color+'">'+fmt(vals[n-1])+'</b> · среднее: '+fmt(avg)+' · максимум: '+fmt(mx)+(unit?' '+unit:'');
  return {avg:avg, max:mx};
}
function drawAll(){
  const ch = DATA.charts;
  chart('c-cpu', ch.cpu, '#61afef', '#61afef22', '%');
  chart('c-cpu2', ch.cpuPriv, '#4cc38a', '#4cc38a18', '%', 100, true);
  const ramEl = chart('c-ram', ch.ram, '#4cc38a', '#4cc38a22', 'МБ', ch.ramMax);
  const lg=document.getElementById('lg-c-ram');
  if(lg && ramEl) lg.innerHTML += ' · всего ОЗУ: '+DATA.meta.ram+' ГБ';
  chart('c-lat', ch.lat, '#e5c07b', '#e5c07b22', 'мс');
  chart('c-q',   ch.q,   '#d19a66', '#d19a6622', '');
  chart('c-net', ch.net, '#c678dd', '#c678dd22', 'Мбит/с');
  chart('c-tcp', ch.tcp, '#56b6c2', '#56b6c222', '');
}
window.addEventListener('resize', function(){ clearTimeout(window._rt); window._rt=setTimeout(drawAll,150); });

// --------------------- вкладки ---------------------
document.querySelectorAll('.tab').forEach(function(b){
  b.addEventListener('click', function(){
    document.querySelectorAll('.tab').forEach(function(x){ x.classList.remove('active'); });
    document.querySelectorAll('.tabv').forEach(function(x){ x.classList.remove('show'); });
    b.classList.add('active');
    const el=document.getElementById(b.dataset.t); if(el) el.classList.add('show');
  });
});

// --------------------- таблицы процессов ---------------------
function row(cells){ return '<tr>'+cells.map(function(c){ return '<td>'+c+'</td>'; }).join('')+'</tr>'; }
function num(v, cls){ return '<td class="num '+(cls||'')+'">'+h(v)+'</td>'; }
if(DATA.topCpu) document.getElementById('b-cpu').innerHTML = DATA.topCpu.map(function(p){
  return row([h(p.n)+ (p.hung>0?' <span class="hung">ЗАВИСАЛ</span>':''), num(p.pid), num(p.avg), num(p.max), num(p.hung>0?('?'+p.hung):'—', p.hung>0?'hung':'')]);
}).join('') || '<tr><td colspan=5>нет данных</td></tr>';
if(DATA.topMem) document.getElementById('b-mem').innerHTML = DATA.topMem.map(function(p){
  return row([h(p.n), num(p.pid), num(p.avg), num(p.max), num(p.last), num((p.gr>0?'+':'')+p.gr, p.gr>100?'gr':'')]);
}).join('') || '<tr><td colspan=6>нет данных</td></tr>';
if(DATA.topIo) document.getElementById('b-io').innerHTML = DATA.topIo.map(function(p){
  return row([h(p.n), num(p.pid), num(p.r), num(p.w), num(p.ro), num(p.wo)]);
}).join('') || '<tr><td colspan=6>нет данных</td></tr>';
if(DATA.topConn) document.getElementById('b-net').innerHTML = DATA.topConn.map(function(p){
  return row([h(p.n), num(p.pid), num(p.est), num(p.tot)]);
}).join('') || '<tr><td colspan=4>нет данных</td></tr>';
if(DATA.ev) document.getElementById('b-ev').innerHTML = DATA.ev.map(function(e){
  return row([h(e.log), h(e.prov), num(e.id), h(e.lvl), num(e.cnt), h(e.last), e.dur?'<span class="hung">ДА</span>':'']);
}).join('') || '<tr><td colspan=7>событий нет</td></tr>';

// --------------------- находки ---------------------
const CMD = /^(powercfg|net |netstop|net start|taskkill|tasklist|del |chkdsk|Get-|Set-|Add-|Restart-|Enable-|appcmd|wmic|logman|typeperf|procdump|xperf|jmap|nltest|gpresult|query |reset |poolmon|lodctr|winmgmt|schtasks|sp_configure|rd |start )/i;
const FSEV = { CRITICAL:0, WARNING:1, INFO:2 };
(DATA.findings||[]).slice().sort(function(a,b){ return (FSEV[a.sev]||3)-(FSEV[b.sev]||3); }).forEach(function(f){
  const d = document.createElement('div'); d.className='finding sev-'+h(f.sev);
  let body = '<div class="lbl">Симптомы</div><div>'+h(f.sym)+'</div>';
  if(f.cause) body += '<div class="lbl">Вероятная причина</div><div>'+h(f.cause)+'</div>';
  if(f.act && f.act.length){ body += '<div class="lbl">Что делать</div><ol>'+f.act.map(function(a){
      const t = h(a); return '<li>'+(CMD.test(a)?'<code>'+t+'</code>':t)+'</li>'; }).join('')+'</ol>'; }
  if(f.ver && f.ver.length){ body += '<div class="lbl">Как проверить, что помогло</div><ol>'+f.ver.map(function(v){ return '<li>'+h(v)+'</li>'; }).join('')+'</ol>'; }
  d.innerHTML = '<div class="fhead"><span class="badge">'+h(f.sev)+'</span><span class="cat">'+h(f.cat)+
    '</span><span class="ftitle">'+h(f.title)+'</span></div><div class="fbody">'+body+'</div>';
  d.querySelector('.fhead').addEventListener('click', function(){ d.querySelector('.fbody').classList.toggle('show'); });
  document.getElementById('findings').appendChild(d);
});
// критические находки раскрыты по умолчанию
document.querySelectorAll('.finding.sev-CRITICAL .fbody').forEach(function(x){ x.classList.add('show'); });

// --------------------- выгрузка CSV ---------------------
function dl(name, text){
  const blob = new Blob(['\ufeff'+text], {type:'text/csv;charset=utf-8'});
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob); a.download = name; a.click();
  setTimeout(function(){ URL.revokeObjectURL(a.href); }, 3000);
}
document.getElementById('dl-s').addEventListener('click', function(){ dl('samples.csv', CSV.s); });
document.getElementById('dl-p').addEventListener('click', function(){ dl('processes.csv', CSV.p); });
document.getElementById('dl-e').addEventListener('click', function(){ dl('events.csv', CSV.e); });

drawAll();
""";
}
