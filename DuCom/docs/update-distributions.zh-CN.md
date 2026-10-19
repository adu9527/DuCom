# 构建类型与更新资源

三个类型共用产品版本号与 GitHub Release。类型通过程序集的 `Distribution`
元数据写入，不依赖程序文件名或文件大小。

| 类型 | 构建标识 | 更新资源 |
| --- | --- | --- |
| 安装版 | `Installed` | Velopack 更新资源 |
| 完整便携版 | `PortableFull` | `DuCom.exe` |
| 轻量便携版 | `PortableLite` | `DuCom-lite.exe` |

## 构建

从 DuCom 解决方案目录执行：

```powershell
.\scripts\publish-velopack.ps1
.\scripts\publish-portable.ps1
.\scripts\publish-portable.ps1 -FrameworkDependent
```

轻量版脚本同时生成首次下载 ZIP 和 `artifacts/DuCom-lite.exe` 更新资源。
用户本地程序仍可命名为 `DuCom.exe`。轻量版需要 .NET 10 Desktop Runtime x64。
直接构建默认标识为 `PortableFull`；自定义发布需显式传入
`-p:DuComDistribution=Installed|PortableFull|PortableLite`。

## 发布

同一个 Release 上传 `artifacts/portable/DuCom.exe`、
`artifacts/DuCom-lite.exe`、轻量 ZIP，以及 Velopack 发布脚本生成的更新资源和安装包。
便携更新严格按资源名选择，缺少对应资源时提示错误，不回退至其他类型。
安装版优先使用实际的 Velopack 安装状态；标记为安装版但缺少安装组件时不走便携更新。

暂存文件分别放入 `Updates/PortableFull` 与 `Updates/PortableLite`，
不复用旧版 `Updates` 根目录的暂存包，避免手动切换类型后误用旧包。
旧版本仍可通过原有 `DuCom.exe` 资源在线更新。


未来升级 .NET 主版本时，需要同步调整轻量版的运行时要求与升级前检查。
