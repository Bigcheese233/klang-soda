# 第三方来源与说明

Klang Soda 源自 [gwj0323/klang](https://github.com/gwj0323/klang)，保留上游 Git 历史、图标、原作者归属与根目录 [GNU GPL v3 LICENSE](LICENSE)。独立维护功能不代表原作者或汽水音乐官方的认可。

## 随源码保存的汽水引用

`Klang.WinUI/Runtime/Soda/ThirdParty/qishui-api/` 中的 `audioDecryptor.js`、`errors.js` 来自 [guowenye/qishui-api](https://github.com/guowenye/qishui-api)，采用 MIT 许可。完整许可与 Copyright (c) 2026 arrow 保留为该目录下的 `LICENSE`。

本项目对 `audioDecryptor.js` 的 `stsd` / `enca` 容器遍历作过定点修订，并在 [ATTRIBUTION.md](Klang.WinUI/Runtime/Soda/ThirdParty/qishui-api/ATTRIBUTION.md) 记录，不将它描述为上游未修改原件。

扫码页面的协议核对与运行时 SDK 来源另见 [登录资源说明](Klang.WinUI/Assets/SodaLogin/SOURCES.md)。官方验证 SDK 与固定版本 React/ReactDOM 18.3.1 UMD 按需经 HTTPS 加载，不属于本仓库自有代码；React 的许可见 [facebook/react v18.3.1 LICENSE](https://github.com/facebook/react/blob/v18.3.1/LICENSE)。

## 发布时附带的 Node

源码仓库不提交 Node 二进制。发布准备脚本下载 [Node.js 24.22.1 x64](https://nodejs.org/en/blog/release/v24.22.1/)，核对官方可执行文件 SHA-256：

`d702daa815eabbff763c7d3808e235cf031cf2ca70211b56847bc72f51e240e5`

发布目录 `Runtime/Soda/Node/` 保留 `node.exe`、完整 `LICENSE`（包含 Node 及其所含组件的第三方声明）和 `DOWNLOAD.json`。许可证来源为 [nodejs/node v24.22.1 LICENSE](https://github.com/nodejs/node/blob/v24.22.1/LICENSE)。

官方汽水音乐的 `device.node`、`bdms.node`、`metasecml.dll` 不随源码或发布包分发；程序使用本机固定版本的公共组件。它们的权利归原权利人所有，本项目没有把它们改为自身许可。

## 主要 WinUI 依赖与上游引用

| 项目 | 许可 / 用途 |
| --- | --- |
| Windows App SDK / WinUI 3、Win2D | MIT；界面与绘制 |
| Microsoft.Windows.CsWin32 | MIT；仅编译时生成原生 API/COM 绑定，无额外运行时包 |
| NAudio | MIT；音频处理与 WASAPI |
| TagLibSharp | LGPL-2.1-only；标签、封面和歌词读写 |
| QRCoder | MIT；二维码 |
| ComputeSharp | MIT；GPU 动态背景 |
| AutoEq | MIT；耳机频响补偿数据 |
| LRCLIB | 歌词数据接口，按服务自身规则使用 |
| Open-Meteo | 天气数据，沿用上游的可选功能与数据归属 |
| Codrops RainEffect / Lucas Bebber | 沿用上游的算法参考与 Codrops 许可说明 |

准确包版本见 `Klang.WinUI/Klang.WinUI.csproj`，完整上游依赖及 PyQt6 旧版引用保留于 [UPSTREAM_README.md](docs/UPSTREAM_README.md) 和对应源码/供应目录。NuGet 包的许可信息以其包内声明为准；未将第三方代码归为本项目原创。
