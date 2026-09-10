using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.Plugins.LogPackage;

public sealed partial class Plugin
{
    private async Task PushToolPageAsync()
    {
        try
        {
            await Api.Ui.UpdateToolPageAsync("packager", BuildFormNodes());
        }
        catch (PluginHostException)
        {
        }
    }

    private IReadOnlyList<UiNode> BuildFormNodes()
    {
        LogPackagePreferences preferences;
        string status;
        int? percent;
        lock (_gate)
        {
            preferences = _preferences with { };
            status = _status;
            percent = _progressPercent;
        }

        List<UiNode> rows =
        [
            new UiLabelNode { Text = Zh("日志打包", "Log package"), Style = UiTextStyle.Heading },
            new UiPanelNode
            {
                Direction = UiDirection.Horizontal,
                Children =
                [
                    new UiTextNode { FieldId = "currentTime", ReadOnly = true, Placeholder = Zh("当前时间（毫秒）", "Current time (ms)") },
                ],
            },
            new UiPanelNode
            {
                Direction = UiDirection.Horizontal,
                Children =
                [
                    new UiTextNode { FieldId = "projectName", Text = preferences.ProjectName, Placeholder = Zh("项目名称", "Project name") },
                    new UiTextNode { FieldId = "tester", Text = preferences.Tester, Placeholder = Zh("测试人员", "Tester") },
                ],
            },
            new UiPanelNode
            {
                Direction = UiDirection.Horizontal,
                Children =
                [
                    new UiTextNode { FieldId = "title", Text = preferences.Title, Placeholder = Zh("问题标题", "Issue title") },
                    new UiTextNode { FieldId = "reproductionTime", Text = preferences.ReproductionTime, Placeholder = Zh("复现时间", "Reproduction time") },
                ],
            },
            new UiPanelNode
            {
                Direction = UiDirection.Horizontal,
                Children =
                [
                    new UiTextNode { FieldId = "deviceSoftwareVersion", Text = preferences.DeviceSoftwareVersion, Placeholder = Zh("设备软件版本", "Device software version") },
                    new UiTextNode { FieldId = "reproductionProbability", Text = preferences.ReproductionProbability, Placeholder = Zh("复现概率", "Reproduction probability") },
                ],
            },
            new UiTextNode { FieldId = "outputDirectory", Text = EffectiveOutputDirectory(preferences), ReadOnly = true, Placeholder = Zh("输出目录", "Output directory") },
            new UiPanelNode
            {
                Direction = UiDirection.Horizontal,
                Children =
                [
                    new UiButtonNode { CommandId = "browse-output", Text = Zh("选择目录…", "Browse..."), SubmitForm = true },
                    new UiCheckBoxNode { FieldId = "followLogDirectory", Label = Zh("跟随日志目录", "Follow log directory"), IsChecked = preferences.FollowLogDirectory },
                ],
            },
            new UiTextNode { FieldId = "problemDescription", Text = preferences.ProblemDescription, Placeholder = Zh("问题描述", "Problem description"), Multiline = true },
            new UiTextNode { FieldId = "reproductionSteps", Text = preferences.ReproductionSteps, Placeholder = Zh("复现步骤", "Reproduction steps"), Multiline = true },
            new UiTextNode { FieldId = "notes", Text = preferences.Notes, Placeholder = Zh("备注", "Notes"), Multiline = true },
            new UiButtonNode { CommandId = "refresh-sessions", Text = Zh("刷新会话", "Refresh sessions"), SubmitForm = true },
            BuildSessionList(preferences),
            new UiPanelNode
            {
                Direction = UiDirection.Horizontal,
                Children =
                [
                    new UiButtonNode { CommandId = "save-form", Text = Zh("保存表单", "Save form"), SubmitForm = true },
                    new UiButtonNode { CommandId = "pack", Text = Zh("打包", "Package"), Accent = true, SubmitForm = true },
                    new UiButtonNode { CommandId = "cancel", Text = Zh("取消", "Cancel") },
                ],
            },
        ];

        if (percent.HasValue)
        {
            rows.Add(new UiProgressNode { Percent = percent, Label = status });
        }
        else if (status.Length > 0)
        {
            rows.Add(new UiLabelNode { Text = status, Style = UiTextStyle.Caption });
        }

        return [new UiPanelNode { Direction = UiDirection.Vertical, Children = rows }];
    }

    private string EffectiveOutputDirectory(LogPackagePreferences preferences)
    {
        if (!preferences.FollowLogDirectory && !string.IsNullOrWhiteSpace(preferences.OutputDirectory))
        {
            return preferences.OutputDirectory;
        }

        return string.IsNullOrEmpty(_logDirectory) ? Zh("（跟随日志目录）", "(follows the log directory)") : _logDirectory;
    }

    private UiNode BuildSessionList(LogPackagePreferences preferences)
    {
        try
        {
            IReadOnlyList<DuCom.Plugin.Dto.SerialSessionInfo> sessions = Api.Logs.ListSessionsAsync(CancellationToken.None).GetAwaiter().GetResult();
            List<UiNode> items = [];
            foreach (DuCom.Plugin.Dto.SerialSessionInfo session in sessions)
            {
                string device = preferences.PortDevices.TryGetValue(session.Port, out string? mapped) && !string.IsNullOrWhiteSpace(mapped) ? mapped : string.Empty;
                items.Add(new UiPanelNode { Direction = UiDirection.Horizontal, Children =
                [
                    new UiCheckBoxNode { FieldId = "selection:" + session.SessionId, Label = session.Port, IsChecked = preferences.SessionSelection is null || preferences.SessionSelection.GetValueOrDefault(session.SessionId) },
                    new UiTextNode { FieldId = "device:" + session.Port, Text = device, Placeholder = Zh("设备名", "Device name") },
                ] });
            }

            return new UiPanelNode { Direction = UiDirection.Vertical, Children = items };
        }
        catch (Exception)
        {
            return new UiLabelNode { Text = Zh("无法枚举会话", "Cannot enumerate sessions"), Style = UiTextStyle.Caption };
        }
    }
}
