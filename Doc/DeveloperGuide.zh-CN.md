# DuCom 开发者文档

[English](DeveloperGuide.en-US.md) · [用户手册](UserManual.zh-CN.md) · [网页版](Web/developer-guide-zh.html) · [GitHub](https://github.com/adu9527/DuCom)

> 技术栈：.NET 10、WPF、C# 14、WPF-UI、CommunityToolkit.Mvvm  
> 文档更新：2026-09-02

## 1. 项目目标

DuCom 是 Windows 平台的开源串口工具，核心目标是在嵌入式设备持续高速输出时保持接收完整、日志可靠和 UI 可操作。工程将串口 I/O、解析、持久化和界面展示分离，避免串口回调直接驱动 UI。

仓库：https://github.com/adu9527/DuCom

## 2. 开发环境

- Windows 10/11
- .NET 10 SDK
- Visual Studio（推荐，需安装“.NET 桌面开发”工作负载）或支持 C#/.NET 的 IDE
- Git
- 可选：真实串口硬件、USB 转串口适配器、com0com

验证环境：

```powershell
dotnet --info
git --version
```

## 3. 获取源码与构建

```powershell
git clone https://github.com/adu9527/DuCom.git
cd DuCom\DuCom
dotnet restore
dotnet build
dotnet run --project src\DuCom\DuCom.csproj
```

运行全部测试：

```powershell
dotnet test DuCom.slnx
```

运行 Core 测试：

```powershell
dotnet test tests\DuCom.Core.Tests\DuCom.Core.Tests.csproj
```

## 4. 仓库结构

```text
DuCom/                         仓库根目录
├─ README.md                   项目首页
├─ Doc/                        面向用户和贡献者的中英文文档
├─ Image/                      README 截图
└─ DuCom/                      .NET 解决方案根目录
   ├─ DuCom.slnx
   ├─ Directory.Build.props
   ├─ Directory.Packages.props
   ├─ src/DuCom/               WPF 应用、窗口、ViewModel、服务与资源
   ├─ src/DuCom.Core/          无 WPF 依赖的核心逻辑
   ├─ tests/DuCom.Core.Tests/  xUnit 测试
   ├─ tools/                   负载与辅助工具
   ├─ benchmarks/              BenchmarkDotNet 基准
   └─ docs/                    ADR、设计、测试和内部行为资料
```

`Doc/` 是对外文档，`DuCom/docs/` 是工程内部设计与验证资料，两者职责不同。

## 5. 架构概览

### 5.1 分层

- **DuCom.Core**：串口生命周期、接收管线、解析、搜索、发送、存储、Telnet、监控与持久化基础能力。
- **DuCom**：WPF 视图、ViewModel、应用服务、配置装配、本地化和 Windows 集成。
- **Tests**：Core 行为和关键边界的自动化验证。
- **Tools/Benchmarks**：确定性负载、完整性检查和性能基准。

Core 不应引用 WPF 类型。可测试的协议、解析、存储和状态机逻辑优先放入 Core。

### 5.2 接收数据流

```text
SerialPort callback
  → copy bytes / bounded receive pipeline
  → incremental formatter (STR / HEX / ANSI)
  → session sink
       ├─ asynchronous file log
       └─ budgeted display store
            → UI reads snapshots on render cadence
```

关键约束：

1. 串口回调只做必要的数据复制和入队。
2. 不在回调中解析、写文件或调用 UI Dispatcher。
3. 日志写入不依赖 UI 是否跟随或是否裁剪。
4. 显示存储有预算，磁盘日志保持完整。
5. 串口打开、关闭和异常断开通过显式生命周期串行化。

### 5.3 发送数据流

发送内容由 STR/HEX 编码器生成字节，附加可选换行，再由会话异步写入串口。成功的发送记录进入 TX 日志。命令组在此基础上增加多端口目标、延时、结果匹配、超时和循环控制。

## 6. 关键目录与职责

### `src/DuCom.Core`

- `Ports/`：串口设置、传输和生命周期。
- `Pipeline/`：接收块和有界接收管线。
- `Parsing/`：STR/HEX、ANSI、样式、高亮与过滤。
- `Storage/`：预算化行存储和显示快照。
- `Sending/`：发送编码、历史、命令脚本和多目标运行器。
- `Search/`：安全搜索和正则超时。
- `Diagnostics/`：负载指标、WatchDog、变量与内存评估。
- `Telnet/`：Telnet 服务、认证和协议处理。
- `Persistence/`：原子文件存储和迁移基础设施。

### `src/DuCom`

- `ViewModels/`：界面状态和命令，不应承载可复用协议算法。
- `Services/`：WPF/Windows 侧服务、配置存储和 Core 适配。
- `Resources/Languages/`：`zh-CN.xaml` 与 `en-US.xaml`。
- `Resources/DesignTokens*`：主题颜色、间距和控件样式。
- `Controls/`、`Behaviors/`、`Converters/`：可复用 UI 组件。
- `MainWindow`、`SessionWorkspace`：主窗口和会话工作区。
- `ToolCenterWindow`：虚拟串口、Telnet、监控、命令组等工具入口。

## 7. MVVM 与命令

项目使用 `CommunityToolkit.Mvvm`：

- `[ObservableProperty]` 生成属性通知。
- `[RelayCommand]` 生成命令。
- 长时间 I/O 使用异步命令和 `CancellationToken`。
- ViewModel 不直接实现复杂协议；将纯逻辑下沉到 Core。
- 窗口打开、文件对话框、剪贴板等 Windows 行为可由应用层服务或薄窗口代码承接。

新增功能时，先定义可测试的状态/契约，再连接 UI，避免在 XAML 后置代码中堆积业务逻辑。

## 8. 本地化

可见文本应放入：

```text
src/DuCom/Resources/Languages/zh-CN.xaml
src/DuCom/Resources/Languages/en-US.xaml
```

使用相同资源键，并在 XAML 中通过 `{DynamicResource Key}` 引用。新增或删除键时必须同步两个语言文件。避免硬编码仅一种语言的状态提示。

## 9. 配置与用户数据

主要数据保存在用户本地应用数据目录下的 DuCom 文件夹，包含设置、快捷键、高亮规则、历史、命令组、WatchDog 和监视规则等 JSON 文件。持久化应遵循：

- 原子写入，避免异常退出留下半个文件。
- 新字段提供默认值和向后兼容。
- 不记录 Telnet 明文密码；密码仅保存在当前运行内存。
- 配置导入应验证字段并报告 imported/skipped/invalid。
- 日志和诊断文件不得提交仓库。

## 10. 测试策略

- **单元测试**：编码、解析、搜索、正则超时、状态机、存储预算、迁移映射。
- **集成测试**：串口会话管线、日志完整性、关闭顺序、多目标命令。
- **Smoke 测试**：本地化键、设置迁移、工具页路由、分屏行为。
- **硬件验证**：真实 USB 串口、拔插、端口占用、错误帧、高波特率长跑。
- **负载测试**：使用确定性负载生成器，比较生成字节、接收字节、日志字节和显示裁剪指标。

提交前至少运行：

```powershell
dotnet build DuCom.slnx --configuration Release
dotnet test DuCom.slnx --configuration Release
```

涉及 XAML、本地化、串口和发布行为时，还应进行手工启动验证。

## 11. 性能与并发规范

- 不要在 `SerialPort.DataReceived` 中阻塞。
- UI 更新应批量化，并限制频率。
- 磁盘写入、搜索和导出放到后台执行。
- 高吞吐路径避免逐字节对象分配。
- 所有后台循环必须支持取消和幂等停止。
- 对用户可控正则设置超时。
- 共享串口生命周期通过明确锁或状态机串行化。
- 显示裁剪不等于接收丢失，指标和提示必须区分两者。

## 12. 新增功能流程

1. 在 Issue 中描述场景、输入输出和边界。
2. 判断逻辑属于 Core 还是 WPF 应用层。
3. 为纯逻辑先补测试或测试夹具。
4. 实现最小行为，保持取消、错误和持久化语义明确。
5. 添加中英文资源。
6. 更新用户手册、开发者文档或内部 ADR。
7. 运行 Debug/Release 构建与测试。
8. 进行必要的硬件/高负载手测。
9. 向 `test` 分支提交 Pull Request。

## 13. 代码风格

- C# 14、可空引用启用、隐式 using 启用。
- 类型和公开成员使用 PascalCase；局部变量和参数使用 camelCase；私有字段使用 `_camelCase`。
- 对外契约明确参数验证和异常语义。
- 不吞异常；可恢复错误转为状态，诊断细节写系统日志。
- 注释解释“不明显的原因和约束”，而不是重复代码。
- Core 保持零 UI 依赖。

## 14. 分支、提交与 PR

- `main`：稳定代码和 Release，由维护者管理。
- `test`：日常开发与外部贡献的目标分支。

推荐从 `test` 创建功能分支。PR 应包含：问题背景、变更摘要、测试证据、界面截图（如有）、兼容性/性能影响以及文档更新。不要提交 `bin`、`obj`、发布 EXE、日志、数据库或个人配置。

## 15. Release 发布

DuCom 面向用户发布为自包含单文件 EXE，当前实测约 78 MB（不同版本可能略有浮动），用户下载后直接双击，无需安装 .NET。

Visual Studio 发布建议：

| 配置 | 值 |
|---|---|
| Configuration | Release |
| Runtime | win-x64 |
| Deployment | Self-contained |
| Produce single file | Enabled |
| ReadyToRun | 可选，需评估体积与启动速度 |

命令行示例：

```powershell
dotnet publish src\DuCom\DuCom.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true
```

发布前检查版本、关于页发布日期、用户手册、README 链接、Release Notes、病毒扫描结果和干净机器启动。发布产物作为 GitHub Release 附件，不提交 Git。

## 16. 文档维护

- `Doc/*.md`：GitHub 阅读和版本审查。
- `Doc/Web/*.html`：浏览器阅读和 GitHub Pages。
- 中英文文档应保持章节和事实同步。
- 菜单“帮助”指向仓库中的当前语言用户手册。
- 改变用户可见行为时，同一 PR 更新用户手册。
- 改变架构边界或重要取舍时，在 `DuCom/docs/decisions/` 增加 ADR。

## 17. 问题反馈

- QQ 群：`1107820408`
- Issues：https://github.com/adu9527/DuCom/issues
- 仓库：https://github.com/adu9527/DuCom

安全问题、凭据和包含设备机密的日志不要直接公开提交；先做脱敏并以最小复现数据说明。
