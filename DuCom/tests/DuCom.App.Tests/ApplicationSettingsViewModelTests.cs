using DuCom.ViewModels;
using Xunit;

namespace DuCom.App.Tests;

public sealed class ApplicationSettingsViewModelTests
{
    [Fact]
    public void TransportChangeRaisesPersistAndTransportSignal()
    {
        ApplicationSettingsViewModel viewModel = new(["Cascadia Mono"]);
        ApplicationSettingChangedEventArgs? change = null;
        viewModel.SettingChanged += (_, args) => change = args;

        viewModel.BaudRate = 115_200;

        Assert.NotNull(change);
        Assert.Equal(nameof(ApplicationSettingsViewModel.BaudRate), change.PropertyName);
        Assert.Equal(
            ApplicationSettingChangeCategory.Persist | ApplicationSettingChangeCategory.Transport,
            change.Category);
        Assert.Equal(115_200, change.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PortVisibilityChangeRaisesRebuildSignal(bool showSerialPorts)
    {
        ApplicationSettingsViewModel viewModel = new(["Cascadia Mono"])
        {
            ShowSerialPorts = !showSerialPorts,
        };
        ApplicationSettingChangedEventArgs? change = null;
        viewModel.SettingChanged += (_, args) => change = args;

        viewModel.ShowSerialPorts = showSerialPorts;

        Assert.NotNull(change);
        Assert.True(change.Category.HasFlag(ApplicationSettingChangeCategory.Persist));
        Assert.True(change.Category.HasFlag(ApplicationSettingChangeCategory.PortVisibility));
    }

    [Fact]
    public void SpecializedChangesExposeSignalsThatMainCanApplyDuringOrAfterLoading()
    {
        ApplicationSettingsViewModel viewModel = new(["Cascadia Mono"]);
        List<ApplicationSettingChangeCategory> categories = [];
        viewModel.SettingChanged += (_, args) => categories.Add(args.Category);

        viewModel.PreventSleep = true;
        viewModel.LogFileNameFormat = "{Port}";
        viewModel.PrivateMemoryMonitorEnabled = true;
        viewModel.PrivateMemoryMonitorEnabled = false;

        Assert.Contains(categories, category => category.HasFlag(ApplicationSettingChangeCategory.PreventSleep));
        Assert.Contains(categories, category => category.HasFlag(ApplicationSettingChangeCategory.LogFileNamePreview));
        Assert.Contains(categories, category => category.HasFlag(ApplicationSettingChangeCategory.PrivateMemoryMonitor));
        Assert.All(categories, category => Assert.True(category.HasFlag(ApplicationSettingChangeCategory.Persist)));
    }

    [Fact]
    public void ConstructorUsesInjectedFontProviderAndApplicationDefaults()
    {
        ApplicationSettingsViewModel viewModel = new(["Consolas", "Cascadia Mono", "consolas"]);

        Assert.Equal(["Cascadia Mono", "Consolas"], viewModel.LogFontFamilies);
        Assert.Equal(1_152_000, viewModel.BaudRate);
        Assert.Equal("HH:mm:ss.fff", viewModel.TimestampFormat);
        Assert.Equal("Cascadia Mono", viewModel.LogFontFamily);
        Assert.True(viewModel.LoggingEnabled);
    }
}
