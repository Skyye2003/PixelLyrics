# 网易云组件集成安装包

`installer/PixelLyrics.iss` 构建真正的 Setup EXE。它内置 PixelLyrics 自包含程序、BetterNCM 1.3.4 x64、InfLink-rs 3.4.0、微软 VC++ x64 运行库、GPL 许可证及固定版本的组件源码。安装组件无需下载文件，不使用旧 BetterNCM 在线安装器，不结束播放器进程。歌词请求仍在用户开始运行并授权后才发生。

## 使用

1. 从网易云托盘完全退出客户端。
2. 运行 `PixelLyrics-0.2.0-rc.2-Setup-win-x64.exe`，选择包含“网易云同步组件”的安装类型。
3. 确认网易云目录及组件目录，继续安装。组件目录读取安装进程的 `BETTERNCM_PROFILE`，缺失时为 `C:\betterncm`，不改环境变量。请使用日常登录的同一账户安装；暂未验收其他管理员账户代输凭据的场景。
4. 重启网易云并播放在线歌曲，再打开 PixelLyrics，授权并开始运行。

只使用 QQ 音乐可选择 PixelLyrics only。安装需要管理员权限，日常启动不要求管理员。仅允许已实测的网易云 3.1.41.205564 x64，不支持的版本会明确报错，不自动降级。已有相同组件复用，不认领其文件；不同版本或未知同名文件拒绝覆盖。

组件写入失败会回滚本次新增文件。日志在安装目录 `installer/state/component-error.txt`，Inno Setup 另有标准安装日志。组件失败时主程序仍可能已安装，修正原因后重新运行 Setup。VC++ 安装失败不会继续写组件；运行库要求重启时先重启再重试，不回滚共享微软运行库。

卸载前退出网易云，卸载只删除安装清单中哈希仍一致的自有文件。已有或后来修改的文件保留；检测到其他打包插件时保留共享 BetterNCM。不会删除用户配置、其他插件、组件运行后生成的缓存或插件商店。BetterNCM 自带联网检查和强制插件更新机制，此安装器不修改上游二进制；运行后的组件版本可能因此变化。

## 构建与验证

仓库不提交编译器、二进制依赖及生成物。准备以下文件：

- `release/netease-research/BetterNCMII-1.3.4.dll`、`InfLink-rs-3.4.0.plugin`，使用上游固定版本附件。
- `release/installer-input/vc_redist.x64.exe`，微软签名 14.44.35211.0；固定 SHA256 见构建脚本。
- `release/component-source/{BetterNCM,InfLink,libcef,js-framework,PluginMarket}`，每个目录中解压一份 [固定提交](../installer/SOURCE.md) 的源码归档。
- 本地 Inno Setup 6.7.3 编译器，可通过构建脚本 `-Compiler` 指定路径，不要求全局安装。

```powershell
./installer/Build-Setup.ps1 -ApplicationExe ./release/v0.2.0-rc.1/package/PixelLyrics.exe
./installer/Test-Components.ps1 -FixtureExe D:/Program/CloudMusic/cloudmusic.exe
```

构建脚本补齐三个 Git 子模块路径并打包源码，校验组件哈希，生成 `release/setup` 下的安装 EXE。rc.2 增加安装包装和主程序、安装程序、快捷方式图标；同步逻辑沿用 rc.1。图标由 `installer/Build-Icon.ps1` 从现有 SVG 生成多尺寸 ICO，并通过 ApplicationIcon 嵌入主程序。

测试仅复制已验证版本的播放器 EXE 到隔离目录，不执行该副本。测试进程模拟播放器已退出，所有写入及卸载均限于 `release/installer-tests`；真实运行的播放器不受影响。覆盖安装、重复安装、未知文件拒绝、既有文件保留、修改后保留、运行中拒绝、写入失败回滚、共享框架保留、未知版本和损坏附件。

当前验证：28 项隔离安装断言及主程序 349 项离线自测通过；从发布 EXE 提取图标确认正确，用户重新安装后确认桌面图标正常。

尚未验证：干净 Windows 安装/卸载、受保护目录和普通权限运行、不同账户提权、自定义环境变量、VC++ 缺失/重启、强制中断后恢复。尚未完成全部传递依赖源码与上游发布二进制的对应性审核。已附主组件、子模块和内置插件商店源码，但不将这些材料宣称为已审核的完整 Corresponding Source。当前按用户要求发布到现有私有仓库的 v0.2.0-rc.2 预发布版本；这些未验收项继续保留，不视为正式版或公开分发审核完成。
