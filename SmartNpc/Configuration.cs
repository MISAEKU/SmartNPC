using System;
using Dalamud.Configuration;

namespace SmartNpc;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    /// <summary>配置结构版本。以后加字段时递增，用于迁移。</summary>
    public int Version { get; set; } = 1;

    // ───────────────────────── 移动与站位 ─────────────────────────

    /// <summary>常态跟随时站在玩家身后多远（米）。</summary>
    public float FollowBehind { get; set; } = 1.8f;

    /// <summary>常态跟随时的侧移（米）。偏一点才像"跟着"而不像"贴着"。</summary>
    public float SideOffset { get; set; } = 0.8f;

    /// <summary>超过这个距离就直接瞬移过去（米）。</summary>
    public float TeleportDistance { get; set; } = 14f;

    /// <summary>行走速度（米/秒）。FFXIV 玩家步行约 2.2。</summary>
    public float WalkSpeed { get; set; } = 2.2f;

    /// <summary>奔跑速度（米/秒）。FFXIV 玩家跑步约 4.5。</summary>
    public float RunSpeed { get; set; } = 4.6f;

    /// <summary>加速度（米/秒²）。</summary>
    public float Acceleration { get; set; } = 7.5f;

    /// <summary>减速度（米/秒²）。比加速度大，停脚才干脆。</summary>
    public float Deceleration { get; set; } = 11f;

    /// <summary>最大转向角速度（弧度/秒）。</summary>
    public float TurnRate { get; set; } = 3.6f;


    // ───────────────────────── 行为系统 ─────────────────────────

    /// <summary>
    /// 行为系统总开关（参考 TRUST 亲信战友的那套行为层次）。
    /// 关掉后退回"纯跟随"：一直站在身后，到位就站定，不会自己走开。
    /// </summary>
    public bool BehaviourEnabled { get; set; } = true;

    /// <summary>被拉开超过这个距离就<b>跑</b>着追（米）。</summary>
    public float RunDistance { get; set; } = 7.5f;

    /// <summary>回到这个距离以内才降回<b>走</b>（米）。比 <see cref="RunDistance"/> 小，
    /// 这个差值就是迟滞带 —— 没有它她会在边界反复横跳。</summary>
    public float WalkDistance { get; set; } = 4f;

    /// <summary>超过这个距离说明跟丢了，直接追并取消当前目标（米）。</summary>
    public float StrayDistance { get; set; } = 13f;

    /// <summary>近到这个距离以内算"到位"，转入待机（米）。</summary>
    public float ArriveDistance { get; set; } = 2.6f;

    /// <summary>游荡最远能走多远（米，相对锚点）。</summary>
    public float RoamRange { get; set; } = 7f;

    /// <summary>
    /// 速度倍率（v0.13.0）。基速本身已经改成**跟随本地玩家的实测速度**，
    /// 这个只用于微调：1 = 与你完全同速，调高一点追上来更利索。
    /// </summary>
    public float SpeedScale { get; set; } = 1f;

    /// <summary>游荡至少要离开玩家多远（米）。</summary>
    public float RoamMin { get; set; } = 2f;

    /// <summary>待机时随机插小动作的间隔下限（秒）。</summary>
    /// <summary>
    /// 站定时让她的头 / 视线跟着镜头看（v0.14.0）。
    ///
    /// 这是"头部面向"能做到的最稳的一档：用游戏自己的 look-at（<c>Character.LookAt</c>），
    /// 不动骨骼、不改姿态，只是告诉游戏"这个 actor 允许看向镜头"。
    /// 第三人称下镜头就在你背后，于是她看镜头 ≈ 看着你，比整个人拧过来自然得多。
    ///
    /// <para>⚠️ <b>v0.18.32 最终语义（用户裁定）：纯移动闸门，不再覆盖「始终注视玩家」——</b>
    /// 勾选 = 怎么看交给 <see cref="AlwaysGazeAtPlayer"/> 决定，
    /// 但<b>一走起来视线立刻收回</b>、头回正目视前方；
    /// 不勾选 = 同样交给 <see cref="AlwaysGazeAtPlayer"/> 决定，站定/行动都照它执行。</para>
    /// </summary>
    public bool CameraHeadLook { get; set; } = true;

    /// <summary>
    /// 始终注视玩家（#2，v0.18.29；v0.18.32 最终语义）。
    ///
    /// <para>注视频率开关（v0.18.32 起不再被「站定时用头/视线看」覆盖）：
    /// <b>勾选</b> = 一直看着你（行动中指头/眼视线追踪，超出转幅时收回，
    /// 绝不边走边转身）；
    /// <b>不勾选</b> = 自然张望 —— 约 60% 的时间在看你（一次 10~20 秒），
    /// 剩下约 40% 视线移开（一次 5~10 秒；整段保持、不逐帧重掷，避免抖动）。</para>
    ///
    /// <para>⚠️ 「始终不注视玩家」（<see cref="NeverGazeAtPlayer"/>）优先级最高，
    /// 勾选后本开关与移动闸门全部失效。</para>
    /// </summary>
    public bool AlwaysGazeAtPlayer { get; set; } = true;

    // ── 自然张望参数（v0.18.34，面板可热调）──
    // 「始终注视玩家」不勾选时生效：按 GazeLookPercent 的时间占比在看你/移开视线，
    // 每段时长在对应区间内随机。整段保持、不逐帧重掷。

    /// <summary>自然张望：多少比例的时间在看你（**%**，其余移开视线）。</summary>
    public float GazeLookPercent { get; set; } = 60f;

    /// <summary>自然张望：一次"在看你"的最短时长（秒）。</summary>
    public float GazeLookMin { get; set; } = 10f;

    /// <summary>自然张望：一次"在看你"的最长时长（秒）。</summary>
    public float GazeLookMax { get; set; } = 20f;

    /// <summary>自然张望：一次"移开视线"的最短时长（秒）。</summary>
    public float GazeAwayMin { get; set; } = 5f;

    /// <summary>自然张望：一次"移开视线"的最长时长（秒）。</summary>
    public float GazeAwayMax { get; set; } = 10f;

    /// <summary>
    /// 可被玩家选中为目标（#5，v0.18.35）。
    ///
    /// <para>勾选 = 每帧保持她 <c>GameObject.TargetableStatus</c> 的可选中位（0x02），
    /// 鼠标点选 / Tab 都能选中她；不勾 = 每帧清除该位（恢复默认的不可选中）。
    /// 位语义经 Ktisis <c>GameObjectEx.SetTargetable</c> 的 IL 实锤。</para>
    /// </summary>
    public bool TargetableByPlayer { get; set; } = true;

    /// <summary>
    /// 点击兜底（v0.18.39）。
    ///
    /// <para>第二轮 trace 的实锤：她在屏上可点选清单里、<c>GetIsTargetable()=Y</c>、
    /// 鼠标悬停也认得出她，但**点下去之后**<c>TargetSystem.Target</c> 长时间不是她 ——
    /// 说明"能不能选"的字节没问题，是"点击 → 硬目标"这段接线没接上。</para>
    ///
    /// <para>勾选 = 捕捉左键按下沿，只在「悬停确实是她 且 当前目标不是她」这一极窄条件下
    /// 替游戏把硬目标设上。不勾 = 完全不碰目标系统，只保持可选中位。</para>
    /// </summary>
    public bool TargetClickFallback { get; set; } = true;

    /// <summary>
    /// 始终不注视玩家（v0.18.31，用户要求新增）。
    ///
    /// <para>勾选 = 她完全不看玩家（头/眼从不追踪你）。**优先级最高**：
    /// 覆盖「站定时用头/视线看」与「始终注视玩家」的一切组合。</para>
    /// </summary>
    public bool NeverGazeAtPlayer { get; set; }

    // ── 说话配音（v0.18.36 引入，v0.18.37 改为真正的角色语音）──

    /// <summary>她说话时是否发声。关闭 = 只有字幕 / 聊天栏。</summary>
    public bool VoiceEnabled { get; set; } = true;

    /// <summary>
    /// <b>嗓音</b>编号 —— 就是角色创建时选的那种声音（写进 <c>Character.Vfx.VoiceId</c>）。
    /// <b>0 = 不改动</b>，沿用抄外观时从玩家继承来的嗓音。
    ///
    /// <para>它决定"她用哪种嗓音说话"；做动作时的配音也走同一个嗓音。</para>
    /// </summary>
    public int VoicePresetId { get; set; }

    /// <summary>
    /// <b>语音行号</b> —— 说话时具体发哪一声
    /// （<c>VfxContainer.LoadCharacterSound</c> 的第一个参数）。<b>0 = 不发声</b>。
    ///
    /// <para>行号与实际声音之间没有可用的对照表（离线读不到游戏数据表），
    /// 所以面板提供<b>试听</b>按钮：选好行号点一下就能听到，按耳朵挑。</para>
    /// </summary>
    public int VoiceLineId { get; set; }

    // ── 头顶说话气泡（v0.18.49）──

    /// <summary>
    /// 她说话时头顶弹<b>游戏原生气泡</b>（走 <c>Character.YellBalloon.OpenBalloon</c>，
    /// 和游戏里 NPC 喊话那一套是同一套，不是我们画的 UI）。
    /// 关掉 = 只在聊天栏看到台词。
    /// </summary>
    public bool BalloonEnabled { get; set; } = true;

    /// <summary>
    /// 气泡停留多久 —— 直接喂给 <c>NpcYellBalloon.OpenBalloon</c> 的 <c>PlayTimer</c>，
    /// 到点由我们自己收回。
    ///
    /// <para>⚠️ <c>PlayTimer</c> 的单位还没实锤：若气泡<b>一闪就没</b>，说明游戏按帧递减，
    /// 把这里调大（比如 240 ≈ 60fps 下的 4 秒）即可，不用等我改代码。</para>
    /// </summary>
    public float BalloonSeconds { get; set; } = 4f;

    public float IdleGestureMin { get; set; } = 6f;

    /// <summary>待机时随机插小动作的间隔上限（秒）。</summary>
    public float IdleGestureMax { get; set; } = 15f;

    /// <summary>待机多久之后她会环顾一下（秒）。</summary>
    public float LookAroundAfter { get; set; } = 20f;

    /// <summary>
    /// 玩家静止这么久之后她会过来搭话（秒）。<b>0 = 不主动搭话</b>。
    /// </summary>
    public int ChatTriggerIdle { get; set; } = 45;

    /// <summary>是否让她对玩家的状态有反应（战斗中、采集制作中、被甩开时开口）。</summary>
    public bool SceneReactions { get; set; } = true;

    // ───────────────────────── 人格与对话 ─────────────────────────

    /// <summary>她的名字。</summary>
    public string NpcName { get; set; } = "缇娅";

    /// <summary>人格（对应 <c>NpcBrain.Persona</c> 的枚举值：0 安静 / 1 元气 / 2 傲娇 / 3 弱气）。</summary>
    public int Persona { get; set; } = 1;

    // ── 台词库（v0.18.50）──

    /// <summary>
    /// 她怎么称呼<b>玩家</b>。台词里写的是 <c>{{称谓}}</c>，出口替换成这个值。
    /// 留空 = 回退到 <c>NpcLines.DefaultTitle</c>（「主人」）。
    /// </summary>
    public string PlayerTitle { get; set; } = "主人";

    /// <summary>天气变了说一句（晴 / 雨 / 雪）。</summary>
    public bool WeatherLines { get; set; } = true;

    /// <summary>时段变了说一句（白天 / 黄昏 / 夜晚）。</summary>
    public bool DayPhaseLines { get; set; } = true;

    /// <summary>战斗台词（进入 / 交战中 / 掉血 / 胜利 / 撤退）。</summary>
    public bool CombatLines { get; set; } = true;

    /// <summary>玩家互动台词（被选中 / 被注视 / 被靠近 / 你受伤时她关心）。</summary>
    public bool TouchLines { get; set; } = true;

    /// <summary>
    /// 玩家<b>换了职业</b>说一句跟那个职业有关的话（v0.18.51）。
    ///
    /// <para>⚠️ 触发纪律（用户明确要求）：<b>切换后只触发一次</b>。
    /// 记住当前职业，下一次开口必须是切到了<b>与现在不同</b>的职业。
    /// 所以"骑士→武士"说一次、"武士→骑士"再说一次，而"骑士→骑士"一句都不说。</para>
    /// </summary>
    public bool JobLines { get; set; } = true;

    /// <summary>
    /// 两句台词之间的<b>全局最短间隔</b>（秒，v0.18.51）。
    ///
    /// <para>用户反馈「现在台词太频繁了」。这个闸门对<b>所有</b>台词生效
    /// （闲聊 / 天气 / 时段 / 战斗 / 互动），玩家主动触发的除外。</para>
    ///
    /// <para>0 = 不冷却（回到 v0.18.50 的老手感）。建议 10~30 秒。</para>
    /// </summary>
    public float LineCooldownSeconds { get; set; } = 12f;

    /// <summary>
    /// 贴到多近算「被靠近」（米，v0.18.51）。
    ///
    /// <para>玩家站定并靠到这个距离以内持续 3 秒，她才说"被靠近"那类台词。</para>
    ///
    /// <para>v0.18.52：下限放宽到 <b>0.05 米</b>（用户要求）——
    /// 想让她"几乎贴上了才开口"就往小调。</para>
    /// </summary>
    public float CloseTouchDistance { get; set; } = 2f;

    /// <summary>
    /// 台词配上肢体动作的概率（**%**，v0.18.51）。
    ///
    /// <para>用户要求"不是每句台词都要附带表情动作"。
    /// 台词的含义决定**配哪个**动作，这个百分数决定**配不配**。</para>
    ///
    /// <para>0 = 说话永远不比划（只保留待机时的小动作）；100 = 只要匹配得上就一定比划。</para>
    /// </summary>
    public float GestureChancePercent { get; set; } = 60f;

    /// <summary>两个肢体动作之间的最小间隔（秒，v0.18.51）。调大 = 她更安静。</summary>
    public float GestureMinGapSeconds { get; set; } = 6f;

    /// <summary>
    /// 说话时<b>嘴巴开合</b>（v0.18.53）。
    ///
    /// <para>⚠️ 这不是表情动作 —— 走的是 <c>TimelineContainer.LipsOverride</c>（+0x2E8），
    /// 是叠在身体动画之上的**口型层**，所以她可以一边说话一边比划，两者互不干扰。</para>
    ///
    /// <para>关掉就完全不碰口型字段。</para>
    /// </summary>
    public bool LipSyncEnabled { get; set; } = true;

    /// <summary>
    /// 嘴巴开合一次的时间（秒，v0.18.53）。调小 = 嘴动得快（像急急地说）。
    /// </summary>
    public float LipFlapSeconds { get; set; } = 0.16f;

    /// <summary>
    /// 玩家<b>选中她</b>并对她做表情动作时，她回一句（v0.18.53）。
    ///
    /// <para>⚠️ 触发纪律：<b>必须玩家当前目标就是她</b>，且是一个<b>新的</b>表情动作。
    /// 没选中她 → 一句都不说。连做两个动作之间有 3 秒防连击。</para>
    /// </summary>
    public bool EmoteReactions { get; set; } = true;

    /// <summary>
    /// 时段以什么为准（v0.18.51）：0 = 游戏内艾欧泽亚时间（默认），1 = 现实本地时间。
    ///
    /// <para>⚠️ 艾时一天只有 70 分钟真实时间，和现实时钟<b>完全不同步</b> ——
    /// 现实凌晨一点，游戏里完全可能是黄昏。想让她跟着你的现实作息说话就改成 1。</para>
    /// </summary>
    public int DayPhaseSource { get; set; }

    /// <summary>
    /// 天气台词用<b>游戏里那个真实名词</b>（v0.18.51）。
    ///
    /// <para>开：「碧空」→"今天真是碧空如洗呢~！"，「薄雾」→"今天起了点薄雾呢。"
    /// 关：只说归类后的晴/雨/雪台词。</para>
    /// </summary>
    public bool WeatherNameLines { get; set; } = true;

    /// <summary>
    /// 副本 / 特殊天气（妖雾、极光之类）也开口（v0.18.51）。
    /// 默认关 —— 在没有雨的副本里喊"下雨了"很出戏；打开后会沿用那个名词说一句。
    /// </summary>
    public bool SpecialWeatherLines { get; set; }

    /// <summary>
    /// 手动钉死艾欧泽亚小时（-1 = 用<b>游戏内</b>时间自动算）。
    ///
    /// <para>只是个校准旋钮：万一 <c>ClientTime.EorzeaTimeMilliseconds</c> 的单位
    /// 和我们的假设不一致，用户在这里填个 0~23 就能立刻对上，不用等改代码。</para>
    /// </summary>
    public int EorzeaHourOverride { get; set; } = -1;

    /// <summary>是否让她自言自语。</summary>
    public bool AutoChatter { get; set; } = true;

    /// <summary>两句自言自语之间的最短间隔（秒）。</summary>
    public int MinIdleSeconds { get; set; } = 18;

    /// <summary>两句自言自语之间的最长间隔（秒）。</summary>
    public int MaxIdleSeconds { get; set; } = 40;

    /// <summary>聊天栏也显示她说的话（关掉则只在日志里）。</summary>
    public bool ShowInChat { get; set; } = true;

    // ───────────────────────── 外观设计 ─────────────────────────

    /// <summary>召唤后自动套用的 Glamourer 设计（<see cref="Guid.Empty"/> = 不套）。</summary>
    public Guid GlamourerDesignId { get; set; }

    // ───────────────────────── Penumbra 分组 ─────────────────────────

    /// <summary>
    /// v0.18.55：召唤时给她挂上的 Penumbra 分组（<see cref="Guid.Empty"/> = 不挂）。
    /// 挂上后她的资源就按这个分组解析，mod 只对她一个人生效 —— 也就是
    /// Penumbra 对象面板里能看到的那条「个人指派」。
    /// </summary>
    public Guid PenumbraCollectionId { get; set; }

    /// <summary>选中分组时的显示名备份（列表里查不到这个名字时给 UI 回退显示）。</summary>
    public string PenumbraCollectionName { get; set; } = string.Empty;

    // ───────────────────────── 待机动作 ─────────────────────────

    /// <summary>
    /// v0.18.57：她的待机姿势（序号 1~6 = 玩家 /pose 的姿势一~六，0 = 游戏默认待机）。
    /// 照玩家换姿势的机制播：站着时保持该姿势，走动自动中断、停下自动恢复。
    /// </summary>
    public int IdleEmoteRowId { get; set; }

    /// <summary>待机姿势的显示名备份（候选列表查不到时给 UI 回退显示）。</summary>
    public string IdleEmoteName { get; set; } = string.Empty;

    /// <summary>v0.18.58：她站立够久后自动随机换一个站立姿势。</summary>
    public bool IdleAutoRandom { get; set; }

    /// <summary>放松等待时间（秒）：停止移动后，站立不动满这么久就进入姿势（下限 0 = 立即）。</summary>
    public float IdleRelaxSeconds { get; set; } = 15f;

    /// <summary>
    /// v0.18.66：随机姿势切换间隔（秒）——「自动随机」开着时，进入姿势后<b>每隔多久自己换一个</b>。
    /// 与「放松等待时间」解耦：那个管"停下后多久首次进姿势"，这个管"进了之后多久换下一个"。
    /// （v0.18.64~65 两者共用 Relax 值，用户把 Relax 调小后起手动画每 3 秒重播一次，
    /// 实测表现就是"进入动画重复两三次"。实际生效下限 5 秒。）
    /// </summary>
    public float IdleSwitchSeconds { get; set; } = 20f;

    /// <summary>走路动画播放速度微调（1 = 按实际移动速度自动匹配）。</summary>
    public float WalkSpeedScale { get; set; } = 1f;

    /// <summary>
    /// 手填的行走时间轴行号（0 = 用自动发现的结果）。
    /// 自动发现失败时可以在面板里手填。
    /// </summary>
    public int WalkTimelineId { get; set; }

    // ───────────────────────── 避障 ─────────────────────────

    /// <summary>被挡住时是否会自动绕行。关掉她就会直愣愣地撞在柱子上。</summary>
    public bool AvoidanceEnabled { get; set; } = true;

    /// <summary>互相避让的作用半径（米）。</summary>
    public float SeparationRadius { get; set; } = 1.6f;

    /// <summary>被推开的最大强度。</summary>
    public float SeparationStrength { get; set; } = 1.5f;

    /// <summary>多久算"卡住"（秒）。</summary>
    public float StuckTime { get; set; } = 0.35f;

    /// <summary>绕行持续多久（秒）。</summary>
    public float DetourDuration { get; set; } = 1.1f;

    /// <summary>绕行时斜着走多远（米）。</summary>
    public float DetourLength { get; set; } = 2.6f;

    /// <summary>两次绕行之间的冷却（秒）。</summary>
    public float DetourCooldown { get; set; } = 0.8f;


    // ───────────────────────── 其他 ─────────────────────────

    /// <summary>召唤后自动照抄玩家外观。</summary>
    public bool CopyPlayerOnSummon { get; set; } = true;

    /// <summary>骑乘中隐藏她（骑乘时宠物位置很挤）。</summary>
    public bool HideWhileRiding { get; set; }

    // ───────────────────────── 细则参数（v0.17.0 起面板可调）─────────────────────────

    /// <summary>头 / 眼能转到的最大角度（**度**）。超过才可能触发"转身追 / 干脆不看"。</summary>
    public float HeadTurnLimitDeg { get; set; } = 66f;

    /// <summary>超转幅时选择"转身追"的概率（**%**），其余为"干脆不看"。</summary>
    public float TurnChancePercent { get; set; } = 80f;

    /// <summary>高低差超过这个值（米）才允许瞬移。调大 = 更少瞬移、更多自己爬。</summary>
    public float TeleportHeight { get; set; } = 20f;

    /// <summary>转身动作的播放速度倍率（1 = 正常）。</summary>
    public float TurnPlaybackSpeed { get; set; } = 1f;

    /// <summary>
    /// 她到位进入待机后，玩家离开她超过这个距离（米）就重新跟随（v0.17.6）。
    ///
    /// ⚠️ 语义按用户定义：<b>不是</b>"玩家速度超阈值就跟随"（v0.17.5 的错误实现），
    /// 而是看<b>玩家离她多远</b>。这个距离可以自定义：调小 = 玩家刚起步她就跟，
    /// 调大 = 允许玩家自己先走一段。
    /// </summary>
    public float FollowAgainDistance { get; set; } = 4.5f;

    /// <summary>
    /// 换区后自动重新召唤（v0.17.8）。
    ///
    /// 换区时游戏会清掉我们的对象，旧版只能提示"请重新召唤"——每次换区都要手动
    /// 点一次太麻烦（用户反馈）。开启后：她消失 → 等新区域加载稳定 → 自动在
    /// 你身边重新出现，接着跟。
    /// </summary>
    public bool AutoRespawnAfterZoneChange { get; set; } = true;

    /// <summary>自动重召唤前等待的秒数（等区域加载稳定，太快会创建失败）。</summary>
    public float AutoRespawnDelay { get; set; } = 3f;

    /// <summary>
    /// 卡住 / 跟不上多少秒后强制瞬移到你身边（秒）。<b>0 = 关闭</b>（v0.18.2）。
    ///
    /// <para>为什么需要它：避障被挡住时原本只有两条出路 ——
    /// 「矮障跳过去」与「卡住 3 秒且离目标 2 米以外才瞬移」。
    /// 于是出现一个<b>死锁</b>：被<b>实心墙 / 关着的门</b>挡住，
    /// 而目标恰好就在 2 米以内（跟随时你的站位点本来就在身后约 1.8 米），
    /// 跳不了（实心墙不是矮障）、也不够远（不满足 distance &gt; 2f）——
    /// 她就<b>永远站在原地</b>，而跟随触发又因为"你就在旁边"而不生效。
    /// 用户实测：「跟着跟着突然愣在原地不动」。</para>
    ///
    /// <para>这里给一个兜底上限：被挡超过这个秒数就无条件瞬移到你身边，
    /// 不再受距离与障碍类型限制。调小 = 更早脱困；调大 = 更能自己想办法。
    /// 建议 2~5 秒。</para>
    /// </summary>
    public float StuckTeleportSeconds { get; set; } = 3f;

    /// <summary>
    /// 出生点：出现在你<b>身后</b>多少米（v0.18.3，负数 = 面前，0 = 紧贴你的位置）。
    /// 召唤与换区自动重召唤共用。不再死钉在玩家坐标上。
    /// </summary>
    public float SpawnOffsetBehind { get; set; } = 1.2f;

    /// <summary>出生点：侧向偏移多少米（召唤/重召唤共用）。</summary>
    public float SpawnOffsetSide { get; set; } = 0.6f;

    /// <summary>
    /// 跟随强度（引力，v0.18.4）。1 = 与玩家同速追；
    /// <b>&gt;1 = 落后时追得更凶</b>（奔跑提速、迟滞带外行走也提速，上限压在跑走分界之下不会横跳）；
    /// &lt;1 = 更松弛。建议 1.0~1.5，太高会显得她一刻不停地怼着你。
    /// </summary>
    public float FollowStrength { get; set; } = 1.25f;

    /// <summary>
    /// 正在移动时不允许自动插小动作 / 说话（v0.17.3）。
    /// 开着更干净，关掉则允许她边走边做动作（更容易平移）。
    /// </summary>
    public bool BlockGesturesWhileMoving { get; set; } = true;

    /// <summary>诊断开关：开启后在转身期间**逐帧**写 pluginConfigs\SmartNpc\turn-trace.txt。
    /// 默认关闭（v0.16.9 起）—— 那个文件是定位"动画被谁清掉"时临时用的，平时不该产生。
    /// </summary>
    public bool TurnTraceDebug { get; set; }

    /// <summary>把配置限制在合理范围内，防止手滑改出奇怪的值。</summary>
    ///
    /// ⚠️ **别在这里用会抛异常的钳位。**
    /// <c>Math.Clamp(v, min, max)</c> 在 <c>min &gt; max</c> 时抛 <c>ArgumentException</c>，
    /// 而 <c>min/max</c> 又是别的字段算出来的 —— 只要用户从旧版本升级、某个字段在
    /// 磁盘配置里不存在（反序列化成 0），依赖它的钳位就会当场炸掉，
    /// 而插件是在构造函数里加载配置的，炸了就是**整个插件加载不出来**。
    ///
    /// 实测踩过：<c>RoamMin = Clamp(RoamMin, 0.5f, RoamRange - 0.5f)</c>，
    /// 旧配置没有 <c>RoamRange</c> → 它是 0 → max 变成 -0.5 → 抛异常 → 插件起不来。
    ///
    /// 所以下面统一用 <see cref="Clamp"/>：它先摆平 min/max 的顺序，永不抛。
    /// </summary>
    public void Sanitize()
    {
        // ── 基础量程（不依赖其他字段，随便钳）──
        FollowBehind       = Clamp(FollowBehind, -6f, 6f);   // 负值 = 站到玩家前面
        SideOffset         = Clamp(SideOffset, -8f, 8f);
        WalkSpeed          = Clamp(WalkSpeed, 0.5f, 6f);
        RunSpeed           = Clamp(RunSpeed, WalkSpeed + 0.5f, 12f);
        Acceleration       = Clamp(Acceleration, 1f, 30f);
        Deceleration       = Clamp(Deceleration, 1f, 40f);
        TurnRate           = Clamp(TurnRate, 0.5f, 12f);
        IdleGestureMin     = Clamp(IdleGestureMin, 1f, 120f);
        IdleGestureMax     = Clamp(IdleGestureMax, IdleGestureMin, 300f);
        LookAroundAfter    = Clamp(LookAroundAfter, 3f, 300f);
        ChatTriggerIdle    = Clamp(ChatTriggerIdle, 0, 600);
        MinIdleSeconds     = Clamp(MinIdleSeconds, 5, 600);
        MaxIdleSeconds     = Clamp(MaxIdleSeconds, MinIdleSeconds, 1800);
        Persona            = Clamp(Persona, 0, 3);
        SeparationRadius   = Clamp(SeparationRadius, 0.3f, 6f);
        SeparationStrength = Clamp(SeparationStrength, 0f, 5f);
        StuckTime          = Clamp(StuckTime, 0.05f, 3f);
        DetourDuration     = Clamp(DetourDuration, 0.1f, 5f);
        DetourLength       = Clamp(DetourLength, 0.3f, 12f);
        DetourCooldown     = Clamp(DetourCooldown, 0f, 10f);
        // v0.18.49 头顶气泡：太短看不清，太长会一直挂头上挡视线。
        // 上限放宽到 30 是故意的 —— 万一 PlayTimer 按帧递减（4 秒 ≈ 240），用户能自己拉大试出来。
        BalloonSeconds     = Clamp(BalloonSeconds, 0.5f, 30f);
        // v0.18.45：允许留空 —— 留空 = 显示游戏分配的默认名（"未命名显示默认 ID"）。
        // 各消费方自行回退：显示名 NpcNaming.EffectiveName，气泡/台词用"她"。
        NpcName = (NpcName ?? string.Empty).Trim();

        // v0.18.50：玩家称谓允许留空（回退默认「主人」），但要去空白，
        // 否则"主人 "会被塞进台词里，看上去像多打了个空格。
        PlayerTitle = (PlayerTitle ?? string.Empty).Trim();

        // -1 = 自动（跟着游戏内艾欧泽亚时间走），0~23 = 手动钉死。
        EorzeaHourOverride = (int)Clamp(EorzeaHourOverride, -1, 23);

        // v0.18.51：台词节奏 / 动作密度 / 时段来源
        LineCooldownSeconds = Clamp(LineCooldownSeconds, 0f, 300f);
        // v0.18.52：下限从 0.5 放宽到 **0.05** —— 用户要"几乎贴在一起才算贴近"。
        CloseTouchDistance  = Clamp(CloseTouchDistance, 0.05f, 8f);
        GestureChancePercent = Clamp(GestureChancePercent, 0f, 100f);
        GestureMinGapSeconds = Clamp(GestureMinGapSeconds, 0f, 120f);
        DayPhaseSource      = (int)Clamp(DayPhaseSource, 0, 1);

        // v0.18.53：说话开合（口型层）
        LipFlapSeconds = Clamp(LipFlapSeconds, 0.05f, 0.6f);

        // ── 有依赖链的：必须按「被依赖者先钳」的顺序 ──
        //
        // 迟滞带至少 0.8 米：WalkDistance 必须严格小于 RunDistance，
        // 否则她会在边界反复"跑两步→走两步→又跑"，看起来很蠢。
        WalkDistance   = Clamp(WalkDistance, 0.5f, 30f);
        RunDistance    = Clamp(RunDistance, WalkDistance + 0.8f, 40f);
        StrayDistance  = Clamp(StrayDistance, RunDistance + 1f, 60f);
        TeleportDistance = Clamp(TeleportDistance, StrayDistance + 1f, 60f);
        ArriveDistance = Clamp(ArriveDistance, 0.5f, RunDistance);

        // 游荡区间：先定上限再定下限，且下限必须小于上限。
        RoamRange = Clamp(RoamRange, 1f, 30f);
        RoamMin   = Clamp(RoamMin, 0.5f, RoamRange - 0.5f);

        // ── 细则参数 ──
        HeadTurnLimitDeg  = Clamp(HeadTurnLimitDeg, 20f, 160f);
        TurnChancePercent = Clamp(TurnChancePercent, 0f, 100f);
        TeleportHeight    = Clamp(TeleportHeight, 4f, 40f);
        TurnPlaybackSpeed = Clamp(TurnPlaybackSpeed, 0.25f, 3f);

        FollowAgainDistance = Clamp(FollowAgainDistance, 1f, 30f);
        StuckTeleportSeconds = Clamp(StuckTeleportSeconds, 0f, 30f);
        SpawnOffsetBehind    = Clamp(SpawnOffsetBehind, -5f, 5f);
        SpawnOffsetSide      = Clamp(SpawnOffsetSide, -5f, 5f);
        FollowStrength       = Clamp(FollowStrength, 0.5f, 2.5f);
        AutoRespawnDelay    = Clamp(AutoRespawnDelay, 1f, 15f);

        // ── 自然张望参数 ──
        GazeLookPercent = Clamp(GazeLookPercent, 0f, 100f);
        GazeLookMin = Clamp(GazeLookMin, 0.5f, 120f);
        GazeLookMax = Clamp(GazeLookMax, GazeLookMin, 300f);
        GazeAwayMin = Clamp(GazeAwayMin, 0.5f, 120f);
        GazeAwayMax = Clamp(GazeAwayMax, GazeAwayMin, 300f);

        // 嗓音 0 = 不改动（沿用继承来的）；行号 0 = 不发声。上限只防手滑填出离谱值。
        VoicePresetId = (int)Clamp(VoicePresetId, 0, 255);
        VoiceLineId   = (int)Clamp(VoiceLineId, 0, 9999);
    }

    /// <summary>
    /// 永不抛的钳位：<c>min &gt; max</c> 时自动交换两端。
    ///
    /// <see cref="Math.Clamp(float,float,float)"/> 在 <c>min &gt; max</c> 时抛
    /// <c>ArgumentException</c>，而插件配置是在构造函数里加载的 —— 在那里抛异常
    /// 等于整个插件起不来。为了让「旧配置缺字段」永远只降级成「参数不好看」，
    /// 而不是「插件加载失败」，这里不用它。
    /// </summary>
    private static float Clamp(float value, float min, float max)
    {
        if (min > max) (min, max) = (max, min);
        return Math.Clamp(value, min, max);
    }

    /// <summary>整数版 <see cref="Clamp(float,float,float)"/>。</summary>
    private static int Clamp(int value, int min, int max)
    {
        if (min > max) (min, max) = (max, min);
        return Math.Clamp(value, min, max);
    }
}