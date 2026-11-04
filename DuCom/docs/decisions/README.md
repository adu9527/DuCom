# 决策记录（ADR）

架构和取舍类决策写在这里，一个决策一个文件，按登记顺序编号：

| 编号 | 主题 |
| --- | --- |
| [0001](0001-solution-and-dependency-boundaries.md) | 解决方案结构与依赖边界 |
| [0002](0002-load-measurement-contracts.md) | 负载测量契约 |
| [0003](0003-development-diagnostic-logging.md) | 开发期诊断日志 |
| [0004](0004-serialport-close-quiesce-drain-order.md) | 串口关闭前静默排空的顺序 |
| [0005](0005-test-project-source-link-boundary.md) | 测试项目引用应用源码的边界 |

编号规则：

- 新决策取当前最大编号加一，四位数字前缀，后接待办短名。
- 编号一旦发布不再改；`0005` 是历史重号修正的例外，文件内有说明。
- 决策过时用新 ADR 取代（Supersedes 字段），不直接删文件。
