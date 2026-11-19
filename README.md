# FMTalking · 足球经理 27 / Football Manager 27

《足球经理 27》（FM27）的可玩对话模组雏形。 A playable dialogue mod prototype for Football Manager 27 (FM27, part of the FM series).

[English](#english) · [简体中文](#简体中文)

## English

FMTalking is a playable dialogue prototype for Football Manager 27. It adds an **交流 (Talk)** menu to Manager Center where you can chat with players, staff, and club executives from your current career.

The first preview lets you build relationships, ask your personal assistant to introduce a player, and discuss waiving a position or role promise the game has recorded. Board funding is experimental. Try it with a career you know, then change the dialogue, relationship rules, or pacing to fit the way you play. The fun is seeing what different players make of the same starting point.

**[Download FMTalking 1.0 from Releases](../../releases)** · **[Installation guide](docs/INSTALL.md)** · **[Guide for local coding agents](AGENTS.md)**

### Install and try it

1. In Steam, use **Browse local files** to find the FM27 folder containing `fm.exe`. Save your career and close the game.
2. Install BepInEx Unity IL2CPP for Windows x64 in that folder if you need a loader. Start FM27 once to generate its loader files, then close it. [BepInEx installation guide](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html) · [Tested BepInEx build](https://builds.bepinex.dev/projects/bepinex_be/791/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.791%2Bb782da4.zip)
3. From the release's **Assets**, download `FMTalking-1.0.zip`. Merge its `BepInEx` folder into the game folder. The plugin should be at `BepInEx/plugins/FmPlayerTalk/FmPlayerTalk.dll` with `ZstdSharp.dll` alongside it. GitHub's **Source code** archives are for building from source.
4. Open a career and choose **Manager Center → Talk → Settings** (经理中心 → 交流 → 设置 in Simplified Chinese). Test a model connection, save and enable it, and send an ordinary message to a listed character.

In **Talk → Settings → Interface Language**, choose **Follow Game**, **简体中文**, **English**, **日本語**, or **한국어**. The setting changes the mod's controls and labels. Player names, game data, and saved messages retain their original text.

FMTalking runs independently of academy, FMLife, and portrait packs. If you use a local coding agent to install or troubleshoot, ask it to read `AGENTS.md` first. The release includes the guide at the archive root and in the installed plugin folder. Upgrade, save-history recovery, and uninstall instructions are in the [installation guide](docs/INSTALL.md).

### Compatibility and save data

| Item | Status |
| --- | --- |
| Tested environment | Windows x64, Steam FM27 `27.1.0+2505117`, BepInEx `6.0.0-be.791+b782da4` |
| Tested workflows | Clean standalone installation, new career roster and assistant, a conversation with a mock local model, and explicit relinking of conversation history to a renamed save |
| Game data access | FM build checks control access to native records. The older Windows Steam build `27.0.1+2503222` has partial support in the code. |
| Still to verify | Other computers, platforms, game builds, and cloud-save workflows |

Start with a separate local copy of your career when testing changes to in-game records. Save that copy in FM to keep verified changes. Conversation history is kept locally and paired with a save version. When moving or renaming a career, relink history before chatting or saving again. [Save and migration instructions](docs/INSTALL.md)

### Model connections

| Mode | Setup |
| --- | --- |
| Codex on this computer | Use an already authenticated local Codex installation. The model ID is optional. |
| Local model | Start an OpenAI-compatible Chat Completions server. Enter its loopback endpoint, model ID, and any key the server requires. |
| Remote API | Enter the provider's HTTPS endpoint, model ID, and API key. Tests and chats can count toward provider usage. |

The chosen connection receives the dialogue context needed for a reply, which can include in-game character and relationship information and relevant club finance summaries. Choose a model service that runs entirely on your computer if you want those details to remain local.

### Build and customize

Use PowerShell 7 on Windows. Restore the pinned public build dependencies and run the offline checks from the repository root. After restore, those checks run without FM27 or an API key. For the game plugin build, use your own FM27 installation. Run it with BepInEx first to generate its `BepInEx/interop` assemblies.

```powershell
pwsh -File ./restore-dependencies.ps1
pwsh -File ./build.ps1 -VerifyConversation
pwsh -File ./build.ps1 -GameDir 'D:\SteamLibrary\steamapps\common\Football Manager 27'
```

Use the actual game path as `-GameDir`. The plugin sources are in [`src/`](src/), and the offline checks are in [`verify/`](verify/). To experiment with how characters respond, start with `BuildPrompt` in [`Conversation.cs`](src/Conversation.cs) and the topic-specific conversation files alongside it. Test your build with a separate career copy. Use `release.ps1` to build and verify an installable ZIP.

### Feedback

Feedback on what happened in a career is as welcome as code contributions. In [Issues](../../issues), include the FM build, platform, BepInEx version, FMTalking version, what you expected, and steps to reproduce what happened. If the mod failed to load, add the relevant BepInEx log lines with paths, credentials, and private dialogue redacted. For a pull request, describe the change and the checks you ran.

### License

FMTalking is released under the [MIT License](LICENSE). The [third-party notices](third-party/zstdsharp-0.8.8/) cover the bundled compression dependency.

---

## 简体中文

FMTalking 1.0 是《足球经理 27》的首个可试玩预览版。在「经理中心 → 交流」里，你可以跟当前生涯里的球员、职员和俱乐部高层聊天、发展关系，请个人助理介绍球员，或和球员协商放弃游戏已记录的位置或角色承诺。董事会注资是实验功能。你可以从熟悉的存档玩起，再按自己的喜好改对话提示词、关系规则和交流节奏。

**[下载 FMTalking 1.0](../../releases)** · **[完整安装说明](docs/INSTALL.md)** · **[本地智能体指南](AGENTS.md)**

### 安装和首次使用

1. 在 Steam 中打开 FM27 的「浏览本地文件」，找到包含 `fm.exe` 的目录。保存生涯并退出游戏。
2. 如尚未安装加载器，将 Windows x64 版 BepInEx Unity IL2CPP 安装到游戏目录。启动 FM27 一次，等它生成加载器文件后再退出。 [BepInEx 官方安装说明](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html) · [实测 BepInEx 版本](https://builds.bepinex.dev/projects/bepinex_be/791/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.791%2Bb782da4.zip)
3. 在 [Releases](../../releases) 页面，从 **Assets** 下载 `FMTalking-1.0.zip`，将其中的 `BepInEx` 文件夹合并到游戏目录。确认 `BepInEx/plugins/FmPlayerTalk/` 下同时有 `FmPlayerTalk.dll` 和 `ZstdSharp.dll`。GitHub 自动生成的 **Source code** 压缩包用于源码构建。
4. 载入生涯，打开「经理中心 → 交流 → 设置」。选好模型后先点「测试连接」，通过后点「保存并启用」。选择一位名单中的人物，发送一条普通消息检查对话。

在「交流 → 设置 → 界面语言」中，可以选择跟随游戏、简体中文、English、日本語或한국어。此设置影响模组的菜单、按钮和标签。球员姓名、游戏数据和历史消息会按原文显示。

FMTalking 可以独立运行。青训、FMLife 和头像包均为选装。如需本地智能体辅助安装或排错，请先让它阅读 `AGENTS.md`。安装包根目录和安装后的插件目录都留有这份指南。升级、恢复对话历史和停用模组请见[完整安装说明](docs/INSTALL.md)。

### 兼容范围和存档

| 项目 | 当前情况 |
| --- | --- |
| 实测环境 | Windows x64、Steam 版 FM27 `27.1.0+2505117`、BepInEx `6.0.0-be.791+b782da4` |
| 实测流程 | 独立安装、新开生涯读取名单和助理、本地模拟模型收发、为改名存档手动重新关联对话历史 |
| 原版数据读写 | 模组会检查 FM 构建号。代码对较旧的 Windows Steam 构建 `27.0.1+2503222` 有部分支持。 |
| 待实测 | 其他电脑、平台、游戏构建及云存档流程 |

测试亲密度、放弃承诺或注资等涉及原版数据的功能前，先在 FM 内另存一份本地生涯副本。完成变更后，在 FM 内保存这份副本。模组对话历史保存在本机，并与具体的游戏存档版本配对。移动或改名后，可在第一次聊天或再次保存前手动重新关联。 [存档与迁移操作](docs/INSTALL.md)

### 连接模型

| 模式 | 设置方法 |
| --- | --- |
| 本机 Codex | 使用已登录的本机 Codex。模型 ID 可以留空。 |
| 本地模型 | 在这台电脑启动兼容 OpenAI Chat Completions 的模型服务，填写回环地址、模型 ID。服务要求密钥时也请填写。 |
| 远程 API | 填写服务商提供的 HTTPS 接口、模型 ID 和 API 密钥。连接测试和对话可能计入服务商用量。 |

所选连接会收到生成回复所需的对话上下文，可能包括游戏人物、关系信息，以及相关场景下可核实的俱乐部财务摘要。如果希望这些内容留在本机，请选择完全在本机运行的模型服务。

### 从源码构建和修改

在 Windows 上使用 PowerShell 7，从仓库根目录运行以下命令。还原公开依赖后，离线检查无需安装 FM27 或填写 API 密钥。构建游戏插件还需要自己的 FM27 游戏目录，并事先使用 BepInEx 启动一次，生成 `BepInEx/interop` 程序集。

```powershell
pwsh -File ./restore-dependencies.ps1
pwsh -File ./build.ps1 -VerifyConversation
pwsh -File ./build.ps1 -GameDir 'D:\SteamLibrary\steamapps\common\Football Manager 27'
```

将 `-GameDir` 指向实际游戏目录。插件源码在 [`src/`](src/)，离线检查在 [`verify/`](verify/)。如果想调整人物怎么回应，可以先看 [`Conversation.cs`](src/Conversation.cs) 里的 `BuildPrompt`，再查看同目录按对话主题拆分的文件。改完先用独立存档副本验证，可以通过 `release.ps1` 构建和校验安装包。

### 反馈与贡献

欢迎分享试玩时的体验，也欢迎提交代码。请在 [Issues](../../issues) 里写明平台、FM 构建号、BepInEx 版本、FMTalking 版本、预期结果和复现步骤。加载失败时可附上相关的 BepInEx 日志片段，先遮盖私人路径、密钥、账号信息和对话内容。提交 Pull Request 时，请说明改了什么、做了哪些验证。

### 许可证

FMTalking 采用 [MIT 许可证](LICENSE)。随包附带的压缩组件适用其[第三方许可声明](third-party/zstdsharp-0.8.8/)。
