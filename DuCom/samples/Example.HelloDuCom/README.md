# Example.HelloDuCom：第一个 DPP/1 插件

这是最小的第三方插件：订阅一个打开的串口会话，统计收到的字节数，在工具页上实时显示。三个文件，照着改就能变成自己的插件：

- `Example.HelloDuCom.csproj` — 引用 `DuCom.Plugin.Sdk`，把清单复制到输出目录
- `plugin.manifest.json` — 声明插件身份、能力和权限
- `HelloPlugin.cs` — 插件本体，继承 `DuComPlugin`

协议本身在 [Doc/PluginProtocol.DPP1.zh-CN.md](../../../Doc/PluginProtocol.DPP1.zh-CN.md)，目前是 v0.9，随 DuCom 0.0.0.8 提供运行时和 SDK，还没冻结。本文只讲动手部分。

## 准备

- Windows 10 及以上，.NET 10 SDK
- 能跑的 DuCom（0.0.0.8 或更高，版本在关于窗口里看）

## 清单怎么写

```json
{
  "manifestVersion": 1,
  "id": "org.example.hello-ducom",
  "name": "Hello DuCom",
  "version": "1.0.0",
  "protocolVersion": "1.0",
  "minHostVersion": "0.0.1",
  "entryAssembly": "Example.HelloDuCom.dll",
  "entryType": "Example.HelloDuCom.HelloPlugin",
  "runtime": { "framework": "net10.0", "rid": "win-x64" },
  "capabilities": [ "menu", "tool-page" ],
  "permissions": [ "serial.read", "storage.own" ],
  "defaultCulture": "en-US"
}
```

几个字段容易写错：

- `id` 是反域名风格，至少两段，小写。`com.ducom.*` 留给官方插件，别用。
- `entryAssembly` 只是文件名，不能带路径；`entryType` 是插件类的全名。
- `capabilities` 是你要注册的扩展点（菜单、工具页、设置面板等），`permissions` 是你要经宿主代理访问的资源（串口读、私有存储、文件选择等）。两者分开校验，声明了没实现的能力会被拒绝，权限要用户确认后才生效。
- 版本兼容看两道：`protocolVersion` 过了还要过 `minHostVersion`。拿不准就填你现在用的 DuCom 版本。

能力和权限的完整清单以协议文档 §10 为准，未知项一律拒绝。

## 代码结构

插件跑在独立进程里，界面是宿主渲染的声明式描述，不是 WPF 控件。生命周期四个入口：

```csharp
public sealed class HelloPlugin : DuComPlugin
{
    public override async Task InitializeAsync(CancellationToken ct)
    {
        // 启动阶段。这里读自己的私有存储（storage.own 权限），把上次的计数捡回来。
        string? stored = await Api.Storage.ReadAsync(ct);
    }

    public override async Task<PluginActivation> ActivateAsync(CancellationToken ct)
    {
        // 激活阶段。订阅串口、注册菜单和工具页。
        var sessions = await Api.Serial.ListSessionsAsync(ct);
        await Api.Serial.SubscribeAsync(sessions[0].SessionId, ct);
        Api.Serial.DataReceived += (_, args) => { /* args.Data 是字节块 */ };

        return new PluginActivation
        {
            Menus = [new MenuContribution { ContributionId = "open-hello", Label = "Hello DuCom", PageId = "hello" }],
            ToolPages = [new ToolPageContribution { ContributionId = "hello", Title = "Hello DuCom", Nodes = [...] }],
        };
    }

    public override Task<CommandInvokeOutcome> OnCommandAsync(string commandId, string? arg, ...)
    {
        // 页面上按钮的 CommandId 会回到这里。
        return Task.FromResult(CommandInvokeOutcome.Complete());
    }

    public override Task DeactivateAsync(CancellationToken ct) => Api.Serial.UnsubscribeAllAsync(ct);
}
```

页面更新用 `Api.Ui.UpdateToolPageAsync(pageId, nodes)`，可用的节点有标签、按钮、复选框、下拉、滑轨、列表、进度等，见协议文档 §11。长任务要先返回任务句柄、支持取消、按进度报告，超时会被停用。

几个约束写代码时就要记住：

- 串口旁路是有界队列，消费太慢时宿主会丢最旧的块并报告 gap，不会为你暂停接收。
- 你的存储有配额，宿主代理写入。
- 不要想着读宿主目录、开网络连接、起子进程——默认都不允许，需要什么就在清单里申请对应权限。

## 打包

```powershell
cd DuCom
powershell -File scripts\pack-example-plugin.ps1
```

产物在 `samples\Example.HelloDuCom\artifacts\org.example.hello-ducom-1.0.0.dcpack`。`.dcpack` 就是 ZIP：入口 DLL、`DuCom.Plugin.Sdk.dll`、`plugin.manifest.json` 三个文件放在压缩包根目录。自己的插件改一下脚本里的路径就能用。

## 安装和调试

安装：DuCom 里打开「设置 → 插件 → 安装」，选 `.dcpack`，确认它申请的权限。插件装在 `%LocalAppData%\DuCom\Plugins`，同一 id 同时只有一个版本生效。

调试的几个抓手：

- 宿主诊断日志：`%LocalAppData%\DuCom\Logs`，插件启停、故障、超时都记在这里。
- 插件进程就是宿主自己带 `--plugin-worker` 参数起的子进程。要断点调试，在 Visual Studio 里「调试 → 附加到进程」，选命令行带 `--plugin-worker` 的那个 DuCom.exe。
- worker 启动跟踪：`%TEMP%\ducom-worker-trace-<pid>.log`，能看出它有没有连上宿主管道。
- 插件崩溃不会影响主程序收发，但该插件会被持久禁用并弹一次警告，重新启用要你手动来。这符合预期，不是安装坏了。

## 接下去看什么

- 协议细节和兼容规则：`Doc/PluginProtocol.DPP1.zh-CN.md`
- 现成的实现参考：`src/DuCom.Plugins.*` 下七个内置插件，走的是同一套清单和权限流程
- 打包后的自检：`scripts/run-plugin-gates.ps1`（清单校验、插件冒烟和示例包构建）
