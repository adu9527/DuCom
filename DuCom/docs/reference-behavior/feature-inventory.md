# M2 Feature Inventory — 2026-08-28（最终自动化完结审查）

对照 SuperCom（只读参照）的完整功能库存。状态含义：✅ 已实现并有自动化证据；🟡 部分实现（说明缺口）；❌ 未实现（明确报告）；🚫 排除（经批准）。

| # | 功能 | 参考源码（SuperCom-master_260825/…） | 可见行为要点 | 持久化字段 | DuCom 实现路径 | 自动测试 | 手测状态 | 完成状态 |
|---|---|---|---|---|---|---|---|---|
| 1 | 多会话 Tab / 独立连接 | MainWindow.xaml.cs 会话管理 | 每端口一个会话、独立开关、端口行按钮 | — | MainViewModel.Sessions / SessionViewModel | 管线集成测试 | 已手测（真机 COM7） | ✅ |
| 2 | 分屏与会话排序 | MainWindow 分屏行为 | 横向/纵向双分屏、Tab 重排；× 完整 Close+Dispose+移除；另提供退出分屏并移回主区 | settings.json 保存布局、顺序和比例 | MainWindow 拖放 + AssignRightPaneAsync/CloseRightPane | split smoke；ADR-0004 测试族 | 待真机双口 | ✅（Quad 为记录差异） |
| 3 | 接收显示 STR/HEX、软换行 | StatefulReceiveFormatter 参照 | STR/HEX 增量格式化、4096 软换行、时间戳前缀 | 日志为格式化文本 | Core Parsing/Pipeline | formatter 测试族 | 已手测 | ✅ |
| 4 | ANSI/VT 显示 | 新增原创 | 颜色/粗体/下划线/反显/256色/RGB/跨段容错；HEX 模式不误解析 | 无新增（仅显示层） | Core AnsiParser + StyledRunsTextBlock 渲染控件（单 TextBlock Inlines，支持自动换行与不可见字符叠加） | AnsiParserTests 30+；StyledTextComposerTests 9 | 待真机彩色设备 | 🟡 剩真机色彩验证 |
| 5 | 高亮/过滤规则 | Highlight* 行为对齐 | Contains/Regex、大小写、前景/背景/粗体/斜体、逐端口过滤开关；Regex 100ms 超时，过滤超时 fail-open | highlight-filter-rules.json + per-port settings | Core HighlightFilterRule* + 规则编辑窗口 | Matcher/Service/Validation/Timeout/round-trip | 已手测基础 | ✅ |
| 6 | 会话内搜索 | — | 文本/Regex/大小写、上一个/下一个、后台+防抖；结果实际滚动到逻辑行/分段；Regex 超时双语提示 | — | Core Search + SearchViewModel + search scroll behavior | LogSearchEngine/Timeout/SafeExecutor | 已手测 | ✅（全量索引属 M3） |
| 7 | 发送 STR/HEX + 换行策略 | SendCustomCommand 行为 | None/CR/LF/CRLF、TX 记录写入日志、非法 HEX 双语提示、**IO 发送失败双语状态** | settings.json 默认项 | SerialSession.SendAsync + SendPayloadEncoder | encoder/session TX 测试 | 已手测 | ✅ |
| 8 | 发送历史 | 输入框历史行为 | 上/下键导航、草稿保留、去重、容量100、Enter 发送/Shift+Enter 换行 | send-history.json | Core SendHistory/SendHistoryNavigator + SendHistoryFileService | 13 项测试 | 待手测往返 | 🟡 手测待做 |
| 9 | 发送历史窗口（搜索/删除） | Window 历史列表 | 即时搜索、单条删除、清空、填回发送框 | 同上 | ToolCenter「发送历史」页 | history 测试族 | 待手测 | ✅（待手测勾选） |
| 10 | 高级脚本化命令（命令组） | AdvancedSend.cs 全套 | CRUD/导入导出、延时、结果检查、超时、循环、停止、逐端口状态；多选目标且每轮动态读取 | command-scripts.json + settings.json | Core runner + multi-target host + ToolCenter | 多目标、独立 tail、错误隔离、迁移测试 | 待真机手测 | ✅（待手测勾选） |
| 11 | 迷你日志窗口（每端口） | BaseWindow 迷你窗 | 固定端口且切换主 Tab 后继续按帧更新；独立 Follow/置顶/位置/STR·HEX发送/换行/清屏/保存 | mini-log-preferences.json | MiniLogWindow + PortWindowRegistry + 主渲染帧去重 | registry/偏好测试 | 待真机 | ✅（待手测勾选） |
| 12 | 冻结/清屏/跟随 | 显示控制 | 清屏仅清显示；冻结停跟随不停接收；eviction 计数提示（双语） | 无 | SessionViewModel.ClearDisplay/FollowEnd + EvictionDisplay | store clear 不影响日志的集成测试 | 已手测 | ✅ |
| 13 | 端口生命周期/故障显示 | PortTabItem | 四态文字+颜色点冗余、**Frame/RXOver/Overrun/RXParity/TXFull 双语告警**、拔出→fault 横幅、开失败不留死 Tab | ducom.log 诊断 | PortLifecycle + Warnings + 状态转换器 + SerialWorkspaceSession 本地化映射 | lifecycle 测试族 + ADR-0004 关闭顺序测试 7 项 | 真机 Frame 告警已验证 | ✅ |
| 14 | 每端口设置记忆 | 各端口 Tab 配置 | 串口、DTR/RTS/DiscardNull、接收、时间戳、日志 profile、显示预算、发送、Follow、过滤逐端口保存；捕获型设置重开应用 | settings.json PortOverrides | MainViewModel per-port snapshot/apply/rebuild | settings/split smoke + migration tests | 待手测 | ✅（待手测勾选） |
| 15 | 快捷键系统 | Tools-Shortcuts + HotKey | 清单/搜索/编辑捕获/冲突检测/恢复默认/JSON 持久化 | shortcuts.json | Services/Shortcuts + ShortcutEngine 白名单 | 33 项测试 | GUI 捕获待真机 | ✅ |
| 16 | 设置导入导出 | 全局配置备份 | 导出/导入 ConfigurationSnapshot JSON | ducom-settings.json | MainViewModel Import/ExportConfiguration | — | 部分 | ✅ |
| 17 | 工具中心壳 | Window_Tools | 设置/主题/侧栏/快捷键/插件/监视器+变量规则/com0com/ASCII/参考/Telnet+桥接/命令组/历史/看门狗；**页 key↔tab index 集中常量（ToolCenterPages），tools smoke 逐页校验选中页与页头身份一致** | 各自文件 | ToolCenterWindow/ViewModel + ToolCenterPages | tools smoke（10 页逐页身份校验） | 部分 | ✅ |
| 18 | VirtualPort 完整管理 | Core\Entity\VirtualPort* | com0com 配对解析、安装/删除/改参数；支持浏览和持久化自定义 setupc.exe；10s 超时、verb 白名单、不静默提权 | settings.json 保存 setupc 路径 | Com0ComService + parser + VirtualPort 页 | parser/verb/path 测试 | 待管理员真机 | ✅（待手测勾选） |
| 19 | Telnet shell / 串口桥接 | TelnetServer* | 应用级单实例；help/clear/exit/quit/sendtoall；可选认证；远程监听强制认证；默认 loopback；增量 UTF-8、IAC 过滤、8KB 上限、慢客户端隔离、双向桥接 | settings.json 保存非秘密项，密码仅内存 | Core Telnet + TelnetBridgeService | Telnet 34 项专项测试 | 待网络/真机 | ✅（待手测勾选） |
| 20 | WatchDog / MemoryDog | WatchDog/*.cs | 内容心跳规则独立运行；私有内存阈值 10s 采样并显式告警，不自动杀进程或清空显示 | watchdog-rules.json + settings.json | WatchdogService + PrivateMemoryMonitorService | evaluator/worker/settings smoke | 待真机长跑 | ✅（待手测勾选） |
| 21 | 插件执行 | PluginLoader | 仅 manifest/DLL 元数据浏览；**禁止加载第三方 DLL 执行**（安全模型未批） | 插件目录枚举 | RefreshPlugins | — | ✅（有意只读） | ✅ |
| 22 | ASCII 表/参考资料 | 内置表 | DEC/HEX/ASCII 三列 + 文档入口 | — | AsciiRows | — | ✅ | ✅ |
| 23 | 文件菜单保存 | SaveLog/SaveLogAsBin/SaveLogAsHex | **文本/HEX/二进制三种保存，均后台线程写入可见行快照；加载日志以系统查看器打开（设计差异，见下）** | — | SaveVisibleLog/AsHex/AsBinary + HexRepresentation | HexRepresentationTests 12 项 | 待手测 | 🟡 加载日志为外部打开（差异） |
| 24 | 编辑菜单 | Edit 菜单 | 复制/清屏/跟随/JSON格式化/合并行/**剪贴板 HEX↔文本/时间戳→本地时间/自动换行/行号/当前行高亮/显示CR·LF·空格·Tab/字号**；只读为结构性恒真（显示层无编辑） | settings.json 显示选项字段 | Edit 菜单 + DisplayTextTransform + StyledRunsTextBlock + 选项持久化 | DisplayTextTransformTests 13 项 | 待手测 | 🟡 字体族固定（设计差异） |
| 25 | About/反馈/帮助 | About 窗口 | About/邮件反馈/文档入口/实时时钟 | — | AboutWindow | about smoke | ✅ | ✅ |
| 26 | SuperCom 数据迁移（只读导入） | user_data.sqlite + app_configs/JSON | 数据库或数据目录只读发现；导入串口及逐端口偏好、历史、多端口目标、命令组、高亮样式、隐藏端口和 common settings；字段级 imported/skipped/invalid；原子提交与回滚 | 写入 DuCom 各 JSON 存储 | Core Persistence + AtomicFileStore + MainViewModel | SQLite/目录 fixture、源字节不变、原子提交、settings smoke | 待真实副本手测 | ✅（待手测勾选） |
| 27 | 运行/变量监视器 | VarMonitor + 运行监控 | CPU/工作集/私有内存/GC/线程 + **变量规则（name/port/regex/enabled/order）1s 快照评估、首捕获组取值、实时值表、CSV 导出；Regex 100ms 超时** | monitor-rules.json | VariableMonitorEvaluator/Service + 监视器页 | VariableMonitorEvaluatorTests 9 项 | 待真机 | 🟡 无绘图（诚实部分：数值表+导出） |
| 28 | 桌面看板娘 PetManager 等 | — | — | — | — | — | 🚫 排除（批准例外） |
| 29 | ja-JP 语言 | — | — | — | LocalizationResourceTests 保证不存在且双语键一致 + **重复键防护** | ✅ 测试锁定 | 🚫 排除 |

## 本轮（2026-08-28 GLM 长任务）新增实现索引

Core：`Presenting/PortWindowRegistry`、`Parsing/DisplayTextTransform`、`Sending/HexRepresentation`、`Diagnostics/WatchdogRule/WatchdogEvaluator`、`Diagnostics/VariableMonitorEvaluator`、`HighlightFilterEvaluation.HasRegexTimeout`、`LogSearchEngine` 超时语义、`SerialSession` ADR-0004 关闭顺序、`ReceivePipeline.StopAsync` 幂等。
App：`Controls/StyledRunsTextBlock`、`Services/{WatchdogService,WatchdogRuleStore,VariableMonitorService,VariableMonitorRuleStore,TelnetBridgeService,Com0ComService,SuperComImportService}`、`MiniLogWindow` 重构为每端口窗口、Edit 菜单与显示选项、文件菜单三种保存、串口告警双语映射、发送失败状态、右 pane 完整关闭。
测试新增：HighlightFilter timeout 11、LogSearchEngine timeout 8、PortWindowRegistry 9、SerialSessionCloseOrder 7、HexRepresentation 12、DisplayTextTransform 13、WatchdogEvaluator 11、VariableMonitorEvaluator 9、Localization duplicate-key 2（合计 256→339 中其余为此前轮次）。

## 与 SuperCom 的已知差异（有意为之）

1. 结果检查在 SuperCom 中因接收管线被注释而必然超时（半成品）；DuCom 通过 GetDisplaySnapshot 增量游标重新设计并真实生效。迁移时 SuperCom 的 RecvResult/RecvTimeOut/IsResultCheck 完整映射到 DuCom 的 ExpectedResult/ResultTimeoutMilliseconds/IsResultCheck。
2. 无 per-command repeat 次数：参照即为“整组无限循环直到手动停止”，repeat 属 M3 发送计划范畴。
3. 高级命令无 SQLite：JSON 单文件存储。
4. “加载日志”用系统查看器外部打开而非载入编辑器：DuCom 显示层为只读虚拟化行视图，无“编辑器文档”可载入；该行为进 final-hardware-checklist 复核。
5. WatchDog 语义：DuCom 同时保留内容心跳规则和独立私有内存阈值监控；预算化行存储取代参照针对 AvalonEdit 的强制清空动作，改为显式告警和诊断记录。
6. 编辑菜单“只读”：DuCom 日志显示层天然只读（无可编辑文本文档），该项结构性恒真。
7. 字体族固定为 Cascadia Mono/Consolas（Fluent 排版决策），用户可调字号；参照允许任意字体。
8. 迷你窗口为每端口固定绑定，与参照“单窗口跟随选中”不同；主 Tab 切换后仍持续更新。
9. SuperCom 的 per-port AddTimeStamp/AddNewLineWhenWrite/SendHex/RecvShowHex/EnabledFilter 与 DTR/RTS/DiscardNull/隐藏端口已真实导入；无直接等价项仍明确 skipped。
10. 分屏支持横向和纵向双 pane，并持久化顺序与比例；Quad 未实现，作为已记录产品差异保留。
