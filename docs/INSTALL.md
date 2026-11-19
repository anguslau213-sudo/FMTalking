# FMTalking 1.0

## English quick start

1. In Steam, open **Browse local files** for FM27. Install [BepInEx Unity IL2CPP for Windows x64](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html) if needed, start FM once to initialize it, and save and exit.
2. Merge the install ZIP's `BepInEx/` folder into the folder containing `fm.exe`. Keep one active copy of `BepInEx/plugins/FmPlayerTalk/`. When upgrading, back up the original plugin folder outside `BepInEx/plugins/` before replacing it.
3. Load a career and open **Manager Center → Talk → Settings**. In **Interface Language** choose Follow Game, Simplified Chinese, English, Japanese, or Korean. Test a model connection, then select **Save and Enable** and try one ordinary message.

This is a playable preview. The base mod was tested with Windows x64, Steam FM27 `27.1.0+2505117` and BepInEx `6.0.0-be.791+b782da4`. Save a separate local copy before trying actions that edit FM records. For setup help, ask your local agent to read `AGENTS.md`.


在 Football Manager 27 的「经理中心 → 交流」中，和当前生涯里的球员、职员及俱乐部高层自然聊天。你可以发展关系、请个人助理介绍球员，也可以和球员协商放弃已核实的位置或角色承诺。董事会注资属于实验功能。

使用本地智能体协助安装、排错或修改时，请先让它读取压缩包根目录的 `AGENTS.md`。安装后的插件目录也保留了这份说明。

## 运行环境

- **实测环境**为 Windows x64、Steam 版 FM27 `27.1.0+2505117`，使用 BepInEx Unity IL2CPP `6.0.0-be.791+b782da4`。独立安装中验证了新生涯的名单、个人助理、模拟模型收发及改名存档重新关联。异机、其他平台和云存档仍待实测。
- 原生读写也能识别 Windows Steam 构建 `27.0.1+2503222`，该构建功能有差异，例如空关系列表的初始化需要更新的构建。未知构建会停用相关原生读写，可在「交流 → 设置 → 影响 FM 原版数据」查看状态。
- 本包包含模组和 ZstdSharp。BepInEx Unity IL2CPP 加载器需单独安装。青训、FMLife 和头像包可按个人喜好另装。 [BepInEx 官方安装说明](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html) · [实测加载器官方 ZIP](https://builds.bepinex.dev/projects/bepinex_be/791/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.791%2Bb782da4.zip)

## 安装和首次使用

1. 保存并退出 FM。在 Steam 的游戏管理菜单选「浏览本地文件」，打开有 `fm.exe` 的目录。
2. 如果这里还没有可用的加载器，按上面的官方说明安装 **Windows x64、Unity IL2CPP** 版 BepInEx，启动 FM 一次完成初始化，再退出。已有可用加载器的玩家可以从下一步开始。
3. 解压 `FMTalking-1.0.zip`，把里面的 `BepInEx` 文件夹合并到游戏目录。确认以下文件在同一个插件子目录中。

   ```text
   BepInEx/plugins/FmPlayerTalk/FmPlayerTalk.dll
   BepInEx/plugins/FmPlayerTalk/ZstdSharp.dll
   ```

4. 启动 FM，进入一个生涯，在「经理中心 → 交流 → 设置」配置连接。点击「测试连接」，通过后点击「保存并启用」。选一名已显示的球员或职员，发送一条普通消息检查对话。

「交流 → 设置 → 界面语言」可以选择跟随游戏、简体中文、English、日本語或한국어。此设置影响模组的按钮、菜单和界面标签。游戏提供的姓名、球队等数据及历史对话内容按原样显示。

### 选用模型

| 设置 | 如何配置 |
| --- | --- |
| 本机 Codex | 使用本机已登录的 Codex。模型 ID 可留空，由 Codex 选择。 |
| 本地模型 | 先启动本机的 OpenAI 兼容服务，填写回环地址（例如 `http://127.0.0.1:1234/v1`，端口以服务为准）和模型 ID。密钥按服务要求填写。 |
| 远程 API | 填写服务商的 HTTPS 地址、模型 ID 和 API 密钥。连接测试和对话会向该服务发出请求，可能计入用量。 |

所选连接会收到生成当前回复所需的游戏人物资料、对话上下文，以及适用场景下可核实的俱乐部财务摘要。需要让这些内容留在本机时，选用完全在本机运行的模型服务。

## 存档、升级和卸载

- **测试原生写入。** 先在 FM 中另存一份本地副本，再在该副本里试亲密度、放弃承诺或注资，并保存 FM 生涯以保留变更。云存档玩家可以先建立本地副本。对话历史与具体 FM 存档版本配对。
- **恢复移动或改名后的历史。** 读取内容完全相同的本地 `.fm`，在新路径首次聊天或再次保存前，打开「交流 → 设置 → 存档历史重新关联」。查找同一存档的历史，预览后手动确认。对尚未建立索引的旧历史，也可填写原存档完整路径，即使原文件已经搬走。当前存档已有记录时会保留这些记录。
- **换电脑。** 把本地 `.fm` 和模组数据目录里的 `checkpoints` 一起带过去，再按上一步重新关联。默认模组数据目录是 `%LOCALAPPDATA%\Sports Interactive\Football Manager 27\FmPlayerTalk`，「交流 → 设置 → 使用说明」会显示实际路径。请在新电脑单独配置模型连接。
- **从 FM Player Talk 升级。** 退出 FM，把旧的 `BepInEx/plugins/FmPlayerTalk` 备份到游戏目录之外，再用新版包里的同名目录更新。插件目录及本地数据路径沿用旧版。请保留模组数据和 FM 存档，并确认 `BepInEx/plugins` 里只有一份本模组。
- **停用模组。** 退出 FM，把整个 `BepInEx/plugins/FmPlayerTalk` 文件夹移到 `BepInEx/plugins` 之外。对话历史留在模组数据目录。要撤回已写进游戏存档的变化，请读取写入前的 FM 存档副本。

## 遇到问题

- **经理中心没有「交流」入口**。载入生涯后，查看游戏目录的 `BepInEx/LogOutput.log`。发布版 1.0 的加载记录是 `Loading [FMTalking 1.0.29]`。没有日志时，先检查实际游戏目录和加载器。有日志时，查看加载行及随后出现的相关错误。
- **能看到入口，模型无法回复**。在交流设置里复查所选方式、接口地址和模型 ID，然后重新测试连接。本地模式还要确认模型服务正在运行。
- **报告问题时**，附上操作系统、游戏平台、FM 构建号、BepInEx 版本、模组版本、复现步骤和相关报错片段。请遮盖 API 密钥、账号信息、私人路径和聊天内容。

随包的 `ZstdSharp-LICENSE.txt` 和 `Zstandard-LICENSE.txt` 记录第三方组件的许可。
