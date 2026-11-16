# 运行依赖与排查

## 两种使用方式

**正式发布包**由 `dotnet publish --self-contained true` 和 `installer/PreparePublish.ps1` 生成，包含 .NET 10、Windows App SDK 与固定 Node.js 24.22.1 x64。主程序为 `Klang.Soda.exe`。准备脚本附带对应源码和许可证，CI 默认只生成 artifacts，不自动发布 Release。

**直接从源码启动**需要 .NET 10 SDK。源码仓库不提交 Node 可执行文件，因此汽水功能需要本机 Node，或先自行准备应用运行目录中的 `Runtime/Soda/Node/node.exe`。本地播放和网易云不依赖汽水的 Node 桥。

## 汽水外部组件

- 本机需安装官方汽水音乐 **3.7.0**。程序只定位并暂存 `device.node`、`bdms.node`、`metasecml.dll` 这三件公共组件，不读取官方账号数据库，不修改官方安装目录。
- 其它版本不会自动当作 3.7.0 混用。开发时可通过 `KLANG_SODA_SDK_DIR` 指定组件目录，兼容性仍由使用者验证。
- 扫码窗口需要本机可用的 WebView2 Runtime。官方组件、WebView2、账号验证与网络均不是随附 Node 能替代的。

Node 查找顺序：`KLANG_SODA_NODE_EXE` / `KLANG_SODA_NODE` 有效显式覆盖 → 应用随附 Node → PATH → `Program Files\nodejs`。随附版本不向系统安装 Node，也不修改系统 PATH。

## 资料目录

默认使用 `%LocalAppData%\KlangSoda`。曲库与设置、网易云缓存、封面、歌词、歌词选择结果、波形、错误/崩溃日志和汽水数据都通过 `StateService.ResolveDataDirectory()` 定位。

设置 `KLANG_DATA_DIR=<目录>` 时，实际根目录为 `<目录>\KlangSoda`。保留原环境变量名称是为了兼容已有开发脚本。不会自动导入或删除 `%LocalAppData%\Klang` 的资料。

| 相对路径 | 用途 |
| --- | --- |
| `player_state.json` / `.bak` | 曲库、设置、任务栏偏好及 DPAPI 保护的网易云凭据 |
| `CoversCache`、`covers`、`LyricsCache`、`LyricsChosen`、`Waveforms` | 封面、歌词与波形缓存 |
| `netease_cache` | 网易云媒体缓存 |
| `soda/session.bin` | 当前汽水会话，使用当前 Windows 用户作用域 DPAPI 加密 |
| `soda/device.json` | 本项目独立的设备注册标识与注册状态 |
| `soda/native/<版本-哈希>` | 本机官方公共组件的暂存副本，不来自安装包 |
| `soda/u_<账号哈希>` | 按账号分开的音频、封面和歌词缓存；目录哈希不是文件加密 |
| `soda/login-temp-<随机编号>` | 独立 WebView2 登录临时资料，关闭时尝试清理 |
| `error.log`、`trail.log`、`taskbar-bar.log` | 错误、崩溃线索与任务栏状态日志 |

## 登录故障

登录窗口会区分设备准备、页面读取和 WebView2 启动等失败阶段。日志只记录阶段、异常类型和 HRESULT，不写 Cookie、账号、设备标识或完整接口响应。

1. **未找到 Node**：正式包应重新运行发布准备脚本；源码运行需检查 Node 查找路径。显式环境变量会覆盖随附版本。
2. **官方原生组件不可用/版本不匹配**：确认本机确实存在官方 3.7.0 及三件组件。安装 Node 或修复 WebView2不能解决这一项。
3. **缺少 login.js**：发布资源不完整，应重新发布/安装。
4. **无法启动 WebView2**：检查或修复本机 WebView2 Runtime。
5. **注册/请求超时**：检查到汽水官方服务和必要 CDN 的连接；不要公开日志中的个人路径或凭据。
6. **额外账号验证**：在官方验证页面由账号持有人完成。本项目不承诺所有账号和风控流程均能通过。

退出汽水会清除保存的会话，保留设备标识和已有缓存；卸载不删除资料目录。需要完全清理时，先关闭本程序，再处理本项目自己的资料目录，避免误删原版 Klang 或官方汽水客户端数据。
