using System.Diagnostics;
using System.Reflection;

namespace DuCom.SetupWizard;

internal static class Program
{
    private const string EngineResource = "DuCom.SetupWizard.VelopackSetup.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--verify-package", StringComparer.OrdinalIgnoreCase))
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EngineResource);
            return stream is { Length: > 0 } ? 0 : 1;
        }

        ApplicationConfiguration.Initialize();
        using SetupForm form = new();
        Application.Run(form);
        return form.ExitCode;
    }

    internal static async Task<int> InstallAsync(string installDirectory, IProgress<int> progress, CancellationToken cancellationToken)
    {
        string workDirectory = Path.Combine(Path.GetTempPath(), "DuCom-Setup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        string enginePath = Path.Combine(workDirectory, "DuCom-Velopack-Setup.exe");
        try
        {
            await using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(EngineResource)
                ?? throw new InvalidOperationException("安装包缺少内部安装引擎。"))
            await using (FileStream destination = File.Create(enginePath))
                await source.CopyToAsync(destination, cancellationToken);

            progress.Report(15);
            ProcessStartInfo startInfo = new(enginePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDirectory,
            };
            startInfo.ArgumentList.Add("--silent");
            startInfo.ArgumentList.Add("--installto");
            startInfo.ArgumentList.Add(installDirectory);
            startInfo.ArgumentList.Add("--log");
            startInfo.ArgumentList.Add(Path.Combine(workDirectory, "velopack-install.log"));

            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动内部安装引擎。");
            int displayedProgress = 20;
            while (!process.HasExited)
            {
                await Task.Delay(250, cancellationToken);
                displayedProgress = Math.Min(90, displayedProgress + 1);
                progress.Report(displayedProgress);
            }
            await process.WaitForExitAsync(cancellationToken);
            progress.Report(process.ExitCode == 0 ? 100 : displayedProgress);
            return process.ExitCode;
        }
        finally
        {
            try { Directory.Delete(workDirectory, true); }
            catch { }
        }
    }
}

internal sealed class SetupForm : Form
{
    private enum Page { Welcome, License, Location, Installing, Complete }

    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly Panel _content = new();
    private readonly Button _back = new();
    private readonly Button _next = new();
    private readonly Button _cancel = new();
    private readonly CheckBox _accept = new();
    private readonly TextBox _path = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _progressText = new();
    private readonly CheckBox _launch = new();
    private Page _page;

    public int ExitCode { get; private set; }

    public SetupForm()
    {
        Text = "DuCom 安装向导";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(720, 500);
        Font = new Font("Microsoft YaHei UI", 9F);

        Panel header = new() { Dock = DockStyle.Top, Height = 92, BackColor = Color.FromArgb(28, 35, 48) };
        _title.SetBounds(34, 20, 640, 30);
        _title.Font = new Font(Font.FontFamily, 17F, FontStyle.Bold);
        _title.ForeColor = Color.White;
        _subtitle.SetBounds(36, 56, 640, 22);
        _subtitle.ForeColor = Color.FromArgb(190, 201, 216);
        header.Controls.AddRange([_title, _subtitle]);

        _content.Dock = DockStyle.Fill;
        _content.Padding = new Padding(38, 28, 38, 20);

        Panel footer = new() { Dock = DockStyle.Bottom, Height = 66, BackColor = Color.FromArgb(245, 246, 248) };
        _back.Text = "上一步";
        _next.Text = "下一步";
        _cancel.Text = "取消";
        _back.SetBounds(408, 17, 90, 32);
        _next.SetBounds(506, 17, 90, 32);
        _cancel.SetBounds(604, 17, 90, 32);
        _back.Click += (_, _) => NavigateBack();
        _next.Click += async (_, _) => await NavigateNextAsync();
        _cancel.Click += (_, _) => Close();
        footer.Controls.AddRange([_back, _next, _cancel]);

        Controls.Add(_content);
        Controls.Add(footer);
        Controls.Add(header);
        FormClosing += OnFormClosing;
        ShowPage(Page.Welcome);
    }

    private void ShowPage(Page page)
    {
        _page = page;
        _content.Controls.Clear();
        _back.Enabled = page is Page.License or Page.Location;
        _next.Enabled = true;
        _cancel.Enabled = page != Page.Installing;
        _next.Text = page switch { Page.Location => "安装", Page.Complete => "完成", _ => "下一步" };

        switch (page)
        {
            case Page.Welcome:
                _title.Text = "欢迎安装 DuCom";
                _subtitle.Text = "串口调试、数据分析与设备工具平台";
                AddText("此向导将引导您完成 DuCom 的安装。\r\n\r\n安装前建议关闭正在运行的 DuCom，以避免文件被占用。\r\n\r\n点击“下一步”继续。", 15F);
                break;
            case Page.License:
                _title.Text = "软件许可协议";
                _subtitle.Text = "请阅读并确认许可条款";
                RichTextBox license = new() { Dock = DockStyle.Top, Height = 300, ReadOnly = true, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Text = ReadAgreement() };
                _accept.Text = "我已阅读并同意许可协议";
                _accept.SetBounds(0, 315, 360, 28);
                _accept.CheckedChanged += (_, _) => _next.Enabled = _accept.Checked;
                _content.Controls.AddRange([license, _accept]);
                _next.Enabled = _accept.Checked;
                break;
            case Page.Location:
                _title.Text = "选择安装位置";
                _subtitle.Text = "DuCom 将安装到以下目录";
                Label prompt = new() { Text = "安装目录：", AutoSize = true, Location = new Point(0, 20) };
                _path.SetBounds(0, 50, 545, 30);
                if (string.IsNullOrWhiteSpace(_path.Text)) _path.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DuCom");
                Button browse = new() { Text = "浏览..." };
                browse.SetBounds(555, 48, 90, 32);
                browse.Click += (_, _) => BrowseLocation();
                Label hint = new() { Text = "建议安装到当前用户目录，无需管理员权限。您也可以选择其他有写入权限的位置。", AutoSize = true, ForeColor = Color.DimGray, Location = new Point(0, 95) };
                _content.Controls.AddRange([prompt, _path, browse, hint]);
                break;
            case Page.Installing:
                _title.Text = "正在安装 DuCom";
                _subtitle.Text = "请稍候，不要关闭安装程序";
                _progress.SetBounds(0, 70, 644, 28);
                _progress.Style = ProgressBarStyle.Continuous;
                _progressText.SetBounds(0, 115, 644, 28);
                _progressText.Text = "正在准备安装...";
                _content.Controls.AddRange([_progress, _progressText]);
                _back.Enabled = false;
                _next.Enabled = false;
                break;
            case Page.Complete:
                _title.Text = "DuCom 安装完成";
                _subtitle.Text = "安装向导已完成";
                AddText("DuCom 已成功安装到：\r\n" + _path.Text + "\r\n\r\n点击“完成”退出安装向导。", 13F);
                _launch.Text = "完成后启动 DuCom";
                _launch.Checked = true;
                _launch.SetBounds(38, 275, 300, 28);
                _content.Controls.Add(_launch);
                _back.Enabled = false;
                _cancel.Enabled = false;
                break;
        }
    }

    private void AddText(string text, float size)
    {
        Label label = new() { Text = text, AutoSize = false, Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, size), ForeColor = Color.FromArgb(48, 55, 66) };
        _content.Controls.Add(label);
    }

    private static string ReadAgreement()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DuCom.SetupWizard.InstallerAgreement.txt")
            ?? throw new InvalidOperationException("安装包缺少许可协议。");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private void BrowseLocation()
    {
        using FolderBrowserDialog dialog = new() { Description = "选择 DuCom 安装目录", SelectedPath = _path.Text, UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) _path.Text = dialog.SelectedPath;
    }

    private void NavigateBack()
    {
        ShowPage(_page switch { Page.License => Page.Welcome, Page.Location => Page.License, _ => _page });
    }

    private async Task NavigateNextAsync()
    {
        switch (_page)
        {
            case Page.Welcome:
                ShowPage(Page.License);
                break;
            case Page.License:
                if (_accept.Checked) ShowPage(Page.Location);
                break;
            case Page.Location:
                if (!ValidateInstallPath()) return;
                ShowPage(Page.Installing);
                await InstallAsync();
                break;
            case Page.Complete:
                if (_launch.Checked) LaunchInstalledApplication();
                ExitCode = 0;
                Close();
                break;
        }
    }

    private bool ValidateInstallPath()
    {
        string path = _path.Text.Trim();
        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
        {
            MessageBox.Show(this, "请选择有效的完整安装路径。", "安装位置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        _path.Text = Path.GetFullPath(path);
        return true;
    }

    private async Task InstallAsync()
    {
        try
        {
            Progress<int> progress = new(value =>
            {
                _progress.Value = Math.Clamp(value, 0, 100);
                _progressText.Text = value >= 100 ? "安装完成。" : $"正在安装... {value}%";
            });
            int exitCode = await Program.InstallAsync(_path.Text, progress, CancellationToken.None);
            if (exitCode != 0) throw new InvalidOperationException($"内部安装引擎返回错误代码 {exitCode}。");
            ShowPage(Page.Complete);
        }
        catch (Exception exception)
        {
            ExitCode = 1;
            MessageBox.Show(this, "DuCom 安装失败。\r\n\r\n" + exception.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ShowPage(Page.Location);
        }
    }

    private void LaunchInstalledApplication()
    {
        string executable = Path.Combine(_path.Text, "current", "DuCom.exe");
        if (!File.Exists(executable)) executable = Path.Combine(_path.Text, "DuCom.exe");
        if (File.Exists(executable)) Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_page == Page.Installing)
        {
            e.Cancel = true;
            return;
        }
        if (_page != Page.Complete && ExitCode == 0) ExitCode = 1;
    }
}
