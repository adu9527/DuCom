namespace DuCom.Plugins.Production;

public sealed record CompoundResult(int Channel, string Text, bool Success);

public static class CompoundCommandRunner
{
    public static async Task<IReadOnlyList<CompoundResult>> RunAsync(ProductionCommand group, int[] channels,
        Func<int, ProductionCommand, CancellationToken, Task<string>> execute, CancellationToken ct)
    {
        Dictionary<int, List<(string Id, string Name, string Value)>> values = channels.ToDictionary(i => i, _ => new List<(string, string, string)>());
        Dictionary<int, string> errors = [];
        bool crossChannel = group.Steps.Any(s => s.Enabled && s.PairingStep);
        if (crossChannel && (!channels.Contains(0) || !channels.Contains(1)))
            return channels.Select(i => new CompoundResult(i, "失败 · 此复合命令需要左右通道", false)).ToArray();
        foreach (ProductionCommand step in group.Steps.Where(s => s.Enabled))
        {
            ct.ThrowIfCancellationRequested();
            int[] targets = step.PairingStep ? step.Channel == EarChannel.Right ? [1] : step.Channel == EarChannel.Left ? [0] : channels : channels;
            foreach (int side in targets)
            {
                if (errors.ContainsKey(side)) continue;
                try
                {
                    string value = await execute(side, step, ct);
                    if (value == "未连接") throw new IOException(value);
                    values[side].Add((step.Id, step.Name, value));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    string error = $"失败 · {step.Name}：{(e is TimeoutException ? "应答超时" : e.Message)}";
                    if (crossChannel) return channels.Select(i => new CompoundResult(i, error, false)).ToArray();
                    errors[side] = error;
                }
            }
            await Task.Delay(step.DelayMs, ct);
        }
        return channels.Select(side =>
        {
            if (errors.TryGetValue(side, out string? error)) return new CompoundResult(side, error, false);
            foreach (ResultComparison rule in group.Comparisons)
            {
                string? actual = values[side].LastOrDefault(v => v.Id == rule.ActualStepId).Value;
                int expectedSide = rule.CompareOppositeChannel ? 1 - side : side;
                string? expected = values.TryGetValue(expectedSide, out var other) ? other.LastOrDefault(v => v.Id == rule.ExpectedStepId).Value : null;
                if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected) || actual.Replace(":", "", StringComparison.Ordinal).All(c => c == '0') || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    return new CompoundResult(side, "失败 · 读回结果与目标不匹配", false);
            }
            string text = group.OutputStepId.Length > 0
                ? values[side].LastOrDefault(v => v.Id == group.OutputStepId).Value ?? "失败 · 输出步骤未在此通道执行"
                : group.SuccessText.Length > 0 ? group.SuccessText : string.Join(" · ", values[side].Select(v => v.Name + "：" + v.Value));
            return new CompoundResult(side, text, !text.StartsWith("失败", StringComparison.Ordinal));
        }).ToArray();
    }
}
