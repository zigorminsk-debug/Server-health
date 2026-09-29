using System.Drawing;
using System.Windows.Forms;
using ServerHealth.Interop;

namespace ServerHealth.Gui;

/// <summary>
/// Главное окно мониторинга. Вёрстка полностью адаптивная (TableLayoutPanel /
/// FlowLayoutPanel / Dock, AutoSize — ни одной фиксированной координаты):
/// корректно переживает масштабирование 100–200% DPI и любой размер окна.
/// Вкладки собственные (кнопки-переключатели) — без системного TabControl,
/// чьи заголовки «наезжают» при PerMonitorV2.
/// </summary>
public sealed class MainForm : Form
{
    private readonly CliOptions _cli;
    private MonitoringEngine? _engine;
    private readonly System.Windows.Forms.Timer _uiTimer = new System.Windows.Forms.Timer();

    // элементы управления
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
    private SplitContainer _splitFindings = new SplitContainer();
    private NotifyIcon? _tray;
    private readonly ToolTip _tip = new ToolTip();
    private StatusStrip _status = new StatusStrip();
    private ToolStripStatusLabel _stLeft = new ToolStripStatusLabel("Остановлен");
    private ToolStripStatusLabel _stRight = new ToolStripStatusLabel("");

    // собственные вкладки
    private readonly List<Button> _tabButtons = new List<Button>();
    private readonly List<Panel> _tabPages = new List<Panel>();

    private static readonly Color Bg = Color.FromArgb(20, 20, 31);
    private static readonly Color Section = Color.FromArgb(24, 24, 36);
    private static readonly Color Card = Color.FromArgb(30, 30, 44);
    private static readonly Color Card2 = Color.FromArgb(38, 38, 58);
    private static readonly Color Fg = Color.FromArgb(232, 232, 242);
    private static readonly Color Muted = Color.FromArgb(165, 165, 190);

    public MainForm(CliOptions cli)
    {
        _cli = cli;
        Text = "ServerHealth — мониторинг терминального сервера";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1200, 780);
        MinimumSize = new Size(1000, 660);
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;

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
            try { _splitFindings.SplitterDistance = (int)(_splitFindings.Width * 0.42); } catch { }
            if (!MonitoringEngine.BuildSysInfo().Elevated)
                AppendLog("[!] Запуск без прав администратора — часть данных будет недоступна. Закройте и запустите от администратора.");
        };

        Resize += (s, e) =>
        {
            if (WindowState == FormWindowState.Minimized && _chkTray.Checked && _tray != null)
            {
                Hide();
                _tray.ShowBalloonTip(1500, "ServerHealth", "Мониторинг продолжается в фоне.", ToolTipIcon.Info);
            }
        };
    }

    // ============================================================ вёрстка
    private void BuildUi()
    {
        SuspendLayout();

        // ---- статус-бар: добавляем в Controls первым (Dock=Bottom)
        _status.Items.AddRange(new ToolStripItem[] { _stLeft, new ToolStripStatusLabel { Spring = true }, _stRight });
        _status.BackColor = Color.FromArgb(24, 24, 36);
        _status.SizingGrip = true;
        foreach (ToolStripStatusLabel it in _status.Items) it.ForeColor = Muted;

        // ---- корневая сетка: [левая колонка 300px | правая 100%]
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Bg
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 308));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // ---------------- ЛЕВАЯ КОЛОНКА ----------------
        var leftHost = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(10, 10, 6, 6), AutoScroll = true };
        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            BackColor = Bg,
            Margin = new Padding(0)
        };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        leftHost.Controls.Add(left);
        Action syncLeftWidth = () => { left.Width = Math.Max(120, leftHost.ClientSize.Width - leftHost.Padding.Left - leftHost.Padding.Right); };
        leftHost.Resize += (s, e) => syncLeftWidth();

        // --- секция «Параметры мониторинга»
        _numInterval.Minimum = 1; _numInterval.Maximum = 3600; _numInterval.Value = 5;
        _numCycle.Minimum = 1; _numCycle.Maximum = 1440; _numCycle.Value = (decimal)Math.Max(1, _cli.WatchCycleMin);
        _numKeep.Minimum = 0; _numKeep.Maximum = 999; _numKeep.Value = _cli.KeepReports;

        _chkOpen = new CheckBox
        {
            Text = "Открывать отчёт автоматически",
            Checked = true,
            AutoSize = false,
            Height = 22,
            Dock = DockStyle.Top,
            ForeColor = Muted,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var secParams = MakeSection("Параметры мониторинга",
            SettingRow("Интервал замера, секунд", _numInterval),
            SettingRow("Период отчёта, минут", _numCycle),
            SettingRow("Хранить отчётов (0 = все)", _numKeep),
            _chkOpen);

        // --- секция «Управление»
        _btnStart = FlatBtn("▶  Запустить мониторинг", Color.FromArgb(46, 140, 96), 40, bold: true);
        _btnStart.Click += (s, e) => StartMonitoring();
        _btnStop = FlatBtn("⏸  Остановить", Color.FromArgb(88, 88, 112), 34);
        _btnStop.Enabled = false;
        _btnStop.Click += (s, e) => StopMonitoring();
        _btnReport = FlatBtn("🌐  Открыть последний отчёт", Color.FromArgb(52, 52, 76), 30);
        _btnReport.Enabled = false;
        _btnReport.Click += (s, e) => OpenLastReport();
        _btnFolder = FlatBtn("📂  Папка отчётов", Color.FromArgb(52, 52, 76), 30);
        _btnFolder.Click += (s, e) => OpenFolder();
        var secRun = MakeSection("Управление", _btnStart, _btnStop, _btnReport, _btnFolder);

        // --- секция «Статус»
        _lblState = new Label
        {
            Text = "●  ОСТАНОВЛЕН",
            ForeColor = Muted,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(2, 2, 2, 4)
        };
        _lblCycle = new Label { Text = "Циклов: 0 · отчётов: 0", ForeColor = Muted, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(2, 2, 2, 6) };
        _lblVerdict = new Label
        {
            Text = "Вердикт появится после первого отчёта.",
            ForeColor = Color.FromArgb(185, 185, 205),
            AutoSize = true,
            MaximumSize = new Size(252, 0),
            Margin = new Padding(2, 2, 2, 2)
        };
        var secState = MakeSection("Статус", _lblState, _lblCycle, WrapRow(_lblVerdict));

        _chkTray = new CheckBox
        {
            Text = "Сворачивать в область уведомлений",
            Checked = true,
            AutoSize = true,
            ForeColor = Muted,
            Margin = new Padding(2, 8, 2, 2)
        };

        var hint = new Label
        {
            Text = "Каждый цикл — новая папка с report.html (графики), report.txt, JSON и CSV. Отчёты пишутся автоматически, пока мониторинг запущен.",
            ForeColor = Color.FromArgb(120, 120, 148),
            AutoSize = true,
            MaximumSize = new Size(258, 0),
            Margin = new Padding(4, 10, 2, 4)
        };

        // строки левой колонки
        Control[] leftItems = { secParams, secRun, secState, _chkTray, hint };
        for (int r = 0; r < leftItems.Length; r++)
        {
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Controls.Add(leftItems[r], 0, r);
        }
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var filler = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Margin = new Padding(0) };
        left.Controls.Add(filler, 0, leftItems.Length);

        // ---------------- ПРАВАЯ КОЛОНКА ----------------
        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Bg,
            Padding = new Padding(6, 10, 10, 6),
            Margin = new Padding(0)
        };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));  // карточки оценок
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 42));    // живой график
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 58));    // вкладки

        // карточки оценок
        var scores = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Bg,
            Margin = new Padding(0, 0, 0, 6)
        };
        for (int i = 0; i < 4; i++) scores.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        scores.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        scores.Controls.Add(_scCpu, 0, 0);
        scores.Controls.Add(_scRam, 1, 0);
        scores.Controls.Add(_scDisk, 2, 0);
        scores.Controls.Add(_scNet, 3, 0);
        foreach (Control c in scores.Controls) { c.Dock = DockStyle.Fill; c.Margin = new Padding(2, 0, 2, 0); }

        // живой график
        _chart.Dock = DockStyle.Fill;
        _chart.Margin = new Padding(0, 0, 0, 6);

        // собственные вкладки: полоса кнопок + контейнер страниц
        var tabHost = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Margin = new Padding(0) };
        var tabBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            WrapContents = false,
            BackColor = Bg,
            Padding = new Padding(0, 2, 0, 4)
        };
        var pages = new Panel { Dock = DockStyle.Fill, BackColor = Card, Padding = new Padding(6) };
        tabHost.Controls.Add(pages);   // добавляем первым → Dock=Fill займёт остаток
        tabHost.Controls.Add(tabBar);  // добавляем вторым → Dock=Top сверху

        // страницы вкладок
        AddTab(tabBar, pages, "Находки и инструкции", BuildFindingsPage());
        AddTab(tabBar, pages, "Процессы", BuildProcsPage());
        AddTab(tabBar, pages, "Журнал", BuildLogPage());
        ShowTab(0);

        right.Controls.Add(tabHost, 0, 2);
        right.Controls.Add(_chart, 0, 1);
        right.Controls.Add(scores, 0, 0);

        root.Controls.Add(leftHost, 0, 0);
        root.Controls.Add(right, 1, 0);

        Controls.Add(root);    // Dock=Fill
        Controls.Add(_status); // Dock=Bottom

        // ---- шапка: добавляем последней → Dock=Top, докится первой (верх)
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 54,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.FromArgb(24, 24, 36),
            Padding = new Padding(12, 4, 12, 4),
            Margin = new Padding(0)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var title = new Label
        {
            Text = "ServerHealth",
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };
        _lblHost = new Label
        {
            Text = "",
            ForeColor = Muted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(0)
        };
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(_lblHost, 1, 0);
        Controls.Add(header);

        ResumeLayout(true);
        syncLeftWidth();

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

        UpdateHostLabel();
    }

    // ------------------------------------------------- содержимое вкладок
    private Control BuildFindingsPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Card, Margin = new Padding(0) };
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, BackColor = Card };
        _splitFindings = split;
        _lstFindings = new ListBox
        {
            Dock = DockStyle.Fill,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 36,
            IntegralHeight = false,
            BackColor = Color.FromArgb(28, 28, 42),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.None
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
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 9.5F)
        };
        split.Panel1.Padding = new Padding(0, 0, 4, 0);
        split.Panel1.Controls.Add(_lstFindings);
        split.Panel2.Padding = new Padding(4, 0, 0, 0);
        split.Panel2.Controls.Add(_txtFinding);
        page.Controls.Add(split);
        return page;
    }

    private Control BuildProcsPage()
    {
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
            BorderStyle = BorderStyle.None,
            EnableHeadersVisualStyles = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            GridColor = Color.FromArgb(40, 40, 60),
            Margin = new Padding(0)
        };
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(40, 40, 60);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.DefaultCellStyle.BackColor = Color.FromArgb(28, 28, 42);
        _grid.DefaultCellStyle.ForeColor = Color.FromArgb(220, 220, 235);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(50, 60, 90);
        _grid.Columns.Add("c1", "Процесс");
        _grid.Columns.Add("c2", "PID");
        _grid.Columns.Add("c3", "CPU ср., %");
        _grid.Columns.Add("c4", "CPU макс., %");
        _grid.Columns.Add("c5", "Память тек., МБ");
        _grid.Columns.Add("c6", "Рост, МБ/ч");
        _grid.Columns.Add("c7", "IO, Мбит/с");
        _grid.Columns.Add("c8", "Зависал");
        _grid.Columns[0].FillWeight = 26;
        _grid.Columns[1].FillWeight = 8;
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Card, Padding = new Padding(2), Margin = new Padding(0) };
        page.Controls.Add(_grid);
        return page;
    }

    private Control BuildLogPage()
    {
        _txtLog = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(24, 24, 36),
            ForeColor = Color.FromArgb(190, 190, 210),
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 9F),
            Margin = new Padding(0)
        };
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Card, Padding = new Padding(2), Margin = new Padding(0) };
        page.Controls.Add(_txtLog);
        return page;
    }

    // -------------------------------------------------- свои вкладки
    private void AddTab(FlowLayoutPanel bar, Panel host, string title, Control content)
    {
        int index = _tabPages.Count;
        var btn = new Button
        {
            Text = title,
            AutoSize = true,
            MinimumSize = new Size(0, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Card2,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 9F),
            Margin = new Padding(0, 0, 4, 0),
            Padding = new Padding(12, 2, 12, 2),
            Tag = index,
            UseVisualStyleBackColor = false
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(50, 50, 76);
        btn.Click += (s, e) => ShowTab(index);
        bar.Controls.Add(btn);
        _tabButtons.Add(btn);

        content.Dock = DockStyle.Fill;
        content.Visible = false;
        host.Controls.Add(content);
        _tabPages.Add((Panel)content);
    }

    private void ShowTab(int index)
    {
        if (index < 0 || index >= _tabPages.Count) return;
        for (int i = 0; i < _tabPages.Count; i++)
        {
            bool active = i == index;
            _tabPages[i].Visible = active;
            _tabButtons[i].BackColor = active ? Color.FromArgb(58, 58, 92) : Card2;
            _tabButtons[i].ForeColor = active ? Color.White : Muted;
            _tabButtons[i].Font = new Font("Segoe UI", 9F, active ? FontStyle.Bold : FontStyle.Regular);
        }
    }

    // -------------------------------------------------- фабрики контролов
    /// <summary>
    /// Секция левой колонки: подзаголовок + вертикальный список контролов.
    /// Внутри — TableLayoutPanel (не FlowLayoutPanel: тот при Anchor Left|Right
    /// схлопывает кнопки в нулевую ширину). Высота — по содержимому.
    /// </summary>
    private static Panel MakeSection(string title, params Control[] items)
    {
        var section = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Section,
            Padding = new Padding(10, 24, 10, 10),
            Margin = new Padding(0, 0, 0, 8)
        };
        var caption = new Label
        {
            Text = title,
            AutoSize = true,
            ForeColor = Color.FromArgb(120, 120, 150),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            BackColor = Section,
            Location = new Point(10, 5),
            Margin = new Padding(0)
        };
        var rows = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Section,
            Margin = new Padding(0)
        };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int r = 0; r < items.Length; r++)
        {
            rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            items[r].Margin = new Padding(0, 1, 0, 6);
            rows.Controls.Add(items[r], 0, r);
        }
        section.Controls.Add(rows);
        section.Controls.Add(caption);
        return section;
    }

    /// <summary>Строка-обёртка для переносящегося текста (Label с MaximumSize).</summary>
    private static Control WrapRow(Label l)
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            BackColor = Section,
            Margin = new Padding(0, 0, 0, 4)
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(l, 0, 0);
        return t;
    }

    /// <summary>Строка «подпись — числовое поле».</summary>
    private static Control SettingRow(string label, NumericUpDown num)
    {
        num.Dock = DockStyle.Fill;
        num.Margin = new Padding(0, 1, 0, 1);

        var tlp = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Section,
            Margin = new Padding(0, 1, 0, 1),
            Tag = label
        };
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var lbl = new Label
        {
            Text = label,
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Muted,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 1, 8, 1)
        };
        tlp.Controls.Add(lbl, 0, 0);
        tlp.Controls.Add(num, 1, 0);
        return tlp;
    }

    private static Button FlatBtn(string text, Color back, int height, bool bold = false)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", bold ? 10F : 9F, bold ? FontStyle.Bold : FontStyle.Regular),
            Dock = DockStyle.Top,
            MinimumSize = new Size(0, height),
            Margin = new Padding(0, 1, 0, 6),
            UseVisualStyleBackColor = false
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.15f);
        return b;
    }

    // ------------------------------------------------------------- прочее
    private void UpdateHostLabel()
    {
        var sys = MonitoringEngine.BuildSysInfo();
        Text = "ServerHealth — " + sys.Machine;
        _lblHost.Text = sys.Machine + " · " + sys.Cores + " лог. ядер · ОЗУ " + sys.RamGb.ToString("0.#") + " ГБ" +
                        (sys.Elevated ? "" : " · БЕЗ ПРАВ АДМИНИСТРАТОРА");
        try { _tip.SetToolTip(_lblHost, sys.CpuName + " ×" + sys.Cores); } catch { }
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
        _lblState.Text = "●  ИДЁТ МОНИТОРИНГ";
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
        _lblState.Text = "●  ОСТАНОВЛЕН";
        _lblState.ForeColor = Muted;
        _stLeft.ForeColor = Muted;
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
            _lblVerdict.Text = Trunc(r.VerdictTitle, 400);
            _lblVerdict.ForeColor = r.HasCritical ? Color.FromArgb(224, 108, 117) : Color.FromArgb(185, 185, 205);
        }
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
            new Font(Font, FontStyle.Bold), new Rectangle(e.Bounds.X + 14, e.Bounds.Y + 2, e.Bounds.Width - 20, 15),
            sevColor, TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, item?.Title ?? "", Font,
            new Rectangle(e.Bounds.X + 14, e.Bounds.Y + 18, e.Bounds.Width - 20, 16),
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
