# 功能实现与模块地图

本项目保留上游 WinUI 3 / .NET 10 播放器。汽水与任务栏功能均接入原音频引擎，没有并行创建第二套声音输出。

## 任务栏播放条

`MainWindow.SetTaskbarPlayerEnabled` 接收视图菜单、设置开关和 Ctrl+B，创建 `TaskbarPlayerWindow`，并连接原播放/暂停、上一首、下一首、喜欢与恢复窗口操作。原曲目、播放状态和歌词更新反向同步到播放条。

- `Services/TaskbarAutomation.cs`：调用 Windows 原生 `IUIAutomation` COM 接口。绑定由微软 CsWin32 0.3.371 编译时生成，不引入 WPF；所有 COM 对象在单一 MTA 线程创建、使用和释放。
- `Services/TaskbarEnvironmentService.cs`：后台查询任务栏子树，缓存边界、控件类型、可见性与进程信息。界面只读取物理像素矩形快照，不等待跨进程 UIA。查询失败或无法识别有效按钮时，不将未知区域当成空白。
- `Services/TaskbarLayout.cs`：计算占用矩形的补集与左右停靠点。设计大小为普通 388×40 DIP、紧凑 108×40 DIP，空间再不足则隐藏；坐标边界处按 DPI 换算。
- `Views/TaskbarNative.cs`：任务栏/显示器枚举、其他窗口占用、全屏/弹层/收起检测、非激活窗口与边框样式。
- `Views/TaskbarPlayerWindow.xaml(.cs)`：播控、主题、悬停淡入、指针捕获、拖动、吸附确认与持久化；拖离有效区域后回位。
- `Views/TaskbarSnapPreviewWindow.cs`：停靠预览；`TaskbarTransparentBackdrop.cs`：合成画刷、DWM alpha 与背景清除，不对整窗降低透明度。

播放条是独立窗口，不注入或修改 Explorer。Explorer 重启时按新句柄重新关联；第三方任务栏程序和外壳结构变化仍可能影响识别。布局检查不等于实际系统兼容性验收。

## 汽水页面与播放队列

- `Controls/SodaPanel.xaml(.cs)`：左侧账号、推荐、收藏和八个模式入口，侧栏内展开列表；右侧保留上游封面、频谱和歌词舞台。
- `MainWindow.Soda.cs`：把模式选择与曲目选择接到主播放器，取消旧选择、准备音源并同步舞台。
- `Services/OnlineMusicService.cs`：区分网易云和汽水的音源/歌词获取路径。
- `Services/SodaFeedQueue.cs`：保存当前模式、按汽水曲目 ID 去重、接近队尾继续取批次；切换模式后丢弃旧任务结果。
- `Models/TrackItem.cs`：标识在线来源和汽水曲目，不把不同来源的 ID 混在一起。

## 汽水服务、登录与缓存

| 文件（位于 `Klang.WinUI/`） | 责任 |
| --- | --- |
| `Services/Soda/SodaService.cs` | 独立设备注册、账号接收/退出、模式与收藏读取、账号代次和签名入口 |
| `Services/Soda/SodaService.Media.cs` | 详情、音源、缓存、解密、歌词与播放准备 |
| `Services/Soda/SodaHttp.cs` | 汽水 HTTPS 请求、状态分类、响应大小及媒体域名/重定向边界 |
| `Services/Soda/SodaNative.cs` | 固定官方版本组件定位/暂存、Node 查找和隐藏子进程桥 |
| `Services/Soda/SodaMedia.cs` | 音频容器及歌词归一化辅助逻辑 |
| `Services/Soda/SodaSession.cs` | DPAPI 加密的独立会话保存/读取 |
| `Services/Soda/SodaCache.cs` | 共享/游客/账号目录及按键哈希的文件缓存 |
| `Views/SodaLoginWindow.cs` | 临时 WebView2、来源/导航限制、二维码渲染、官方验证弹窗、会话验证后接收 |
| `Assets/SodaLogin/login.js` | Passport 二维码取码、轮询、官方额外验证交互 |
| `Runtime/Soda/helper.mjs` | stdin/stdout 有界 JSON 桥：注册、签名和文件解密；stderr 排空、超时后结束子进程 |
| `Runtime/Soda/ThirdParty/qishui-api/` | 两个有修改归属说明的 MIT 文件及原 LICENSE |

API 客户端只向 `api.qishui.com` 附带当前 Klang Soda 会话 Cookie；媒体和图片 CDN 不附带该 Cookie。扫码临时浏览器按官方流程与认证/验证服务交换站点数据，必要时加载固定版本 React/ReactDOM CDN。会话只在接口验证后接收，取消或失败保留原账号。实现不会导入官方客户端的已登录账号。

## 独立项目的边界

`AppIdentity` 定义仓库、应用标识和资料目录名称。默认数据、所有缓存和错误日志都归属 `KlangSoda`；安装 AppId、目录和快捷方式与上游分开。`UpdateService` 支持四段修订号，只读本项目发布列表；没有独立发布时不回退到上游安装包。

`Program.cs` 和 `AnalysisWorker` 保留上游的分析子进程设计；`WindowHelpers` 保留上游的拖动阈值、屏幕限制与跨 DPI 尺寸调整；`CrashReporter` 保留 OEM 编码修复，仅更改本项目资料根目录与版本显示。

## 构建与分发

`BundleSodaNode.ps1` 固定下载官方 Node.js 24.22.1 x64，核验 SHA-256，附完整 LICENSE 和下载记录。`PreparePublish.ps1` 附带隐私/来源说明，并在干净已提交的工作区导出匹配源码 ZIP。官方三件原生组件和用户资料不进入发布包。

GitHub 工作流的 WinUI 作业负责自包含 publish、准备 Node/说明/源码、验证关键文件、ZIP 和 Inno Setup。Qt 作业默认不启用；没有自动发布 Release 的步骤。更多运行细节见 [RUNTIME.md](RUNTIME.md)，第三方来源见 [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md)。
