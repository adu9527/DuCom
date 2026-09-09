using System.Text.Json;

Dictionary<string, string> options = new(StringComparer.Ordinal);
for (int index = 0; index + 1 < args.Length; index += 2) options[args[index]] = args[index + 1];
if (!options.TryGetValue("--ducom-task", out string? requestPath)
    || !options.TryGetValue("--ducom-result", out string? resultPath)
    || !options.TryGetValue("--ducom-cancel", out string? cancelPath)) return 2;

using JsonDocument request = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath));
string mode = request.RootElement.GetProperty("mode").GetString() ?? "success";
if (mode == "hang")
{
    while (!File.Exists(cancelPath)) await Task.Delay(25);
    await File.WriteAllTextAsync(resultPath, "{\"state\":\"cancelled\"}");
    return 1;
}
if (mode == "missing") return 0;
if (mode == "contradict")
{
    await File.WriteAllTextAsync(resultPath, "{\"state\":\"succeeded\",\"result\":\"bad\"}");
    return 4;
}
if (mode == "fail")
{
    await File.WriteAllTextAsync(resultPath, "{\"state\":\"failed\",\"error\":\"expected\"}");
    return 3;
}
await File.WriteAllTextAsync(resultPath, "{\"state\":\"succeeded\",\"result\":\"ok\"}");
return 0;
