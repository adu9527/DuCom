using System.Globalization;
using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.Plugins.Checksum;

public sealed class Plugin : DuComPlugin
{
    private const string PageId = "checksum";
    private FirmwareResult? _result;
    private string _status = "选择固件文件，查询版本校验码及固件信息。";
    private bool _busy;

    public override Task<PluginActivation> ActivateAsync(CancellationToken cancellationToken) => Task.FromResult(new PluginActivation
    {
        Menus = [new MenuContribution { ContributionId = "open-checksum", Label = "校验码查询工具", CommandId = "open", PageId = PageId, Order = 120 }],
        ToolPages = [new ToolPageContribution { ContributionId = PageId, Title = "校验码查询工具", Order = 120, PreferredWidth = 700, PreferredHeight = 480, MinWidth = 600, Nodes = BuildNodes() }],
    });

    public override async Task<CommandInvokeOutcome> OnCommandAsync(string commandId, string? arg, IReadOnlyDictionary<string, string> formValues, CancellationToken cancellationToken)
    {
        if (commandId == "open") await PushAsync(cancellationToken);
        if (commandId != "select" || _busy) return CommandInvokeOutcome.Complete();
        _busy = true;
        try
        {
            FilesPickResult? picked = await Api.Files.PickReadAsync(new FilePickReadOptions(FilterName: "固件文件", Extensions: ["bin", "fw", "ufw", "prd", "upd"]), cancellationToken);
            if (picked is null) return CommandInvokeOutcome.Complete();
            _result = null;
            _status = "正在读取并分析固件…";
            await PushAsync(cancellationToken);
            FilesStatResult before = await Api.Files.StatAsync(picked.Token, cancellationToken);
            if (!before.Exists || before.Length <= 0 || before.Length > FirmwareAnalyzer.MaxBytes)
                throw new InvalidDataException("文件为空或超过 64 MiB，请选择完整固件。");
            byte[] bytes = new byte[checked((int)before.Length)];
            int offset = 0;
            while (offset < bytes.Length)
            {
                FileReadChunk chunk = await Api.Files.ReadChunkAsync(picked.Token, offset, Math.Min(128 * 1024, bytes.Length - offset), cancellationToken);
                if (chunk.Data.Length == 0 || chunk.Data.Length > bytes.Length - offset)
                    throw new IOException("读取期间固件长度发生变化，请重新选择文件。");
                chunk.Data.CopyTo(bytes, offset);
                offset += chunk.Data.Length;
            }
            FilesStatResult after = await Api.Files.StatAsync(picked.Token, cancellationToken);
            if (!after.Exists || before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc)
                throw new IOException("固件在读取过程中被修改，请等待构建完成后重新查询。");
            _result = await Task.Run(() => FirmwareAnalyzer.Analyze(bytes, picked.DisplayPath, before.LastWriteTimeUtc), cancellationToken);
            _status = _result.Status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _status = "查询已取消。";
        }
        catch (Exception exception)
        {
            _result = null;
            _status = "查询失败：" + exception.Message;
            Api.Diagnostics.Warning(_status);
        }
        finally
        {
            _busy = false;
            await PushAsync(CancellationToken.None);
        }
        return CommandInvokeOutcome.Complete();
    }

    private Task PushAsync(CancellationToken cancellationToken) => Api.Ui.UpdateToolPageAsync(PageId, BuildNodes(), cancellationToken);

    private IReadOnlyList<UiNode> BuildNodes()
    {
        List<UiNode> nodes =
        [
            new UiTextNode { FieldId = "native-state", ReadOnly = true, Text = JsonSerializer.Serialize(new
            {
                Busy = _busy, Status = _status, Path = _result?.Path,
                Code = _result?.FirmwareCode, Name = _result?.BluetoothName, Chip = _result?.Chip,
                Manufacturer = _result?.Manufacturer, Date = _result?.BuildDate, FileType = _result?.FileType,
                Verified = _result is { IsCorrupt: false } && (_result.VendorContentVerified is true || (_result.ImageValid is true && _result.CrcValid is true)),
                Error = _result?.IsCorrupt == true || _status.StartsWith("查询失败", StringComparison.Ordinal),
            }) },
            new UiButtonNode { Text = _busy ? "正在查询…" : "打开固件", CommandId = "select", Accent = true, IsEnabled = !_busy, DisableOnClick = true },
            new UiLabelNode { Text = "支持恒玄（需接入）、杰理、蓝讯。恒玄详细接入流程见插件详情页的“恒玄接入文档”。", Style = UiTextStyle.Caption, Wrap = true },
            new UiLabelNode { Text = _status, Style = _result?.IsCorrupt == true ? UiTextStyle.Warning : UiTextStyle.Normal, Wrap = true },
        ];
        if (_result is { } result)
        {
            nodes.Add(new UiLabelNode { Text = "固件校验码", Style = UiTextStyle.Heading });
            nodes.Add(new UiTextNode { FieldId = "checksum", Text = string.IsNullOrEmpty(result.FirmwareCode) ? "未嵌入 / 未识别" : result.FirmwareCode, ReadOnly = true });
            nodes.Add(new UiDividerNode());
            Add("文件", result.FileName);
            Add("路径", result.Path);
            Add("蓝牙名", result.BluetoothName);
            Add("芯片", result.Chip);
            Add("厂商", result.Manufacturer);
            Add("时间", result.BuildDate);
            Add("文件类型", result.FileType);
            Add("文件大小", result.FileSize.ToString("N0", CultureInfo.InvariantCulture) + " 字节");
            nodes.Add(new UiExpanderNode { Title = "校验详情", Children = [new UiLabelNode { Text = result.Note, Wrap = true }, new UiLabelNode { Text = "文件 SHA-256", Style = UiTextStyle.Caption }, new UiTextNode { FieldId = "sha256", Text = result.FileSha256, ReadOnly = true }] });
        }
        return nodes;

        void Add(string label, string value) => nodes.Add(new UiLabelNode { Text = label + "：" + value, Wrap = true });
    }
}
