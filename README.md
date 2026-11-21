# 听页

听页是 Windows 桌面 EPUB 阅读和朗读软件。支持无 DRM 的 EPUB 2 和 EPUB 3，可按章节或段落朗读，并保存阅读进度。

## 编译和使用

在装有 64 位 .NET Framework 4.x 的 Windows 上，从 PowerShell 进入仓库目录，运行 `./Build.ps1`，然后打开生成的 `Tingye.exe`。

1. 点击「导入 EPUB」，也可以用 `Ctrl+O` 或直接拖入 EPUB 文件。
2. 从书架打开书籍，选择章节或段落，点击「朗读」。正文获得焦点时，空格键可开始、暂停和继续朗读。
3. 在「设置 → 朗读」中选择 Windows 系统语音。语音列表取决于 Windows 已安装并向桌面程序提供的语音，基础朗读无需下载第三方模型。

书架、阅读进度、设置和语音缓存默认存放在 `%LOCALAPPDATA%\Tingye`。

## 可选语音

本地 Kokoro、Breeze TTS 2 和 Qwen3-TTS 的下载来源及文件放置方式见 [第三方语音导入指南](VOICE-SETUP.md)。导入后重启听页，再到「设置 → 朗读」选择音色。

若要使用在线「大肥鱼（机灵版）」，请在设置中输入自己的 BreezeBlue API 密钥。该功能会把待朗读文本发送给服务商，费用按服务商规则计算。应用使用 Windows 当前用户的加密机制保存密钥。 [BreezeBlue 密钥管理](https://breezeblue.ai/zh/app/developer/api-keys)
