using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.Plugins.Production;

public sealed class Plugin : DuComPlugin
{
    private ProductionProfile _profile = ProductionProfile.CreateExample();
    private readonly Connection?[] _connections = new Connection?[2];
    private readonly string[] _connectionStatus = ["未连接", "未连接"];
    private readonly Dictionary<string, string> _results = [];
    private readonly Dictionary<string, string>[] _earResults = [[], []];
    private readonly Dictionary<string, byte[]> _responses = [];
    private readonly Queue<string> _logs = new();
    private readonly Lock _gate = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _run;
    private string _status = "就绪";
    private int _round;
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private long _lastPublish;
    private int _logDirty;
    private readonly List<string> _pendingRecordLogs = [];
    private bool _recording;
    private int _passed;
    private int _failed;
    private string _recordPath = "";
    private readonly string[] _earState = ["待执行", "待执行"];
    private string _activeCommand = "";
    private bool _continuous;

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        string? saved = await Api.Storage.ReadAsync(cancellationToken);
        if (saved is not null)
            try { _profile = ProductionProfile.FromJson(saved); }
            catch (Exception e) { _status = "配置加载失败 · " + e.Message; }
    }

    public override Task<PluginActivation> ActivateAsync(CancellationToken cancellationToken) => Task.FromResult(new PluginActivation
    {
        Menus = [new() { ContributionId = "open-production", Label = "耳机产测", CommandId = "open", PageId = "production", Order = 130 }],
        ToolPages = [new() { ContributionId = "production", Title = "耳机产测", PreferredWidth = 1000, PreferredHeight = 720, Nodes = Nodes() }],
    });

    public override async Task<CommandInvokeOutcome> OnCommandAsync(string commandId, string? arg, IReadOnlyDictionary<string, string> formValues, CancellationToken cancellationToken)
    {
        try
        {
            if (commandId is "stop" or "close")
            {
                _runCancellation?.Cancel();
                if (_run is not null) await _run;
                if (commandId == "close") for (int i = 0; i < 2; i++) await DisconnectAsync(i);
                _status = "已停止";
            }
            else if (commandId == "apply")
            {
                EnsureIdle();
                ProductionProfile updated = ProductionProfile.FromJson(formValues["profile"]);
                if (_connections.Any(c => c is not null) && JsonSerializer.Serialize(updated.Protocol) != JsonSerializer.Serialize(_profile.Protocol))
                    throw new InvalidOperationException("修改帧格式前请断开串口；命令和时序可直接保存");
                await Api.Storage.WriteAsync(updated.ToJson(), cancellationToken);
                _profile = updated;
                lock (_gate) { _results.Clear(); _responses.Clear(); foreach (var results in _earResults) results.Clear(); }
                _status = "已保存 · " + _profile.Name;
            }
            else if (commandId.StartsWith("connect-", StringComparison.Ordinal))
            {
                EnsureIdle();
                int index = commandId == "connect-left" ? 0 : 1;
                if (_connections[index] is not null) await DisconnectAsync(index);
                else await ConnectAsync(index, formValues["port"], formValues, cancellationToken);
            }
            else if (commandId == "raw-send")
            {
                EnsureIdle();
                int index = formValues["channel"] == "left" ? 0 : 1;
                Connection connection = _connections[index] ?? throw new InvalidOperationException("请先连接串口");
                byte[] bytes = Hex.Parse(formValues["hex"]);
                if (bytes.Length is < 1 or > 4096) throw new InvalidOperationException("HEX 需为 1–4096 字节");
                await Api.Serial.WriteLeasedPortAsync(connection.LeaseId, bytes, cancellationToken);
                lock (_gate) AddLog(index, "TX", bytes);
                _status = "已发送";
            }
            else if (commandId == "clear-batch")
            {
                EnsureIdle();
                lock (_gate) { _results.Clear(); foreach (var r in _earResults) r.Clear(); _responses.Clear(); _passed = _failed = 0; _earState[0] = _earState[1] = "待执行"; _activeCommand = ""; }
                _status = "本批次记录已清空，文件保留";
            }
            else if (commandId == "clear")
            {
                EnsureIdle();
                string side = formValues.GetValueOrDefault("channel") ?? "all";
                lock (_gate)
                {
                    if (side == "all") { _logs.Clear(); _results.Clear(); foreach (var results in _earResults) results.Clear(); }
                    else
                    {
                        _earResults[side == "left" ? 0 : 1].Clear();
                        string marker = side == "left" ? "  L  " : "  R  ";
                        string[] retained = _logs.Where(line => !line.Contains(marker, StringComparison.Ordinal)).ToArray();
                        _logs.Clear(); foreach (string line in retained) _logs.Enqueue(line);
                    }
                    _responses.Clear();
                }
                _status = "已清空";
            }
            else if (commandId is "once" or "loop" or "send" or "pair" or "continuous")
            {
                EnsureIdle();
                ProductionProfile snapshot = ProductionProfile.FromJson(formValues["profile"]);
                // Checkboxes are execution state, but connection framing stays immutable.
                if (JsonSerializer.Serialize(snapshot.Protocol) != JsonSerializer.Serialize(_profile.Protocol))
                    throw new InvalidOperationException("请先保存项目设置");
                List<ProductionCommand> selected = commandId == "pair" ? snapshot.Commands.Where(c => c.PairingStep && c.Enabled).ToList() : commandId == "send"
                    ? snapshot.Commands.Where(c => c.Id == formValues["id"]).ToList()
                    : snapshot.Commands.Where(c => c.Enabled).ToList();
                if (selected.Count == 0) throw new InvalidOperationException("请勾选命令");
                int? channel = formValues.TryGetValue("channel", out string? side) ? side == "left" ? 0 : side == "right" ? 1 : null : null;
                int[] channels = formValues.TryGetValue("channels", out string? choices) ? choices.Split(',').Where(s => s is "0" or "1").Select(int.Parse).Distinct().ToArray() : channel.HasValue ? [channel.Value] : [0, 1];
                if (channels.Length == 0) throw new InvalidOperationException("请选择测试通道");
                if (channel.HasValue) selected = selected.Where(c => c.Channel == EarChannel.Both || c.Channel == (channel == 0 ? EarChannel.Left : EarChannel.Right)).ToList();
                if (selected.Count == 0) throw new InvalidOperationException("此通道没有勾选的命令");
                if (commandId == "pair" && _connections.Any(c => c is null || c.Faulted)) throw new InvalidOperationException("组队需要连接左右耳");
                _profile = snapshot;
                await Api.Storage.WriteAsync(snapshot.ToJson(), cancellationToken);
                _runCancellation?.Dispose();
                _runCancellation = new CancellationTokenSource();
                _round = 0;
                lock (_gate) { _results.Clear(); foreach (var r in _earResults) r.Clear(); for (int i = 0; i < 2; i++) _earState[i] = channels.Contains(i) ? "执行中" : "未参与"; _activeCommand = ""; }
                _run = commandId == "continuous" ? RunContinuousAsync(selected, channels, _runCancellation.Token) : RunAsync(selected, commandId == "loop", _runCancellation.Token, channel, commandId == "pair", channels);
            }
            else if (commandId == "clear-log") { lock (_gate) _logs.Clear(); }
        }
        catch (Exception e) { _status = e.Message; }
        await PushAsync();
        return CommandInvokeOutcome.Complete();
    }

    private void EnsureIdle()
    {
        if (_run is { IsCompleted: false }) throw new InvalidOperationException("请先停止执行");
    }

    private async Task RunContinuousAsync(List<ProductionCommand> commands, int[] channels, CancellationToken ct)
    {
        await Task.Yield();
        _continuous = true;
        try
        {
            ProductionCommand probe = new() { Id = "fixture-presence", Name = "在位检测", Hex = "94 01", ResponseMatch = "94 02", TimeoutMs = 350,
                Fields = [new() { Type = FieldType.Mac, Offset = 5 }] };
            PlacementDetector detector = new();
            ProductionCommand? configuredProbe = _profile.Commands.FirstOrDefault(c => c.Steps.Count == 0 && c.Fields.Count == 1 && c.Fields[0].Type == FieldType.Mac && c.Hex == "94 01");
            if (configuredProbe is not null)
            {
                probe = System.Text.Json.JsonSerializer.Deserialize<ProductionCommand>(System.Text.Json.JsonSerializer.Serialize(configuredProbe, ProductionProfile.JsonOptions), ProductionProfile.JsonOptions)!;
                probe.Id = "fixture-presence";
            }
            probe.TimeoutMs = Math.Max(1000, probe.TimeoutMs);
            int insertionTimeout = probe.TimeoutMs;
            while (true)
            {
                long cycleStart = Environment.TickCount64;
                bool checkingRemoval = detector.WaitingForRemoval;
                probe.TimeoutMs = checkingRemoval ? _profile.RemovalTimeoutMs : insertionTimeout;
                ct.ThrowIfCancellationRequested();
                if (channels.Any(i => _connections[i] is null || _connections[i]!.Faulted)) throw new IOException("串口连接异常，请检查模块后重新启动");
                string? identity = "";
                bool absent = true;
                foreach (int side in channels)
                {
                    bool timedOut = false;
                    try
                    {
                        string mac = await ExecuteAsync(side, probe, ct);
                        if (mac.Replace(":", "", StringComparison.Ordinal).All(c => c == '0')) { identity = null; absent = false; }
                        else { identity = identity is null ? null : identity + mac + "/"; absent = false; }
                    }
                    catch (TimeoutException) { identity = null; timedOut = true; }
                    catch (InvalidDataException) { identity = null; absent = false; }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception e) { throw new IOException("在位检测失败 · " + e.Message, e); }
                    if (checkingRemoval && detector.ObserveRemoval(side, timedOut, channels)) break;
                }
                if (!checkingRemoval && detector.Observe(identity, absent))
                {
                    await RunAsync(commands, false, ct, channels: channels);
                    ct.ThrowIfCancellationRequested();
                    if (_status.StartsWith("执行失败", StringComparison.Ordinal) || _status.StartsWith("保存失败", StringComparison.Ordinal)) throw new IOException(_status);
                }
                _status = detector.WaitingForRemoval ? "本对完成 · 等待取走耳机" : "连续产测 · 等待放入耳机";
                if (!detector.WaitingForRemoval) for (int i = 0; i < 2; i++) _earState[i] = channels.Contains(i) ? "等待放入" : "未参与";
                await PushAsync(force: false);
                int pollMs = checkingRemoval || detector.WaitingForRemoval ? 100 : 500;
                int remaining = (int)Math.Max(0, pollMs - (Environment.TickCount64 - cycleStart));
                if (remaining > 0) await Task.Delay(remaining, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { _status = "连续产测已停止"; }
        catch (Exception e) { _status = "连续产测停止 · " + e.Message; }
        finally { for (int i = 0; i < 2; i++) if (_earState[i] is "等待放入" or "执行中") _earState[i] = "已停止"; _continuous = false; await PushAsync(running: false); }
    }

    private async Task ConnectAsync(int index, string portName, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        if (_connections.Any(c => string.Equals(c?.PortName, portName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("左右耳需选择不同端口");
        SerialLeaseResult lease = await Api.Serial.AcquireLeaseAsync("production-connection", portName, null, false, ct);
        if (lease.State != "leased") throw new IOException(lease.Message ?? "端口不可用");
        Connection connection = new(portName, lease.LeaseId, new FrameDecoder(_profile.Protocol));
        try
        {
            int baud = values.TryGetValue("baud", out string? baudText) ? int.Parse(baudText, System.Globalization.CultureInfo.InvariantCulture) : _profile.Protocol.BaudRate;
            string format = values.GetValueOrDefault("format") ?? "8N1";
            if (format.Length != 3 || format[0] is not ('7' or '8') || format[1] is not ('N' or 'E' or 'O') || format[2] is not ('1' or '2')) throw new InvalidOperationException("串口格式无效");
            await Api.Serial.OpenConfiguredPortAsync(new SerialLeaseIoRequest { LeaseId = lease.LeaseId, BaudRate = baud,
                DataBits = format[0] - '0', Parity = format[1] == 'E' ? "Even" : format[1] == 'O' ? "Odd" : "None", StopBits = format[2] == '2' ? "Two" : "One" }, ct);
            _connections[index] = connection;
            connection.Reader = ReadLoopAsync(index, connection);
            _connectionStatus[index] = $"{portName} · {baud} · {format}";
            _status = "就绪";
        }
        catch
        {
            connection.Cancellation.Dispose();
            await Api.Serial.ReleaseLeaseAsync(lease.LeaseId, CancellationToken.None);
            throw;
        }
    }

    private async Task DisconnectAsync(int index)
    {
        Connection? connection = _connections[index];
        if (connection is null) return;
        _connections[index] = null;
        lock (_gate) connection.Pending?.TrySetException(new IOException("连接已断开"));
        try
        {
            connection.Cancellation.Cancel();
            if (connection.Reader is not null) await connection.Reader;
        }
        finally
        {
            await Api.Serial.ReleaseLeaseAsync(connection.LeaseId, CancellationToken.None);
            connection.Cancellation.Dispose();
            _connectionStatus[index] = "未连接";
        }
    }

    private async Task ReadLoopAsync(int index, Connection connection)
    {
        try
        {
            while (!connection.Cancellation.IsCancellationRequested)
            {
                byte[] bytes = await Api.Serial.ReadLeasedPortAsync(connection.LeaseId, connection.Cancellation.Token);
                if (bytes.Length > 0) Receive(index, connection, bytes);
                if (_run is not { IsCompleted: false } && Volatile.Read(ref _logDirty) != 0) await PushAsync(force: false);
                await Task.Delay(10, connection.Cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (connection.Cancellation.IsCancellationRequested) { }
        catch (Exception e)
        {
            lock (_gate)
            {
                connection.Faulted = true;
                _connectionStatus[index] = "连接异常";
                connection.Pending?.TrySetException(new IOException("连接异常 · " + e.Message, e));
            }
            await PushAsync();
        }
    }

    private void Receive(int index, Connection connection, byte[] bytes)
    {
        try
        {
            lock (_gate)
            {
                AddLog(index, "RX", bytes);
                foreach (byte[] frame in connection.Decoder.Feed(bytes, connection.Command?.FixedResponseLength ?? 0))
                {
                    ProductionCommand? command = connection.Command;
                    if (command is null || !ProtocolCodec.Matches(command, frame)) continue;
                    if (!ProtocolCodec.CheckChecksum(command.Frame ?? _profile.Protocol, frame)) connection.Pending?.TrySetException(new InvalidDataException("CRC 错误"));
                    else connection.Pending?.TrySetResult(frame);
                }
            }
        }
        catch (Exception e)
        {
            lock (_gate)
            {
                connection.Faulted = true;
                connection.Pending?.TrySetException(new IOException("接收失败 · " + e.Message, e));
            }
        }
    }

    private async Task RunAsync(List<ProductionCommand> commands, bool loop, CancellationToken ct, int? channel = null, bool pairing = false, int[]? channels = null)
    {
        // Yield so _run is assigned before the first page publication.
        await Task.Yield();
        try
        {
            _recordPath = await Api.Serial.RecordProductionAsync(new() { Action = "begin", Project = _profile.Name }, ct);
            _recording = true;
            do
            {
                ct.ThrowIfCancellationRequested();
                _round++;
                bool[] earFailed = [false, false];
                bool roundFailed = false;
                List<(int Channel, string Name, string Result, bool Ok)> roundResults = [];
                lock (_gate)
                {
                    _results.Clear(); foreach (var r in _earResults) r.Clear();
                    for (int i = 0; i < 2; i++) _earState[i] = channels is null || channels.Contains(i) ? "执行中" : "未参与";
                    _responses.Clear();
                    foreach (ProductionCommand selected in commands)
                    {
                        _results.Remove(selected.Id);
                        if (channel != 1 && selected.Channel != EarChannel.Right) _earResults[0].Remove(selected.Id);
                        if (channel != 0 && selected.Channel != EarChannel.Left) _earResults[1].Remove(selected.Id);
                    }
                }
                foreach (ProductionCommand command in commands)
                {
                    ct.ThrowIfCancellationRequested();
                    _status = $"第 {_round} 轮 · {command.Name}";
                    _activeCommand = command.Id;
                    lock (_gate) _results[command.Id] = "执行中…";
                    await PushAsync(force: false);
                    if (command.Steps.Count > 0)
                    {
                        lock (_gate) _responses.Clear();
                        IReadOnlyList<CompoundResult> compound = await CompoundCommandRunner.RunAsync(command, channels ?? [0, 1],
                            async (side, step, token) =>
                            {
                                _status = command.Name + " · " + step.Name;
                                string response = await ExecuteAsync(side, step, token);
                                lock (_gate) _pendingRecordLogs.Add($"{DateTime.Now:HH:mm:ss.fff} STEP {step.Name} · {response}");
                                await FlushRecordAsync([]);
                                await PushAsync(force: false);
                                return response;
                            }, ct);
                        foreach (CompoundResult result in compound)
                        {
                            roundFailed |= !result.Success;
                            earFailed[result.Channel] |= !result.Success;
                            lock (_gate) _earResults[result.Channel][command.Id] = result.Text;
                            roundResults.Add((result.Channel, command.Name, result.Text, result.Success));
                        }
                        lock (_gate) _results[command.Id] = string.Join(" · ", compound.Select(r => r.Text));
                        await PushAsync(force: false);
                        await Task.Delay(command.DelayMs, ct);
                        continue;
                    }
                    List<string> values = [];
                    foreach (int index in (command.PairingStep ? command.Channel switch { EarChannel.Left => new[] { 0 }, EarChannel.Right => new[] { 1 }, _ => new[] { 0, 1 } } : channels ?? [0, 1]).Where(i => channels is null || channels.Contains(i)))
                    {
                        ct.ThrowIfCancellationRequested();
                        string result;
                        bool ok = true;
                        try { result = await ExecuteAsync(index, command, ct); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (TimeoutException) { ok = false; result = "应答超时"; if (pairing) { lock (_gate) _earResults[index][command.Id] = result; throw; } }
                        catch (Exception e)
                        {
                            result = "错误 · " + e.Message;
                            ok = false;
                            lock (_gate) _earResults[index][command.Id] = result;
                            if (pairing) throw;
                            if (_connections[index] is { Faulted: true })
                                try { await DisconnectAsync(index); } catch (Exception releaseError) { Api.Diagnostics.Warning(releaseError.Message); }
                        }
                        values.Add((index == 0 ? "左：" : "右：") + result);
                        if (result == "未连接") ok = false;
                        roundFailed |= !ok;
                        earFailed[index] |= !ok;
                        roundResults.Add((index, command.Name, result, ok));
                        lock (_gate) { _results[command.Id] = string.Join(" · ", values); _earResults[index][command.Id] = result; }
                        await PushAsync(force: false);
                    }
                    await FlushRecordAsync([]);
                    await Task.Delay(command.DelayMs, ct);
                }
                if (roundFailed) _failed++; else _passed++;
                for (int i = 0; i < 2; i++) _earState[i] = channels is not null && !channels.Contains(i) ? "未参与" : earFailed[i] ? "执行完成 · 不通过" : "执行完成 · 通过";
                string[] csv = roundResults.Select(r => string.Join(",", new[] { DateTime.Now.ToString("O"), _round.ToString(), r.Channel == 0 ? "左耳" : "右耳", r.Name, r.Result, r.Ok ? "通过" : "不通过", roundFailed ? "不通过" : "通过" }.Select(Csv))).ToArray();
                await FlushRecordAsync(csv);
                if (loop) await Task.Delay(Math.Max(10, _profile.LoopIntervalMs), ct);
            } while (loop);
            _status = $"完成 · 第 {_round} 轮";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _status = "已停止";
            for (int i = 0; i < 2; i++) if (_earState[i] == "执行中") _earState[i] = "已中止";
            if (_recording) await FlushRecordAsync([string.Join(",", new[] { DateTime.Now.ToString("O"), _round.ToString(), "", "已中止", "", "已中止", "已中止" }.Select(Csv))]);
            lock (_gate)
                foreach (string key in _results.Where(p => p.Value == "执行中…").Select(p => p.Key).ToList()) _results[key] = "已停止";
        }
        catch (Exception e) { _status = "执行失败 · " + e.Message; if (_recording) { _failed++; try { await FlushRecordAsync([string.Join(",", new[] { DateTime.Now.ToString("O"), _round.ToString(), "", "执行异常", e.Message, "不通过", "不通过" }.Select(Csv))]); } catch (Exception saveError) { _status += " · 保存失败：" + saveError.Message; } } }
        finally
        {
            if (_recording) { try { await FlushRecordAsync([]); } catch (Exception e) { _status = "保存失败 · " + e.Message; } }
            for (int i = 0; i < 2; i++) if (_earState[i] == "执行中") _earState[i] = "执行异常 · 不通过";
            _recording = false; await PushAsync(running: _continuous);
        }
    }

    private async Task<string> ExecuteAsync(int index, ProductionCommand command, CancellationToken ct)
    {
        Connection? connection = _connections[index];
        if (connection is null) return "未连接";
        if (connection.Faulted) throw new IOException("连接异常，请重新连接");
        byte[]? source = null;
        if (command.ResponseSourceId.Length > 0)
            lock (_gate)
                if (!_responses.TryGetValue($"{command.ResponseSourceChannel}:{command.ResponseSourceId}", out source))
                    throw new InvalidOperationException("来源命令未收到有效应答");
        ProtocolSettings frameSettings = command.Frame ?? _profile.Protocol;
        byte[] frame = ProtocolCodec.BuildFrame(frameSettings, command, source);
        TaskCompletionSource<byte[]> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            connection.Decoder = new FrameDecoder(frameSettings);
            connection.Command = command;
            connection.Pending = pending;
        }
        try
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                lock (_gate) AddLog(index, "TX", frame);
                await Api.Serial.WriteLeasedPortAsync(connection.LeaseId, frame, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or PluginHostException)
            { connection.Faulted = true; throw; }
            if (!command.WaitResponse) return "已发送";
            byte[] response = await pending.Task.WaitAsync(TimeSpan.FromMilliseconds(command.TimeoutMs), ct);
            string result = ProtocolCodec.Decode(command, response);
            lock (_gate) _responses[$"{(index == 0 ? EarChannel.Left : EarChannel.Right)}:{command.Id}"] = response;
            return result;
        }
        finally { lock (_gate) { connection.Pending = null; connection.Command = null; } }
    }

    private void AddLog(int index, string direction, byte[] bytes)
    {
        _logs.Enqueue($"{DateTime.Now:HH:mm:ss.fff}  {(index == 0 ? "L" : "R")}  {direction}  {Hex.Format(bytes)}");
        if (_recording) _pendingRecordLogs.Add(_logs.Last());
        Interlocked.Exchange(ref _logDirty, 1);
        while (_logs.Count > 300 || _logs.Sum(line => line.Length) > 64000) _logs.Dequeue();
    }
    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private async Task FlushRecordAsync(string[] csv)
    {
        string[] logs;
        lock (_gate) { logs = _pendingRecordLogs.ToArray(); _pendingRecordLogs.Clear(); }
        await Api.Serial.RecordProductionAsync(new() { Logs = logs, CsvRows = csv }, CancellationToken.None);
    }

    private IReadOnlyList<UiNode> Nodes(bool? running = null)
    {
        lock (_gate)
        {
            string json = JsonSerializer.Serialize(new
            {
                Profile = _profile, Running = running ?? (_run is { IsCompleted: false }), Status = _status,
                Left = _connectionStatus[0], Right = _connectionStatus[1],
                LeftConnected = _connections[0] is not null, RightConnected = _connections[1] is not null,
                Results = _results, LeftResults = _earResults[0], RightResults = _earResults[1], Logs = _logs.ToArray(),
                Passed = _passed, Failed = _failed, RecordPath = _recordPath,
                LeftState = _earState[0], RightState = _earState[1], ActiveCommand = _activeCommand,
            }, ProductionProfile.JsonOptions);
            return json.Chunk(3000).Select((chunk, index) => (UiNode)new UiTextNode
                { FieldId = "native-state-" + index, ReadOnly = true, Text = new string(chunk) }).ToArray();
        }
    }
    private async Task PushAsync(bool? running = null, bool force = true)
    {
        await _publishGate.WaitAsync();
        try
        {
            // Stay below the host's 240 updates/minute budget, including both readers.
            int delay = (int)Math.Max(0, 300 - (Environment.TickCount64 - _lastPublish));
            // Coalesce progress instead of stretching configured command timing.
            if (delay > 0 && !force) return;
            if (delay > 0) await Task.Delay(delay);
            _lastPublish = Environment.TickCount64;
            Interlocked.Exchange(ref _logDirty, 0);
            await Api.Ui.UpdateToolPageAsync("production", Nodes(running), CancellationToken.None);
        }
        finally { _publishGate.Release(); }
    }

    public override async Task DeactivateAsync(CancellationToken cancellationToken)
    {
        _runCancellation?.Cancel();
        if (_run is not null) await _run;
        for (int i = 0; i < 2; i++) await DisconnectAsync(i);
        _runCancellation?.Dispose();
    }

    private sealed class Connection(string portName, string leaseId, FrameDecoder decoder)
    {
        public string PortName { get; } = portName;
        public string LeaseId { get; } = leaseId;
        public FrameDecoder Decoder { get; set; } = decoder;
        public ProductionCommand? Command { get; set; }
        public TaskCompletionSource<byte[]>? Pending { get; set; }
        public bool Faulted { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Reader { get; set; }
    }
}
