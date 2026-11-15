# 网易云组件随安装包交付：调研

后续已获安装测试授权并完成基础实机测试：网易云重启后版本为 3.1.41.205564，候选组合通过连续播放及手动操作的进度桥接检查。详见 [实机测试记录](netease-plugin-test.md)。以下为此前调研记录，不应将其中“未安装”的历史状态视为当前状态。

用户要求：如依赖插件或 API，集成到提供给用户的安装 EXE 中，不要求用户自行寻找、下载或部署依赖。本文仅记录调研，尚未安装插件、修改网易云或执行真实歌词请求。

## 推荐方案与当前结论

推荐评估 `PixelLyrics-Setup.exe → 网易云进度组件 → Windows SMTC → PixelLyrics → 网易云歌词接口`。进度组件候选为 BetterNCM + InfLink-rs；API 客户端直接集成现有 .NET 程序，不额外运行 Node.js 服务。

该方案具有实现依据，但不能宣布已兼容当前网易云 3.1.33。InfLink-rs 文档声称覆盖该版本，BetterNCM 1.3.4 发布说明却仅列出 2.10.*–3.0.*，必须先找出并实测适配 3.1.33 的框架构建。不要自动降级用户网易云，也不要使用未知来源 DLL。

## 已核实的上游资料

1. [InfLink-rs README](https://github.com/apoint123/inflink-rs/blob/main/README.md)：声明测试过网易云 3.1.20–3.1.41、2.10.13；通过 SMTC 提供歌名、歌手、专辑、每秒更新的播放进度，并在流派字段写入 `NCM-{ID}`。这是上游声明，尚非本机验证。
2. [InfLink-rs v3.4.0](https://github.com/apoint123/inflink-rs/releases/tag/v3.4.0)：实际发布附件为 `InfLink-rs.plugin`；[manifest](https://github.com/apoint123/inflink-rs/blob/main/packages/frontend/manifest.json) 包含前端注入和原生 `backend.dll`，要求 BetterNCM >=1.0.0，并修改网易云内置 SMTC 初始化行为。其作用不只是 PixelLyrics 内部库。
3. [BetterNCM 1.3.4](https://github.com/std-microblock/chromatic/releases/tag/1.3.4)：附件有 `BetterNCMII.dll`、`BetterNCMII86.dll`；[对应 README](https://github.com/std-microblock/chromatic/blob/1.3.4/README.md) 标明兼容 2.10.*–3.0.*。DLL 必须匹配网易云进程位数，不能按 PixelLyrics x64 推断。
4. [框架当前主分支](https://github.com/std-microblock/chromatic)：BetterNCM 已重写并改名 chromatic，文档称仍在开发，不能假设它直接兼容旧插件。
5. [安装器 README](https://github.com/std-microblock/BetterNCM-Installer/blob/master/README.md)：传统安装将框架 DLL 放入网易云安装目录并命名为 `msimg32.dll`，插件需放入 BetterNCM 实际数据目录，随后重启网易云。
6. [安装器源码](https://github.com/std-microblock/BetterNCM-Installer/blob/master/src/main.rs)：启动会访问外部适配清单并选择下载 URL；仅将安装器 EXE 内置，不能实现完整离线安装。源码还包含 VC++ 运行库检测，需对选定二进制进一步验证依赖。

上游源码主分支可变，真正打包时需记录提交、发布标签、二进制 SHA-256，不能发布时临时取 latest。

## 安装 EXE 的交付设计

- 将现有自包含程序与安装程序区分：现有 EXE 解决 .NET 依赖，新增 Setup EXE 负责组件安装、升级和卸载。安装器工具可评估 Inno Setup，尚未选定或引入。
- Setup 内置主程序、经过验证的框架 DLL、插件、必要的可分发运行库及许可证；用户不需要 Git、Node.js、Rust 或插件商店。运行库能否省略须检查实际构建，不能因主程序自包含就忽略插件依赖。
- 安装界面提供“安装网易云同步组件”，明确告知将修改网易云安装目录且需要退出并重启网易云。只用 QQ 的用户可跳过；网易云未安装时给出可恢复状态。
- 检测网易云路径、版本、位数、已有框架和自定义插件目录。兼容性未知时不执行安装；已有第三方同名 DLL 不覆盖。受保护目录按需提权，主程序日常运行不要求管理员权限。
- 使用固定清单及哈希校验。只备份和修改本次负责的文件，失败时回滚；卸载仅移除确认属于本程序且未被后续修改的文件，不删除用户已有插件环境。
- 不强制结束网易云进程。由用户退出后继续安装，重启后自动检查有效进度与歌曲 ID。客户端更新后如果组件失效，清楚提示而不继续输出猜测的同步结果。

## API 集成

API 获取歌曲详情和歌词，不能补出本地桌面播放器的实时进度。两个问题需要分别解决。

候选调用链为：插件 SMTC 中的数字歌曲 ID → 歌曲详情核验 → 原文逐行 LRC。插件已提供 ID 时不需要搜索候选；本地歌曲或无有效在线 ID 的内容首版应明确不支持，不冒险匹配。

公开实现参考：[lyric.js](https://github.com/NeteaseCloudMusicApiEnhanced/api-enhanced/blob/main/module/lyric.js) 使用 `/api/song/lyric`；[song_detail.js](https://github.com/NeteaseCloudMusicApiEnhanced/api-enhanced/blob/main/module/song_detail.js) 使用 `/api/v3/song/detail` 并选择 `weapi` 请求方式。源码只是接口调查依据，不证明匿名 HTTP 请求已可用，详情请求也不能直接假设普通 GET 即可替代。

推荐独立实现最小 .NET 客户端，避免随包部署整套第三方 API 服务。下一阶段用公开测试歌曲核实响应、匿名可用性、必要请求格式、无歌词及限流错误；不要求 Cookie、不处理登录挑战，不借用用户网易云登录凭证。具体调用链尚未固定。

网络歌词不应随安装包预置；安装包内置的是请求与解析代码。真实歌词获取仍需联网，不能承诺完全离线歌词功能。保留现有授权、取消、超时、请求预算、网络锁止及旧结果隔离。

## 许可证与发布

[InfLink-rs](https://github.com/apoint123/inflink-rs/blob/main/LICENSE)、[BetterNCM 1.3.4](https://github.com/std-microblock/chromatic/blob/1.3.4/LICENSE)、[旧安装器](https://github.com/std-microblock/BetterNCM-Installer/blob/master/LICENSE) 均有 GPLv3 许可证文本。可再分发不等于可省略许可证及对应源代码义务；私有 PixelLyrics 仓库不能替代向组件接收者提供其对应源码。发布前需针对实际选定版本准备完整源码和构建资料，核实依赖许可证。

建议保持独立组件，通过 Windows SMTC 通信，不把 GPL 源码直接复制进 PixelLyrics。这样可以清楚划分交付物，但是否属于独立聚合及最终许可证义务仍需按实际组合核查，不能仅凭不同文件名断言 PixelLyrics 无相关义务。若修改 GPL 组件，还需提供修改后的对应源码。

## 下一步验证门槛

### 第二轮：实际附件与本机静态核验

已下载公开附件至本地忽略目录 `release/netease-research`，未执行或加载。

| 附件 | 大小（字节） | SHA-256 |
| --- | --- | --- |
| InfLink-rs v3.4.0 插件 | 1,637,976 | `7d0b7b863b072a99b88eb614c788099315e690920889aaca22685d3663661468` |
| BetterNCMII 1.3.4 x64 DLL | 1,204,224 | `a7c77af418d7940e63faa58ea036fba1f4baad497947109ea52ed78c8e86608f` |
| BetterNCM 1.3.4 标签源码 ZIP | 180,798 | `81032a31073d96e0343c963e619980284a065e8bd451e002a262b8523018c05c` |

这些哈希是本次下载的本地记录，不是上游签名或独立真实性背书。附件尚未被纳入分发清单。

- 插件 ZIP 含 `backend.dll`（x86）、`backend.dll.x64.dll`（x64）、前端脚本和 manifest；无需用户自行选择插件架构。
- 本机 `D:/Program/CloudMusic/cloudmusic.exe` 为 x64，故候选框架为 x64 DLL。
- 直接解析 PE 导入表确认：框架依赖 `MSVCP140.dll`、`VCRUNTIME140.dll`、`VCRUNTIME140_1.dll`，两个插件后端也依赖 `VCRUNTIME140.dll`。安装包需检测相应 VC++ 运行库，或在核实再分发要求后内置运行库安装文件。完整导入表保存在本地 `pe-imports.json`。
- 对照本机网易云 `libcef.dll` 导出表：框架的 16 个、插件 x64 后端的 6 个直接 CEF 导入函数均存在。该结果仅排除直接导入缺失，不能证明 CEF ABI、注入钩子及插件运行兼容。未调用任何下载 DLL 中的函数。
- 固定标签源码 `src/PluginManager.cpp` 和 `src/App.cpp` 实现了插件的 `hijacks` 字段及版本范围筛选，具有加载当前插件清单的机制基础；与 README 版本范围的差异仍必须实测。
- `src/dllmain.cpp` 显示默认数据目录为 `C:/betterncm`，支持环境变量 `BETTERNCM_PROFILE`。此前提到的 LocalAppData 路径不是该固定版本默认值，安装程序不能凭猜测选路径，也不应为了安装而修改用户全局环境变量。
- 框架会初始化内置 PluginMarket；还需审查其默认网络行为和更新行为，避免后续自动更新破坏已验证的固定组件组合。
- 旧安装器适配清单 URL 本次返回 CSDN 登录 HTML，不能作为无人值守安装依赖。安装源码还包含强制 `taskkill /f`、在线下载框架和 VC++ 运行库的流程，因此不采用直接启动旧安装器的方案。
- 框架标签源码 ZIP 含 `src/js-framework`、`src/3rd/libcef` 两个 Git 子模块声明，普通 GitHub 源码 ZIP 并不等于完整可构建的对应源码；源码交付还需固定并补齐子模块及构建依赖。
- 本机未通过常见命令及标准路径发现 Windows Sandbox、VMware、VirtualBox 或 Inno Setup 编译器。此结果仅为有限路径检查，未尝试安装工具或创建虚拟环境。

当前可准备的候选组合为 **网易云 3.1.33 x64 + BetterNCM 1.3.4 x64 + InfLink-rs 3.4.0**，状态为“静态检查未发现直接导入缺失，运行兼容性未验证”。若运行验证失败，继续调查框架适配构建，不自动改装或降级播放器。

下一阶段在隔离环境或取得明确安装授权后，先验证该组合能否正常启动网易云、产生有效 SMTC 时间线和歌曲 ID；这一步通过前，不制作声称支持网易云的正式安装包。API 部分仍停留在源码调研，未发送真实歌词请求。

1. 固定兼容网易云 3.1.33 的框架来源与版本；验证插件二进制、运行库和许可证材料齐全。当前最大未决项为框架版本适配。
2. 在隔离环境安装候选组件，确认 SMTC 的元数据、在线歌曲 ID、时长和更新时间；验证连续播放、暂停恢复、双向拖动、切歌及双播放器。
3. 验证 API 的匿名调用链和错误分类，再接入播放器选择与会话隔离。
4. 制作安装 EXE，覆盖无框架、已有框架、保护目录、安装中断、修复、卸载及客户端升级。用断网环境验证组件安装确实不依赖外部下载。
5. 单独取得实机插件安装及真实联网/USB 测试授权后执行对应操作；通过后再发布支持版本范围和已知限制。当前调研授权不执行这些修改。
