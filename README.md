# DuCom

DuCom 是一款面向嵌入式设备高吞吐日志的 Windows 串口通信与调试工具。

项目基于 .NET 10 与 WPF 构建。串口 I/O、日志落盘和 UI 渲染相互解耦，目标是在设备持续高速输出时仍保持界面可用，并避免静默丢失已接收的日志记录。
![Uploading c2c2536d9860e2dd74d618fef4b58d45.png…]()

## 当前状态

DuCom 当前为初始版本。核心功能已经稳定，高波特率长时间运行、不同 USB 转串口驱动、热插拔和网络工具等老化验证仍在持续进行。

## 功能

- 多串口独立会话，支持标签页和分屏视图。
- STR/HEX 收发、时间戳、ANSI/VT 显示、过滤、高亮和搜索。
- 异步 UTF-8 会话日志落盘、可配置滚动和每会话日志文件定位。
- 按帧拉取、预算化的日志显示，适用于持续高速串口输出。
- 发送历史、命令组、换行策略和定时命令执行。
- 每端口设置、显示偏好、主题、中英文界面和可配置快捷键。
- 迷你日志窗口、虚拟串口管理、Telnet Shell/串口桥接、WatchDog 规则和运行监控。
- 支持只读导入兼容串口工具的既有数据。

## 环境要求

- Windows 10 或更高版本。
- 从源码构建需要 .NET 10 SDK。
- 推荐使用 Visual Studio 进行 WPF 开发。

## 构建与运行

```powershell
cd DuCom
dotnet build
dotnet run --project src\DuCom
```

运行测试：

```powershell
cd DuCom
dotnet test
```

## 发布

在 Visual Studio 中右键 `DuCom` 项目，选择**发布**。生成可独立运行的 Windows 版本时，使用以下设置：

- 配置：`Release`
- 目标运行时：`win-x64`
- 部署模式：`Self-contained`（自包含）
- 生成单个文件：启用
- ReadyToRun：可选

发布生成的可执行文件应作为 GitHub Release 附件上传，不应直接提交到源码仓库。

## 项目结构

```text
DuCom/
  src/DuCom/                 WPF 主程序、视图、ViewModel 和应用装配
  src/DuCom.Core/            无 UI 依赖的串口、管线、日志和存储逻辑
  tests/DuCom.Core.Tests/    Core 的 xUnit 测试
  tools/DuCom.LoadGenerator/ 负载与完整性测试工具
  benchmarks/                BenchmarkDotNet 基准测试
  docs/                      架构决策、设计和验证资料
```

## 设计原则

- 串口回调只将接收数据复制到有界管线。
- 日志写入独立于显示裁剪和 UI 渲染。
- UI 按渲染节奏读取快照，而不是每个数据包都投递一次 UI 消息。
- 显示存储受内存预算约束，超长物理行会安全分段。
- 串口打开和关闭通过明确的生命周期串行化。

## 许可证

DuCom 使用 [GNU General Public License v3.0](LICENSE) 许可证。

## 贡献

欢迎提交 Issue 和 Pull Request。请保持 Core 不依赖 WPF，遵守串口接收管线的不变量，为纯逻辑修改补充测试；不要提交生成的 `bin`、`obj`、日志、数据库、报告或发布产物。
