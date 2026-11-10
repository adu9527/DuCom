# 发布时内嵌私有 BES 插件

## 交付约定

- 便携版仍交付一个 `DuCom.exe`，BES 运行文件作为程序集资源包含在 EXE 内。
- 安装版使用相同机制，主程序内包含 BES，不需要用户导入 dcpack。
- 运行时使用现有 factory 插件校验、部署、升级与修复机制，将插件写入当前用户插件目录（默认 `%LOCALAPPDATA%\DuCom\Plugins`）；单 EXE 交付不代表运行时不释放文件。
- BES 源码仍位于 Git 已忽略的私有目录，不作为主程序项目引用，也不进入发行包。只嵌入私有打包脚本生成的 DLL、EXE、JSON、BIN、TXT。
- 厂商组件和烧录资源由项目方确认已获授权。

## 本地正式发布

从解决方案目录执行：

```powershell
.\scripts\publish-portable.ps1
.\scripts\publish-velopack.ps1
```

两个入口默认执行本地私有 BES 打包脚本，读取包内清单的版本并完整嵌入运行文件。缺失私有源码/包时明确失败，避免误发不含 BES 的正式版本。

也可提供已构建的私有包，无须构建机持有 BES 源码：

```powershell
.\scripts\publish-portable.ps1 -BesPluginPackage C:\Private\bes.dcpack
.\scripts\publish-velopack.ps1 -BesPluginPackage C:\Private\bes.dcpack
```

公开仓库的普通 `dotnet build` 不依赖 BES；需要明确发布不含 BES 的公开版本时使用 `-WithoutBesPlugin`。不要将私有包、解压目录或权限修复备份提交到 Git。

## 用户体验与验证

首次安装 BES 默认启用，已有用户的禁用状态、配置和授权保留。原生烧录助手仍遵循现有安全确认，确认绑定组件摘要；组件改变后可能再次确认。无需手工导入不等于绕过安全确认。

发布验收应包括：清空隔离的测试插件根目录后首次启动、内嵌字节与部署文件摘要一致、x86 烧录助手/厂商 DLL/所有 programmer BIN 完整、重启和升级、同版本修复、授权拒绝/同意以及实机烧录与取消。仅构建成功不能证明设备烧录成功。
