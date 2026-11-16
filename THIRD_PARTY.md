# 第三方代码

`vendor-pixelbar/src/PixelBar.Sdk` 来自 [traceless929/PixelBar](https://github.com/traceless929/PixelBar)，原始检出版本为 `862b726ef04412ca80b0d6b54d88436dc62694b8`。本仓库保留本地使用的 SDK 源码快照及其构建属性文件，不包含上游客户端、开发缓存或 Git 元数据。

PixelBar 使用 MIT License，版权归 PixelBar Contributors 所有；完整许可证见 `vendor-pixelbar/LICENSE`。

安装包可选组件包括 BetterNCM 1.3.4（std-microblock/chromatic）和 InfLink-rs 3.4.0（apoint123/inflink-rs），均附 GPLv3 许可证；其固定源码、子模块及内置 PluginMarket 0.8.0 的源码随包保存在 `sources/component-source.zip`。版本与构建资料见 `installer/SOURCE.md`。组件通过 Windows SMTC 与 PixelLyrics 通信。BetterNCM 自带插件商店和联网更新机制，安装程序不修改该上游行为。

安装包内置微软签名的 Visual C++ x64 Redistributable，仅在运行库缺失时启动其安装程序，适用微软随附许可条款。安装编译工具 Inno Setup 仅用于构建，不安装到用户机器。
