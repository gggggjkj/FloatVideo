namespace FloatVideo;

internal sealed class HotkeyForm : Form
{
    private static readonly (string Key, string Label)[] Actions =
    {
        ("toggle", "播放／暫停"), ("back", "倒轉 10 秒"), ("forward", "快轉 10 秒"),
        ("volumeDown", "降低音量"), ("volumeUp", "提高音量")
    };
    private readonly Dictionary<string, HotkeyBinding> _pending;
    private readonly Dictionary<string, TextBox> _boxes = new();
    private readonly Label _hint = new();
    private string? _listeningAction;

    public Dictionary<string, HotkeyBinding> Bindings => _pending;

    public HotkeyForm(Dictionary<string, HotkeyBinding> current)
    {
        _pending = current.ToDictionary(x => x.Key, x => new HotkeyBinding(x.Value.Key, x.Value.Ctrl, x.Value.Alt, x.Value.Shift));
        Text = "全域快捷鍵";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = true;
        ClientSize = new Size(380, 275);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(29, 31, 37);
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Microsoft JhengHei UI", 9F);
        KeyPreview = true;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7, Padding = new Padding(14, 12, 14, 10) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        Controls.Add(grid);

        for (var i = 0; i < Actions.Length; i++)
        {
            var (key, label) = Actions[i];
            grid.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, i);
            var box = new TextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true, ShortcutsEnabled = false,
                BackColor = Color.FromArgb(45, 48, 57), ForeColor = Color.White,
                Cursor = Cursors.Hand, Text = _pending[key].ToString(), Tag = key
            };
            box.Enter += (_, _) => StartListening(key);
            box.Click += (_, _) => StartListening(key);
            box.Leave += (_, _) => { if (_listeningAction == key) StopListening(); };
            _boxes[key] = box;
            grid.Controls.Add(box, 1, i);
        }
        _hint.Text = "點選欄位後按單一鍵或組合鍵。\nDelete 清除；Esc 取消錄製。";
        _hint.Dock = DockStyle.Fill;
        _hint.ForeColor = Color.Silver;
        grid.SetColumnSpan(_hint, 2);
        grid.Controls.Add(_hint, 0, 5);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var save = new Button { Text = "儲存", Width = 70 };
        save.Click += Save;
        var cancel = new Button { Text = "取消", Width = 70, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel);
        grid.SetColumnSpan(buttons, 2);
        grid.Controls.Add(buttons, 0, 6);
        CancelButton = cancel;
        Shown += (_, _) => { BringToFront(); Activate(); };
    }

    private void StartListening(string action)
    {
        StopListening();
        _listeningAction = action;
        _boxes[action].BackColor = Color.FromArgb(52, 75, 102);
        _boxes[action].Text = "請按新按鍵…";
        _hint.Text = "正在監聽：直接按單一鍵，或按住\nCtrl／Alt／Shift 再按另一個鍵。";
    }

    private void StopListening()
    {
        if (_listeningAction is not { } action) return;
        _boxes[action].BackColor = Color.FromArgb(45, 48, 57);
        _boxes[action].Text = _pending[action].ToString();
        _listeningAction = null;
        _hint.Text = "點選欄位後按單一鍵或組合鍵。\nDelete 清除；Esc 取消錄製。";
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_listeningAction != null && CaptureKey(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (_listeningAction != null && CaptureKey(keyData)) return true;
        return base.ProcessDialogKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_listeningAction != null && CaptureKey(e.KeyData))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private bool CaptureKey(Keys keyData)
    {
        if (_listeningAction is not { } action) return false;
        var key = keyData & Keys.KeyCode;
        var modifiers = keyData & Keys.Modifiers;
        if (key == Keys.Escape && modifiers == Keys.None) { StopListening(); return true; }
        if (key is Keys.Delete or Keys.Back && modifiers == Keys.None)
        {
            _pending[action] = new HotkeyBinding();
            StopListening();
            return true;
        }
        if (key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or
            Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey)
        {
            _boxes[action].Text = $"{(modifiers.HasFlag(Keys.Control) ? "Ctrl + " : "")}{(modifiers.HasFlag(Keys.Alt) ? "Alt + " : "")}{(modifiers.HasFlag(Keys.Shift) ? "Shift + " : "")}…";
            return true;
        }
        if (key is Keys.LWin or Keys.RWin)
        {
            _hint.Text = "Windows 鍵由系統保留，請選其他按鍵。";
            return true;
        }
        _pending[action] = new HotkeyBinding(key,
            modifiers.HasFlag(Keys.Control), modifiers.HasFlag(Keys.Alt), modifiers.HasFlag(Keys.Shift));
        StopListening();
        _hint.Text = modifiers == Keys.None
            ? "已記錄單一鍵；它會占用其他程式的該鍵。\n按「儲存」套用。"
            : "已記錄新組合鍵，按「儲存」即可套用。";
        return true;
    }

    private void Save(object? sender, EventArgs e)
    {
        StopListening();
        var used = new HashSet<string>();
        foreach (var binding in _pending.Values)
        {
            if (binding.Key == Keys.None) continue;
            if (!binding.IsValid || !used.Add(binding.ToString()))
            {
                MessageBox.Show(this, "快捷鍵不能重複，也不能只設定 Ctrl、Alt、Shift 或 Windows 鍵。", "無法儲存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
