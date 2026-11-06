# 12 island — Windows 灵动岛

12 island 是屏幕顶部的灵动岛：收起时是一枚刘海或胶囊，鼠标移上去展开成面板，把通知提醒、音乐控制、系统读数、
剪贴板历史和文件格式转换放在同一处。

## 功能

- **通知**：监听固定的聊天与邮件白名单——微信、QQ、TIM、企业微信、钉钉、飞书、Teams、Slack、Discord、Telegram、WhatsApp、LINE、Skype、Zoom、腾讯会议、Notion，以及 Outlook、Foxmail、Thunderbird、网易邮箱大师、QQ 邮箱共 21 个程序。收到消息时它们的任务栏（或 Dock）图标闪动，收起的小岛变成胶囊，显示来源图标与未读条数，并以循环的弹跳或音爆动态提醒，直至点击胶囊；点击胶囊直接拉起对应软件，并把该程序的全部通知标记为已读。白名单之外的一切软件与系统通知均不显示。
- **音乐**：封面、律动条、歌词与播放控制，来自系统媒体会话；可开启完整控制（网易云音乐 / 酷狗 / Apple Music）。收起的小岛上，音量律动条随声音起伏。
- **系统监控**：CPU、内存、磁盘、网速、温度、功耗、帧率与进程列表；收起的小岛上也能显示读数。
- **剪贴板**：记录文字、图片与文件，可搜索、收藏、备注，点一下复制回去；清空时保留收藏。
- **格式转换**：图片、文档、表格、文本、音频、视频与 PDF 全部在本机转换（音视频用内置的 FFmpeg）；每个项目带复选框，勾选哪个转换哪个，结果与源文件放在一起。
- **外观**：刘海 / 胶囊 / 指示条三种收起样式；深色 / 浅色主题（可跟随系统），界面透明度可调；页面顺序、大小与位置均可调整；每个显示器一块小岛。展开面板触发后悬停 5 秒自动收起，右下角有「悬停」「收起」按钮，可长期保持或立即收起。

## 运行

- **系统**：Windows 10（build 19041 及以上）与 Windows 11，x64。
- **免安装、零依赖**：发布的 `12Island.exe` 是单个自包含文件——.NET 运行时、WPF 原生库与 FFmpeg 全部在内，放到任何目录直接运行。
- 数据保存在 `%LOCALAPPDATA%\12Island`；换机时复制该目录即可带走设置与历史。

## 使用说明

- **通知**：检测的是任务栏与 Dock 图标的闪动（系统在窗口闪动时会向注册了 Shell Hook 的程序通报），因此任务栏、Winstep Nexus、RocketDock 等处的提醒都能接住。12 island 不读取聊天记录，也不上传任何内容。
- **邮箱**：在「设置 › 通知」中填写邮箱地址与授权码（在邮箱后台生成，不是登录密码），支持 QQ 邮箱、163、Gmail、Outlook 等标准 IMAP 服务器。授权码以密码方式输入，已保存的不会显示，留空即保持不变。
- **温度与功耗**：需要外部传感器来源（如 HWiNFO 的共享内存或 LibreHardwareMonitor 的网页服务）；没有来源时对应读数自动隐藏。
- **格式转换**：把文件拖进转换页，或在资源管理器中右键选择「用 12 island 转换」。

## 构建

需要 .NET 10 SDK（x64）：

```
dotnet publish src/12Island/12Island.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

- 版本号取自 `releases/` 里最新的文件名；`scripts/Version-Bump.ps1` 按源码改动量自动判定下一次是大、中还是小版本，每个版本的改动写在该版本的更新日志里。
- 安装器与便携包由 `scripts/Publish-Release.ps1` 生成。

## 发布

1. 在 GitHub 上以 `vX.Y.Z` 为 tag 创建 Release，描述使用 `releases/X.Y.Z.md` 的内容；
2. 上传发布产物 `12Island-X.Y.Z-windows-x64.exe`（应用内自动更新按这个名字识别新版本）。

## 许可

本项目以 [MIT License](LICENSE) 开源。
