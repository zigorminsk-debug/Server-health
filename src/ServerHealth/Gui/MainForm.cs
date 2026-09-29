using System.Drawing;
using System.Windows.Forms;
using ServerHealth.Interop;

namespace ServerHealth.Gui;

/// <summary>
/// Главное окно мониторинга: живой график, оценки подсистем, находки,
/// управление постоянным мониторингом с автоформированием отчётов.
/// </summary>
public sealed class MainForm : Form
{
    private readonly CliOptions _cli;
    private MonitoringEngine? _engine;
    private readonly System.Windows.Forms.Timer _uiTimer = new System.Windows.Forms.Timer();
    private int _reportsOpenedTotal;

    // элементы
    private NumericUpDown _numInterval = new NumericUpDown();
    private NumericUpDown _numCycle = new NumericUpDown();
    private NumericUpDown _numKeep = new NumericUpDown();
    private CheckBox _chkOpen = new CheckBox();
    private CheckBox _chkTray = new CheckBox();
    private Button _btnStart = new Button();
    private Button _btnStop = new Button();
    private Button _btnReport = new Button();
    private Button _btnFolder = new Button();
    private Label _lblState = new Label();
    private Label _lblCycle = new Label();
    private Label _lblHost = new Label();
    private Label _lblVerdict = new Label();
    private readonly ScoreCard _scCpu = new ScoreCard("CPU — процессор");
    private readonly ScoreCard _scRam = new ScoreCard("RAM — память");
    private readonly ScoreCard _scDisk = new ScoreCard("DISK — диски");
    private readonly ScoreCard _scNet = new ScoreCard("NET — сеть");
    private readonly ChartPanel _chart = new ChartPanel();
    private ListBox _lstFindings = new ListBox();
    private TextBox _txtFinding = new TextBox();
    private DataGridView _grid = new DataGridView();
    private TextBox _txtLog = new TextBox();
    private TabControl _tabs = new TabControl();
    private SplitContainer _splitFindings = new SplitContainer();
    private NotifyIcon? _tray;
    private StatusStrip _status = new StatusStrip();
    private ToolStripStatusLabel _stLeft = new ToolStripStatusLabel("Остановлен");
    private ToolStripStatusLabel _stRight = new ToolStripStatusLabel("");

    public MainForm(CliOptions cli)
    {
        _cli = cli;
        Text = "ServerHealth — мониторинг терминального сервера";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(1020, 680);
        BackColor = Color.FromArgb(20, 20, 31);
        ForeColor = Color.FromArgb(232, 232, 242);
        Font = new Font("Segoe UI", 9F);

        try
        {
            using var big = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (big != null) Icon = new Icon(big, 32, 32);
        }
        catch { }

        BuildUi();

        _uiTimer.Interval = 1000;
        _uiTimer.Tick += UiTick;
        _uiTimer.Start();

        FormClosing += (s, e) =>
        {
            _uiTimer.Stop();
            _engine?.Stop();
            if (_tray != null) _tray.Visible = false;
        };

        Load += (s, e) =>
        {
            try { _splitFindings.SplitterDistance = 380; } catch { }
            if (!MonitoringEngine.BuildSysInfo().Elevated)
                AppendLog("[!] Запуск без прав администратора — часть данных будет недоступна. Закройте и запустите от администратора.");
        };
    }

    // ------------------------------------------------------------------- UI
    private void BuildUi()
    {
        // шапка
        var header = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Color.FromArgb(24, 24, 36) };
        var title = new Label
        {
            Text = "ServerHealth",
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(14, 12)
        };
        _lblHost = new Label
        {
            Text = "",
            ForeColor = Color.FromArgb(150, 150, 175),
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleRight
        };
        header.Controls.Add(title);
        header.Controls.Add(_lblHost);
        header.Resize += (s, e) => _lblHost.Location = new Point(header.Width - _lblHost.Width - 14, 16);

        // статус-бар
        _status.Items.AddRange(new ToolStripItem[] { _stLeft, new ToolStripStatusLabel { Spring = true }, _stRight });
        _status.BackColor = Color.FromArgb(24, 24, 36);
        Controls.Add(_status);

        // левая панель настроек
        var left = new Panel { Dock = DockStyle.Left, Width = 296, BackColor = Color.FromArgb(20, 20, 31), Padding = new Padding(10, 8, 8, 8) };

        var grpParams = new GroupBox
        {
            Text = "Параметры мониторинга",
            Location = new Point(8, 8),
            Size = new Size(280, 178),
            ForeColor = Color.FromArgb(200, 200, 215)
        };
        AddLabeledNum(grpParams, "Интервал замера, с", _numInterval, 5, 1, 3600, 14, 24);
        AddLabeledNum(grpParams, "Период отчёта, мин", _numCycle, (decimal)_cli.WatchCycleMin, 1, 1440, 14, 70);
        AddLabeledNum(grpParams, "Хранить отчётов (0=все)", _numKeep, _cli.KeepReports, 0, 999, 14, 116);

        _chkOpen = new CheckBox
        {
            Text = "Открывать отчёт автоматически",
            Location = new Point(14, 148),
            Size = new Size(258, 20),
            Checked = true,
            ForeColor = grpParams.ForeColor
        };
        grpParams.Controls.Add(_chkOpen);

        var grpRun = new GroupBox
        {
            Text = "Управление",
            Location = new Point(8, 192),
            Size = new Size(280, 158),
            ForeColor = grpParams.ForeColor
        };
        _btnStart = new Button
        {
            Text = "▶  Запустить мониторинг",
            BackColor = Color.FromArgb(46, 140, 96),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Location = new Point(12, 24),
            Size = new Size(256, 40)
        };
        _btnStart.FlatAppearance.BorderSize = 0;
        _btnStart.Click += (s, e) => StartMonitoring();
        _btnStop = new Button
        {
            Text = "⏸  Остановить",
            BackColor = Color.FromArgb(70, 70, 90),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false,
            Location = new Point(12, 70),
            Size = new Size(256, 30)
        };
        _btnStop.FlatAppearance.BorderSize = 0;
        _btnStop.Click += (s, e) => StopMonitoring();
        _btnReport = new Button
        {
            Text = "🌐  Открыть последний отчёт",
            BackColor = Color.FromArgb(52, 52, 76),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false,
            Location = new Point(12, 106),
            Size = new Size(256, 22)
        };
        _btnReport.FlatAppearance.BorderSize = 0;
        _btnReport.Click += (s, e) => OpenLastReport();
        _btnFolder = new Button
        {
            Text = "📂  Папка отчётов",
            BackColor = Color.FromArgb(52, 52, 76),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Location = new Point(12, 130),
            Size = new Size(256, 22)
        };
        _btnFolder.FlatAppearance.BorderSize = 0;
        _btnFolder.Click += (s, e) => OpenFolder();
        grpRun.Controls.AddRange(new Control[] { _btnStart, _btnStop, _btnReport, _btnFolder });

        var grpState = new GroupBox
        {
            Text = "Статус",
            Location = new Point(8, 356),
            Size = new Size(280, 150),
            ForeColor = grpParams.ForeColor
        };
        _lblState = new Label
        {
            Text = "● ОСТАНОВЛЕН",
            ForeColor = Color.FromArgb(150, 150, 175),
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Location = new Point(12, 22),
            Size = new Size(256, 20)
        };
        _lblCycle = new Label
        {
            Text = "Циклов: 0 · отчётов: 0",
            ForeColor = Color.FromArgb(150, 150, 175),
            Location = new Point(12, 44),
            Size = new Size(256, 18)
        };
        _lblVerdict = new Label
        {
            Text = "Вердикт появится после первого отчёта.",
            ForeColor = Color.FromArgb(180, 180, 200),
            Location = new Point(12, 64),
            Size = new Size(256, 76)
        };
        grpState.Controls.AddRange(new Control[] { _lblState, _lblCycle, _lblVerdict });

        var chkTrayHolder = new Panel { Location = new Point(8, 512), Size = new Size(280, 24) };
        _chkTray = new CheckBox
        {
            Text = "Сворачивать в область уведомлений",
            Location = new Point(6, 2),
            Size = new Size(270, 20),
            Checked = true,
            ForeColor = grpParams.ForeColor
        };
        chkTrayHolder.Controls.Add(_chkTray);

        var hint = new Label
        {
            Text = "Каждый цикл → папка с report.html (графики),\nreport.txt, JSON и CSV. Отчёты пишутся автоматически,\nпока мониторинг запущен.",
            ForeColor = Color.FromArgb(120, 120, 145),
            Location = new Point(14, 540),
            Size = new Size(272, 60)
        };

        left.Controls.AddRange(new Control[] { grpParams, grpRun, grpState, chkTrayHolder, hint });

        // правая часть
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 20, 31), Padding = new Padding(8, 6, 8, 4) };

        var scores = new TableLayoutPanel { Dock = DockStyle.Top, Height = 92, ColumnCount = 4, RowCount = 1, BackColor = right.BackColor };
        for (int i = 0; i < 4; i++) scores.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        scores.Controls.Add(_scCpu, 0, 0);
        scores.Controls.Add(_scRam, 1, 0);
        scores.Controls.Add(_scDisk, 2, 0);
        scores.Controls.Add(_scNet, 3, 0);
        foreach (Control c in scores.Controls) { c.Dock = DockStyle.Fill; c.Margin = new Padding(2, 0, 2, 0); }

        _chart.Dock = DockStyle.Top;
        _chart.Height = 240;
        _chart.Padding = new Padding(2);

        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            BackColor = right.BackColor
        };
        var tabFindings = new TabPage("Находки и инструкции") { BackColor = Color.FromArgb(20, 20, 31) };
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
        _splitFindings = split;
        _lstFindings = new ListBox
        {
            Dock = DockStyle.Fill,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 34,
            IntegralHeight = false,
            BackColor = Color.FromArgb(28, 28, 42),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        _lstFindings.DrawItem += LstFindings_DrawItem;
        _lstFindings.SelectedIndexChanged += (s, e) => ShowFinding();
        _txtFinding = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(24, 24, 36),
            ForeColor = Color.FromArgb(220, 220, 235),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9.5F)
        };
        split.Panel1.Controls.Add(_lstFindings);
        split.Panel2.Controls.Add(_txtFinding);
        tabFindings.Controls.Add(split);

        var tabProcs = new TabPage("Процессы (топ CPU за цикл)");
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.FromArgb(24, 24, 36),
            BorderStyle = BorderStyle.FixedSingle,
            EnableHeadersVisualStyles = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false
        };
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(40, 40, 60);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.DefaultCellStyle.BackColor = Color.FromArgb(28, 28, 42);
        _grid.DefaultCellStyle.ForeColor = Color.FromArgb(220, 220, 235);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(50, 60, 90);
        _grid.GridColor = Color.FromArgb(40, 40, 60);
        _grid.Columns.Add("c1", "Процесс");
        _grid.Columns.Add("c2", "PID");
        _grid.Columns.Add("c3", "CPU ср., %");
        _grid.Columns.Add("c4", "CPU макс., %");
        _grid.Columns.Add("c5", "Память тек., МБ");
        _grid.Columns.Add("c6", "Рост, МБ/ч");
        _grid.Columns.Add("c7", "IO, Мбит/с");
        _grid.Columns.Add("c8", "Зависал");
        tabProcs.Controls.Add(_grid);

        var tabLog = new TabPage("Журнал");
        _txtLog = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(24, 24, 36),
            ForeColor = Color.FromArgb(190, 190, 210),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9F)
        };
        tabLog.Controls.Add(_txtLog);

        _tabs.TabPages.AddRange(new[] { tabFindings, tabProcs, tabLog });

        // порядок добавления важен: последний добавленный докится первым
        right.Controls.Add(_tabs);   // Dock.Fill — займёт остаток
        right.Controls.Add(_chart);  // Dock.Top  — под карточками
        right.Controls.Add(scores);  // Dock.Top  — самый верх

        Controls.Add(right);   // Dock.Fill
        Controls.Add(left);    // Dock.Left
        Controls.Add(_status); // Dock.Bottom
        Controls.Add(header);  // Dock.Top

        // трей
        try
        {
            _tray = new NotifyIcon
            {
                Icon = Icon,
                Text = "ServerHealth — мониторинг",
                Visible = true
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть", null, (s, e) => RestoreFromTray());
            menu.Items.Add("Выход", null, (s, e) => Close());
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => RestoreFromTray();
        }
        catch { }

        Resize += (s, e) =>
        {
            if (WindowState == FormWindowState.Minimized && _chkTray.Checked && _tray != null)
            {
                Hide();
                _tray.ShowBalloonTip(1500, "ServerHealth", "Мониторинг продолжается в фоне.", ToolTipIcon.Info);
            }
        };

        UpdateHostLabel();
    }

    private void AddLabeledNum(GroupBox parent, string label, NumericUpDown num, decimal val, decimal min, decimal max, int x, int y)
    {
        var lbl = new Label { Text = label, Location = new Point(x, y), Size = new Size(240, 16), ForeColor = parent.ForeColor };
        num.Location = new Point(x, y + 16);
        num.Size = new Size(120, 24);
        num.Minimum = min;
        num.Maximum = max;
        num.Value = val;
        parent.Controls.Add(lbl);
        parent.Controls.Add(num);
    }

    private void UpdateHostLabel()
    {
        var sys = MonitoringEngine.BuildSysInfo();
        Text = "ServerHealth — " + sys.Machine;
        _lblHost.Text = sys.Machine + " · " + Trunc(sys.CpuName, 40) + " ×" + sys.Cores + " · ОЗУ " + sys.RamGb.ToString("0.#") + " ГБ" +
                        (sys.Elevated ? "" : " · БЕЗ ПРАВ АДМИНИСТРАТОРА");
        _chart.SetRam(sys.RamGb);
    }

    private static string Trunc(string s, int n) { return string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n - 1) + "…"); }

    // ------------------------------------------------------------ управление
    private void StartMonitoring()
    {
        if (_engine != null && _engine.IsRunning) return;
        var opts = new MonitoringOptions
        {
            IntervalSec = (int)_numInterval.Value,
            CycleMinutes = (double)_numCycle.Value,
            EventsHours = _cli.EventsHours > 0 ? _cli.EventsHours : 24,
            OutRoot = string.IsNullOrWhiteSpace(_cli.OutDir) ? "" : _cli.OutDir,
            KeepReports = (int)_numKeep.Value,
            HtmlRefreshSec = 60
        };
        _engine = new MonitoringEngine(opts);
        _engine.Log += m => BeginInvoke(new Action(() => AppendLog(m)));
        _engine.Tick += p => BeginInvoke(new Action(() => OnTick(p)));
        _engine.CycleCompleted += r => BeginInvoke(new Action(() => OnCycle(r)));
        _engine.CriticalDetected += msg =>
        {
            BeginInvoke(new Action(() =>
            {
                if (_tray != null) _tray.ShowBalloonTip(6000, "ServerHealth: КРИТИЧНО", msg, ToolTipIcon.Error);
            }));
        };
        _engine.Start();
        _btnStart.Enabled = false;
        _btnStop.Enabled = true;
        _numInterval.Enabled = false;
        _numCycle.Enabled = false;
        _numKeep.Enabled = false;
        _lblState.Text = "● ИДЁТ МОНИТОРИНГ";
        _lblState.ForeColor = Color.FromArgb(76, 195, 138);
        _stLeft.ForeColor = Color.FromArgb(76, 195, 138);
        AppendLog("Запущено. Период отчёта: " + (double)_numCycle.Value + " мин, интервал: " + (int)_numInterval.Value + " с.");
    }

    private void StopMonitoring()
    {
        if (_engine == null) return;
        _engine.Stop();
        _btnStart.Enabled = true;
        _btnStop.Enabled = false;
        _numInterval.Enabled = true;
        _numCycle.Enabled = true;
        _numKeep.Enabled = true;
        _lblState.Text = "● ОСТАНОВЛЕН";
        _lblState.ForeColor = Color.FromArgb(150, 150, 175);
        _stLeft.ForeColor = ForeColor;
    }

    private void OpenLastReport()
    {
        var last = _engine?.Last;
        if (last?.Files == null || !File.Exists(last.Files.Html))
        {
            MessageBox.Show("Отчёт ещё не сформирован — дождитесь окончания первого цикла.", "ServerHealth",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(last.Files.Html) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("Не удалось открыть: " + ex.Message); }
    }

    private void OpenFolder()
    {
        try
        {
            string dir = _engine?.OutRoot ?? "";
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) Directory.CreateDirectory(dir ?? "ServerHealth");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + dir + "\""));
        }
        catch { }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // ------------------------------------------------------------- события
    private void OnTick(LivePoint p)
    {
        if (_engine == null) return;
        _chart.UpdateData(_engine.LiveSnapshot());
    }

    private void OnCycle(CycleResult r)
    {
        _reportsOpenedTotal = r.CycleNumber;
        if (r.Analysis != null)
        {
            int i = 0;
            foreach (var s in r.Analysis.Scores)
            {
                string cap = s.Value >= 70 ? "норма" : s.Value >= 40 ? "проблема" : "критично";
                var card = i switch { 0 => _scCpu, 1 => _scRam, 2 => _scDisk, _ => _scNet };
                card.Set(s.Value, cap);
                i++;
            }
            _lblVerdict.Text = Trunc(r.VerdictTitle, 180);
            _lblVerdict.ForeColor = r.HasCritical ? Color.FromArgb(224, 108, 117) : Color.FromArgb(180, 180, 200);
        }
        // находки
        _lstFindings.BeginUpdate();
        _lstFindings.Items.Clear();
        if (r.Analysis != null)
        {
            var ordered = r.Analysis.Findings
                .OrderBy(f => f.Severity == "CRITICAL" ? 0 : f.Severity == "WARNING" ? 1 : 2);
            foreach (var f in ordered)
                _lstFindings.Items.Add(new FindingItem(f));
        }
        _lstFindings.EndUpdate();
        // процессы
        _grid.Rows.Clear();
        foreach (var p in r.TopProcs)
            _grid.Rows.Add(p.Name, p.Pid,
                p.CpuAvg.ToString("0.#"), p.CpuMax.ToString("0.#"),
                p.PrivLastMb.ToString("0"), p.GrowthMbPerHour(r.Duration).ToString("+0;-0;0"),
                (p.ReadMbpsAvg + p.WriteMbpsAvg).ToString("0.#"),
                p.HungCount > 0 ? "ДА (" + p.HungCount + ")" : "");
        _btnReport.Enabled = r.Files != null;
        AppendLog("Готов отчёт #" + r.CycleNumber + ": " + r.Dir);
        if (_chkOpen.Checked && r.Files != null && File.Exists(r.Files.Html))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.Files.Html) { UseShellExecute = true }); }
            catch { }
        }
    }

    private void LstFindings_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _lstFindings.Items.Count) return;
        var item = _lstFindings.Items[e.Index] as FindingItem;
        e.DrawBackground();
        bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        using var bg = new SolidBrush(sel ? Color.FromArgb(45, 55, 85) : Color.FromArgb(28, 28, 42));
        e.Graphics.FillRectangle(bg, e.Bounds);
        Color sevColor = item?.Severity == "CRITICAL" ? Color.FromArgb(224, 108, 117)
            : item?.Severity == "WARNING" ? Color.FromArgb(229, 192, 123) : Color.FromArgb(97, 175, 239);
        using var sevBrush = new SolidBrush(sevColor);
        e.Graphics.FillRectangle(sevBrush, e.Bounds.X + 4, e.Bounds.Y + 4, 4, e.Bounds.Height - 8);
        TextRenderer.DrawText(e.Graphics, "[" + (item?.Severity ?? "") + "] " + (item?.Category ?? ""),
            new Font(Font, FontStyle.Bold), new Rectangle(e.Bounds.X + 14, e.Bounds.Y + 2, e.Bounds.Width - 20, 14),
            sevColor, TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, item?.Title ?? "", Font,
            new Rectangle(e.Bounds.X + 14, e.Bounds.Y + 16, e.Bounds.Width - 20, 16),
            Color.FromArgb(220, 220, 235), TextFormatFlags.EndEllipsis);
    }

    private void ShowFinding()
    {
        if (_lstFindings.SelectedItem is not FindingItem item || item.Finding == null) return;
        var f = item.Finding;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(f.Title);
        sb.AppendLine(new string('─', 80));
        sb.AppendLine("Симптомы: " + f.Symptom);
        if (!string.IsNullOrEmpty(f.Cause)) sb.AppendLine().AppendLine("Вероятная причина: " + f.Cause);
        if (f.Actions.Count > 0)
        {
            sb.AppendLine().AppendLine("Что делать:");
            int i = 1;
            foreach (var a in f.Actions) sb.AppendLine("  " + i++ + ") " + a);
        }
        if (f.Verify.Count > 0)
        {
            sb.AppendLine().AppendLine("Как проверить, что помогло:");
            foreach (var v in f.Verify) sb.AppendLine("  - " + v);
        }
        _txtFinding.Text = sb.ToString();
        _txtFinding.SelectionStart = 0;
    }

    private void UiTick(object? sender, EventArgs e)
    {
        if (_engine == null || !_engine.IsRunning)
        {
            _stRight.Text = "";
            return;
        }
        var eng = _engine;
        TimeSpan left = eng.NextReportAt - DateTime.Now;
        string leftStr = left.TotalSeconds > 0
            ? (int)left.TotalMinutes + ":" + left.Seconds.ToString("00")
            : "формирование…";
        _lblCycle.Text = "Цикл #" + eng.CycleNumber + " · замер " + eng.TickCount + "/" + eng.PlannedTicks +
                         " · до отчёта: " + leftStr;
        _stLeft.Text = "Мониторинг идёт";
        _stRight.Text = "отчётов: " + eng.ReportsDone + " · каталог: " + Trunc(eng.OutRoot, 60);
        _chart.UpdateData(eng.LiveSnapshot());
    }

    private void AppendLog(string m)
    {
        _txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + m + Environment.NewLine);
    }

    private sealed class FindingItem
    {
        public readonly Finding? Finding;
        public FindingItem(Finding f) { Finding = f; }
        public string Severity { get { return Finding?.Severity ?? ""; } }
        public string Category { get { return Finding?.Category ?? ""; } }
        public string? Title { get { return Finding?.Title; } }
        public override string ToString() { return "[" + Severity + "] " + Title; }
    }
}
