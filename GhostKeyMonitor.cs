// Ghost Key Monitor - detecta e gerencia "toque fantasma" do teclado no Windows 11.
// 100% nativo: C# / WinForms compilado com o csc.exe que ja vem no Windows.
// Roda em segundo plano (icone na bandeja). NAO grava o que voce digita: so as
// teclas classificadas como fantasma vao para o log.
//
// Argumentos:
//   --background        inicia sem abrir a janela (so o icone na bandeja)
//   --accept-injected   (teste) tambem analisa teclas injetadas por software
//   --demo              (teste) mostra um alerta de exemplo apos 1,5 s
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GhostKey
{
    static class NativeMethods
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        public const uint LLKHF_LOWER_IL_INJECTED = 0x02, LLKHF_INJECTED = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode, scanCode, flags, time;
            public UIntPtr dwExtraInfo;
        }

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        /// <summary>Envia um KEY_UP por software (libera uma tecla que o Windows acha que esta presa).</summary>
        public static void SendKeyUp(int vk)
        {
            INPUT[] inputs = new INPUT[1];
            inputs[0].type = 1; // INPUT_KEYBOARD
            inputs[0].u.ki.wVk = (ushort)vk;
            inputs[0].u.ki.dwFlags = 0x0002; // KEYEVENTF_KEYUP
            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }
    }

    // ------------------------------------------------------------------ log
    static class AppLog
    {
        const string FileName = "ghost_key_log.txt";
        static readonly object sync = new object();
        public static string Path;

        public static void Init()
        {
            string[] candidates = new string[]
            {
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GhostKeyMonitor", FileName)
            };
            foreach (string p in candidates)
            {
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
                    using (new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
                    Path = p;
                    return;
                }
                catch (Exception) { }
            }
            Path = candidates[1];
        }

        static void Write(string level, string fmt, object[] args)
        {
            string msg = args.Length == 0 ? fmt : string.Format(fmt, args);
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss,fff") + " - " + level + " - " + msg + Environment.NewLine;
            lock (sync)
            {
                try
                {
                    FileInfo fi = new FileInfo(Path);
                    if (fi.Exists && fi.Length > 1000000)
                    {
                        string old = Path + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(Path, old);
                    }
                    File.AppendAllText(Path, line, Encoding.UTF8);
                }
                catch (Exception) { }
            }
        }

        public static void Info(string fmt, params object[] a) { Write("INFO", fmt, a); }
        public static void Warn(string fmt, params object[] a) { Write("WARNING", fmt, a); }
        public static void Error(string fmt, params object[] a) { Write("ERROR", fmt, a); }
    }

    // ------------------------------------------------------- iniciar com o Windows
    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "GhostKeyMonitor";

        public static bool IsEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(ValueName) != null;
        }

        public static void Set(bool on)
        {
            Set(on, Application.ExecutablePath);
        }

        public static void Set(bool on, string exePath)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null) return;
                if (on) k.SetValue(ValueName, "\"" + exePath + "\" --background");
                else k.DeleteValue(ValueName, false);
            }
        }
    }

    // ------------------------------------------------------------ configuracoes
    // Guardadas em settings.ini, na mesma pasta do log (dentro da pasta de instalacao).
    static class Settings
    {
        public static bool Notify = true; // balao de notificacao ao detectar

        static string FilePath
        {
            get { return Path.Combine(Path.GetDirectoryName(AppLog.Path), "settings.ini"); }
        }

        public static void CopyThresholds(GhostKeyDetector from, GhostKeyDetector to)
        {
            to.GhostThreshold = from.GhostThreshold;
            to.GhostDwell = from.GhostDwell;
            to.RapidRepeatThreshold = from.RapidRepeatThreshold;
            to.TimeWindow = from.TimeWindow;
            to.StuckThreshold = from.StuckThreshold;
            to.StuckThresholdModifier = from.StuckThresholdModifier;
        }

        static double Num(Dictionary<string, string> kv, string key, double fallback, double min, double max)
        {
            string s;
            double v;
            if (kv.TryGetValue(key, out s) && double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v))
                return Math.Max(min, Math.Min(max, v));
            return fallback;
        }

        public static void Load(GhostKeyDetector d)
        {
            Dictionary<string, string> kv = new Dictionary<string, string>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) kv[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                    }
            }
            catch (Exception) { }
            d.GhostThreshold = Num(kv, "GhostThreshold", d.GhostThreshold, 1, 500);
            d.GhostDwell = Num(kv, "GhostDwell", d.GhostDwell, 0, 200);
            d.RapidRepeatThreshold = (int)Num(kv, "Recurrence", d.RapidRepeatThreshold, 2, 20);
            d.TimeWindow = Num(kv, "TimeWindow", d.TimeWindow, 100, 5000);
            d.StuckThreshold = Num(kv, "StuckThreshold", d.StuckThreshold, 1000, 120000);
            d.StuckThresholdModifier = Num(kv, "StuckThresholdModifier", d.StuckThresholdModifier, 5000, 600000);
            Notify = Num(kv, "Notify", 1, 0, 1) >= 1;

            // Carregar teclas desativadas permanentemente
            if (kv.ContainsKey("DisabledKeys"))
            {
                string[] keys = kv["DisabledKeys"].Split(',');
                foreach (string key in keys)
                {
                    string trimmed = key.Trim();
                    string hexStr = trimmed.StartsWith("0x") ? trimmed.Substring(2) : trimmed;
                    int vk;
                    if (int.TryParse(hexStr, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out vk))
                    {
                        d.DisableKeyTemporarily(vk);
                    }
                }
            }
        }

        public static void Save(GhostKeyDetector d)
        {
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;

            // Salvar teclas desativadas em formato hexadecimal
            string disabledKeys = "";
            int[] disabled = d.DisabledKeys();
            if (disabled.Length > 0)
            {
                disabledKeys = string.Join(",", System.Array.ConvertAll(disabled, vk => "0x" + vk.ToString("X2")));
            }

            string[] lines = new string[]
            {
                "GhostThreshold=" + d.GhostThreshold.ToString(inv),
                "GhostDwell=" + d.GhostDwell.ToString(inv),
                "Recurrence=" + d.RapidRepeatThreshold.ToString(inv),
                "TimeWindow=" + d.TimeWindow.ToString(inv),
                "StuckThreshold=" + d.StuckThreshold.ToString(inv),
                "StuckThresholdModifier=" + d.StuckThresholdModifier.ToString(inv),
                "Notify=" + (Notify ? "1" : "0"),
                "DisabledKeys=" + disabledKeys
            };
            try { File.WriteAllLines(FilePath, lines, Encoding.UTF8); }
            catch (Exception ex) { AppLog.Error("Falha ao salvar configurações: {0}", ex.Message); }
        }
    }

    // ------------------------------------------------------ janela de configuracao
    class SettingsForm : Form
    {
        public bool AutostartChosen;
        readonly GhostKeyDetector det;
        readonly NumericUpDown nGap, nDwell, nBurst, nWindow, nStuck, nStuckMod;
        readonly CheckBox autostartBox, notifyBox;
        readonly ListBox disabledList;
        readonly List<int> disabledVKs = new List<int>();

        public SettingsForm(GhostKeyDetector detector, bool autostartOn)
        {
            det = detector;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Configurações - " + Installer.AppName;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(16);

            TableLayoutPanel t = new TableLayoutPanel();
            t.ColumnCount = 3;
            t.AutoSize = true;
            t.AutoSizeMode = AutoSizeMode.GrowAndShrink;

            AddHeader(t, "Sensibilidade");
            nGap = AddRow(t, "Soltar e apertar a mesma tecla de novo em menos de", 1, 500, "ms");
            nDwell = AddRow(t, "Toque (apertar e soltar) mais curto que", 0, 200, "ms");
            nBurst = AddRow(t, "Recorrência mínima: só acusa depois de tantas repetições da mesma tecla " +
                "(toques rápidos em 2 s, ou pressionamentos na rajada)", 2, 20, "vezes");
            nWindow = AddRow(t, "Rajada: janela de contagem dos pressionamentos", 100, 5000, "ms");
            nStuck = AddRow(t, "Tecla presa: pressionada sem soltar por mais de", 1, 120, "s");
            nStuckMod = AddRow(t, "Shift/Ctrl/Alt/Win presos: mais de", 5, 600, "s");

            AddHeader(t, "Geral");
            notifyBox = AddCheck(t, "Mostrar notificação do Windows ao detectar", Settings.Notify);
            autostartBox = AddCheck(t, "Iniciar com o Windows (em segundo plano)", autostartOn);

            AddHeader(t, "Teclas desativadas");
            disabledList = new ListBox();
            disabledList.Height = 80;
            disabledList.Width = 400;
            RefreshDisabledList();
            t.Controls.Add(disabledList);
            t.SetColumnSpan(disabledList, 3);
            FlowLayoutPanel disabledButtons = new FlowLayoutPanel();
            disabledButtons.AutoSize = true;
            disabledButtons.WrapContents = false;
            Button reactivate = new Button(); reactivate.Text = "Reativar selecionada"; reactivate.AutoSize = true;
            reactivate.Click += delegate
            {
                int idx = disabledList.SelectedIndex;
                if (idx >= 0 && idx < disabledVKs.Count)
                {
                    det.EnableKey(disabledVKs[idx]);
                    RefreshDisabledList();
                }
            };
            Button removeAll = new Button(); removeAll.Text = "Limpar todas"; removeAll.AutoSize = true;
            removeAll.Click += delegate
            {
                if (MessageBox.Show("Reativar todas as teclas desativadas?", "Confirmar",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    foreach (int vk in det.DisabledKeys())
                        det.EnableKey(vk);
                    RefreshDisabledList();
                }
            };
            disabledButtons.Controls.Add(reactivate);
            disabledButtons.Controls.Add(removeAll);
            t.Controls.Add(disabledButtons);
            t.SetColumnSpan(disabledButtons, 3);

            LoadValues(det);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.WrapContents = false;
            buttons.Anchor = AnchorStyles.Right;
            buttons.Margin = new Padding(3, 12, 3, 3);
            Button save = new Button(); save.Text = "Salvar"; save.AutoSize = true;
            save.Click += delegate { Save(); };
            Button defaults = new Button(); defaults.Text = "Restaurar padrões"; defaults.AutoSize = true;
            defaults.Click += delegate { LoadValues(new GhostKeyDetector()); };
            Button cancel = new Button(); cancel.Text = "Cancelar"; cancel.AutoSize = true;
            cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(save);
            buttons.Controls.Add(defaults);
            buttons.Controls.Add(cancel);
            t.Controls.Add(buttons);
            t.SetColumnSpan(buttons, 3);

            AcceptButton = save;
            CancelButton = cancel;
            Controls.Add(t);
            ResumeLayout(false);
            PerformLayout();
        }

        static void AddHeader(TableLayoutPanel t, string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            l.AutoSize = true;
            l.Margin = new Padding(3, 10, 3, 4);
            t.Controls.Add(l);
            t.SetColumnSpan(l, 3);
        }

        static NumericUpDown AddRow(TableLayoutPanel t, string label, int min, int max, string unit)
        {
            Label l = new Label();
            l.Text = label;
            l.AutoSize = true;
            l.MaximumSize = new Size(340, 0);
            l.Anchor = AnchorStyles.Left;
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Width = 70;
            n.Anchor = AnchorStyles.Left;
            Label u = new Label();
            u.Text = unit;
            u.AutoSize = true;
            u.Anchor = AnchorStyles.Left;
            t.Controls.Add(l);
            t.Controls.Add(n);
            t.Controls.Add(u);
            return n;
        }

        static CheckBox AddCheck(TableLayoutPanel t, string text, bool on)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Checked = on;
            c.AutoSize = true;
            t.Controls.Add(c);
            t.SetColumnSpan(c, 3);
            return c;
        }

        static decimal Clamp(NumericUpDown n, double v)
        {
            return Math.Max(n.Minimum, Math.Min(n.Maximum, (decimal)v));
        }

        void LoadValues(GhostKeyDetector src)
        {
            nGap.Value = Clamp(nGap, src.GhostThreshold);
            nDwell.Value = Clamp(nDwell, src.GhostDwell);
            nBurst.Value = Clamp(nBurst, src.RapidRepeatThreshold);
            nWindow.Value = Clamp(nWindow, src.TimeWindow);
            nStuck.Value = Clamp(nStuck, src.StuckThreshold / 1000.0);
            nStuckMod.Value = Clamp(nStuckMod, src.StuckThresholdModifier / 1000.0);
        }

        void RefreshDisabledList()
        {
            disabledList.Items.Clear();
            disabledVKs.Clear();
            int[] disabled = det.DisabledKeys();
            if (disabled.Length == 0)
            {
                disabledList.Items.Add("(nenhuma)");
            }
            else
            {
                foreach (int vk in disabled)
                {
                    string name = KeyNames.Get(vk);
                    disabledList.Items.Add(name + " (0x" + vk.ToString("X2") + ")");
                    disabledVKs.Add(vk);
                }
            }
        }

        void Save()
        {
            det.GhostThreshold = (double)nGap.Value;
            det.GhostDwell = (double)nDwell.Value;
            det.RapidRepeatThreshold = (int)nBurst.Value;
            det.TimeWindow = (double)nWindow.Value;
            det.StuckThreshold = (double)nStuck.Value * 1000.0;
            det.StuckThresholdModifier = (double)nStuckMod.Value * 1000.0;
            Settings.Notify = notifyBox.Checked;
            Settings.Save(det);
            AppLog.Info("Configurações salvas");
            AutostartChosen = autostartBox.Checked;
            DialogResult = DialogResult.OK;
        }
    }

    // ----------------------------------------------------------- janela do alerta
    class GhostAlertForm : Form
    {
        public event Action FixClicked;
        public event Action DisableClicked;
        public event Action Dismissed;

        readonly string keyName;
        readonly FlowLayoutPanel body;
        Label counter;
        bool closedByApp;

        // Nao rouba o foco de quem esta digitando.
        protected override bool ShowWithoutActivation { get { return true; } }

        public GhostAlertForm(GhostInfo g)
        {
            keyName = g.KeyName;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Toque Fantasma Detectado";
            TopMost = true;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(24, 16, 24, 16);

            Color sev = g.Severity == "CRÍTICO" ? Color.FromArgb(198, 40, 40)
                      : g.Severity == "ALTO" ? Color.FromArgb(239, 108, 0) : Color.FromArgb(181, 137, 0);

            TableLayoutPanel root = new TableLayoutPanel();
            root.ColumnCount = 1;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.Controls.Add(Line("⚠ Toque Fantasma Detectado!", new Font("Segoe UI", 15f, FontStyle.Bold), sev));
            root.Controls.Add(Line("Tecla: '" + g.KeyName + "'", new Font("Segoe UI", 13f, FontStyle.Bold), SystemColors.ControlText));
            root.Controls.Add(Line("Padrão: " + g.Pattern + "  (" + g.Detail + ")", new Font("Segoe UI", 10f), SystemColors.ControlText));
            root.Controls.Add(Line("Severidade: " + g.Severity, new Font("Segoe UI", 10f, FontStyle.Bold), sev));
            root.Controls.Add(Line("Hora: " + g.Time.ToString("HH:mm:ss"), new Font("Segoe UI", 10f), SystemColors.ControlText));

            body = new FlowLayoutPanel();
            body.FlowDirection = FlowDirection.TopDown;
            body.WrapContents = false;
            body.AutoSize = true;
            body.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            body.Anchor = AnchorStyles.None;
            body.Margin = new Padding(0, 10, 0, 0);
            AddChoiceButtons();
            root.Controls.Add(body);

            Controls.Add(root);
            ResumeLayout(false);
            PerformLayout();

            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (!closedByApp && Dismissed != null) Dismissed();
            };
        }

        static Label Line(string text, Font font, Color color)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = font;
            l.ForeColor = color;
            l.AutoSize = true;
            l.TextAlign = ContentAlignment.MiddleCenter;
            l.Anchor = AnchorStyles.None;
            l.Margin = new Padding(0, 3, 0, 3);
            return l;
        }

        Button ChoiceButton(string text, Action onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(10, 4, 10, 4);
            b.MinimumSize = new Size(300, 0);
            b.Margin = new Padding(0, 3, 0, 3);
            b.TabStop = false; // uma tecla fantasma (Enter/Espaço) nao pode "clicar" no botao
            b.Click += delegate { onClick(); };
            return b;
        }

        void AddChoiceButtons()
        {
            body.Controls.Add(ChoiceButton("Pressionar '" + keyName + "' para tentar resolver",
                delegate { if (FixClicked != null) FixClicked(); }));
            body.Controls.Add(ChoiceButton("Desativar '" + keyName + "' (até reiniciar)",
                delegate { if (DisableClicked != null) DisableClicked(); }));
        }

        public void ShowFixMode()
        {
            body.Controls.Clear();
            body.Controls.Add(Line("Pressione '" + keyName + "' várias vezes\npara limpar o contato.",
                new Font("Segoe UI", 11f), SystemColors.ControlText));
            counter = Line("", new Font("Segoe UI", 12f, FontStyle.Bold), SystemColors.ControlText);
            body.Controls.Add(counter);
        }

        public void SetCounter(string text)
        {
            if (counter != null) counter.Text = text;
        }

        public void CloseByApp()
        {
            closedByApp = true;
            Close();
        }
    }

    // --------------------------------------------------------------- aplicativo
    class MonitorApp : ApplicationContext
    {
        const int FixSeconds = 10;
        const long AlertCooldownMs = 3000;

        readonly GhostKeyDetector detector = new GhostKeyDetector();
        readonly bool acceptInjected;
        readonly Form mainForm;
        readonly NotifyIcon tray;
        readonly Label statusLabel, countLabel;
        readonly ListBox disabledList;
        readonly CheckBox autostartBox;
        readonly ToolStripMenuItem autostartItem;
        readonly System.Windows.Forms.Timer fixTimer = new System.Windows.Forms.Timer();

        NativeMethods.HookProc hookProc; // campo: impede o GC de coletar o delegate do hook
        IntPtr hook = IntPtr.Zero;
        GhostAlertForm alert;
        GhostInfo currentGhost;
        int ghostCount;
        long quietUntil;
        long fixEnd;
        volatile int fixKey = -1;
        int fixCount;
        bool exiting;
        bool syncingAutostart;

        readonly System.Windows.Forms.Timer stuckTimer = new System.Windows.Forms.Timer();

        static long NowMs() { return Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency; }
        static double NowMsD() { return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency; }

        public MonitorApp(bool background, bool acceptInjected, bool demo, double stuckMs, EventWaitHandle showEvent)
        {
            this.acceptInjected = acceptInjected;
            Settings.Load(detector);
            if (stuckMs > 0)
            {
                detector.StuckThreshold = stuckMs;
                detector.StuckThresholdModifier = stuckMs * 4;
            }

            // ---- janela principal ----
            mainForm = new Form();
            mainForm.SuspendLayout();
            mainForm.AutoScaleDimensions = new SizeF(96F, 96F);
            mainForm.AutoScaleMode = AutoScaleMode.Dpi;
            mainForm.Text = "Ghost Key Monitor";
            mainForm.ClientSize = new Size(480, 420);
            mainForm.MinimumSize = new Size(460, 400);
            mainForm.StartPosition = FormStartPosition.CenterScreen;
            mainForm.Icon = MakeIcon();

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.Padding = new Padding(12);
            for (int i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label title = new Label();
            title.Text = "Ghost Key Monitor";
            title.Font = new Font("Segoe UI", 15f, FontStyle.Bold);
            title.AutoSize = true;
            title.Anchor = AnchorStyles.None;
            root.Controls.Add(title);

            statusLabel = new Label();
            statusLabel.Text = "● Monitorando...";
            statusLabel.ForeColor = Color.FromArgb(46, 125, 50);
            statusLabel.Font = new Font("Segoe UI", 11f);
            statusLabel.AutoSize = true;
            statusLabel.Anchor = AnchorStyles.None;
            root.Controls.Add(statusLabel);

            countLabel = new Label();
            countLabel.Text = "Toques fantasma detectados: 0";
            countLabel.AutoSize = true;
            countLabel.Anchor = AnchorStyles.None;
            countLabel.Margin = new Padding(3, 4, 3, 10);
            root.Controls.Add(countLabel);

            Label header = new Label();
            header.Text = "Teclas desativadas (até reiniciar):";
            header.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            header.AutoSize = true;
            header.Anchor = AnchorStyles.Left;
            root.Controls.Add(header);

            disabledList = new ListBox();
            disabledList.Dock = DockStyle.Fill;
            disabledList.IntegralHeight = false;
            root.Controls.Add(disabledList);

            autostartBox = new CheckBox();
            autostartBox.Text = "Iniciar com o Windows (em segundo plano)";
            autostartBox.AutoSize = true;
            autostartBox.Anchor = AnchorStyles.Left;
            autostartBox.Margin = new Padding(3, 8, 3, 4);
            autostartBox.Checked = SafeIsAutostart();
            autostartBox.CheckedChanged += delegate { ToggleAutostart(autostartBox.Checked); };
            root.Controls.Add(autostartBox);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.WrapContents = true;
            buttons.Dock = DockStyle.Fill;
            buttons.Controls.Add(MakeButton("Configurações...", ShowSettings));
            buttons.Controls.Add(MakeButton("Reativar selecionada", ReenableSelected));
            buttons.Controls.Add(MakeButton("Ver Log", ShowLog));
            buttons.Controls.Add(MakeButton("Segundo plano", HideMain));
            if (Installer.IsInstalled())
                buttons.Controls.Add(MakeButton("Desinstalar...", Uninstall));
            buttons.Controls.Add(MakeButton("Sair", Quit));
            root.Controls.Add(buttons);

            mainForm.Controls.Add(root);
            mainForm.ResumeLayout(false);
            mainForm.FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideMain(); }
            };
            IntPtr forceHandle = mainForm.Handle; // cria o handle (necessario para BeginInvoke) sem exibir

            // Se o usuario abrir o programa de novo (Menu Iniciar/atalho), a outra copia sinaliza
            // este evento e a janela desta instancia aparece.
            Thread showWatcher = new Thread(delegate()
            {
                while (showEvent.WaitOne())
                {
                    if (exiting) break;
                    try { mainForm.BeginInvoke(new Action(ShowMain)); } catch (Exception) { break; }
                }
            });
            showWatcher.IsBackground = true;
            showWatcher.Start();

            // ---- icone da bandeja ----
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Abrir", null, delegate { ShowMain(); });
            menu.Items.Add("Configurações...", null, delegate { ShowSettings(); });
            menu.Items.Add("Ver log", null, delegate { ShowLog(); });
            autostartItem = new ToolStripMenuItem("Iniciar com o Windows");
            autostartItem.Checked = autostartBox.Checked;
            autostartItem.Click += delegate { autostartBox.Checked = !autostartBox.Checked; };
            menu.Items.Add(autostartItem);
            if (Installer.IsInstalled())
                menu.Items.Add("Desinstalar...", null, delegate { Uninstall(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Sair", null, delegate { Quit(); });

            tray = new NotifyIcon();
            tray.Icon = MakeIcon();
            tray.Text = "Ghost Key Monitor - monitorando";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowMain(); };
            tray.Visible = true;

            fixTimer.Interval = 100;
            fixTimer.Tick += FixTick;

            // Procura teclas "presas" (pressionadas ha muito tempo sem KEY_UP) 1x por segundo.
            stuckTimer.Interval = 1000;
            stuckTimer.Tick += StuckTick;
            stuckTimer.Start();
            // Ao bloquear/desbloquear a sessao o Windows nao entrega os KEY_UP: esquece o estado.
            SystemEvents.SessionSwitch += OnSessionSwitch;

            InstallHook();
            RefreshDisabledList();
            if (!background) ShowMain();

            if (demo)
            {
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 1500;
                t.Tick += delegate
                {
                    t.Stop();
                    OnGhost(new GhostInfo
                    {
                        Key = 0x41, KeyName = "A", Pattern = "RAPID_REPEAT", Severity = "ALTO",
                        Detail = "UP->DOWN em 23.5ms (demo)", IntervalMs = 23.5, Time = DateTime.Now
                    });
                };
                t.Start();
            }
        }

        // ---- helpers de UI ----
        static Button MakeButton(string text, Action onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Click += delegate { onClick(); };
            return b;
        }

        static Icon MakeIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.FillRectangle(new SolidBrush(Color.FromArgb(40, 44, 52)), 1, 8, 30, 20);
                    SolidBrush keyBrush = new SolidBrush(Color.FromArgb(190, 195, 205));
                    for (int row = 0; row < 3; row++)
                        for (int col = 0; col < 5; col++)
                        {
                            if (row == 2 && col > 3) continue;
                            g.FillRectangle(keyBrush, 4 + col * 5, 11 + row * 5, 3, 3);
                        }
                    g.FillEllipse(new SolidBrush(Color.FromArgb(60, 200, 110)), 20, 1, 11, 11);
                }
                IntPtr h = bmp.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(h).Clone();
                NativeMethods.DestroyIcon(h);
                return icon;
            }
        }

        void ShowMain()
        {
            mainForm.Show();
            if (mainForm.WindowState == FormWindowState.Minimized) mainForm.WindowState = FormWindowState.Normal;
            mainForm.Activate();
        }

        bool hintShown;

        void HideMain()
        {
            mainForm.Hide();
            if (!hintShown && tray != null)
            {
                hintShown = true;
                tray.ShowBalloonTip(4000, Installer.AppName,
                    "Continua em segundo plano. Para abrir, clique no ícone na bandeja " +
                    "(se não aparecer, clique na seta ^ dos ícones ocultos).", ToolTipIcon.Info);
            }
        }

        void ShowSettings()
        {
            using (SettingsForm f = new SettingsForm(detector, autostartBox.Checked))
            {
                if (f.ShowDialog() == DialogResult.OK && f.AutostartChosen != autostartBox.Checked)
                    autostartBox.Checked = f.AutostartChosen; // dispara ToggleAutostart
            }
        }

        void Uninstall()
        {
            try { Process.Start(Installer.InstalledExe, "--uninstall"); } catch (Exception) { }
        }

        void RefreshDisabledList()
        {
            disabledList.Items.Clear();
            foreach (int vk in detector.DisabledKeys())
                disabledList.Items.Add(new KeyItem(vk));
        }

        class KeyItem
        {
            public readonly int Vk;
            public KeyItem(int vk) { Vk = vk; }
            public override string ToString() { return "'" + KeyNames.Get(Vk) + "'  (VK 0x" + Vk.ToString("X2") + ")"; }
        }

        void ReenableSelected()
        {
            KeyItem item = disabledList.SelectedItem as KeyItem;
            if (item == null)
            {
                MessageBox.Show("Selecione uma tecla na lista.", "Ghost Key Monitor");
                return;
            }
            detector.EnableKey(item.Vk);
            AppLog.Info("Usuário re-ativou tecla: '{0}'", KeyNames.Get(item.Vk));
            RefreshDisabledList();
        }

        static bool SafeIsAutostart()
        {
            try { return Autostart.IsEnabled(); } catch (Exception) { return false; }
        }

        void ToggleAutostart(bool on)
        {
            if (syncingAutostart) return;
            syncingAutostart = true;
            try
            {
                Autostart.Set(on);
                AppLog.Info("Iniciar com o Windows: {0}", on ? "ativado" : "desativado");
            }
            catch (Exception ex)
            {
                AppLog.Error("Falha ao configurar inicialização automática: {0}", ex.Message);
                autostartBox.Checked = SafeIsAutostart();
            }
            autostartItem.Checked = autostartBox.Checked;
            syncingAutostart = false;
        }

        // ---- hook de teclado ----
        void InstallHook()
        {
            hookProc = HookCallback;
            using (Process p = Process.GetCurrentProcess())
            using (ProcessModule m = p.MainModule)
                hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, hookProc,
                    NativeMethods.GetModuleHandle(m.ModuleName), 0);
            if (hook == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                AppLog.Error("Falha ao instalar o hook de teclado (erro {0})", err);
                statusLabel.Text = "● Falha ao instalar o hook de teclado";
                statusLabel.ForeColor = Color.FromArgb(198, 40, 40);
                return;
            }
            AppLog.Info("Monitoramento iniciado");
        }

        // Roda no hook de baixo nivel: precisa ser rapido (o Windows remove hooks lentos).
        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                bool isDown = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
                bool isUp = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;
                if (isDown || isUp)
                {
                    NativeMethods.KBDLLHOOKSTRUCT kb = (NativeMethods.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(
                        lParam, typeof(NativeMethods.KBDLLHOOKSTRUCT));
                    bool injected = (kb.flags & (NativeMethods.LLKHF_INJECTED | NativeMethods.LLKHF_LOWER_IL_INJECTED)) != 0;
                    if (!injected || acceptInjected)
                    {
                        int vk = (int)kb.vkCode;
                        if (detector.IsKeyDisabled(vk)) return (IntPtr)1; // bloqueia a tecla desativada
                        try
                        {
                            GhostInfo g = detector.Analyze(vk, isDown);
                            if (isDown && detector.LastWasNewPress && vk == fixKey) Interlocked.Increment(ref fixCount);
                            if (g != null)
                                mainForm.BeginInvoke(new Action(delegate { OnGhost(g); }));
                        }
                        catch (Exception) { }
                    }
                }
            }
            return NativeMethods.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        void StuckTick(object sender, EventArgs e)
        {
            if (exiting || alert != null || NowMs() < quietUntil) return;
            GhostInfo g = detector.CheckStuck(NowMsD(),
                delegate(int vk) { return (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0; });
            if (g != null) OnGhost(g);
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            detector.ClearPressState();
        }

        // ---- ghost detectado ----
        void OnGhost(GhostInfo g)
        {
            if (exiting || alert != null || NowMs() < quietUntil) return;
            ghostCount++;
            countLabel.Text = "Toques fantasma detectados: " + ghostCount;
            currentGhost = g;
            AppLog.Warn("Ghost detectado - {0}: {1} ({2})", g.Pattern, g.KeyName, g.Detail);

            alert = new GhostAlertForm(g);
            alert.FixClicked += TryFixGhost;
            alert.DisableClicked += DisableGhostKey;
            alert.Dismissed += delegate
            {
                AppLog.Info("Alerta ignorado pelo usuário: '{0}'", currentGhost.KeyName);
                AlertClosed();
            };
            alert.Show();
            AppLog.Info("Modal exibida para tecla: '{0}' - Padrão: {1}", g.KeyName, g.Pattern);
            if (Settings.Notify)
                tray.ShowBalloonTip(3000, "Toque fantasma detectado",
                    "Tecla '" + g.KeyName + "' - " + g.Pattern + " (" + g.Severity + ")", ToolTipIcon.Warning);
        }

        void AlertClosed()
        {
            fixTimer.Stop();
            fixKey = -1;
            alert = null;
            quietUntil = NowMs() + AlertCooldownMs;
        }

        void CloseAlert()
        {
            GhostAlertForm a = alert;
            AlertClosed();
            if (a != null) a.CloseByApp();
        }

        void TryFixGhost()
        {
            if (alert == null) return;
            fixCount = 0;
            fixKey = currentGhost.Key;
            if (currentGhost.Pattern == "STUCK_KEY")
            {
                // Tecla presa: manda um KEY_UP por software para o Windows soltar a tecla.
                NativeMethods.SendKeyUp(currentGhost.Key);
                detector.ReleaseKeyState(currentGhost.Key);
                AppLog.Info("KEY_UP enviado por software para '{0}'", currentGhost.KeyName);
            }
            fixEnd = NowMs() + FixSeconds * 1000L;
            alert.ShowFixMode();
            fixTimer.Start();
            FixTick(null, EventArgs.Empty);
        }

        void FixTick(object sender, EventArgs e)
        {
            if (alert == null || fixKey < 0) { fixTimer.Stop(); return; }
            long left = fixEnd - NowMs();
            if (left <= 0)
            {
                AppLog.Info("Usuário pressionou '{0}' {1} vezes - continuando monitoramento",
                    currentGhost.KeyName, fixCount);
                CloseAlert();
                return;
            }
            alert.SetCounter("Pressionamentos: " + fixCount + "    Restam: " + ((left + 999) / 1000) + "s");
        }

        void DisableGhostKey()
        {
            if (alert == null) return;
            GhostInfo g = currentGhost;
            detector.DisableKeyTemporarily(g.Key);
            AppLog.Warn("Tecla desativada permanentemente: '{0}'", g.KeyName);
            CloseAlert();
            RefreshDisabledList();
            MessageBox.Show(
                "A tecla '" + g.KeyName + "' foi desativada permanentemente.\n\n" +
                "Ela continuará desativada até você:\n" +
                "• Reativá-la pelas Configurações\n" +
                "• Excluir da lista de teclas desativadas\n" +
                "• Desinstalar o programa",
                "Tecla desativada", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---- log ----
        void ShowLog()
        {
            Form f = new Form();
            f.SuspendLayout();
            f.AutoScaleDimensions = new SizeF(96F, 96F);
            f.AutoScaleMode = AutoScaleMode.Dpi;
            f.Text = "ghost_key_log.txt";
            f.ClientSize = new Size(780, 440);
            f.StartPosition = FormStartPosition.CenterScreen;

            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.ColumnCount = 1;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            TextBox box = new TextBox();
            box.Multiline = true;
            box.ReadOnly = true;
            box.ScrollBars = ScrollBars.Both;
            box.WordWrap = false;
            box.Font = new Font("Consolas", 9f);
            box.Dock = DockStyle.Fill;

            Action load = delegate
            {
                try
                {
                    Queue<string> lines = new Queue<string>();
                    using (FileStream fs = new FileStream(AppLog.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            lines.Enqueue(line);
                            if (lines.Count > 2000) lines.Dequeue();
                        }
                    }
                    box.Text = string.Join(Environment.NewLine, lines.ToArray());
                }
                catch (Exception ex) { box.Text = "Não foi possível ler o log: " + ex.Message; }
                box.SelectionStart = box.TextLength;
                box.ScrollToCaret();
            };

            Button refresh = MakeButton("Atualizar", load);
            refresh.Anchor = AnchorStyles.Left;
            t.Controls.Add(refresh);
            t.Controls.Add(box);
            f.Controls.Add(t);
            f.ResumeLayout(false);
            load();
            f.Show();
        }

        // ---- encerramento ----
        void Quit()
        {
            if (exiting) return;
            exiting = true;
            fixTimer.Stop();
            stuckTimer.Stop();
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            if (hook != IntPtr.Zero) { NativeMethods.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            if (alert != null) alert.CloseByApp();
            tray.Visible = false;
            tray.Dispose();
            AppLog.Info("Aplicação encerrada");
            mainForm.Close();
            ExitThread();
        }
    }

    // ------------------------------------------------- instalador / desinstalador
    // Instala por usuario (sem administrador) em %LOCALAPPDATA%\Programs\GhostKeyMonitor, cria
    // atalhos, e registra em "Configuracoes > Aplicativos" para desinstalar pelo Windows.
    static class Installer
    {
        public const string AppName = "Ghost Key Monitor";
        public const string Version = "1.2.0";
        const string ProcessName = "GhostKeyMonitor";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GhostKeyMonitor";

        public static string InstallDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Programs\GhostKeyMonitor");
            }
        }
        public static string InstalledExe { get { return Path.Combine(InstallDir, ProcessName + ".exe"); } }
        static string StartMenuDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName); }
        }
        static string StartMenuLink { get { return Path.Combine(StartMenuDir, AppName + ".lnk"); } }
        static string StartMenuUninstallLink { get { return Path.Combine(StartMenuDir, "Desinstalar " + AppName + ".lnk"); } }
        // versao 1.1 criava o atalho solto no Menu Iniciar
        static string LegacyStartMenuLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); }
        }
        public static bool HasDesktopShortcut() { return File.Exists(DesktopLink); }
        static string DesktopLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); }
        }

        public static bool IsInstalled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                    return k != null && File.Exists(InstalledExe);
            }
            catch (Exception) { return false; }
        }

        public static bool RunningFromInstallDir()
        {
            return string.Equals(Path.GetDirectoryName(Application.ExecutablePath), InstallDir,
                StringComparison.OrdinalIgnoreCase);
        }

        static void StopOtherInstances()
        {
            int me = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcessesByName(ProcessName))
            {
                if (p.Id == me) continue;
                try { p.Kill(); p.WaitForExit(3000); } catch (Exception) { }
            }
        }

        static void CreateShortcut(string lnkPath, string target, string arguments)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath));
            Type shell = Type.GetTypeFromProgID("WScript.Shell");
            object sh = Activator.CreateInstance(shell);
            object lnk = shell.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                null, sh, new object[] { lnkPath });
            Type lt = lnk.GetType();
            lt.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { target });
            if (arguments != null)
                lt.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { arguments });
            lt.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, lnk,
                new object[] { Path.GetDirectoryName(target) });
            lt.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, lnk,
                new object[] { "Detecta toque fantasma no teclado" });
            lt.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
        }

        /// <summary>Instala. Retorna null se deu certo, ou a mensagem de erro.</summary>
        public static string Install(bool desktopShortcut, bool autostart)
        {
            try
            {
                StopOtherInstances();
                Directory.CreateDirectory(InstallDir);
                string self = Application.ExecutablePath;
                if (!string.Equals(self, InstalledExe, StringComparison.OrdinalIgnoreCase))
                    File.Copy(self, InstalledExe, true);

                if (File.Exists(LegacyStartMenuLink)) File.Delete(LegacyStartMenuLink);
                CreateShortcut(StartMenuLink, InstalledExe, null);
                CreateShortcut(StartMenuUninstallLink, InstalledExe, "--uninstall");
                if (desktopShortcut) CreateShortcut(DesktopLink, InstalledExe, null);
                else if (File.Exists(DesktopLink)) File.Delete(DesktopLink);
                Autostart.Set(autostart, InstalledExe);

                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    k.SetValue("DisplayName", AppName);
                    k.SetValue("DisplayVersion", Version);
                    k.SetValue("Publisher", "Ghost Key Monitor");
                    k.SetValue("InstallLocation", InstallDir);
                    k.SetValue("DisplayIcon", InstalledExe);
                    k.SetValue("UninstallString", "\"" + InstalledExe + "\" --uninstall");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // O Windows 11 guarda uma entrada por icone de bandeja em Control Panel\NotifyIconSettings
        // (lembra se o icone esta fixo ou oculto). Remove so a do nosso .exe.
        static void RemoveTrayIconMemory()
        {
            using (RegistryKey root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true))
            {
                if (root == null) return;
                foreach (string name in root.GetSubKeyNames())
                {
                    string path = null;
                    using (RegistryKey k = root.OpenSubKey(name))
                        if (k != null) path = k.GetValue("ExecutablePath") as string;
                    if (path != null && string.Equals(path, InstalledExe, StringComparison.OrdinalIgnoreCase))
                        root.DeleteSubKeyTree(name, false);
                }
            }
        }

        /// <summary>Desinstala. Retorna null se deu certo, ou a mensagem de erro.</summary>
        public static string Uninstall()
        {
            string error = null;
            try { StopOtherInstances(); } catch (Exception) { }
            try { Autostart.Set(false); } catch (Exception ex) { error = ex.Message; }
            try { if (Directory.Exists(StartMenuDir)) Directory.Delete(StartMenuDir, true); } catch (Exception ex) { error = ex.Message; }
            try { if (File.Exists(LegacyStartMenuLink)) File.Delete(LegacyStartMenuLink); } catch (Exception ex) { error = ex.Message; }
            try { if (File.Exists(DesktopLink)) File.Delete(DesktopLink); } catch (Exception ex) { error = ex.Message; }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch (Exception ex) { error = ex.Message; }
            try { RemoveTrayIconMemory(); } catch (Exception) { }
            try
            {
                // pasta alternativa de log/configuracoes (usada se a pasta do programa nao fosse gravavel)
                string alt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GhostKeyMonitor");
                if (Directory.Exists(alt)) Directory.Delete(alt, true);
            }
            catch (Exception) { }
            try
            {
                if (Directory.Exists(InstallDir))
                {
                    if (RunningFromInstallDir())
                    {
                        // Nao da para apagar o proprio .exe em execucao: um cmd apaga a pasta logo depois.
                        ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                            "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + InstallDir + "\"");
                        psi.CreateNoWindow = true;
                        psi.UseShellExecute = false;
                        Process.Start(psi);
                    }
                    else Directory.Delete(InstallDir, true);
                }
            }
            catch (Exception ex) { error = ex.Message; }
            return error;
        }

        public static int RunInstall(bool silent, bool desktop, bool autostart, bool launch)
        {
            string err = Install(desktop, autostart);
            if (err != null)
            {
                if (!silent) MessageBox.Show("Falha ao instalar:\n" + err, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            if (launch)
            {
                try { Process.Start(InstalledExe); } catch (Exception) { }
            }
            if (!silent)
                MessageBox.Show(AppName + " instalado em:\n" + InstallDir + "\n\n" +
                    "Ele fica na bandeja do sistema (perto do relógio). Para desinstalar: " +
                    "Configurações > Aplicativos, ou o menu do ícone da bandeja.",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        public static int RunUninstall(bool silent)
        {
            if (!silent && MessageBox.Show("Desinstalar o " + AppName + "?\n\nO programa, os atalhos, a inicialização " +
                "automática e o log serão removidos.", AppName + " - Desinstalar",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return 2;
            string err = Uninstall();
            if (!silent)
            {
                if (err == null) MessageBox.Show(AppName + " foi desinstalado.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                else MessageBox.Show("Desinstalado, mas com avisos:\n" + err, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return err == null ? 0 : 1;
        }
    }

    // Assistente de instalacao (aparece na primeira execucao ou com --install).
    class SetupForm : Form
    {
        public bool DesktopShortcut { get { return desktopBox.Checked; } }
        public bool AutostartChecked { get { return autostartBox.Checked; } }
        public bool RunOnly;

        readonly CheckBox desktopBox, autostartBox;

        public SetupForm(bool offerRunOnly)
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = Installer.AppName + " - Instalação";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(20, 16, 20, 16);

            TableLayoutPanel t = new TableLayoutPanel();
            t.ColumnCount = 1;
            t.AutoSize = true;
            t.AutoSizeMode = AutoSizeMode.GrowAndShrink;

            Label title = new Label();
            title.Text = "Instalar o " + Installer.AppName;
            title.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            title.AutoSize = true;
            title.Margin = new Padding(3, 3, 3, 8);
            t.Controls.Add(title);

            Label info = new Label();
            info.Text = "Será instalado para o seu usuário (sem administrador) em:\n" + Installer.InstallDir +
                        "\ne um atalho será criado no Menu Iniciar.";
            info.AutoSize = true;
            info.MaximumSize = new Size(420, 0);
            info.Margin = new Padding(3, 3, 3, 8);
            t.Controls.Add(info);

            desktopBox = new CheckBox();
            desktopBox.Text = "Criar atalho na Área de Trabalho";
            desktopBox.Checked = true;
            desktopBox.AutoSize = true;
            t.Controls.Add(desktopBox);

            autostartBox = new CheckBox();
            autostartBox.Text = "Iniciar com o Windows (em segundo plano)";
            autostartBox.Checked = true;
            autostartBox.AutoSize = true;
            t.Controls.Add(autostartBox);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.WrapContents = false;
            buttons.Anchor = AnchorStyles.Right;
            buttons.Margin = new Padding(3, 12, 3, 3);

            Button install = new Button();
            install.Text = "Instalar";
            install.AutoSize = true;
            install.DialogResult = DialogResult.OK;
            buttons.Controls.Add(install);
            if (offerRunOnly)
            {
                Button run = new Button();
                run.Text = "Só executar (sem instalar)";
                run.AutoSize = true;
                run.Click += delegate { RunOnly = true; DialogResult = DialogResult.OK; };
                buttons.Controls.Add(run);
            }
            Button cancel = new Button();
            cancel.Text = "Cancelar";
            cancel.AutoSize = true;
            cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(cancel);
            t.Controls.Add(buttons);

            AcceptButton = install;
            CancelButton = cancel;
            Controls.Add(t);
            ResumeLayout(false);
            PerformLayout();
        }
    }

    static class Program
    {
        static bool Has(string[] args, string flag) { return Array.IndexOf(args, flag) >= 0; }

        static double NumArg(string[] args, string prefix)
        {
            foreach (string a in args)
            {
                double v;
                if (a.StartsWith(prefix, StringComparison.Ordinal) &&
                    double.TryParse(a.Substring(prefix.Length), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out v))
                    return v;
            }
            return 0;
        }

        [STAThread]
        static int Main(string[] args)
        {
            bool silent = Has(args, "--silent");
            try { NativeMethods.SetProcessDPIAware(); } catch (Exception) { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // --uninstall / --install: rodam antes de tudo (e encerram outras copias abertas)
            if (Has(args, "--uninstall")) return Installer.RunUninstall(silent);
            if (Has(args, "--install"))
            {
                if (silent)
                    return Installer.RunInstall(true, Has(args, "--desktop"), Has(args, "--autostart"), Has(args, "--launch"));
                using (SetupForm f = new SetupForm(false))
                {
                    if (f.ShowDialog() != DialogResult.OK) return 2;
                    return Installer.RunInstall(false, f.DesktopShortcut, f.AutostartChecked, true);
                }
            }

            // Abrir o .exe (sem argumentos) fora da pasta de instalacao = usar como instalador.
            if (args.Length == 0 && !Installer.RunningFromInstallDir())
            {
                if (!Installer.IsInstalled())
                {
                    using (SetupForm f = new SetupForm(false))
                    {
                        if (f.ShowDialog() != DialogResult.OK) return 2;
                        return Installer.RunInstall(false, f.DesktopShortcut, f.AutostartChecked, true);
                    }
                }
                DialogResult r = MessageBox.Show(
                    Installer.AppName + " já está instalado.\n\n" +
                    "Sim  = reinstalar / atualizar\nNão  = desinstalar\nCancelar = sair",
                    Installer.AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Yes)
                    return Installer.RunInstall(false, Installer.HasDesktopShortcut(), Autostart.IsEnabled(), true);
                if (r == DialogResult.No)
                    return Installer.RunUninstall(false);
                return 0;
            }

            bool background = Has(args, "--background");
            bool acceptInjected = Has(args, "--accept-injected");
            bool demo = Has(args, "--demo");
            double stuckMs = NumArg(args, "--stuck-ms=");
            const string showEventName = @"Local\GhostKeyMonitorShow";

            bool createdNew;
            using (Mutex mutex = new Mutex(true, @"Local\GhostKeyMonitorSingleton", out createdNew))
            {
                if (!createdNew)
                {
                    // Ja esta rodando: pede para a outra copia mostrar a janela (a menos que seja --background).
                    if (!background)
                    {
                        try { EventWaitHandle.OpenExisting(showEventName).Set(); } catch (Exception) { }
                    }
                    return 0;
                }

                AppLog.Init();
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    AppLog.Error("Erro não tratado: {0}", e.Exception);
                };
                using (EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, showEventName))
                    Application.Run(new MonitorApp(background, acceptInjected, demo, stuckMs, showEvent));
            }
            return 0;
        }
    }
}
