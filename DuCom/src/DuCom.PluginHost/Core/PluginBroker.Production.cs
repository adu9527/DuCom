using System.Text;
using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.PluginHost.Core;

public sealed partial class PluginBroker
{
    private readonly object _recordGate = new();
    private string? _productionRecordBase;
    private SerialLeaseIoResult RecordProduction(ProductionRecordRequest request)
    {
        RequirePermission(Permission.SerialLease);
        if (_scope.Manifest.Id != "com.ducom.production") throw new PluginScopeException(PluginErrorCode.PermissionDenied, "Production recording is reserved for the built-in production tool.");
        lock (_recordGate)
        {
            if (request.Action == "begin")
            {
                string directory = _environment.GetPluginLogDirectory(_scope.Manifest.Id);
                Directory.CreateDirectory(directory);
                string name = string.Concat(request.Project.Take(80).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                // One record file pair per worker session per project: every run used to
                // create a new timestamped pair, and the export only reads one file, so
                // consecutive tests split into files the operator could never see whole.
                string prefix = "Production-" + name + "-";
                if (_productionRecordBase is null
                    || !Path.GetFileName(_productionRecordBase).StartsWith(prefix, StringComparison.Ordinal)
                    || !File.Exists(_productionRecordBase + ".csv"))
                {
                    _productionRecordBase = Path.Combine(directory, $"{prefix}{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}");
                    File.WriteAllText(_productionRecordBase + ".log", $"项目：{request.Project}  开始：{DateTime.Now:O}{Environment.NewLine}", new UTF8Encoding(true));
                    File.WriteAllText(_productionRecordBase + ".csv", "时间,轮次,通道,测试项,结果,状态,本轮结果,串口,耗时秒,测试号" + Environment.NewLine, new UTF8Encoding(true));
                    TryEnforcePluginFolderQuota(directory, _productionRecordBase + ".log");
                }
                else
                {
                    // Session marker so the shared log stays readable across runs.
                    File.AppendAllText(_productionRecordBase + ".log", $"{Environment.NewLine}项目：{request.Project}  开始：{DateTime.Now:O}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            else if (request.Action == "clear")
            {
                // Delete every record file of this project across the plugin's dated
                // log folders and start over. Runs host-side: the worker sandbox has
                // no write access to the host log tree.
                string directory = _environment.GetPluginLogDirectory(_scope.Manifest.Id);
                string name = string.Concat(request.Project.Take(80).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                string datedRoot = Directory.GetParent(directory)?.FullName ?? directory;
                string searchRoot = Path.GetFileName(datedRoot).Equals(_scope.Manifest.Id, StringComparison.OrdinalIgnoreCase) ? datedRoot : directory;
                string prefix = "Production-" + name + "-";
                if (Directory.Exists(searchRoot))
                {
                    foreach (string file in Directory.EnumerateFiles(searchRoot, prefix + "*", SearchOption.AllDirectories)
                                 .Where(file => file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".log", StringComparison.OrdinalIgnoreCase)))
                    {
                        try { File.Delete(file); } catch (Exception exception) { PluginHost.Diagnostics.PluginHostTrace.Sink?.Invoke(Diagnostics.PluginLogLevel.Warning, $"记录文件删除失败：{file}", exception); }
                    }
                }
                _productionRecordBase = null;
                return new() { Path = "" };
            }
            if (_productionRecordBase is null) throw new IOException("测试记录尚未创建");
            if (request.Logs.Length > 1000 || request.CsvRows.Length > 1000 || request.Logs.Sum(s => s.Length) + request.CsvRows.Sum(s => s.Length) > 512 * 1024)
                throw new PluginScopeException(PluginErrorCode.ResourceLimit, "Record batch too large.");
            if (request.Logs.Length > 0) File.AppendAllLines(_productionRecordBase + ".log", request.Logs, Encoding.UTF8);
            if (request.CsvRows.Length > 0) File.AppendAllLines(_productionRecordBase + ".csv", request.CsvRows, Encoding.UTF8);
            return new() { Path = _productionRecordBase };
        }
    }
}
