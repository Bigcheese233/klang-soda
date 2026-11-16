# Klang · 汽水运行时（Runtime/Soda）

## 这是什么

- `helper.mjs`：隐藏 Node 子进程脚本，经私有 stdin/stdout 单行 JSON 提供三条命令：
  `register`（官方 `device.node` 匿名设备注册）、`sign`（官方 `bdms.node` 请求签名）、
  `decode-file`（MIT `audioDecryptor.js` 做 CENC/AES-CTR 解密，输入输出都走文件路径）。
- `ThirdParty/qishui-api/`：来自上游 [qishui-api](https://github.com/guowenye/qishui-api) 的 `src/audioDecryptor.js`、`src/errors.js`
  与 MIT LICENSE 原文。Klang 修改了 `audioDecryptor.js` 的 CENC 容器遍历：正确跳过 `stsd` 的 8 字节 FullBox 前缀与 `enca` 的 28 字节 AudioSampleEntry 正文，再处理内部加密信息。修改范围与上游归属见 [ATTRIBUTION.md](ThirdParty/qishui-api/ATTRIBUTION.md)。打包前曾通过合成 CENC 样本检查；按清理要求，源码包不附测试脚本和样本。

## 依赖与分发边界

- 汽水账号接口、签名与音频处理需要 **Node.js**。正式发布准备脚本附带经过 SHA-256 核验的官方 24.22.1 x64 与完整许可证，源码仓库不提交二进制。
  查找顺序为有效 `KLANG_SODA_NODE_EXE` / `KLANG_SODA_NODE` 显式覆盖 → 应用的 `Runtime/Soda/Node/node.exe` → PATH → `Program Files\nodejs`。
  找不到时相关能力不可用；本地播放与网易云功能不依赖汽水的 Node 桥。
- 官方原生组件（`device.node`、`bdms.node`、`metasecml.dll`）**不随本仓库分发**。
  程序会从用户本机已装的固定版本 **3.7.0** 读取这三件公共组件，复制到数据目录
  `StateService.ResolveDataDirectory()/soda/native/<版本-哈希>/` 后在同目录加载；
  不改官方安装目录、不读取官方账号数据。发现其它版本时不静默混用，直接报“只支持 3.7.0”。
- `KLANG_SODA_SDK_DIR` 可显式覆盖组件目录（开发/临时排查用），同样只接受这三件公共组件。

## 许可

独立项目的运行、打包、资料目录与排查说明见根目录 README 和 `docs/RUNTIME.md`。默认资料根目录为 `%LocalAppData%\KlangSoda`；设置 `KLANG_DATA_DIR` 时为 `<自定义目录>\KlangSoda`。

- 本目录自有代码（helper.mjs、README）随 Klang 项目许可。
- `ThirdParty/qishui-api/*` 为 MIT，Copyright (c) 2026 arrow，原文见 `ThirdParty/qishui-api/LICENSE`。
