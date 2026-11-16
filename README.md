<div align="center">
  <img src="Klang.WinUI/Assets/AppLogo.png" width="96" height="96" alt="Klang Soda" />
  <h1>Klang Soda</h1>
  <p>本地音乐、网易云、汽水音乐，一个窗口听歌。</p>
  <p>Windows 桌面音乐播放器 · 同步歌词 · 任务栏播放条</p>

  [![License](https://img.shields.io/badge/license-GPL--3.0-green)](LICENSE)
  ![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-blue)
</div>

Klang Soda 将本地曲库、网易云和汽水音乐放在同一个桌面播放器里。你可以在主窗口浏览音乐、看封面和歌词，也可以把播放控制留在任务栏，工作时随手暂停、切歌。

项目由 **Bigcheese233** 维护，主版本使用 WinUI 3。当前开发候选版本为 **2.7.6.1**，尚未发布正式安装包。版本变化和验证情况见 [版本记录](RELEASE_NOTES.md)。

## 功能一览

| 功能 | 可以做什么 |
| --- | --- |
| 本地曲库 | 添加音乐文件夹，按歌曲、专辑、喜欢和歌单浏览，查看标签与封面 |
| 网易云音乐 | 登录账号，使用每日推荐和我喜欢的音乐，与本地音乐共用播放器 |
| 汽水音乐 | 扫码登录，收听推荐、我喜欢的音乐和八个模式 |
| 任务栏播放条 | 在任务栏空白区域显示封面、曲目信息、歌词及播放控制，支持左右停靠和紧凑布局 |
| 歌词与界面 | 同步歌词、桌面歌词、迷你播放器、封面与频谱显示 |
| 音频设置 | 均衡器、音效、耳机校准、播放倍速与独占输出 |

## 汽水音乐

汽水页面采用左侧导航、右侧播放信息的布局。账号、推荐、收藏和模式都在侧栏；播放后，右侧显示歌曲封面、信息、频谱及歌词。

点击模式即可开始播放，下一首会继续当前模式；接近队列末尾时继续补充歌曲。

| | | | |
| --- | --- | --- | --- |
| 熟悉模式 | 新鲜模式 | Chill 放松 | 驾车 |
| 摸鱼 | 粤语 | 动感健身 | 快乐时光 |

“我喜欢的音乐”支持读取收藏、分页浏览和选择歌曲播放。目前尚未提供汽水搜索、远程添加或取消收藏，以及音频缓存容量上限。汽水歌词使用逐行时间轴；歌曲能否播放取决于账号授权、音源与编码。

## 任务栏播放条

通过顶部 **视图** 菜单、**设置 → 外观** 或快捷键 **`Ctrl+B`** 开启。

- 在任务栏空白位置查看封面、曲目信息和同步歌词。
- 直接播放、暂停、切换上一首或下一首。
- 拖动选择左侧或右侧停靠；空间不足时使用紧凑布局，无法容纳时隐藏。
- 自动判断全屏窗口、任务栏收起等状态。

播放条使用独立窗口叠加显示。多屏、混合 DPI、自动隐藏任务栏及第三方任务栏工具的组合仍需继续验证，详细实现见 [功能实现说明](docs/IMPLEMENTATION.md)。

## 运行与开始使用

当前以源码开发版本为主。正式安装包发布后会放在 [Releases](https://github.com/Bigcheese233/klang-soda/releases)，请以该页面实际发布的文件为准。

| 环境 | 要求 |
| --- | --- |
| 系统 | Windows 10 1809 或更新版本，x64 |
| 程序运行时 | 正式自包含包会附带 .NET 10 与 Windows App SDK，无需安装 .NET SDK |
| 汽水组件 | 使用汽水功能需在本机安装 **官方汽水音乐 3.7.0** |
| 登录窗口 | 本机需有可用的 **WebView2 Runtime** |
| Node.js | 发布准备脚本会随包附带官方 Node.js 24.22.1；源码开发需自行准备 |

首次使用：

1. **听本地音乐**：在曲库中选择“添加”，导入音乐文件夹。
2. **使用网易云**：切换到“云端”，按界面提示登录账号。
3. **使用汽水**：切换到“汽水音乐”，点击登录，用手机汽水音乐 App 的“搜索 → 扫一扫”扫描二维码并确认；出现额外验证时在登录窗口内完成。
4. **开启任务栏播放条**：通过“视图”菜单开启，或按 `Ctrl+B`。

汽水接入使用非官方协议，依赖本机官方组件完成设备注册和请求签名，并包含音频处理与解密逻辑。官方的 `device.node`、`bdms.node`、`metasecml.dll` 不随本项目分发；登录页会按需加载官方验证资源和第三方脚本。协议或验证流程变化可能导致功能失效，依赖及故障排查见 [运行说明](docs/RUNTIME.md)。

## 账号与资料

程序默认将曲库状态、歌词、封面、波形、网易云缓存和汽水资料存放在：

```text
%LocalAppData%\KlangSoda
```

汽水登录会话由当前 Windows 用户的 DPAPI 加密保存，缓存目录中的其他文件并非全部加密。退出账号不会自动清空所有缓存，具体保存内容与网络请求见 [隐私说明](PRIVACY.md)。

Klang Soda 使用独立的安装标识和资料目录，不自动迁移旧 Klang 的曲库或账号。更新检查只读取本项目的发布列表。

## 从源码运行

在 Windows x64 上安装 .NET 10 SDK，并准备好 Node.js。使用汽水功能还需满足上面的官方组件和 WebView2 要求。

在项目根目录执行：

```powershell
# 开发资料保存在项目下 Data/KlangSoda，与日常使用资料分开。
$env:KLANG_DATA_DIR = Join-Path (Get-Location) 'Data'
dotnet run --project Klang.WinUI/Klang.WinUI.csproj -c Debug -p:Platform=x64
```

主程序为 `Klang.Soda.exe`，源码入口位于 `Klang.WinUI/`。也可通过 `KLANG_SODA_NODE_EXE` 指定开发使用的 Node 可执行文件。

<details>
<summary>构建绿色版与安装程序</summary>

```powershell
dotnet publish Klang.WinUI/Klang.WinUI.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o publish/KlangSoda
./installer/PreparePublish.ps1 -PublishDirectory ./publish/KlangSoda
# 需要 Inno Setup；SourceDir 相对 installer/Klang.iss 所在目录解析。
ISCC.exe /DAppVersion=2.7.6.1 /DSourceDir=..\publish\KlangSoda installer/Klang.iss
```

发布准备脚本会校验并附带 Node、许可证、说明及匹配的完整源码 ZIP，要求工作区干净且已提交。仅做本地构建检查时可传 `-SkipSourceArchive`，该模式不生成供正式分发使用的匹配源码归档。

GitHub Actions 的 **Build Klang Soda** 工作流手动触发，默认构建 WinUI 版本，产物保存在 Actions artifacts，不自动发布 Release。正式 WinUI Release 标签需包含 `winui` 和版本号，例如 `winui-v2.7.6.1`，供程序检查更新时识别。

仓库中的 Qt/Python 目录保留历史源码，目前不包含 WinUI 版本的汽水接入功能；可选的旧版 Qt 构建默认关闭。

</details>

## 文档与反馈

- [版本记录与验证情况](RELEASE_NOTES.md)
- [运行依赖、登录与故障排查](docs/RUNTIME.md)
- [功能实现及模块地图](docs/IMPLEMENTATION.md)
- [上游同步记录](docs/UPSTREAM.md)
- [隐私说明](PRIVACY.md)
- [第三方代码与许可证](THIRD_PARTY_NOTICES.md)

使用问题和建议请提交到 [Issues](https://github.com/Bigcheese233/klang-soda/issues)，并附上程序版本、Windows 版本和复现步骤。分享日志前请移除账号、Cookie 等个人信息。

## 致谢与许可

Klang Soda 基于 [gwj0323/klang](https://github.com/gwj0323/klang) 发展，保留原作者署名、提交历史、图标归属和 [GNU GPL v3 许可证](LICENSE)。汽水相关引用及修改记录见 [第三方说明](THIRD_PARTY_NOTICES.md)。本项目独立维护，与网易云音乐及汽水音乐官方没有合作或隶属关系。
