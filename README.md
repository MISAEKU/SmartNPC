# Smart NPC

**凭空造一个会跟着你的智能 NPC** —— 可换装、可做表情动作、会自己说话、有自己的行为逻辑。

FFXIV 国服（XIVLauncherCN）Dalamud 插件 · Dalamud API 15 / 游戏版本 7.5 / .NET 10

> 本插件为**纯视觉**性质：不参与战斗、制作、任务等任何实际游戏流程；NPC 只做视觉表现，走游戏自身的对象管理器创建与销毁，不私自管理任何原生对象的生命周期。

## 功能特色

- **凭空召唤** —— 用游戏自身的 `ClientObjectManager` 在对象表里创建 NPC，不占用随从槽位，可随时送走
- **人类外观** —— 一键照抄你当前的外观，支持读取 Glamourer 设计换装
- **Penumbra 个人分组** —— 可选：把她挂进你选定的 Penumbra collection，让指定 mod 只对她一个人生效
- **活人感跟随** —— 侧后方站位、始终看着你；被拉开会**跑**着追，回到近处再走回来；带加减速与走/跑双动画
- **自主行为** —— 到位待够一会儿会自己在附近挑地方游荡、原地环顾，而不是死板粘在身后
- **绕行避障** —— 被柱子挡住会斜着绕开；附近挤了玩家/NPC 会互相避让
- **人格与台词** —— 安静 / 元气 / 傲娇 / 弱气 四种人格，按天气、时段、战斗、互动情境开口说话；换职业会说职业专属台词
- **表情回应** —— 先选中她，再对她做表情动作，她会给出语义合适的回应（军礼回军礼、跳舞回拍手、道歉回点头……绝非机械镜像）
- **待机姿势** —— 可选玩家 `/pose` 的姿势一~六，支持自动随机切换；说话时嘴巴会动、会配肢体动作
- **全图形化面板** —— `/smartnpc` 一屏控制所有功能，所有参数即时生效并落盘

## 下载安装

### 方式一：第三方插件库（推荐，可自动更新）

1. 打开游戏内 `/xlsettings`（或启动器设置）→ Dalamud → **第三方插件库**，填入：

   ```
   https://raw.githubusercontent.com/MISAEKU/DalamudPlugins/main/pluginmaster.json
   ```

2. 点击保存并重启游戏，在 `/xlplugins` → 插件安装 里搜索 **Smart Npc** 安装。

### 方式二：手动安装

1. 前往 [Releases](https://github.com/MISAEKU/SmartNPC/releases) 页面，下载最新版本的 `latest.zip`
2. 将 `latest.zip` 解压到一个**固定目录**（例如 `%APPDATA%\XIVLauncherCN\devPlugins\SmartNPC`，目录里应有 `SmartNpc.dll` 与 `SmartNpc.json`）
3. 打开启动器设置 → Dalamud → **开发插件位置**，添加上述目录（或 `/xlsettings` 内添加），重启游戏
4. `/xlplugins` → 开发工具页 → 启用 **Smart Npc**
5. 游戏内输入 `/smartnpc` 打开控制面板

## 指令

```
/smartnpc                 打开控制面板
/smartnpc config          打开设置界面
/smartnpc summon          召唤
/smartnpc dismiss         送走
/smartnpc look            照抄你当前外观
/smartnpc naked           裸体默认体型（确认骨架用）
/smartnpc say <内容>      说一句话
/smartnpc emote <动作>    做动作，如 emote wave
/smartnpc persona <档位>  切换人格：安静 / 元气 / 傲娇 / 弱气
/smartnpc status          打印当前状态
```

## 构建

```bat
build.cmd              :: Release 编译，产物在 dist\SmartNpc\
snapshot.cmd <版本号>  :: 改版本号 + 编译 + 出产物 + 快照备份
```

需要 .NET 10 SDK 与国服 XIVLauncherCN 的 Dalamud 开发程序集（`%APPDATA%\XIVLauncherCN\addon\Hooks\dev\`）。

## 诊断

排查问题时优先看插件自建的诊断文件：

```
%APPDATA%\XIVLauncherCN\pluginConfigs\SmartNpc\last-diag.txt
```

（Dalamud 的 `dalamud.log` 到 100MB 会停写且无轮转，插件状态信息可能进不去。）

## 免责声明

- 本项目为第三方工具，与 `SQUARE ENIX`、`盛趣游戏` 均无附属关系。
- 使用任何第三方工具都可能伴随潜在风险，请自行评估并承担后果。

---

Final Fantasy XIV © SQUARE ENIX CO., LTD. / 盛趣游戏。
本项目仅为社区维护的第三方工具。
