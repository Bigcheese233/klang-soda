# 上游与同步记录

## 当前基础

- 原项目与作者：[gwj0323/klang](https://github.com/gwj0323/klang)。
- 已同步上游版本：**2.7.6**。
- 上游提交：[`389b66a8ab20bceb7c598df67fb2f7ae32fae747`](https://github.com/gwj0323/klang/commit/389b66a8ab20bceb7c598df67fb2f7ae32fae747)。
- 本项目候选版本：**2.7.6.1**。第四段用于独立维护修订，不改变上游版本的含义。
- 原始上游说明保留为 [UPSTREAM_README.md](UPSTREAM_README.md)，原始 LICENSE 与 Git 历史保留。

本轮保留了上游新增/修复的播放队列、歌词候选选择、独立音频分析子进程、曲库与歌词处理，以及 WindowHelpers 的拖动和跨屏 DPI 处理、CrashReporter 的事件日志编码修复。

## 独立功能的来源

- 任务栏改动来自 Bigcheese233 的两个独立提交：`9ddd1b9`、`38ba38f`，对应[纯任务栏 PR #19](https://github.com/gwj0323/klang/pull/19)。它们以 2.7.6 为基础，使用原生 UI Automation，未引入 WPF。
- 汽水功能取自此前的 `feat/taskbar-soda` 开发分支（`f32d282`）及本地 fix1 登录修复，再合入上述基础。
- 独立项目不向上游提交汽水接入。上游没有接收该部分功能；本项目自行承担它的兼容维护，不将自己的支持请求转给原作者。

## 同步时需保留的改动

| 范围 | 本项目差异 |
| --- | --- |
| 项目标识 | AppIdentity、Klang.Soda.exe、四段版本号、独立应用标识 |
| 安装与数据 | 独立 AppId、Klang Soda 安装目录、KlangSoda 资料目录和所有缓存目录 |
| 更新渠道 | UpdateService、关于页、安装脚本只指向 Bigcheese233/klang-soda |
| 播放与界面 | 汽水侧栏、MainWindow.Soda.cs、OnlineMusicService、SodaFeedQueue、Soda 服务和登录资源 |
| 任务栏 | 原生 COM 查询、透明窗口、停靠布局及设置入口 |
| 打包 | PreparePublish.ps1、随包 Node、相应许可证与完整源码归档 |
| 说明 | README、PRIVACY、RELEASE_NOTES、第三方归属与本目录 |

维护时先在独立工作分支拉取和本地合并，解决冲突后完成 Debug / Release 与桌面验证，再更新本文件的版本和提交号。不要整体覆盖 MainWindow、WindowHelpers、CrashReporter 等共享文件。

```powershell
# 本地同步示例；不自动创建提交，也不推送。
git fetch upstream main
git merge --no-commit --no-ff upstream/main
```

合并后确认更新检查没有重新指向上游、缓存没有回到原版目录、分析子进程入口仍保留，以及 Node 打包步骤仍位于 WinUI 作业内。提交与上传应在查看具体差异和验证结果后单独确认。
