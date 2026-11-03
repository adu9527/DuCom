using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.Plugins.Crc;

public sealed class Plugin : DuComPlugin
{
    private string _status = "输入数据或打开文件，选择模型后计算。";
    private string _hex = "", _bin = "";
    private bool _error, _busy;
    public override Task<PluginActivation> ActivateAsync(CancellationToken cancellationToken) => Task.FromResult(new PluginActivation
    {
        Menus = [new() { ContributionId = "open-crc", Label = "CRC计算", CommandId = "open", PageId = "crc", Order = 130 }],
        ToolPages = [new() { ContributionId = "crc", Title = "CRC计算", PreferredWidth = 1040, PreferredHeight = 900, MinWidth = 840, Nodes = Nodes() }],
    });

    public override async Task<CommandInvokeOutcome> OnCommandAsync(string commandId, string? arg, IReadOnlyDictionary<string, string> formValues, CancellationToken cancellationToken)
    {
        if (_busy) return CommandInvokeOutcome.Complete();
        if (commandId is not ("calculate" or "file")) { await Push(cancellationToken); return CommandInvokeOutcome.Complete(); }
        _busy = true;
        _hex = _bin = "";
        _error = false;
        try
        {
            CrcModel model = CrcCalculator.ParseModel(formValues);
            uint result;
            long length;
            string source = "数据";
            if (commandId == "file")
            {
                FilesPickResult? picked = await Api.Files.PickReadAsync(new FilePickReadOptions(FilterName: "所有文件"), cancellationToken);
                if (picked is null) { _status = "已取消选择文件。"; return CommandInvokeOutcome.Complete(); }
                FilesStatResult before = await Api.Files.StatAsync(picked.Token, cancellationToken);
                if (!before.Exists || before.Length > 64 * 1024 * 1024) throw new InvalidDataException("请选择不超过 64 MiB 的文件。");
                _status = "正在计算文件 CRC…";
                await Push(cancellationToken);
                uint register = model.Init;
                long offset = 0;
                while (offset < before.Length)
                {
                    FileReadChunk chunk = await Api.Files.ReadChunkAsync(picked.Token, offset, (int)Math.Min(128 * 1024, before.Length - offset), cancellationToken);
                    if (chunk.Data.Length == 0 || chunk.Data.Length > before.Length - offset) throw new IOException("文件长度发生变化，请重新计算。");
                    register = CrcCalculator.Append(register, chunk.Data, model);
                    offset += chunk.Data.Length;
                }
                FilesStatResult after = await Api.Files.StatAsync(picked.Token, cancellationToken);
                if (!after.Exists || after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc) throw new IOException("文件在计算期间被修改，请重新计算。");
                result = CrcCalculator.Finish(register, model);
                length = offset;
                source = picked.DisplayPath;
            }
            else
            {
                byte[] data = CrcCalculator.ParseInput(formValues.GetValueOrDefault("data", ""), formValues.GetValueOrDefault("mode") == "ascii");
                result = CrcCalculator.Compute(data, model);
                length = data.Length;
            }
            _hex = CrcCalculator.Hex(result, model.Width);
            _bin = CrcCalculator.Binary(result, model.Width);
            _status = $"计算完成 · {length:N0} 字节 · {source}";
        }
        catch (Exception exception) when (exception is FormatException or IOException or ArgumentException)
        { _error = true; _status = exception.Message; }
        finally { _busy = false; await Push(CancellationToken.None); }
        return CommandInvokeOutcome.Complete();
    }

    private Task Push(CancellationToken token) => Api.Ui.UpdateToolPageAsync("crc", Nodes(), token);
    private IReadOnlyList<UiNode> Nodes() => [new UiTextNode { FieldId = "native-state", ReadOnly = true,
        Text = JsonSerializer.Serialize(new { Hex = _hex, Bin = _bin, Status = _status, Error = _error, Busy = _busy }) }];
}
