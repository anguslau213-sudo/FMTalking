# FMTalking 1.0 · 本地智能体说明 / Local Agent Guide


解压后先读本文件和同级的 [README.md](README.md)。如果你接手的是已安装版本，可在游戏目录的 `BepInEx/plugins/FmPlayerTalk/` 中查找本说明。只要能读本地文件，就能使用这份指南。自动发现文件的行为取决于你使用的工具。

## 安装与验证

1. 从 Steam 的「管理 → 浏览本地文件」找到实际安装目录，核对该目录有 `fm.exe`。如果有多个游戏副本，核对玩家要使用的那一份。记录当前游戏构建、加载器版本和已安装的模组版本。
2. 在本机干净目录实测的是 Windows x64、Steam FM27 `27.1.0+2505117` 和 BepInEx Unity IL2CPP `6.0.0-be.791+b782da4`。若缺少加载器，按实际环境选择对应版本并先验证加载器能启动。IL2CPP 首次初始化会生成接口文件，等待它完成再判断。可复用正常工作的加载器。其他平台、其他电脑和云存档的运行情况需要分别实测。 [BepInEx 官方安装说明](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html) · [已验证的 Windows x64 构建](https://builds.bepinex.dev/projects/bepinex_be/791/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.791%2Bb782da4.zip)
3. 请玩家先保存并退出，确认目标游戏进程已关闭。留存现有插件文件的备份于 `BepInEx/plugins` 之外，再把包内 `BepInEx/` 合并到实际游戏根目录。核对 `BepInEx/plugins/FmPlayerTalk/` 中同时有 `FmPlayerTalk.dll` 和 `ZstdSharp.dll`，且当前游戏只加载一份 FMTalking（旧名 FM Player Talk）。安装包包含模组及其依赖和说明。青训、FMLife、头像和其他模组都是可选的。
4. 启动游戏并载入一个生涯。用当前启动的 `BepInEx/LogOutput.log` 核对加载记录 `Loading [FMTalking 1.0.29]`。公开版号是 1.0，`1.0.29` 是本版构建号。某些 BepInEx 构建使用 `LogOutput.txt`。在「经理中心 → 交流」核对名单，再在「交流 → 设置」测试并保存启用玩家选择的模型连接。设置的「界面语言」可以跟随游戏，或手动切换简体中文、英文、日文、韩文。发一条普通游戏内消息，验证收到回复。安装、入口、模型连接和原版写入各自记下实测结果。

## 按症状处理

先用现有日志和界面缩小故障范围。能确定原因时只调整相关项，然后用相同入口复查结果。若环境未验证或证据不足，记录具体卡点和实测范围。

| 现象 | 先查这里 |
| --- | --- |
| 当前启动缺少 BepInEx 日志 | 核对实际启动目录、加载器的 Unity IL2CPP 类型和系统架构。等待首次初始化，再检查加载器自身的日志或错误提示。 |
| 有 BepInEx 日志，没载入模组 | 核对 DLL 路径、依赖和日志中的第一条相关错误。旧版可能显示 `Loading [FM Player Talk`。如发现重复副本，列明位置，请玩家确认保留哪一份。 |
| 模组加载了，没看到「交流」或名单 | 进入已加载的生涯和经理中心，等名单同步。再核对日志中经理中心入口、名册读取和助理核实的记录。用新开档或测试副本复现。 |
| 界面正常，模型没回复 | 到「交流 → 设置」使用「测试连接」。本机 Codex 模式核对本机安装与登录状态。本地模型模式核对回环地址、端口和准确模型 ID。远程 API 模式核对 HTTPS、授权和准确模型 ID。HTTP 模式使用兼容 OpenAI 的 Chat Completions 基础地址。测试通过后点击「保存并启用」。 |
| 对话能用，原版功能提示不可用 | 在「交流 → 设置 → 影响 FM 原版数据」查看实际游戏构建和本地存档状态。原版读写有独立的构建检查。兼容代码也识别较早的 Windows Steam `27.0.1+2503222`，空关系列表初始化要求 `27.1.0+2505117`。新生涯先保存本地档，云存档先另存为本地副本。未知构建保留版本检查，在已验证环境复测相关读写。 |
| 移动存档后历史没出现 | 首次聊天或重新保存前，读取内容完全相同的 `.fm`，在「交流 → 设置 → 存档历史重新关联」查找、预览、确认。跨电脑同时迁移模组数据目录的 `checkpoints/`，模型连接在新电脑单独配置。旧记录可按原完整路径核对。已有目标历史会受到保护。 |

## 保留玩家数据和修改记录

- 保留已有插件 ID `fmplayertalk.demo`、安装子目录 `FmPlayerTalk/` 和数据目录名称。默认数据目录是当前 Windows 用户的 `%LOCALAPPDATA%/Sports Interactive/Football Manager 27/FmPlayerTalk/`。「设置 → 使用说明 → 展开说明」里的「保存位置」显示实际目录。并行测试可只给测试 FM 进程设置 `FM_PLAYER_TALK_DATA_DIR`，值为专用的绝对路径，再使用另存的本地副本或全新生涯。
- 优先通过游戏内设置调整连接。测试远程 API 会发送示例请求，可能计入用量。模型或服务变更按玩家授权执行。排查时记录错误摘要、版本和相关路径，报告前隐去密钥、认证信息、私人路径和聊天内容。涉及实际写入、重装现有加载器、移走其他模组或者回退版本，先说明具体影响，保留原文件。
- 如需修改模组源码，从作者提供的源码副本开始，保留原始安装包。先复现问题，再修改和编译到独立目录。源码项目提供 `build.ps1 -VerifyConversation` 和 `build.ps1 -GameDir <游戏目录>`。在测试副本里复测相应功能，保留内置版本检查和存档隔离。下载包内的 DLL 用于直接安装。

## English quick reference

- Read this file and [README.md](README.md) from the extracted package. In an installed copy, look under `BepInEx/plugins/FmPlayerTalk/`. The guide is plain Markdown and works with any agent that can read local files. Automatic discovery depends on the agent host.
- Locate the actual game root with Steam **Browse local files** and confirm it contains `fm.exe`. Ask the player to save and exit. Keep an existing plugin backup outside `BepInEx/plugins`. Merge the package's `BepInEx/` folder into that game root. Check for `BepInEx/plugins/FmPlayerTalk/FmPlayerTalk.dll` and `ZstdSharp.dll`. Keep one active copy. Install BepInEx Unity IL2CPP separately if needed. [Official loader instructions](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html)
- The verified environment was a clean local Windows x64 Steam installation, FM `27.1.0+2505117`, BepInEx `6.0.0-be.791+b782da4`, a new career and a local mock model. Look for `Loading [FMTalking 1.0.29]` in the current `BepInEx/LogOutput.log` or `LogOutput.txt`. The public release is 1.0 and its internal build is 1.0.29. Then check the in-career **Manager Center → Talk (交流)** entry and roster. Check the selected model in **Settings (设置)** with **Test Connection (测试连接)** and **Save and Enable (保存并启用)**. Interface Language offers Follow Game, Simplified Chinese, English, Japanese and Korean. Test one ordinary in-game message. Local HTTP uses a loopback address and remote HTTP uses HTTPS. Both use Chat Completions with an exact model ID. Report what you actually verified.
- Check **FM game data (影响 FM 原版数据)** in Settings before trying native changes. Start with a disposable local save. A cloud career needs a local copy for native writes. Preserve the build guards. The older recognized `27.0.1+2503222` build has limited coverage. Other machines, platforms and cloud saves require separate checks.
- Keep the existing plugin ID `fmplayertalk.demo` and player data in `%LOCALAPPDATA%/Sports Interactive/Football Manager 27/FmPlayerTalk/`. Before playing or resaving a relocated career, load the identical `.fm` and use **Reconnect Save History (存档历史重新关联)** to find, preview and confirm the match. Copy `checkpoints/` when migrating and configure the model separately. For isolated testing, set `FM_PLAYER_TALK_DATA_DIR` to a new absolute path within the test game's process environment.
- Keep logs local unless the player authorizes sharing. Redact credentials and personal content. Use source copies and a test save for code changes. Keep the original package and verify the change before installing it into the player's active game.
