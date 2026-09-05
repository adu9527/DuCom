using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Services;

namespace DuCom.ViewModels;

internal sealed partial class LogPackageViewModel : ObservableObject
{
    private const string DefaultProjectName = "HTS-407";
    private const string DefaultTester = "张三";
    private const string DefaultDeviceSoftwareVersion = "V001";
    private const string DefaultReproductionProbability = "100%";
    private const string LegacyDefaultNotes = "请补充测试距离、断开持续时间、恢复距离、设备角色、设备软件版本、环境干扰情况，以及是否能够通过重启或手动连接恢复。";
    private const string DefaultTitle = "设备超距断开后主设备不回连";
    private const string DefaultProblemDescription = "测试过程中，从设备逐渐远离主设备并超过有效通信距离后，主从设备连接断开。随后将从设备重新移回正常通信距离，主设备未自动发起回连，连接状态持续显示为断开，业务数据无法恢复传输。等待较长时间后现象仍然存在，需要手动重启设备或重新触发连接流程才能恢复。";
    private const string DefaultReproductionSteps = "1. 主设备和从设备正常上电，确认双方已成功连接，业务数据能够正常收发。\r\n2. 保持主设备位置不变，将从设备缓慢移远，直至超过有效通信距离。\r\n3. 观察日志并确认主从设备连接已经断开。\r\n4. 记录断开时间及断开前后的关键日志。\r\n5. 将从设备重新移回正常通信距离，并保持设备持续上电。\r\n6. 等待主设备执行自动回连，观察连接状态和业务数据。\r\n7. 实际结果：主设备未重新连接从设备，连接状态持续为断开。\r\n8. 预期结果：从设备恢复到有效通信距离后，主设备应在规定时间内自动发起回连并恢复业务数据。";
    private const string DefaultNotes = "无";
    private readonly Action _selectOutputDirectory;

    public LogPackageViewModel(IEnumerable<SessionViewModel> sessions, string outputDirectory, LogPackagePreferences preferences, Action selectOutputDirectory)
    {
        _selectOutputDirectory = selectOutputDirectory;
        OutputDirectory = outputDirectory;
        ProjectName = string.IsNullOrWhiteSpace(preferences.ProjectName) ? DefaultProjectName : preferences.ProjectName;
        Tester = string.IsNullOrWhiteSpace(preferences.Tester) ? DefaultTester : preferences.Tester;
        Title = preferences.Title ?? DefaultTitle;
        DeviceSoftwareVersion = string.IsNullOrWhiteSpace(preferences.DeviceSoftwareVersion) ? DefaultDeviceSoftwareVersion : preferences.DeviceSoftwareVersion;
        ReproductionProbability = string.IsNullOrWhiteSpace(preferences.ReproductionProbability) ? DefaultReproductionProbability : preferences.ReproductionProbability;
        ProblemDescription = preferences.ProblemDescription ?? DefaultProblemDescription;
        ReproductionSteps = preferences.ReproductionSteps ?? DefaultReproductionSteps;
        Notes = string.IsNullOrWhiteSpace(preferences.Notes) || string.Equals(preferences.Notes, LegacyDefaultNotes, StringComparison.Ordinal)
            ? DefaultNotes
            : preferences.Notes;
        Dictionary<string, string> deviceNames = preferences.DeviceNames ?? new(StringComparer.OrdinalIgnoreCase);
        Ports = [.. sessions.Where(session => session.IsOpen).Select(session =>
            new LogPackagePortViewModel(session, deviceNames.GetValueOrDefault(session.PortName, string.Empty)))];
    }

    public ObservableCollection<LogPackagePortViewModel> Ports { get; }

    [ObservableProperty]
    public partial string CurrentTime { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProjectName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReproductionTime { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProblemDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReproductionSteps { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Notes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Tester { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeviceSoftwareVersion { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReproductionProbability { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OutputDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreatePackageCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    public Func<Task>? CreatePackageAsync { get; set; }

    [RelayCommand]
    private void CopyCurrentTime() => ReproductionTime = CurrentTime;

    [RelayCommand]
    private void SelectOutputDirectory() => _selectOutputDirectory();

    [RelayCommand(CanExecute = nameof(CanCreatePackage))]
    private async Task CreatePackage()
    {
        if (CreatePackageAsync is not null)
        {
            await CreatePackageAsync();
        }
    }

    private bool CanCreatePackage() => !IsBusy;
}

internal sealed partial class LogPackagePortViewModel : ObservableObject
{
    public LogPackagePortViewModel(SessionViewModel session, string deviceName)
    {
        Session = session;
        PortName = session.PortName;
        DeviceName = deviceName;
    }

    internal SessionViewModel Session { get; }
    public string PortName { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;

    [ObservableProperty]
    public partial string DeviceName { get; set; }
}
