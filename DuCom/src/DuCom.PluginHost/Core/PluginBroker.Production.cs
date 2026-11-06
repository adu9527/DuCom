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
                _productionRecordBase = Path.Combine(directory, $"Production-{name}-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}");
                File.WriteAllText(_productionRecordBase + ".log", $"项目：{request.Project}  开始：{DateTime.Now:O}{Environment.NewLine}", new UTF8Encoding(true));
                File.WriteAllText(_productionRecordBase + ".csv", "时间,轮次,通道,测试项,结果,状态,本轮结果" + Environment.NewLine, new UTF8Encoding(true));
                TryEnforcePluginFolderQuota(directory, _productionRecordBase + ".log");
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
