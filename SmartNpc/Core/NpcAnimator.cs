using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;

namespace SmartNpc.Core;

/// <summary>
/// 表情与动作。
///
/// <para>── ⚠️⚠️ 本类最重要的一条事实（v0.18.0，诊断文件实锤）──</para>
///
/// <para><b>写进 <c>Timeline.BaseOverride</c> 的值会被引擎冲掉。</b>
/// 诊断记录（pluginConfigs\SmartNpc\trace-action.txt）里的原样三行：</para>
/// <code>
/// 21:57:18.641 PLAY「点头」tl=739          ← 我们写 BaseOverride=739 并 SetMode(AnimLock)
/// 21:57:20.851 STOP ... mode=Normal→Normal base=0→13
///                                            ↑ 读回来 Mode 已是 Normal、BaseOverride 已是 0
/// </code>
/// <para>可见：动画状态<b>一次都没留住</b>。走路那条通路之所以看着正常，
/// 是因为 <see cref="SetWalking"/> 是<b>每帧调用</b>的 —— 诊断里
/// <c>BaseOverride(游戏实际值)=13</c> 就是它每帧补写出来的。</para>
///
/// <para>这条事实同时解释了两个长期现象：</para>
/// <list type="bullet">
/// <item><b>"打断没效果"</b> —— 表情从来没有真正播起来过（只剩下 sequencer 里
/// 那条一次性 <c>PlayTimeline</c> 在孤零零地跑）。根本就没有"一个正在播的表情"可打断，
/// 用户看到的只是平移 + 抽一下。</item>
/// <item><b>"边走边做动作"</b> —— <see cref="SetWalking"/> 每帧把
/// <c>BaseOverride</c> 写成走路(id 13)，而 sequencer 里那条一次性动作还在播。
/// 两条并存，画面就是"边走边比划"。</item>
/// </list>
///
/// <para>所以本类在 v0.18.0 做的事只有一件：<b>每帧无条件补写，且只有一个权威出口</b>。
/// 优先级固定为 移动 &gt; 表情 &gt; 转身 &gt; 待机，每帧只写其中<b>一个</b>值，
/// 不再有两套通路互相抢字段。所有"播一次就等着它跑完"的写法都作废。</para>
///
/// ── 名字这一块踩过一个大坑 ──
/// <see cref="Emote.Name"/> 是<b>客户端本地化名字</b>：国服客户端里它就是「挥手」「点头」
/// 这种中文。之前拿 <c>"wave"</c> / <c>"nod"</c> 这类英文去匹配，<b>一个都找不到</b>
/// （表明明加载了，行数 334 —— 是名字对不上，不是表没读）。
/// 所以这里的做法是：<b>表里的真实名字就是唯一可靠的 key</b>，面板直接列它，
/// 英文只作为"手输指令时的别名"额外挂一份，对不上也不影响面板。
///
/// 另：<c>Emote.ActionTimeline</c> 是 <c>Collection&lt;RowRef&lt;ActionTimeline&gt;&gt;</c>
/// （不是单个 RowRef），必须取 <c>[0]</c>；表用 <c>foreach</c> 遍历即可，不要去猜行号上界。
public sealed unsafe class NpcAnimator
{
    /// <summary>表里的一个可播放动作。</summary>
    public readonly record struct ActionEntry(string Name, ushort Timeline);

    /// <summary>
    /// 英文别名 → 国服中文名的对照。<b>尽力而为</b>：猜不中就只是别名不可用，
    /// 面板里列的是表里的真实名字，一定能播。
    /// </summary>
    //
    // v0.18.54：旧别名里有 4 个指向表里**不存在**的名字（鞠躬 / 鼓掌 / 伸懒腰 / 环顾 / 打喷嚏），
    // 等于这些英文别名全是死的。现在全部对着 actions.txt 改成真名，并补上常用的一批。
    private static readonly (string En, string Zh)[] Aliases =
    [
        ("wave", "挥手"), ("beckon", "招手"), ("nod", "点头"), ("shake", "摇头"),
        ("bow", "行礼"), ("clap", "拍手"), ("salute", "敬礼"), ("stretch", "伸展"),
        ("point", "指向"), ("sit", "坐下"), ("look around", "张望"),
        ("deny", "否定"), ("shrug", "耸肩"), ("think", "思考"), ("cheer", "激励"),
        ("comfort", "手抚胸"), ("worry", "手抚胸"), ("love", "表达爱意"), ("shy", "害羞"),
        ("praise", "称赞"), ("cold", "好冷"), ("sleepy", "打盹"), ("cry", "哭泣"),
        ("laugh", "轻笑"), ("angry", "不满"), ("sorry", "行礼"), ("thanks", "行礼"),
    ];

    /// <summary>
    /// v0.18.51：<b>永不进入任何动作池</b>的名字关键字。
    ///
    /// <para>用户点名的两个事故之一：弱气说"我是来跟着你的……可以吗？"时做了
    /// 「用触控板记录」——那是个<b>自拍动作</b>，只因为人格池关键字里有个"记录"
    /// 就被随机命中了。这类"拿道具 / 摆姿势拍照"的动作拿来当日常肢体语言非常出戏。</para>
    ///
    /// <para>这里在<b>建表时就整条剔除</b>，于是人格池、通用池、语义匹配全都碰不到它们。
    /// 这是最彻底的一刀：不用在每一处挑选逻辑里各拦一次。</para>
    /// </summary>
    private static readonly string[] BannedActionKeywords =
        ["触控板", "自拍", "拍照", "摄影", "相机", "留影", "合影", "摹写", "素描"];

    private readonly IPluginLog _log;

    private readonly List<ActionEntry>    _actions  = new();
    private readonly Dictionary<string, ushort> _lookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ushort>         _idlePool = new();

    // ── v0.18.56/57 可选待机姿势 ──
    //
    // 【用户要什么】面板可选一个"待机姿势"—— **玩家用 /pose（改变姿势）切换的那几个特定站立待机动画**，
    // 不是「特别」类表情。初版（v0.18.56）拿 Emote 表「特别」类循环动作当候选被用户纠正，57 版换掉。
    //
    // 【玩家站立待机姿势在数据里长什么样】翻 ActionTimeline 全表实锤（_re/pose_dump.txt）：
    // 每个姿势 = emote/poseNN 的 start + _loop 一对（3123+3124 / 3125+3126 / 3181+3182 /
    // 3183+3184 / 7404+7405 / 7406+7407）。
    //
    // 【★ v0.18.60 正确入口：EmoteController.PlayEmote（用户点名"缺的是过渡动画的使用"）】
    // 再挖一层（_re/pose_emote_search.txt）：每个姿势就是一个**隐藏 Emote 行**，7 槽里
    // 槽[0]=循环段、槽[1]=起手段 ——
    //   姿势一=Emote#91  姿势二=#92  姿势三=#107  姿势四=#108  姿势五=#218  姿势六=#219
    // 游戏自己播 /pose 就是走 **EmoteController.PlayEmote(行号, PlayEmoteOption\*)**：
    // 先播槽[1]起手段、播完 native 链进槽[0]循环段 —— 过渡动画由游戏管线保证，
    // 自己拿 PlayTimeline 拼 start+loop 永远拼不对（59.1 已实测失败）。
    //
    // 【她怎么播】放松计时满（或面板直选/召唤推送）→ PlayEmote 一次；
    // 姿势激活期间（_poseEmoteActive）权威出口第④级**完全让位**（不写 BaseOverride 不踢），
    // 循环由游戏表情管线自己续；走动时第①级显式 StopTimeline 摘掉循环段并恢复接管；
    // 换姿势直接再 PlayEmote（管线自己换）；选"游戏默认待机"→ StopTimeline + ④恢复。
    // 旁路保护：姿势激活但 IsEmoting/IsInEmoteLoop 都没了（被游戏重置）→ 放弃接管，
    // 回到默认待机等下一轮放松计时。

    /// <summary>待机姿势候选（玩家 /pose 的 6 个站立姿势）。</summary>
    /// <param name="EmoteRowId">序号 1~6（配置存它）；0 = 游戏默认待机。</param>
    /// <param name="PlayEmoteRow">姿势对应的隐藏 Emote 行（PlayEmote 用）。</param>
    /// <param name="LoopTimeline">姿势循环段时间轴（取消/StopTimeline 用）。</param>
    public sealed record IdleEmoteChoice(ushort EmoteRowId, string Name, ushort PlayEmoteRow, ushort LoopTimeline);

    /// <summary>当前实际生效的待机姿势（序号 1~6；0 = 游戏默认待机）。面板显示用。</summary>
    public ushort ActiveIdleEmoteRowId { get; private set; }

    private ushort _idleAnnounced;    // ④ 级起播踢记账（照 locomotion 踢的模式）

    // ── v0.18.58/59：待机姿势 + 放松等待时间 ──
    /// <summary>进入待机姿势时自动随机挑一个（与上次不同）。没勾就用配置选的姿势。</summary>
    public bool IdleAutoRandom { get; set; }

    /// <summary>放松等待时间（秒）：<b>停止移动后</b>等这么久才进入待机姿势。</summary>
    public float IdleRelaxSeconds { get; set; } = 15f;

    /// <summary>
    /// v0.18.66：「自动随机」开着时，进入姿势后每隔多久<b>自己换下一个</b>（秒，实际下限 5s）。
    /// 与 <see cref="IdleRelaxSeconds"/>（停下后多久首次进姿势）解耦 —— v0.18.64~65 共用一个值，
    /// 用户把 Relax 调小后起手动画每 3 秒重播（"进入动画重复两三次"的实测主因）。
    /// </summary>
    public float IdleSwitchSeconds { get; set; } = 20f;

    private ushort _cfgPlayEmoteRow;  // 配置选的姿势（隐藏 Emote 行，0 = 默认待机）
    private ushort _cfgIdleLoop;      // 配置姿势的循环段时间轴（取消时 StopTimeline 用）
    private ushort _cfgIdleRow;       // 配置姿势序号（0 = 默认待机）
    private ushort _lastPoseRow;      // 上次实际进入的姿势（自动随机时避开它）
    private int    _lastPoseEnterTick; // v0.18.66：上次进姿势的时刻（Environment.TickCount，重入闸用）
    private ushort _activePoseLoop;   // 当前姿势的循环段时间轴（StopTimeline 取消用）
    private bool   _poseEmoteActive;  // 姿势已交给游戏表情管线（④让位）
    private bool   _relaxed;          // 本轮站立是否已进入姿势
    private float  _idleAutoTimer;    // 停止移动累计计时

    // PlayEmote 是**异步**注册进管线的 —— 刚调完那一两帧 IsEmoting/IsInEmoteLoop 可能还没亮。
    // 旁路保护（UpdatePoseGuard）必须先宽限、且"见过一次"之后才允许判"被游戏重置"，
    // 否则当帧就退出姿势 = 永远进不去（v0.18.60 实测踩过）。
    private bool   _poseSeenEmoting;  // 管线里见过一次表情状态
    private float  _poseEmoteGrace;   // PlayEmote 后的宽限计时（秒）
    private bool   _poseIsForIdle;    // 这轮 PlayEmote 是"待机姿势"（失败可退基底）还是"一次性回应"
    private ushort _poseFallbackBase; // PlayEmote 失败时退回④基底循环（姿势保证可见；0 = 没在退回）

    /// <summary>兜底「起手段」按一次性动作播的时长（秒）。播完由④接循环基底 —— 过渡由此而来。
    /// ⚠️ v0.18.69（用户实测裁决）：2.6s 太长 —— 起手段自然长度比它短，播完一遍后游戏
    /// 把它从头再放 = "过渡动画本身重播两三次"。对半折 1.3f 仍重播 → **1.0f**（用户指定）。</summary>
    private const float PoseStartSeconds = 1.0f;

    /// <summary>姿势一（隐藏行 91）的起手偏长，用户指定单独调（0.7→0.4→0.1→0.6，实测迭代）。
    /// ⚠️ v0.18.69.6：参数必须是 <b>playEmoteRow（隐藏 Emote 行 91/92/…）</b>——
    /// 之前错用 row（序号 1~6）判 `==91`，永远不成立，用户三次改时长全没生效。</summary>
    private static float PoseStartDurationFor(ushort playEmoteRow)
        => playEmoteRow == 91 ? 0.6f : PoseStartSeconds;

    /// <summary>回应动作按一次性播的时长（秒）。回应大多是小动作，3 秒足够走完起手+主体。</summary>
    private const float ResponseSeconds = 3.0f;

    /// <summary>
    /// 玩家 /pose 站立待机姿势全集（隐藏 Emote 行实锤，见类注释）。
    /// 静态方法：面板在她未召唤时也要能列候选，所以不依赖实例状态。
    /// </summary>
    public static List<IdleEmoteChoice> LoadIdleChoices()
        =>
    [
        new(1, "姿势一", 91,  3124),
        new(2, "姿势二", 92,  3126),
        new(3, "姿势三", 107, 3182),
        new(4, "姿势四", 108, 3184),
        new(5, "姿势五", 218, 7405),
        new(6, "姿势六", 219, 7407),
    ];

    /// <summary>
    /// 设置待机姿势（playEmoteRow=姿势的隐藏 Emote 行，loopTimeline=循环段，emoteRowId=序号 1~6；0 = 默认待机）。
    /// 配置变化（面板直选/召唤推送）：她站着就<b>立即进入</b>，不等放松计时；走动中则等停下走计时。
    /// 对同值幂等。
    /// </summary>
    public bool SetIdleEmote(ushort playEmoteRow, ushort loopTimeline, ushort emoteRowId)
    {
        if (emoteRowId == _cfgIdleRow && _cfgPlayEmoteRow == playEmoteRow)
            return true;

        _cfgIdleRow      = emoteRowId;
        _cfgPlayEmoteRow = playEmoteRow;
        _cfgIdleLoop     = loopTimeline;

        if (_npc is { IsAlive: true } && !Frozen && !IsWalking && _remaining <= 0f)
        {
            // 站着且没在做小动作时改选/召唤推送 = 立即进入（这轮算已放松，不再重复进）。
            _relaxed = true;
            if (playEmoteRow != 0)
                EnterPoseEmote(playEmoteRow, loopTimeline, emoteRowId);
            else
                ExitPoseEmote("选回默认待机");
        }
        else
        {
            // 走动中/冻结期：只记配置，等停下重新走放松计时。
            _relaxed       = false;
            _poseEmoteActive = false;
            _idleAutoTimer = 0f;
        }
        return true;
    }

    /// <summary>
    /// 进入姿势（v0.18.65 重写）。
    ///
    /// <para>⚠️ 原生 PlayEmote 对本自定义角色<b>实测无效</b>（last-diag 实锤：行 107/218/219
    /// 全部 ok=True 但 IsEmoting 永不亮、管线里什么都没有）—— v0.18.60~64 一直在等一个
    /// 永远不会来的原生管线。改走已验证的两段式：</para>
    /// <list type="number">
    /// <item>把<b>本次姿势</b>的循环段记成④基底（⚠️ 不是 _cfgIdleLoop —— 面板配置的循环段
    ///       会在自动随机时把每次随机结果都盖回面板姿势，v0.18.64 实测教训）；</item>
    /// <item>把槽[1] 起手段走②一次性通路播 <see cref="PoseStartSeconds"/> 秒（说话配动作同款、有过渡）；</item>
    /// <item>起手播完 → ④ 写循环基底 + 起播踢 → 循环接上，过渡完成。</item>
    /// </list>
    /// </summary>
    private void EnterPoseEmote(ushort playEmoteRow, ushort loopTimeline, ushort row)
    {
        // ⚠️ v0.18.66：重入闸 —— 同一行 2 秒内重复进 = 起手动画连播（用户实测
        // "进入动画重复两三次才进待机"）。合法的连续换姿势由切换间隔（≥5s）保证，
        // 2 秒内的同行重入只可能是计时/推送的毛刺，直接忽略。
        var nowTick = Environment.TickCount;
        if (row == _lastPoseRow && _poseFallbackBase != 0 && nowTick - _lastPoseEnterTick < 2000)
        {
            TracePose($"姿势重入被闸（行{playEmoteRow} 距上次 {(nowTick - _lastPoseEnterTick)}ms）");
            SchedulePoseCycle();   // ⚠️ 照常安排计时，否则 _relaxed 卡死、自动随机停摆
            return;
        }
        _lastPoseEnterTick = nowTick;

        _poseFallbackBase    = loopTimeline;
        _lastPoseRow         = row;
        ActiveIdleEmoteRowId = row;

        var startTl = EmoteStartTimelineOf(playEmoteRow);
        var startDur = PoseStartDurationFor(playEmoteRow);   // ⚠️ 用隐藏行号判（v0.18.69.6 修正）
        TracePose($"姿势进入：行{playEmoteRow} 起手#{startTl}({startDur:F1}s) → ④循环{loopTimeline}");
        if (startTl != 0)
            PlayByTimeline(startTl, "姿势起手", startDur);

        SchedulePoseCycle();
    }

    /// <summary>
    /// 回应玩家的动作（v0.18.65 重写）：PlayEmote 对本自定义角色实测无效，直接走
    /// ②一次性通路 —— 优先槽[1] 起手段，没有就用槽[0] 循环段当一次性播 3 秒。
    ///
    /// <para>⚠️ 不再调 ExitPoseEmote：姿势循环基底（<see cref="_poseFallbackBase"/>）保留，
    /// 回应播完④直接回姿势循环，不必重做起手。</para>
    /// </summary>
    public bool PlayResponseEmote(ushort playEmoteRow, ushort loopTimeline)
    {
        if (playEmoteRow == 0)
            return false;

        // ⚠️ v0.18.66：不再预置 _idleAutoTimer —— 旧版把计时预置到"还差 2.5 秒"，
        // 回应播完 2.5 秒就强制换姿势（起手重播，用户实测"进入动画重复两三次"的帮凶）。
        // 现在姿势循环基底保留、④直接接回循环，随机切换计时照自己的节奏走
        // （回应播放期间 _remaining>0 会冻结计时，本身就有暂停效果）。

        var tl = EmoteStartTimelineOf(playEmoteRow);
        if (tl == 0)
            tl = loopTimeline != 0 ? loopTimeline : EmoteLoopTimelineOf(playEmoteRow);
        if (tl == 0)
        {
            TracePose($"回应失败：行{playEmoteRow} 没有任何时间轴");
            return false;
        }

        TracePose($"回应：行{playEmoteRow} → ②一次性 #{tl} ({ResponseSeconds:F1}s)");
        return PlayByTimeline(tl, $"回应·行{playEmoteRow}", ResponseSeconds);
    }

    /// <summary>
    /// ⚠️ <b>v0.18.65 退役</b>：原生 PlayEmote 对本自定义角色实测无效（ok=True 但
    /// IsEmoting 永不亮、管线里始终什么都没有 —— last-diag 多行实锤）。
    /// 姿势/回应已全部改走②起手 + ④循环的两段式（见 <see cref="EnterPoseEmote"/>）。
    /// 保留本方法备查：OwnerObject 补挂、null-option 重试这些结论仍是有效的排查资产。
    /// </summary>
    private bool PlayEmoteCore(ushort playEmoteRow, ushort loopTimeline, ushort row, bool asPose)
    {
        if (_npc is not { IsAlive: true } || Frozen)
            return false;

        var character = (Character*)_npc.Address;
        var ec        = &character->EmoteController;

        // ⚠️ 自定义角色的 EmoteController 可能没被游戏初始化（+0x8 OwnerObject 空/不指向她），
        // PlayEmote 会因此拒绝 —— 补挂上她再试（字段偏移 dnfile 实锤）。
        var ownerSlot = (nint*)((nint)ec + 0x8);
        if (*ownerSlot != (nint)character)
        {
            _log.Information("[SmartNpc] EmoteController.OwnerObject={0} ≠ 她 {1}，补挂。", *ownerSlot, (nint)character);
            Diag?.Append($"[SmartNpc] OwnerObject={*ownerSlot:X} ≠ 她 {(nint)character:X}，补挂");
            *ownerSlot = (nint)character;
        }

        // 零值 option（内嵌可选项：TargetId=0 无目标 / Flags=0 默认 / 禁坐下动画=false）。
        // ⚠️ PlayEmoteOption 是 EmoteController 的嵌套类型；EmoteController 在 Character 里是内嵌值类型。
        EmoteController.PlayEmoteOption option = default;
        var ok = ec->PlayEmote(playEmoteRow, &option);
        if (!ok)
        {
            // 零值 option 若不被接受，再试 null。
            ok = ec->PlayEmote(playEmoteRow, null);
            if (ok)
                _log.Information("[SmartNpc] PlayEmote 零值 option 被拒，null option 成功。");
        }

        var wasNativeActive = _poseEmoteActive;
        var prevLoop        = _activePoseLoop;
        _poseEmoteActive = ok;
        _activePoseLoop  = ok ? loopTimeline : (ushort)0;
        if (!ok && wasNativeActive && prevLoop != 0)
            character->StopTimeline(prevLoop);   // v0.18.64：换姿势失败先摘旧管线循环，别让它和新起手叠着播
        _poseSeenEmoting = false;
        _poseEmoteGrace  = 1.5f;   // 宽限期：管线注册来得及
        _poseIsForIdle   = asPose;
        _idleAutoTimer   = 0f;

        if (asPose)
        {
            ActiveIdleEmoteRowId = ok ? row : (ushort)0;
            _lastPoseRow         = row;

            if (IdleAutoRandom)
            {
                // ★ v0.18.64：「自动随机改变姿势」的续循环 —— 进完姿势就重开计时，
                // 站立满放松时间再换下一个（≠当前）。v0.18.62 起 _relaxed 进姿势后
                // 永久为 true，随机只换一次就停死（用户实测"随机失效"）。
                // 透支量：relax=0 时也至少隔 3 秒才换，绝不每帧乱跳。
                SchedulePoseCycle();
            }
            else
            {
                _relaxed = true;   // 固定姿势：这一轮进完就等下次移动/推送再说
            }

            // ★ 兜底（v0.18.64）：PlayEmote 拒绝也得让姿势可见，而且**要有过渡** ——
            //   ① 把循环段记成④基底（_poseFallbackBase）：等起手播完，④写 BaseOverride=循环段
            //     + 起播踢，循环接上；
            //   ② 同时把【槽[1] 起手段】走 ②一次性通路播出来 —— 这是说话配动作同款、
            //     实测有过渡的通路（BaseOverride=起手段 + PlayTimeline 一次，播完自然交棒）。
            //   v0.18.62 只裸切循环基底，被用户实测"依旧缺乏过渡动画"打回。
            _poseFallbackBase = ok ? (ushort)0 : loopTimeline;
            if (!ok)
            {
                var startTl = EmoteStartTimelineOf(playEmoteRow);
                _log.Warning("[SmartNpc] PlayEmote 行={0} 被拒（ok=False），姿势走「起手→循环」兜底：起手段 #{1} → ④循环 {2}。",
                             playEmoteRow, startTl, loopTimeline);
                Diag?.Append($"[SmartNpc] PlayEmote 行={playEmoteRow} 被拒 → 兜底 起手#{startTl}({PoseStartSeconds:F1}s)→④循环{loopTimeline}");
                if (startTl != 0)
                    PlayByTimeline(startTl, "姿势起手（兜底）", PoseStartSeconds);
            }
        }
        else
        {
            ActiveIdleEmoteRowId = 0;
            _poseFallbackBase    = 0;
        }

        _log.Information("[SmartNpc] PlayEmote 行={0} ok={1} asPose={2}（IsEmoting 立查={3}）",
                         playEmoteRow, ok, asPose, character->EmoteController.IsEmoting());
        Diag?.Append($"[SmartNpc] PlayEmote 行={playEmoteRow} ok={ok} asPose={asPose} IsEmoting立查={character->EmoteController.IsEmoting()}");
        return ok;
    }

    /// <summary>退出姿势：显式摘掉循环段时间轴（管线不会自己收），④ 恢复基底接管。
    /// ⚠️ v0.18.68：_poseEmoteActive 已随 PlayEmote 退役恒为 false —— 判据改为托管状态，
    /// 否则"选回默认待机"根本清不掉姿势基底（v0.18.65 起的潜伏 bug）。</summary>
    private void ExitPoseEmote(string reason)
    {
        if (!_poseEmoteActive && _poseFallbackBase == 0 && _lastPoseRow == 0)
            return;

        if (_npc is { IsAlive: true } && !Frozen && _activePoseLoop != 0)
            ((Character*)_npc.Address)->StopTimeline(_activePoseLoop);

        _poseEmoteActive = false;
        _activePoseLoop  = 0;
        _idleAnnounced   = 0;   // ④ 恢复时重新起播
        _poseFallbackBase = 0;  // v0.18.62
        _lastPoseRow      = 0;
        ActiveIdleEmoteRowId = 0;
        _log.Information("[SmartNpc] 待机姿势退出（{0}）", reason);
        TracePose($"姿势退出（{reason}）：基底清零，④回默认待机");
    }

    /// <summary>姿势旁路保护：管线里的表情没了（被游戏重置）就放弃接管，回默认待机等下一轮放松。
    /// ⚠️ PlayEmote 异步注册 —— 宽限期内不判，见过一次状态后才允许判"没了"。</summary>
    private void UpdatePoseGuard(float deltaTime)
    {
        if (!_poseEmoteActive)
            return;

        if (_npc is not { IsAlive: true } || Frozen)
            return;

        if (_poseEmoteGrace > 0f)
        {
            _poseEmoteGrace -= deltaTime;
            return;
        }

        var ec = &((Character*)_npc.Address)->EmoteController;
        if (ec->IsEmoting() || ec->IsInEmoteLoop())
        {
            _poseSeenEmoting = true;
            return;
        }

        // 宽限期内一次都没见过表情状态 = PlayEmote 压根没生效，别再假装在姿势里。
        // 见过之后又没了 = 被游戏重置，同样放弃接管。
        if (!_poseSeenEmoting)
            _log.Information("[SmartNpc] PlayEmote 后管线始终没有表情状态（可能不支持该行），放弃接管。");
        _poseEmoteActive = false;
        _activePoseLoop  = 0;
        _poseSeenEmoting = false;
        _relaxed         = false;
        _idleAnnounced   = 0;

        // 待机姿势且 PlayEmote 走不通 → 兜底退回④基底循环（姿势保证可见，日志已说明无过渡）。
        if (_poseIsForIdle && _cfgIdleLoop != 0)
        {
            _poseFallbackBase = _cfgIdleLoop;
            _log.Information("[SmartNpc] 待机姿势退回④基底循环 {0}。", _cfgIdleLoop);
            Diag?.Append($"[SmartNpc] 姿势判失效 seen={_poseSeenEmoting} → 退回④基底循环 {_cfgIdleLoop}");
            SchedulePoseCycle();   // 自动随机开着就重开计时（v0.18.64，否则这里也会卡死循环）
        }
        else
        {
            _log.Information("[SmartNpc] 待机姿势退出（管线状态消失），回默认待机等下一轮放松计时。");
            Diag?.Append($"[SmartNpc] 姿势判失效 seen={_poseSeenEmoting} → 回默认待机");
        }
    }

    private NpcHandle? _npc;
    private float      _remaining;

    /// <summary>
    /// 旧版用于在播放表情前暂存、播完后还原的动画模式（走的是 AnimLock 通路）。
    /// v0.18.0 起统一为 Normal + BaseOverride 单一通路，这套"存/还原"已废弃，
    /// 字段仅保留以免大改时误删，当前<b>没有任何写入方</b>。
    /// </summary>
    private bool _saved;

    public NpcAnimator(IPluginLog log, IDataManager data)
    {
        _log = log;
        LoadActions(data);
    }

    /// <summary>面板用：表里的真实动作名（本地化语言，国服即中文）。</summary>
    public IReadOnlyList<string> AvailableActions { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// v0.18.53：「Emote 行号 → 名字」全表（<b>含</b>被禁的自拍类）。
    /// 玩家对她做表情动作时用来把 <c>EmoteController.EmoteId</c> 翻成中文名。
    /// </summary>
    public static Dictionary<ushort, string> EmoteNames { get; } = new();

    // v0.18.61：回应玩家的动作要用真 Emote 行播（PlayEmote），需要两张反查表。
    private readonly Dictionary<string, ushort> _emoteRowByName  = new();   // 名字 → Emote 行
    private readonly Dictionary<ushort, ushort> _emoteLoopByRow  = new();   // Emote 行 → 槽[0] 循环段时间轴
    private readonly Dictionary<ushort, ushort> _emoteStartByRow = new();   // Emote 行 → 槽[1] 起手段时间轴（v0.18.64 过渡兜底）

    /// <summary>按名字（精确→包含）找 Emote 行；找不到返回 0。用于"回她一个真表情"。</summary>
    public ushort FindEmoteRow(params string[] names)
    {
        foreach (var want in names)
        {
            if (string.IsNullOrWhiteSpace(want))
                continue;

            if (_emoteRowByName.TryGetValue(want, out var row))
                return row;

            foreach (var (name, row2) in _emoteRowByName)
            {
                if (name.Contains(want, StringComparison.Ordinal))
                    return row2;
            }
        }

        return 0;
    }

    /// <summary>Emote 行的槽[0] 循环段时间轴（取消时 StopTimeline 用；没有返回 0）。</summary>
    public ushort EmoteLoopTimelineOf(ushort emoteRow)
        => _emoteLoopByRow.TryGetValue(emoteRow, out var tl) ? tl : (ushort)0;

    /// <summary>Emote 行的槽[1] 起手段时间轴（v0.18.64：PlayEmote 失败时「起手→循环」过渡兜底用；没有返回 0）。</summary>
    public ushort EmoteStartTimelineOf(ushort emoteRow)
        => _emoteStartByRow.TryGetValue(emoteRow, out var tl) ? tl : (ushort)0;

    /// <summary>
    /// 诊断落盘通道（v0.18.64）：姿势管线的关键行同步写进 last-diag.txt。
    /// <para>⚠️ Serilog 的 Information 行<b>不进任何本机可读日志</b>（dalamud.log 实测只收 ERR），
    /// PlayEmote 成败、兜底切换这些排查命脉必须走 Diag 才看得见。</para>
    /// </summary>
    public DiagFile? Diag { get; set; }

    public string? CurrentAction { get; private set; }

    public bool IsBusy => _remaining > 0f;

    /// <summary>行走动画的时间轴行号；0 = 没找到（走路时就没有动画）。</summary>
    public ushort WalkTimeline { get; private set; }

    /// <summary>
    /// 奔跑动画的时间轴行号；0 = 没找到，会退回走路时间轴。
    ///
    /// ⚠️ 速度分级不只是"走快一点"。用走路动画跑 = 腿还在迈小步、身体在平移，
    /// 那个"太空步"观感比没有动画更糟。所以跑必须换一条<b>真的含 run</b> 的时间轴。
    /// </summary>
    public ushort RunTimeline { get; private set; }

    /// <summary>自动发现到的那些（手填覆盖后用它还原）。</summary>
    private ushort _autoWalkTimeline;
    private ushort _autoRunTimeline;

    /// <summary>手填覆盖行走时间轴；传 0 = 还原成自动发现的结果。</summary>
    public void RestoreAutoWalkTimeline(int rowId)
    {
        var wanted = rowId > 0 && rowId <= ushort.MaxValue ? (ushort)rowId : (ushort)0;
        WalkTimeline = wanted == 0 ? _autoWalkTimeline : wanted;
        IsWalking    = false;
    }

    /// <summary>行走时间轴是怎么来的（写进日志，便于人工纠正）。</summary>
    public string WalkTimelineSource { get; private set; } = string.Empty;

    /// <summary>奔跑时间轴是怎么来的。</summary>
    public string RunTimelineSource { get; private set; } = string.Empty;

    /// <summary>当前是否正在播走路动画。</summary>
    public bool IsWalking { get; private set; }

    /// <summary>当前播的是不是奔跑动画（诊断用）。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>走路动画的播放速度微调（1 = 按实际速度自动匹配）。觉得慢就调到 1.2。</summary>
    public float WalkSpeedScale { get; set; } = 1f;

    /// <summary>转身动作的播放速度倍率（1 = 正常）。面板可调。</summary>
    public float TurnPlaybackSpeed { get; set; } = 1f;

    /// <summary>
    /// 走路动画速度的参考步速（米/秒）。必须与 <see cref="NpcMotion.WalkSpeed"/> 一致 ——
    /// 这里是"参考步速 → 实际速度"的换算分母，两边不一致就会出现"脚在倒着走"。
    /// </summary>
    private const float ReferenceWalkSpeed = 2.2f;

    /// <summary>奔跑动画速度的参考步速（米/秒）。同理须与 NpcMotion.RunSpeed 一致。</summary>
    private const float ReferenceRunSpeed = 4.6f;

    public void Bind(NpcHandle handle)
    {
        _npc = handle;
        Stop();
    }

    /// <summary>从 Emote 表建立"名字 → ActionTimeline 行号"的索引。</summary>
    private void LoadActions(IDataManager data)
    {
        var sheet = data.GetExcelSheet<Emote>();
        if (sheet is null)
        {
            _log.Error("[SmartNpc] 拿不到 Emote 表，动作功能不可用。");
            return;
        }

        foreach (var emote in sheet)
        {
            // ActionTimeline 是 Collection<RowRef<…>>，取第一个（常规地面动作）。
            if (emote.ActionTimeline.Count == 0)
                continue;

            var timeline = emote.ActionTimeline[0].RowId;
            if (timeline == 0 || timeline > ushort.MaxValue)
                continue;

            // ⚠️ v0.18.69.2：循环/起手反查表必须在名字过滤**之前**填 ——
            // 姿势一~四的隐藏 Emote 行（91/92/107/108）名字为空，旧版在下面 continue 掉了，
            // _emoteStartByRow 永远查不到 → 起手#0 → 姿势 1~4 没有过渡动画
            // （trace-pose 实锤：姿势进入「起手#0」；218/219 有名字所以 5/6 正常）。
            if (emote.ActionTimeline[0].RowId != 0u
                && emote.ActionTimeline[0].RowId <= ushort.MaxValue
                && !_emoteLoopByRow.ContainsKey((ushort)emote.RowId))
                _emoteLoopByRow[(ushort)emote.RowId] = (ushort)emote.ActionTimeline[0].RowId;
            if (emote.ActionTimeline.Count > 1 && emote.ActionTimeline[1].RowId != 0u
                && emote.ActionTimeline[1].RowId <= ushort.MaxValue
                && !_emoteStartByRow.ContainsKey((ushort)emote.RowId))
                _emoteStartByRow[(ushort)emote.RowId] = (ushort)emote.ActionTimeline[1].RowId;

            var name = emote.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            // v0.18.53：留一份「行号 → 名字」全表 —— 玩家对**她**做表情动作时，
            // 我们只能从 EmoteController.EmoteId 拿到行号，得靠这张表翻成中文名才知道
            // 他做的是「挥手」还是「飞吻」。⚠️ 这里**不**过 BannedActionKeywords：
            // 禁令只针对"她自己要不要做这个动作"，玩家做了拍照动作她照样该有反应。
            EmoteNames[(ushort)emote.RowId] = name;

            // v0.18.61：反查表 —— 「名字 → Emote 行」（玩家动作回应要用真 Emote 行播）。
            // （循环/起手段两张表已在上面名字过滤前填好，v0.18.69.2。）
            if (!_emoteRowByName.ContainsKey(name))
                _emoteRowByName[name] = (ushort)emote.RowId;

            // v0.18.51：自拍 / 拍照类动作整条剔除（见 BannedActionKeywords）。
            if (IsBanned(name))
                continue;

            _actions.Add(new ActionEntry(name, (ushort)timeline));
        }

        AvailableActions = _actions.Select(a => a.Name).ToArray();

        foreach (var (name, timeline) in _actions)
            _lookup[Normalize(name)] = timeline;

        // 英文别名：把能对上的中文名再挂一份英文 key，方便手输指令。
        foreach (var (en, zh) in Aliases)
        {
            if (_lookup.TryGetValue(Normalize(zh), out var id))
                _lookup[Normalize(en)] = id;
        }

        // 自言自语用的小动作池：优先用上面能对上的那几个"通用手势"，
        // 全对不上就退回全表（宁可动作怪一点，也不能"不认识动作"）。
        foreach (var (_, zh) in Aliases)
        {
            if (_lookup.TryGetValue(Normalize(zh), out var id) && !_idlePool.Contains(id))
                _idlePool.Add(id);
        }

        if (_idlePool.Count == 0)
            _idlePool.AddRange(_actions.Select(a => a.Timeline));

        _log.Information("[SmartNpc] 载入动作 {0} 个，小动作池 {1} 个。前 20 个：{2}",
                         _actions.Count, _idlePool.Count, Sample());

        DiscoverWalkTimeline(data);
        DiscoverLipsTimelines(data);
        _autoWalkTimeline = WalkTimeline;
        _autoRunTimeline  = RunTimeline;
        BuildPersonaPools();
    }

    // ── 说话时的嘴巴开合（v0.18.53，底层，不是表情动作）──────────────
    //
    // 【用户要什么】"添加嘴巴说话的开合动画（不是表情动作，应该是底层代码）"。
    //
    // 【路子】dnfile 查 FFXIVClientStructs 实锤：
    //   FFXIVClientStructs.FFXIV.Client.Game.Character.TimelineContainer
    //     +0x2E6  ushort BaseOverride      ← 我们一直在写的身体基底
    //     **+0x2E8  ushort LipsOverride**  ← 嘴巴，就是它
    //     TimelineContainer.SetLipsOverrideTimeline(ushort)  ← 游戏自己的入口（MemberFunctionPointers）
    // 另外 ConfigOption 里有个 LipMotionType，说明客户端确实有"口型动作"这套东西。
    //
    // 【为什么不用表情动作代替】表情动作（Emote）走的是 BaseOverride，会**顶掉身体动画**：
    // 她一边说话一边比划时，两者抢同一个字段（本类开头那份诊断就写了这个坑）。
    // 口型是**叠加层**，和身体动作互不干扰 —— 这才是"边说话边比划"的正确做法。
    //
    // 【怎么让它真的"开合"】不猜：从 ActionTimeline 表里把名字含 lip / talk / mouth 的
    // 行全扫出来当候选。候选 ≥2 就在说话期间轮换（动画本身是开合的 → 看着在说话；
    // 是静态口型 → 轮换也能形成开合）；只有 1 个就跟"0（默认闭嘴）"交替 ——
    // 两种情况都能看出嘴在动。
    private readonly List<ushort> _lipsTalk = new();

    /// <summary>开合一次的时间（秒）。调小 = 嘴动得快（像急急地说）。</summary>
    public float LipFlapSeconds { get; set; } = 0.16f;

    /// <summary>说话开合的总开关（面板 + 配置）。关掉就完全不碰 LipsOverride。</summary>
    public bool LipSyncEnabled { get; set; } = true;

    private ushort _lipsCurrent;
    private float  _lipsTimer;
    private int    _lipsIndex;

    /// <summary>面板/诊断用：发现到几条口型时间轴。</summary>
    public int LipsCount => _lipsTalk.Count;

    /// <summary>面板/诊断用：发现到的口型时间轴清单（行号=名字）。</summary>
    public string LipsDescriptor { get; private set; } = "（没发现）";

    /// <summary>扫 ActionTimeline 表，把口型相关的时间轴找出来。</summary>
    private void DiscoverLipsTimelines(IDataManager data)
    {
        try
        {
            var sheet = data.GetExcelSheet<ActionTimeline>();
            if (sheet is null)
                return;

            var found = new List<string>();

            foreach (var row in sheet)
            {
                var key = row.Key.ExtractText();
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                var n = key.Replace('\\', '/').Trim().ToLowerInvariant();

                // ⚠️⚠️ v0.18.62 实锤：国服 7.5 的 ActionTimeline 表里**没有** lip/mouth/talk 键，
                // 真正的"说话嘴型"是 **speak/ 系列**（all_timeline_keys.txt）：
                //   626 speak/whisper_short  627 whisper_middle  628 whisper_long
                //   629 speak/normal_short   630 normal_middle   631 normal_long
                //   632 speak/shout_short    633 shout_middle    634 shout_long
                // （154 speak/stop 是收嘴，不收）。旧词表 lip/mouth/talk 一个都匹配不到
                // → _lipsTalk 恒空 → 口型从来没数据（"嘴上说话没反应"的真因）。
                var isSpeak = n.StartsWith("speak/normal", StringComparison.Ordinal)
                           || n.StartsWith("speak/shout", StringComparison.Ordinal)
                           || n.StartsWith("speak/whisper", StringComparison.Ordinal);
                var isLegacy = n.Contains("lip") || n.Contains("mouth") || n.Contains("talk");
                if (!isSpeak && !isLegacy)
                    continue;

                var id = (ushort)row.RowId;
                if (id == 0 || _lipsTalk.Contains(id))
                    continue;

                _lipsTalk.Add(id);
                if (found.Count < 24)
                    found.Add($"{id}={key}");
            }

            LipsDescriptor = found.Count > 0 ? string.Join(", ", found) : "（表里没匹配到）";
            _log.Information("[SmartNpc] 口型时间轴 {0} 条：{1}", _lipsTalk.Count, LipsDescriptor);
        }
        catch (Exception ex)
        {
            LipsDescriptor = "扫描异常：" + ex.Message;
        }
    }

    /// <summary>
    /// 每帧调用：<paramref name="speaking"/> = 她此刻是不是在说话（<c>NpcBrain.IsSpeaking</c>）。
    ///
    /// <para>⚠️ 只在<b>值真的变了</b>时才调游戏那个入口 —— 状态切换型的原生调用
    /// 绝不能每帧写（本项目的红线之一）。</para>
    /// </summary>
    public void UpdateLipSync(bool speaking, float deltaTime)
    {
        if (!LipSyncEnabled)
            return;

        if (_npc is not { IsAlive: true } || Frozen || _lipsTalk.Count == 0)
        {
            // 没有口型数据 / 她不在场 / 换区冻结期 —— 无条件出路：还原成默认（闭嘴）一次。
            if (_lipsCurrent != 0)
            {
                _lipsCurrent = 0;
                WriteLipsOverride(0);
            }
            return;
        }

        if (!speaking)
        {
            if (_lipsCurrent != 0)
            {
                _lipsCurrent = 0;
                _lipsIndex   = 0;
                WriteLipsOverride(0);
            }
            return;
        }

        _lipsTimer -= deltaTime;
        if (_lipsTimer > 0f)
            return;

        _lipsTimer = Math.Clamp(LipFlapSeconds, 0.05f, 0.6f);

        // ≥2 条 → 在候选之间轮换；只有 1 条 → 和"默认（0）"交替，照样能看出开合。
        ushort next;

        if (_lipsTalk.Count >= 2)
        {
            _lipsIndex = (_lipsIndex + 1) % _lipsTalk.Count;
            next = _lipsTalk[_lipsIndex];
        }
        else
        {
            next = _lipsCurrent == 0 ? _lipsTalk[0] : (ushort)0;
        }

        if (next == _lipsCurrent)
            return;

        _lipsCurrent = next;
        WriteLipsOverride(next);
    }

    private void WriteLipsOverride(ushort id)
    {
        try
        {
            var ch = (Character*)_npc!.Address;
            ch->Timeline.SetLipsOverrideTimeline(id);
        }
        catch
        {
            // 写不进去就算了 —— 口型是锦上添花，绝不能因为它出任何问题。
        }
    }

    /// <summary>动作名里带这些词的一律不要（自拍 / 拍照类，见 <see cref="BannedActionKeywords"/>）。</summary>
    private static bool IsBanned(string name)
    {
        foreach (var bad in BannedActionKeywords)
        {
            if (name.Contains(bad, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// v0.18.51：把"候选动作词"翻译成<b>表里真实存在的动作名</b>。
    ///
    /// <para>先找完全相同的，再找名字里包含的（国服表名是中文，这一步能救回大部分）。
    /// 全部对不上返回 null —— 调用方拿到 null 就<b>不做动作</b>，绝不硬塞。</para>
    /// </summary>
    public string? ResolveActionName(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            foreach (var a in _actions)
            {
                if (string.Equals(a.Name, candidate, StringComparison.Ordinal))
                    return a.Name;
            }
        }

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            foreach (var a in _actions)
            {
                if (a.Name.Contains(candidate, StringComparison.Ordinal))
                    return a.Name;
            }
        }

        return null;
    }

    /// <summary>播放一个动作（表里的真实名字、英文别名、或直接给时间轴行号）。</summary>
    public bool Play(string nameOrAlias, float duration = 3.0f)
    {
        if (_npc is not { IsAlive: true })
            return false;

        if (!TryResolve(nameOrAlias, out var timelineId))
        {
            _log.Warning("[SmartNpc] 不认识的动作「{0}」。面板里列的就是能播的；也可以直接给时间轴行号。可用举例：{1}",
                         nameOrAlias, Sample());
            return false;
        }

        return PlayByTimeline(timelineId, nameOrAlias, duration);
    }

    private bool PlayByTimeline(ushort timelineId, string label, float duration)
    {
        if (_npc is not { IsAlive: true } || Frozen)   // 换区冻结期不许播
        {
            TraceAction($"PLAY-SKIP「{label}」tl={timelineId} alive={_npc?.IsAlive} frozen={Frozen} dur={duration:F1}");
            return false;
        }

        // ⚠️ v0.18.67：姿势起手保护窗 —— 起手一次性（2.6s）在飞时，任何小动作/说话配动作
        // 插进来都会把它掐断（PlayByTimeline 直接覆盖、不走 Stop），「起手→循环」交接断掉；
        // 下次进姿势又从头播起手 = 用户实测"姿势进入动画重复两三次才进待机"。
        // 起手在飞期间只放行：回应（玩家主动，优先级更高）与姿势起手自身（见下条闸）。
        var startInFlight = _remaining > 0f
                            && CurrentAction != null
                            && CurrentAction.StartsWith("姿势起手", StringComparison.Ordinal);
        if (startInFlight
            && !label.StartsWith("回应", StringComparison.Ordinal)
            && !label.StartsWith("姿势起手", StringComparison.Ordinal))
        {
            TracePose($"起手保护窗：跳过「{label}」#{timelineId}（起手还剩 {_remaining:F1}s）");
            return false;
        }

        // ⚠️ v0.18.67：同一条起手已在飞 → 不重置计时。重复触发不再从第 0 帧重播。
        if (startInFlight
            && label.StartsWith("姿势起手", StringComparison.Ordinal)
            && _currentTimeline == timelineId)
        {
            TracePose($"起手重触发被闸：#{timelineId} 还剩 {_remaining:F1}s，不重播");
            return true;
        }

        // v0.18.67：一次性覆盖全记 trace —— 下次再报"动画重复/被掐"，这里一眼定案。
        TracePose($"一次性「{label}」#{timelineId} {duration:F1}s（覆盖：{CurrentAction ?? "无"} 剩 {_remaining:F1}s）");

        // v0.18.61：姿势激活时，姿势循环占着表情槽 —— 一次性动作（说话配动作/人格小动作）
        // 会被它整个盖住（实测"说话发气泡没动作"的主因）。先退出姿势，播完由放松计时快速回姿势。
        if (_poseEmoteActive)
        {
            ExitPoseEmote($"一次性动作「{label}」接管");
            _relaxed = false;
        }

        //
        // ⚠️⚠️ v0.18.0：这里**不再直接写原生字段**。
        //
        // 旧版在这里一次性写 SetMode(AnimLock) + BaseOverride + PlayTimeline，
        // 然后就撒手不管了。诊断文件证明：这些值当帧就被游戏冲掉（见类注释）。
        // 于是表情从未真正播出，而 SetWalking 仍每帧写 BaseOverride=13，
        // 两条通路并存 → "打断无效" + "边走边做动作" 是同一个根因的两个面。
        //
        // 现在这里只做**记账**（这个动作打算播多久），
        // 真正写原生字段统一交给本帧末尾的 ApplyAuthority（每帧无条件补写）。
        //
        _currentTimeline = timelineId;

        IsWalking = false;
        IsRunning = false;
        // 表情会顶掉 locomotion：清掉记账，免得切回移动时误判"已在播"。
        _locoTimeline = 0;
        _idleTimeline = 0;

        CurrentAction        = label;
        _remaining           = duration;
        _npc.IsPlayingAction = true;

        _verifyIn  = 1.0f;
        _verifyId  = timelineId;
        _verifyFor = label;

        _log.Debug("[SmartNpc] 播放动作「{0}」→ ActionTimeline {1}", label, timelineId);
        return true;
    }

    /// <summary>
    /// 动画权威出口：<b>每帧调用一次，无条件把 BaseOverride 写成当前该播的那条</b>。
    ///
    /// <para>这是 v0.18.0 的全部核心。此前每一版都在"怎么播动作"上打转
    /// （AnimLock 还是 Normal？PlayTimeline 还是 PlayActionTimeline？），
    /// 却从没在"<b>谁每帧守着这个字段</b>"上做对过。</para>
    ///
    /// <para>优先级（高 → 低），每帧只取一个：</para>
    /// <list type="number">
    /// <item><b>移动</b>：她真的在移动 → 走/跑时间轴。移动永远压过表情。</item>
    /// <item><b>表情</b>：_remaining &gt; 0 → 表情时间轴。</item>
    /// <item><b>转身</b>：原地转身中 → 左/右转时间轴。</item>
    /// <item><b>待机</b>：都没有 → 自定义待机循环。</item>
    /// </list>
    ///
    /// <para>⚠️ 只有<b>值真的变了</b>时才调 <c>PlayTimeline</c> 重新起播，
    /// 否则每帧重播会把动画永远按在第 0 帧（这正是"定格+平移"的老毛病）。</para>
    ///
    /// <para>⚠️ 模式统一为 <see cref="CharacterModes.Normal"/>：
    /// 不再用 AnimLock。AnimLock 的语义是"锁住当前姿态"，会让角色定格，
    /// 而我们要的是一条会自己循环的基底动画 —— 那就是 Normal + BaseOverride。</para>
    /// </summary>
    /// <param name="moving">她此刻真的在移动吗（移动层说话）。</param>
    /// <param name="speed">当前速度（换算播放速度用）。</param>
    /// <param name="gait">当前姿态。</param>
    public void ApplyAuthority(bool moving, float speed, Gait gait)
    {
        if (_npc is not { IsAlive: true } || Frozen)
            return;

        // ⚠️ v0.18.8：跳跃动画窗口内不接管 —— 走路/待机/表情都会把刚写进去的
        // 跳跃时间轴覆盖掉（"跳一下没有动画"的最后一个成因）。
        if (_jumpHold > 0f)
            return;

        // v0.18.14：31（起跳）按**原生速度**播放。
        // 正常播放速度由移动状态决定（0.60~2.2），而 31 只在起跳瞬间出现——
        // 慢速态会把这一瞬拉长（trace 实测站立 0.16s ↔ 跑动 0.05s，差 3 倍，
        // 正好等于两个场景的速度倍率差，原生长度 ≈0.1s）。
        // 空中（phase 2）走 _jumpHold 早退、不碰速度 → 整个滞空保持 1.0，32 匀速循环。
        if (_jumpPhase == 1)
        {
            var chJump = (Character*)_npc.Address;
            if (Math.Abs(chJump->Timeline.OverallSpeed - 1f) > 0.01f)
                chJump->Timeline.OverallSpeed = 1f;
            return;   // base 维持起跳那帧的状态即可，31 叠在上面，≤0.1s 后切 32
        }

        var character = (Character*)_npc.Address;

        // ⚠️ v0.18.21：**先读、再写** —— 这一帧读到的值是上一帧写进去的残留。
        // 它是否等于我们上次写的值，就是"动画系统接管了没有"的唯一可靠判据。
        // （写在自己之后读恒等于自己，那就永远"以为生效了"。）
        _lastWriteStuck = _lastWrittenBase != 0
                          && character->Timeline.BaseOverride == _lastWrittenBase;

        // ① 移动优先 —— 移动时绝不播表情（这就是"不允许边走边做"的物理保证）
        if (moving)
        {
            var walk = LocomotionTimeline(gait);
            EnterLocomotionMode(character, walk);
            _lastWrittenBase = walk;

            // ⚠️⚠️ v0.18.20 locomotion 起播踢（"换区后站笔直滑行"的修复）：
            // slide-trace 实锤：L/D 同步、逻辑位置每帧都在写，她是"站着笔直平移"
            // —— 走路动画压根没播。BaseOverride=13 每帧都在写（值没变不重写），
            // 但一个刚重建绘制对象的角色，TimelineSequencer 里**没有任何在播的时间轴**
            // —— BaseOverride 只决定"基底是哪条"，不负责"把它播起来"；
            // 平时它看着正常，是因为召唤流程里有别的 PlayTimeline 把 sequencer 点着了。
            // 所以照表情通路的成熟模式（_announcedTimeline 只播一次）给 locomotion
            // 也加一发起播踢：每条时间轴只在起步时 PlayTimeline 一次，
            // 之后照旧靠每帧 BaseOverride 补写维持 —— 不会出现"每帧重播=定格"。
            //
            // ⚠️ v0.18.21：只踢一次不够 —— 模型重建那 2 秒里我们写的每一脚都被冲掉，
            // 而 _locoAnnounced 已经记账，再也不会补第二脚。现在"没留住"就再踢，
            // 限频 0.5s（每帧踢 = 从第 0 帧重播 = 定格，绝不能那样）。
            if (_locoAnnounced != walk || (!_lastWriteStuck && _locoKickCooldown <= 0f))
            {
                _locoAnnounced     = walk;
                _locoKickCooldown  = 0.5f;
                character->Timeline.TimelineSequencer.PlayTimeline(walk);
            }

            _locoTimeline = walk;
            _idleTimeline = 0;
            _turnTimeline = 0;
            _idleAnnounced = 0;   // v0.18.56：离开待机 —— 回来时④要重新起播
            // v0.18.59/60：姿势随移动退出 —— 显式摘掉管线里的姿势循环段并恢复④接管。
            _idleAutoTimer = 0f;
            _relaxed = false;
            if (_poseFallbackBase != 0)
                TracePose($"移动接管：撤姿势基底 {_poseFallbackBase}（下次停下重走放松计时）");
            if (_poseEmoteActive)
            {
                if (_activePoseLoop != 0)
                character->StopTimeline(_activePoseLoop);
                _poseEmoteActive = false;
                _activePoseLoop  = 0;
            }
            _poseFallbackBase = 0;   // 移动接管，兜底基底一并撤（v0.18.62，v0.18.69 回退到 0.18.67 语义）

            IsWalking = true;
            IsRunning = walk == RunTimeline && RunTimeline != 0;

            // ⚠️ v0.17.2：起步那一帧要喂"参考步速"，否则真实速度≈0 →
            // 播放速度被压到下限 → 动画定格而人已经在位移（"做完动作再走会平移"）。
            ApplyPlaybackSpeed(_pendingStartingUp
                                   ? (gait == Gait.奔跑 ? ReferenceRunSpeed : ReferenceWalkSpeed)
                                   : speed,
                               gait);
            _pendingStartingUp = false;
            return;
        }

        // ② 表情
        if (_remaining > 0f && _currentTimeline != 0)
        {
            EnterLocomotionMode(character, _currentTimeline);
            _lastWrittenBase = _currentTimeline;

            _locoTimeline = 0;
            _idleTimeline = 0;
            _turnTimeline = 0;
            _locoAnnounced = 0;   // 离开移动：下次起步重新起播踢

            IsWalking = false;
            IsRunning = false;

            // ⚠️⚠️ 只在"刚起播"这一帧调 PlayTimeline —— 不能每帧调！
            // 每帧 PlayTimeline = 每帧从第 0 帧重来 = 动画永远迈不出第一步，
            // 这正是"定格 + 平移"的老毛病。起播之后，靠上面那次
            // EnterLocomotionMode 每帧补写 BaseOverride 把它兜住即可。
            if (_announcedTimeline != _currentTimeline)
            {
                _announcedTimeline = _currentTimeline;
                character->Timeline.TimelineSequencer.PlayTimeline(_currentTimeline);
            }

            return;
        }

        // ⚠️ 走到这里说明"没有动作在播"了 —— 清掉起播标记，
        // 下一个新动作才能重新触发 PlayTimeline。
        _announcedTimeline = 0;

        // ③ 转身
        if (_turnRequested && _turnTimeline != 0)
        {
            EnterLocomotionMode(character, _turnTimeline);
            _lastWrittenBase = _turnTimeline;

            _locoTimeline = 0;
            _idleTimeline = 0;
            _locoAnnounced = 0;

            IsWalking = false;
            IsRunning = false;
            return;
        }

        // ④ 待机 —— v0.18.60：姿势激活时**完全让位**（游戏表情管线在续循环段，
        // 我们再写 BaseOverride/PlayTimeline 会把它搅了）；否则默认待机基底。
        if (_poseEmoteActive)
        {
            IsWalking = false;
            IsRunning = false;
            return;
        }

        // v0.18.62：PlayEmote 失败时退回④基底循环（姿势循环时间线保证可见，v0.18.57 已验证的机制；
        // 没有过渡，但好过完全静止不动）。
        var idle = _poseFallbackBase != 0 ? _poseFallbackBase : IdleTimeline;
        EnterLocomotionMode(character, idle);
        _lastWrittenBase = idle;

        _locoTimeline = 0;
        _idleTimeline = idle;
        _locoAnnounced = 0;   // 离开移动：下次起步重新起播踢

        // ⚠️ 待机起播踢（照 locomotion 踢的成熟模式）：BaseOverride 只决定"基底是哪条"，
        // 不负责"把它播起来"。值变了（选中/换选/从移动回归）必须踢一次 PlayTimeline；
        // 写进去的值没留住（模型重建期被冲掉）就再踢，限频 0.5s 共用 _locoKickCooldown
        // —— 每帧踢 = 从第 0 帧重播 = 定格，绝不能那样。
        if (_idleAnnounced != idle || (!_lastWriteStuck && _locoKickCooldown <= 0f))
        {
            _idleAnnounced    = idle;
            _locoKickCooldown = 0.5f;
            TracePose($"④待机起播踢 #{idle}（{(idle == _poseFallbackBase && idle != 0 ? "姿势循环" : "默认待机")}）");
            character->Timeline.TimelineSequencer.PlayTimeline(idle);
        }

        IsWalking = false;
        IsRunning = false;
    }

    /// <summary>四种人格的小动作偏好池（索引 = 人格枚举值）。空池 = 用通用池。</summary>
    private readonly List<ushort>[] _personaPools = [new List<ushort>(), new List<ushort>(), new List<ushort>(), new List<ushort>()];

    /// <summary>按人格挑一个小动作（自言自语时的肢体语言）。不同人格用不同的动作池，手感完全不同。</summary>
    public void PlayIdleGesture(int persona = -1)
    {
        var pool = _idlePool;
        if (persona >= 0 && persona < _personaPools.Length && _personaPools[persona].Count > 0)
            pool = _personaPools[persona];

        if (pool.Count == 0)
            return;

        var id    = pool[Random.Shared.Next(pool.Count)];
        var entry = _actions.FirstOrDefault(a => a.Timeline == id);
        var name  = entry.Name ?? $"#{id}";

        // v0.18.54：待机小动作也走时长分档 —— 从池子里摇到"摇头"时同样只摇一次。
        PlayByTimeline(id, name, NpcGesture.DurationFor(name));
    }

    private float   _verifyIn;
    private ushort  _verifyId;
    private string  _verifyFor = string.Empty;

    // ── 换区冻结窗口（v0.17.6）──
    //
    // ⚠️ 崩溃实录（crash-20261008180932 / 181414，两份堆栈完全相同）：
    //   StopAction → NpcAnimator.Stop() → PlayActionTimeline → C0000005。
    // 换区时游戏会销毁并重建角色对象，而我们缓存的 Address 变成野指针
    // （NpcHandle.IsAlive 只查"地址非零"，形同虚设）。loading 期间玩家坐标瞬移
    // → "移动起来就打断"每帧成立 → 每帧 Stop() → 踩上正在销毁的动画系统。
    // 修法：换区后一段时间内**一个原生动画字段都不碰**（托管状态照常清），
    // 等对象表稳定了再恢复。这是能拿到的最硬的防崩手段
    //（native 的 access violation 在托管层 catch 不住，只能从源头避免）。
    private int _frozenFrames;

    /// <summary>换区冻结是否生效中（true = 禁止一切原生动画写入）。</summary>
    public bool Frozen => _frozenFrames > 0;

    /// <summary>冻结 N 帧的原生动画写入（换区时调用）。重复调用取较大值。</summary>
    public void FreezeFor(int frames) => _frozenFrames = Math.Max(_frozenFrames, frames);

    /// <summary>每帧递减冻结计数（由控制器调用）。</summary>
    public void TickFreeze()
    {
        if (_frozenFrames > 0)
            _frozenFrames--;
    }

    /// <summary>
    /// 换区 / 对象失效时调用：<b>只清托管状态，绝不触碰原生字段</b>。
    /// 与 <see cref="Stop"/> 的区别是这里连 Mode / BaseOverride 都不写 ——
    /// 对象可能已经是野指针，写它就是崩溃。
    /// </summary>
    public void Silence()
    {
        _remaining     = 0f;
        CurrentAction  = null;
        IsWalking      = false;
        IsRunning      = false;
        _saved         = false;      // 原生模式八成已被游戏重置，别再"还原"
        _locoTimeline  = 0;
        _idleTimeline  = 0;
        _turnTimeline  = 0;
        _turnRequested = false;
        _turnDir       = 0;
        _wasMoving     = false;
        _verifyIn      = 0f;
        // v0.18.59/60：姿势状态一并清（换区/冻结前后都会走这里，停下后重新走放松计时）。
        // 对象可能是野指针，StopTimeline 不能调，只清托管。
        _poseEmoteActive = false;
        _activePoseLoop  = 0;
        ActiveIdleEmoteRowId = 0;
        _relaxed       = false;
        _idleAutoTimer = 0f;
        _poseSeenEmoting = false;
        _poseEmoteGrace  = 0f;
        _poseFallbackBase = 0;
        _currentTimeline = 0;        // 对象可能是野指针，连 StopTimeline 都不能调，只清托管
        _idleAnnounced   = 0;        // v0.18.56：换区后待机④要重新起播
        _frozenFrames  = Math.Max(_frozenFrames, 120);   // 顺带冻结 ~2 秒
        _jumpHold      = 0f;
        _jumpPhase     = 0;
        _jumpPhaseT    = 0f;
        _jumpSeqTimeline = 0;
        _jumpSeqSeen   = false;
        _jumpApexSeen  = false;

        if (_npc is not null)
            _npc.IsPlayingAction = false;
    }

    /// <summary>每帧调用：动作到时间就恢复待机；顺便做动画自检。</summary>
    public void Update(float deltaTime)
    {
        // 换区冻结期：连只读的原生访问都不做（对象可能是野指针）。
        if (Frozen)
        {
            _remaining = 0f;
            return;
        }

        VerifyAnimation(deltaTime);

        // 跳跃占有窗口倒计时（v0.18.8）
        if (_jumpHold > 0f)
            _jumpHold -= deltaTime;

        // v0.18.21：locomotion 起播踢的限频计时（没留住时最多每 0.5s 补一脚）。
        if (_locoKickCooldown > 0f)
            _locoKickCooldown -= deltaTime;

        // 跳跃相位推进（v0.18.12）：**轮询 sequencer**——31 从所有槽位里消失的那一刻
        // 就是它播完的那一帧，立刻无缝切 32（零间隙）。
        //
        // ⚠️ 不能用盲定时器：v0.18.11 固定 0.2s，但 31 的实际长度是游戏说了算——
        // 定时器比 31 长就在 31 和 32 之间露底动画（用户实测"31 和 32 间没连上"）。
        // 槽位扫描：TimelineIds[0..13] 任意一个还是 31 = 还在播；全都不是 = 播完了。
        if (_jumpPhase == 1)
        {
            _jumpPhaseT += deltaTime;

            if (_npc is { IsAlive: true })
            {
                var ids          = ((Character*)_npc.Address)->Timeline.TimelineSequencer.TimelineIds;
                var stillPlaying = false;
                for (var i = 0; i < ids.Length; ++i)
                {
                    if (ids[i] == JumpTimeline)
                    {
                        stillPlaying = true;
                        break;
                    }
                }

                // 正点：先"看见过 31"（PlayTimeline 写槽可能晚一帧），
                // 31 从所有槽位消失的那一帧 = 播完 → 立刻切 32，零间隙。
                if (_jumpSeqSeen && !stillPlaying)
                {
                    TraceJump($"31→32（31播完）t={_jumpPhaseT:F2}s");
                    PlayJumpFall();
                }
                else if (stillPlaying)
                {
                    _jumpSeqSeen = true;
                }
            }

            // ★ 硬上限（v0.18.15）：31 只是"起跳那一瞬间"，不允许挂满整个上升段。
            //   trace 实测槽位消失要 0.15~0.20s，用户判定太久 → 无论槽位状态，
            //   0.07 秒必切 32（PlayJumpFall 会显式 StopTimeline 摘掉 31，无残留）。
            if (_jumpPhase == 1 && _jumpPhaseT > 0.07f)
            {
                TraceJump($"31→32（0.07s上限）t={_jumpPhaseT:F2}s");
                PlayJumpFall();
            }
        }

        // v0.18.59：停止移动满「放松等待时间」才进入待机姿势（要在 _remaining 早退之前跑，计时才连续）。
        UpdateIdleAutoRandom(deltaTime);

        // v0.18.60：姿势管线旁路保护（被游戏重置就放弃接管）。
        UpdatePoseGuard(deltaTime);

        if (_remaining <= 0f)
            return;

        _remaining -= deltaTime;
        if (_remaining <= 0f)
            Stop();
    }

    /// <summary>
    /// v0.18.64：姿势进完后的计时安排。
    /// 「自动随机」开着 → <see cref="_relaxed"/> 复位 + 计时重开（站满<b>切换间隔</b>换下一个）；
    /// 固定姿势 → 锁住（<see cref="_relaxed"/>=true），等下次移动/推送再说。
    /// ⚠️ v0.18.66：随机续循环改用独立的 <see cref="IdleSwitchSeconds"/>（默认 20s），
    /// 不再共用放松等待时间 —— 用户把 Relax 调小后起手每 3 秒重播（实测打回）。
    /// 透支量：switch≤5s 时也至少隔 5 秒才换，绝不每帧乱跳。
    /// </summary>
    private void SchedulePoseCycle()
    {
        if (IdleAutoRandom)
        {
            _relaxed       = false;
            _idleAutoTimer = MathF.Min(0f, IdleSwitchSeconds - 5f);
        }
        else
        {
            _relaxed = true;
        }
    }

    /// <summary>
    /// v0.18.59：放松等待时间 —— <b>停止移动后</b>等多久才进入待机姿势。
    /// 站立不动（没走动、没冻结、没在做一次性小动作）累计满等待时间 → 进入姿势：
    /// 开了「自动随机」就随机挑一个与上次不同的；没开就用配置选的（0 = 继续默认待机）。
    /// 走动会把计时清零（在①里），冻结期不数；一轮只进一次，之后等下次移动再说。
    /// </summary>
    private void UpdateIdleAutoRandom(float deltaTime)
    {
        if (IsWalking)
        {
            _idleAutoTimer = 0f;
            _relaxed       = false;
        }

        // 完全没用姿势功能（没选姿势也没开随机）：不用数。
        if (!IdleAutoRandom && _cfgIdleRow == 0)
        {
            _idleAutoTimer = 0f;
            return;
        }

        if (_npc is not { IsAlive: true } || Frozen || IsWalking || _remaining > 0f)
            return;

        if (_relaxed)
            return;

        _idleAutoTimer += deltaTime;
        // v0.18.66：阈值分两档 —— 已在姿势循环里（_poseFallbackBase≠0）→ 用「随机切换间隔」；
        // 首次进姿势（移动清了基底）→ 用「放松等待时间」（下限 0，v0.18.61：停下立即进姿势）。
        var wait = _poseFallbackBase != 0
            ? MathF.Max(5f, IdleSwitchSeconds)
            : MathF.Max(0f, IdleRelaxSeconds);
        if (_idleAutoTimer < wait)
            return;

        _idleAutoTimer = 0f;
        _relaxed       = true;

        ushort playEmoteRow, loopTl, row;
        if (IdleAutoRandom)
        {
            var choices = LoadIdleChoices();
            var others  = choices.Where(c => c.EmoteRowId != _lastPoseRow).ToList();
            var pick    = others.Count > 0
                ? others[Random.Shared.Next(others.Count)]
                : choices[Random.Shared.Next(choices.Count)];
            (playEmoteRow, loopTl, row) = (pick.PlayEmoteRow, pick.LoopTimeline, pick.EmoteRowId);
        }
        else
        {
            (playEmoteRow, loopTl, row) = (_cfgPlayEmoteRow, _cfgIdleLoop, _cfgIdleRow);
        }

        // PlayEmote 管线：先播槽[1]起手段、播完 native 链进槽[0]循环段（过渡由游戏保证）。
        if (playEmoteRow != 0)
            EnterPoseEmote(playEmoteRow, loopTl, row);
    }

    /// <summary>
    /// 给四种人格各分一组小动作。
    ///
    /// 这是"像个人"最省力的一环：傲娇的 NPC 不会做"加油"这种动作，
    /// 元气的会招手拍手，弱气更常做低头 / 拘谨类的小动作。动作来自同一张表，只是分配不同。
    ///
    /// <para>⚠️⚠️ v0.18.51 整段重写：关键字**整份换掉**，不再沿用旧表。</para>
    ///
    /// <para>旧表里有两个坑：<b>"记录"</b> 会命中「用触控板记录」（自拍动作），
    /// <b>"摇头"</b> 让安静打招呼时连摇两次头（用户点名的两个事故）。
    /// 自拍类已在 <see cref="BannedActionKeywords"/> 里整条剔除，这里再把
    /// "摇头"从安静的池子里拿掉 —— 安静的日常肢体语言应该是点头 / 环顾 / 行礼那一类。</para>
    ///
    /// <para>⚠️ 这个池子只用于<b>待机时随机插的小动作</b>（没有台词时的肢体语言）。
    /// 说话时配什么动作由 <see cref="NpcGesture"/> 按台词含义决定，不走这里。</para>
    /// </summary>
    private void BuildPersonaPools()
    {
        // 人格 → 偏好的动作名关键字（表里的本地化名）。
        //
        // ⚠️⚠️ v0.18.54：这份关键字表**全部换成 actions.txt 里实锤存在的名字**。
        // 旧表里 12 个关键字在国服表里根本不存在（环顾 / 伸懒腰 / 打瞌睡 / 跳舞 /
        // 叉腰 / 生气 / 低头 / 紧张 / 道歉 / 惊慌 / 鞠躬 …），最惨的是**弱气**——
        // 六个关键字里只有「害羞」能命中，池子里就剩 1 个动作，等于待机时几乎不动。
        // 这就是"动作太少"的一条暗线：不是概率调低了，是池子本来就是空的。
        //
        // 新表按人格性格重挑，且每个关键字都对着 293 个真实名字核过：
        var likes = new[]
        {
            // 安静：幅度小、不抢戏
            new[] { "点头", "行礼", "思考", "张望", "伸展", "打盹", "托眼镜" },
            // 元气：有存在感、爱招呼
            new[] { "招手", "挥手", "拍手", "加油", "激励", "高兴", "干劲十足", "雀跃" },
            // 傲娇：嘴硬那一挂，但落点是可爱
            new[] { "不满", "愠怒", "摇头", "否定", "害羞", "捂脸", "表情：嘟嘴" },
            // 弱气：拘谨、小心翼翼
            new[] { "害羞", "捂脸", "点头", "行礼", "搓手", "慌张", "表情：担心" },
        };

        for (var persona = 0; persona < likes.Length && persona < _personaPools.Length; ++persona)
        {
            var pool = _personaPools[persona];

            foreach (var (name, timeline) in _actions)
            {
                foreach (var keyword in likes[persona])
                {
                    if (!name.Contains(keyword, StringComparison.Ordinal))
                        continue;

                    if (!pool.Contains(timeline))
                        pool.Add(timeline);

                    break;
                }
            }
        }

        var summary = string.Join(" / ", _personaPools.Select(p => p.Count));
        _log.Information("[SmartNpc] 人格小动作池：{0}", summary);
    }

    /// <summary>从 "行号=Key" 里取出行号。</summary>
    private static ushort ParseRowId(string entry)
    {
        var at = entry.IndexOf('=');
        return at <= 0 || !ushort.TryParse(entry[..at], out var id) ? (ushort)0 : id;
    }

    /// <summary>
    /// 播放后 1 秒自检一次：<b>动画到底有没有被游戏接受</b>。
    ///
    /// 这是本插件目前最关键的一个诊断——"调用发出去了但画面没反应"有两种完全不同的成因：
    ///   · <c>IsTimelinePlaying=True</c>  → 动画在播，问题在**播错了/被覆盖**；
    ///   · <c>IsTimelinePlaying=False</c> → 这个 actor 的动画管线根本没被驱动，
    ///     那么任何时间轴都不会有反应（继续调播放方式是浪费时间，得换实现路线）。
    /// </summary>
    private void VerifyAnimation(float deltaTime)
    {
        if (_verifyIn <= 0f)
            return;

        _verifyIn -= deltaTime;
        if (_verifyIn > 0f)
            return;

        _verifyIn = 0f;

        if (_npc is not { IsAlive: true })
            return;

        var go        = (GameObject*)_npc.Address;
        var character = (Character*)_npc.Address;
        var draw      = go->DrawObject;

        _log.Information(
            "[SmartNpc] 动画自检「{0}」(#{1})：时间轴在播={2}｜模式={3}｜绘制可见={4}｜类型={5}",
            _verifyFor, _verifyId,
            go->IsTimelinePlaying(0), character->Mode,
            draw is not null && draw->IsVisible, go->ObjectKind);
    }

    /// <summary>
    /// 停止动作并还原原始动画模式（写入与还原必须对称）。
    /// 返回是否真的有东西在播 —— 面板/指令用它决定要不要提示用户。
    /// </summary>
    public bool Stop()
    {
        var wasPlaying = _remaining > 0f || IsBusy;

        // ⚠️ 幂等守卫（v0.17.6）：已经是干净状态就**一个原生字段都不碰**。
        //
        // 这是两件事的共同根因：
        //   1. 自动打断路径（"移动起来就打断动作"）**每帧**都会调到这里 ——
        //      旧版每次都执行 EnterLocomotionMode + PlayActionTimeline(待机)，
        //      动画状态每帧被重置一遍（平移/卡帧的来源之一），
        //      动作结束后又立刻插新动作，表现为开关"根本没生效"。
        //   2. 换区窗口期反复踩原生字段 = C0000005（实测崩了两回）。
        // v0.18.53：口型单独看 —— 就算没有身体动作在播，嘴也可能还张着。
        if (_lipsCurrent != 0)
        {
            if (!Frozen && _npc is { IsAlive: true })
                WriteLipsOverride(0);

            _lipsCurrent = 0;
            _lipsIndex   = 0;
            wasPlaying   = true;
        }

        if (!wasPlaying && !_saved)
            return false;

        _remaining    = 0f;
        CurrentAction = null;
        IsWalking     = false;
        IsRunning     = false;

        // 换区冻结期：只清托管记账，原生那边是雷区。
        if (Frozen)
        {
            _saved = false;
            if (_npc is not null)
                _npc.IsPlayingAction = false;
            return wasPlaying;
        }

        if (_npc is { IsAlive: true })
        {
            _npc.IsPlayingAction = false;

            var c = (Character*)_npc.Address;
            var modeBefore = c->Mode;
            var baseBefore = c->Timeline.BaseOverride;

            // 把挂在 sequencer 里的一次性动作摘掉。
            if (_currentTimeline != 0)
            {
                c->StopTimeline(_currentTimeline);
                _currentTimeline = 0;
            }

            //
            // ⚠️⚠️ v0.18.0：**不再用 AnimLock，也不要"强制覆盖成走路"**。
            //
            // 旧版打断时会 SetMode(AnimLock) 再 EnterLocomotionMode(走路)，
            // 两件事都错：AnimLock 的语义是"锁住当前姿态"（会让角色定格），
            // 而"强行写走路"则属于<see cref="ApplyAuthority"/>的职责 ——
            // 在这里抢着写，等于又开了一条竞争的通路。
            //
            // 打断的正确语义只有一句：<b>让计时归零</b>。
            // 计时归零 → 下一帧 ApplyAuthority 自动按优先级落到移动 / 待机，
            // 不需要在这里做任何原生写入。
            //

            // ⚠️ 仍然保留 _saved 的清理：AnimLock 通路已废弃，
            // 不再需要"还原"什么，直接让记账失效即可。
            _saved = false;

            TraceAction($"STOP busy={wasPlaying} tl={baseBefore} mode={modeBefore}→{c->Mode} base={baseBefore}→{c->Timeline.BaseOverride}");
        }
        else
        {
            _saved = false;
        }

        return wasPlaying;
    }

    // ── 动作诊断（v0.17.14）──
    private static int _lastTraceTick;

    private static void TraceAction(string line)
    {
        var now = Environment.TickCount;
        if (now - _lastTraceTick < 300)
            return;
        _lastTraceTick = now;

        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncherCN", "pluginConfigs", "SmartNpc");
            System.IO.Directory.CreateDirectory(dir);

            System.IO.File.AppendAllText(
                System.IO.Path.Combine(dir, "trace-action.txt"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}\n",
                new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // 诊断写入绝不能影响插件本身
        }
    }

    // ── 姿势/回应诊断（v0.18.66）──
    // ⚠️ 不能走 Diag?.Append：last-diag.txt 的心跳 Write() 是整文件覆盖，Append 的行
    // 下一次心跳就被清掉（v0.18.64 的「姿势进入」日志 0 命中的原因）。
    // 这里走独立 append-only 文件，逐条全记、不节流 —— 姿势是低频事件。
    private static void TracePose(string line)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncherCN", "pluginConfigs", "SmartNpc");
            System.IO.Directory.CreateDirectory(dir);

            System.IO.File.AppendAllText(
                System.IO.Path.Combine(dir, "trace-pose.txt"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}\n",
                new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // 诊断写入绝不能影响插件本身
        }
    }

    // ── 跳跃诊断（v0.18.13）──
    // ⚠️ 不能复用 TraceAction：它有 300ms 静态节流，而一次跳跃的三段切换
    // 全部发生在 ~0.7 秒内，会被吞掉两段。跳跃是低频事件，逐条全记，不节流。
    private static void TraceJump(string line)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncherCN", "pluginConfigs", "SmartNpc");
            System.IO.Directory.CreateDirectory(dir);

            System.IO.File.AppendAllText(
                System.IO.Path.Combine(dir, "trace-jump.txt"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}\n",
                new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // 诊断写入绝不能影响插件本身
        }
    }

    /// <summary>
    /// 切换"走路 / 奔跑"动画。跟随逻辑每帧告知她是否真的在移动、以及用的什么姿态。
    ///
    /// 为什么需要它：我们是<b>直接写 <c>Position</c></b> 让她移动的，
    /// 游戏不会给她 locomotion 状态 → 不主动播的话她就是一路平移（像滑冰）。
    /// 把行走时间轴设成 <c>Timeline.BaseOverride</c> 是最直接的办法。
    ///
    /// 优先级：<b>有动作在播时不抢</b>。表情比走路重要，
    /// 否则她会一边挥手一边走。
    ///
    /// ⚠️ 走/跑要切<b>不同的时间轴</b>。用走路动画去跑 = 腿迈小步、身体平移，
    /// 比没有动画更假。切时间轴的代价是一次 PlayTimeline，所以只在"跨档"那一帧切。
    /// </summary>
    /// <summary>待机循环动作（<c>normal/idle</c>）；0 = 没找到，停下时就不额外播。</summary>
    public ushort IdleTimeline { get; private set; }

    /// <summary>原地左转 / 右转的动作；0 = 表里没找到，转身就退回平滑旋转。</summary>
    public ushort TurnLeftTimeline  { get; private set; }
    public ushort TurnRightTimeline { get; private set; }

    /// <summary>
    /// 跳跃动作（v0.17.8）。0 = 表里没找到（那就只做物理跳跃、没有动画）。
    /// 避障被矮障碍挡住且头顶有空间时尝试跳跃用。
    /// </summary>
    public ushort JumpTimeline { get; private set; }

    /// <summary>跳跃中 / 掉落的时间轴（用户实测指定 32，v0.18.9）。</summary>
    public ushort JumpFallTimeline { get; set; } = 32;

    /// <summary>落地的时间轴（用户实测指定 33，v0.18.9）。</summary>
    public ushort JumpLandTimeline { get; set; } = 33;

    /// <summary>
    /// 有没有可用的跳跃动画（v0.17.13）。
    /// 没有就不许跳 —— 用户实测"跳一下完全没有动作"，宁可撞墙也不飞而无骨。
    /// </summary>
    public bool HasJumpAnim => JumpTimeline != 0;

    /// <summary>
    /// 播跳跃动画。<b>刻意不走表情通路（AnimLock + _remaining）</b>：
    /// 跳跃期间她必须继续水平移动（不然跳了个寂寞），而表情通路会触发
    /// "动作中不许移动"的铁律。这里直接走移动通路（Normal + BaseOverride = jump），
    /// 落地后 SetWalking 会把 BaseOverride 切回走/跑，自动恢复，零特殊状态。
    /// </summary>
    public void PlayJump()
    {
        if (_npc is not { IsAlive: true } || Frozen || JumpTimeline == 0)
            return;

        // ⚠️⚠️ v0.18.10：31/33 是**一次性动作**，走 TimelineSequencer 叠加播放，
        // 绝不能进 BaseOverride —— BaseOverride 的语义是**循环**，一次性动作放进去
        // 会反复从头重播 = 用户实测的"起跳落地动作一直在抽搐"。
        // 32（空中/掉落）是可循环的姿态，继续留在 BaseOverride（见 PlayJumpFall）。
        // 起跳阶段不锁权威：BaseOverride 继续由正常流程维持（走路/待机），31 叠在上面播。
        var character = (Character*)_npc.Address;

        // 清掉上一跳的 sequencer 残留（如还没播完的 33）
        if (_jumpSeqTimeline != 0)
            character->StopTimeline(_jumpSeqTimeline);

        character->Timeline.TimelineSequencer.PlayTimeline(JumpTimeline);
        _jumpSeqTimeline = JumpTimeline;
        _jumpSeqSeen     = false;   // 重新开始槽位轮询（v0.18.12）
        _jumpApexSeen    = false;   // v0.18.13：物理到顶标记一并复位

        TraceJump("31 起跳");

        _jumpPhase  = 1;
        _jumpPhaseT = 0f;
        _jumpHold   = 0f;
    }

    /// <summary>切到空中/掉落时间轴（32）。v0.18.11：起跳 0.2 秒后即切（不再等到到顶下落）。</summary>
    public void PlayJumpFall()
    {
        if (_npc is not { IsAlive: true } || Frozen || _jumpPhase is not (1 or 2) || JumpFallTimeline == 0)
            return;

        // 空中：32 进 BaseOverride（可长驻姿态），并锁权威——走路/待机不能把它顶回去；
        // 落地时 PlayJumpLand 解锁。
        var character = (Character*)_npc.Address;

        // 31 若还在 sequencer 里（没播完就到了切换点），先摘掉，别和 32 叠着
        if (_jumpSeqTimeline != 0 && _jumpSeqTimeline != JumpFallTimeline)
        {
            character->StopTimeline(_jumpSeqTimeline);
            _jumpSeqTimeline = 0;
        }

        EnterLocomotionMode(character, JumpFallTimeline);
        _jumpPhase = 2;
        _jumpHold  = 2.0f;
    }

    /// <summary>播落地动画（33）。一次性叠加，播完自然结束，无需锁定（v0.18.10）。</summary>
    public void PlayJumpLand()
    {
        if (_npc is not { IsAlive: true } || Frozen || _jumpPhase == 0 || JumpLandTimeline == 0)
            return;

        var character = (Character*)_npc.Address;
        if (_jumpSeqTimeline != 0)
        {
            character->StopTimeline(_jumpSeqTimeline);
            _jumpSeqTimeline = 0;
        }
        character->Timeline.TimelineSequencer.PlayTimeline(JumpLandTimeline);
        _jumpSeqTimeline = JumpLandTimeline;
        _jumpPhase = 0;   // 立刻交还权威：base 恢复走路/待机，33 叠在上面播完
        _jumpHold  = 0f;

        TraceJump("33 落地");
    }

    /// <summary>
    /// 物理层"到顶"回调（垂直速度转负）。<b>只记录、不切动画</b>（v0.18.13）。
    ///
    /// 32 的唯一正点切换是"31 播完"（槽位轮询）。到顶信息有两个用途：
    /// ① 槽位扫描异常（始终没见过 31）时的第一兜底 —— 她已经在下落，不能再挂起跳；
    /// ② trace-jump.txt 里的时间锚点，用于核对 31 的实际时长与物理滞空的关系。
    /// </summary>
    public void NotifyJumpApex()
    {
        if (_jumpPhase is not (1 or 2))
            return;

        if (!_jumpApexSeen)
        {
            _jumpApexSeen = true;
            TraceJump("apex（到顶，物理）");
        }
    }

    /// <summary>跳跃动画的剩余占有时间（&gt;0 时 ApplyAuthority 不写任何 locomotion，v0.18.8）。</summary>
    private float _jumpHold;

    /// <summary>跳跃动画相位：0 无 / 1 起跳(31) / 2 空中下落(32) / 3 落地(33)（v0.18.9）。</summary>
    private int   _jumpPhase;

    /// <summary>当前相位的已持续时间（秒）。</summary>
    private float _jumpPhaseT;

    /// <summary>当前挂在 TimelineSequencer 里的跳跃一次性动作（31/33），切换/收尾时摘掉（v0.18.11）。</summary>
    private ushort _jumpSeqTimeline;

    /// <summary>本轮起跳是否已在 sequencer 槽位里看见过 31（v0.18.12 槽位轮询用）。</summary>
    private bool _jumpSeqSeen;

    /// <summary>本轮跳跃是否已到物理顶点（v0.18.13：兜底切换 + 诊断锚点，不直接切 32）。</summary>
    private bool _jumpApexSeen;

    /// <summary>当前正在播的 locomotion 时间轴（0 = 没在播）。</summary>
    private ushort _locoTimeline;

    /// <summary>当前正在播的转身动作（0 = 没在播）。</summary>
    private ushort _turnTimeline;

    /// <summary>
    /// 是否正处于"转身"中。
    /// ⚠️ 必须让 SetWalking 看得见：否则停下分支每帧清 BaseOverride / 播待机，
    /// 会把刚播出去的转身动作**立刻抹掉** —— 这是"转身动作一直不出现"的第二个原因。
    /// </summary>
    private bool _turnRequested;

    /// <summary>上一次的转身方向（&lt;0 左 / &gt;0 右 / 0 不转），用来判重。</summary>
    private int _turnDir;

    public bool IsTurning => _turnRequested;

    /// <summary>
    /// ⚠️ 动作（表情）刚结束时必须调用：
    /// StopAction 会把 BaseOverride 清成 0，但 _locoTimeline/_idleTimeline 的记账还留在旧值，
    /// SetWalking 会误以为"动作还在播"而不重播 → 从动作切回走路的瞬间平移。
    /// 清掉记账，强制下一帧重新发起时间轴。
    /// </summary>
    public void ResetLocomotionBookkeeping()
    {
        _locoTimeline  = 0;
        _idleTimeline  = 0;
        _turnTimeline  = 0;
        _turnRequested = false;
        _turnDir       = 0;
        _locoAnnounced = 0;   // v0.18.20：动作结束后起步要重新起播踢

        // ⚠️ 动作期间她是"停着的"，但 _wasMoving 可能是动作开始前的 true。
        // 不清掉的话动作结束那一帧会被判成"已经在移动"，从而拿不到起步提速，
        // 平移照旧（v0.17.2）。
        _wasMoving = false;
    }

    /// <summary>当前正在播的待机循环（0 = 没在播）。</summary>
    private ushort _idleTimeline;

    /// <summary>
    /// 已经对 sequencer 发起过<b> locomotion 起播踢</b>的那条时间轴 id（v0.18.20）。
    ///
    /// ⚠️ 与 <see cref="_announcedTimeline"/>（表情通路）分开记账：
    /// locomotion 只在"起步 / 换步态"时 PlayTimeline 一次，之后靠每帧
    /// BaseOverride 补写维持。停下（表情/转身/待机分支）清零，下次起步重新踢。
    /// </summary>
    private ushort _locoAnnounced;

    /// <summary>上一帧我们往 <c>Timeline.BaseOverride</c> 写的是哪条时间轴（v0.18.21）。</summary>
    private ushort _lastWrittenBase;

    /// <summary>
    /// 上一帧写进去的值**这一帧还在吗**（v0.18.21）。
    ///
    /// <para>slide-trace 实锤：点亮结束后约 2.2 秒里，我们每帧写 13/22，
    /// 下一帧读回来永远是 0 —— 模型重建期间游戏每帧把动画字段冲掉。
    /// 那段时间她照常位移（移动层不看动画），于是就是"站笔直滑行"。</para>
    ///
    /// <para>有了这个标志就不用猜等多久：<b>写进去的值留住了</b> = 动画系统真的接管了。</para>
    /// </summary>
    private bool _lastWriteStuck;

    /// <summary>locomotion 起播踢的冷却（秒）—— 没留住时最多每 0.5s 补一脚。</summary>
    private float _locoKickCooldown;

    /// <summary>
    /// 当前挂进 <b>TimelineSequencer</b> 的一次性动作（表情）的时间轴 id（v0.17.9）。
    ///
    /// ⚠️⚠️ 这是"打断后边走边做动作"的元凶：表情三件套用 PlayTimeline 把一次性
    /// 动作挂进了 sequencer，只清 BaseOverride / AnimLock **拦不住它** —— 它继续播，
    /// 和下一帧的走路 BaseOverride 并存。打断时必须显式 StopTimeline。
    /// </summary>
    private ushort _currentTimeline;

    /// <summary>
    /// 上一帧是不是"正在移动"。
    /// 用来区分<b>起步</b>与<b>减速</b> —— 两者需要完全相反的动画速度策略（见 ApplyPlaybackSpeed）。
    /// </summary>
    private bool _wasMoving;

    /// <summary>本帧"想"播走路吗（由移动层每帧告知）。</summary>
    private bool _wantMoving;

    /// <summary>本帧的移动速度（换算播放速度用）。</summary>
    private float _wantSpeed;

    /// <summary>本帧的姿态。</summary>
    private Gait _wantGait;

    /// <summary>本帧是"刚起步"吗（起步帧要喂参考步速，避免动画定格）。</summary>
    private bool _pendingStartingUp;

    /// <summary>
    /// 已经对 sequencer 发起过起播的那条时间轴 id。
    ///
    /// ⚠️ 必须与 _currentTimeline 分开记账：_currentTimeline 表示"当前想播哪条"，
    /// 而它只用来保证"每条时间轴只 PlayTimeline 一次"。
    /// 两者混用会导致后续帧反复重启动画（v0.17.2 "做完动作再走会平移" 的教训）。
    /// </summary>
    private ushort _announcedTimeline;

    /// <summary>
    /// 移动层每帧的告知：<b>只在托管侧记账，不写任何原生字段</b>。
    ///
    /// <para>⚠️ v0.18.0 最大的行为变化就在这里。旧版本方法会顺手
    /// <c>EnterLocomotionMode</c> + <c>StopTimeline</c> + <c>ApplyPlaybackSpeed</c>
    /// 一把全做了 —— 于是"移动"和"表情"是两条<b>各自写原生字段</b>的通路，
    /// 谁后跑谁生效，且互相把对方的动画清掉。诊断里那个
    /// <c>base=0→13</c>（表情被写成走路）就是这个竞争的直接证据。</para>
    ///
    /// <para>现在：本方法只存下意图，真正落地统一在 <see cref="ApplyAuthority"/>。</para>
    /// </summary>
    public void SetWalking(bool walking, float speed = 0f, Gait gait = Gait.行走)
    {
        if (_npc is not { IsAlive: true } || Frozen)   // 换区冻结期不记账也不写
            return;

        // 用"这一帧还有没有速度"决定是否在动 —— 不能只听 walking 标志：
        // 到达目标 / 减速时 walking 会逐帧抖动（true ↔ false 来回切），
        // 动画跟着它切就会出现"一帧有走路、一帧是待机"（v0.16.3 的老毛病）。
        var moving = walking || speed > 0.05f;

        _pendingStartingUp = moving && !_wasMoving;
        _wasMoving         = moving;

        _wantMoving = moving;
        _wantSpeed  = speed;
        _wantGait   = gait;
    }

    /// <summary>
    /// 把 <c>BaseOverride</c> 无条件维持成 <paramref name="baseOverride"/>，并把模式维持在 Normal。
    ///
    /// <para><b>为什么必须每帧无条件补写：</b>诊断文件证明，一次写入会被引擎当帧冲掉
    /// （详见类注释）。只有持续补写，动画才能连续播下去。</para>
    ///
    /// <para><b>为什么模式固定 Normal 而不是 AnimLock：</b>
    /// AnamCore（Anamnesis 官方 Dalamud 插件）的原文是
    /// <c>"For movement states (Walk, Run, Sprint, Swim), we must use BaseOverride so it loops correctly."</c>
    /// —— BaseOverride 是用来<b>循环播放</b>的，它要求角色处在 Normal 态。
    /// AnimLock 的语义恰恰相反：把角色钉在当前姿态、不再推进。
    /// 我们 v0.14.0 用 AnimLock（→ 定格）、v0.16.0 改用 PlayActionTimeline
    ///（→ 进槽但骨骼不动），两版都错在没理解这条语义。</para>
    ///
    /// <para>⚠️ 两个"只在变化时写"的守卫<b>仍然必须保留</b>（v0.16.0 的教训）：
    /// <c>SetMode</c> 与 <c>PlayActionTimeline</c> 都是带副作用的调用，
    /// 每帧无条件执行会把动画永远按在第 0 帧。这里只在值真的不同时才写。</para>
    /// </summary>
    private static void EnterLocomotionMode(Character* character, ushort baseOverride = 0)
    {
        if (character->Mode != CharacterModes.Normal)
            character->SetMode(CharacterModes.Normal, 0);

        if (character->Timeline.BaseOverride != baseOverride)
            character->Timeline.BaseOverride = baseOverride;
    }

    /// <summary>
    /// 诊断用：读游戏里 <c>BaseOverride</c> 的**实际当前值**。
    /// 注意它可能与我们刚写进去的不同 —— 那个差值正是"动画被谁清掉了"的线索。
    /// </summary>
    public ushort ActualBaseOverride
    {
        get
        {
            if (_npc is not { IsAlive: true })
                return 0;

            return ((Character*)_npc.Address)->Timeline.BaseOverride;
        }
    }

    /// <summary>
    /// 诊断用：读游戏里 <c>Mode</c> 的实际当前值（v0.18.15）。
    /// 若被游戏置成 Swimming 一类，脚下会常驻踩水涟漪——排查"水波纹"就看它。
    /// </summary>
    public CharacterModes ActualMode
        => _npc is not { IsAlive: true } ? default : ((Character*)_npc.Address)->Mode;

    /// <summary>按姿态挑 locomotion 时间轴。奔跑时间轴没找到就退回走路。</summary>
    private ushort LocomotionTimeline(Gait gait)
        => gait == Gait.奔跑 && RunTimeline != 0 ? RunTimeline : WalkTimeline;

    /// <summary>
    /// 诊断用：游戏 sequencer 里当前<b>有没有时间轴在播</b>（v0.18.20）。
    ///
    /// <para>"站笔直滑行"的判定线索：她在动（BaseOverride=13 每帧都在写）
    /// 但 sequencer 里空空如也 —— BaseOverride 只决定基底是哪条，
    /// 不负责把它播起来。false + 在移动 = 正是那个病。</para>
    /// </summary>
    /// <summary>
    /// 动画系统<b>是否已接管</b>（v0.18.21）：上一帧写进 BaseOverride 的值这一帧还在。
    /// 移动层拿它当"能不能开始位移"的闸门 —— 没接管就动 = 站笔直滑行。
    /// </summary>
    public bool AnimationConfirmed => _lastWriteStuck;

    /// <summary>诊断用：上一帧写进 BaseOverride 的是哪条时间轴。</summary>
    public ushort WrittenBase => _lastWrittenBase;

    /// <summary>
    /// 诊断用：游戏 sequencer 里当前<b>有没有时间轴在播</b>。
    /// ⚠️ 这个读数在本机上恒为 false（v0.18.20 实测），只能当参考，别拿它做判据。
    /// </summary>
    public bool TimelinePlaying
        => _npc is not { IsAlive: true } || _npc.Address == nint.Zero
            ? false
            : ((GameObject*)_npc.Address)->IsTimelinePlaying(0);

    /// <summary>
    /// 原地转身时用<b>转身动作</b>，而不是"整个人平滑地转过去"。
    ///
    /// <paramref name="dir"/>：&lt; 0 左转，&gt; 0 右转，0 = 不转了。
    /// 表里找不到转身动作就什么都不做（退回平滑旋转，和旧行为一致）。
    /// </summary>
    public void SetTurning(int dir)
    {
        if (_npc is not { IsAlive: true } || Frozen)   // 换区冻结期不写任何原生动画字段
            return;

        var wanted = dir < 0 ? TurnLeftTimeline
                   : dir > 0 ? TurnRightTimeline
                             : (ushort)0;

        // ⚠️ 表里大概率找不到转身动作 → wanted 恒为 0；
        // 这不是"状态没变"，不能拿它当判重条件直接 return
        //（v0.16.4 的教训：那样下面的兜底永远执行不到）。
        // 兜底：退到"边转边迈步"，左右分别用固定的 7 / 8 号时间轴。
        if (dir != 0 && wanted == 0)
            wanted = dir < 0 ? KnownTurnLeftTimeline : KnownTurnRightTimeline;

        if (dir == _turnDir)
            return;

        _turnDir = dir;

        if (_turnTimeline != 0)
        {
            ((Character*)_npc.Address)->StopTimeline(_turnTimeline);
            _turnTimeline = 0;
        }

        _turnTimeline  = wanted;
        _turnRequested = dir != 0;
    }

    /// <summary>
    /// 转身要用的时间轴（供 <see cref="ApplyAuthority"/> 取用）。
    /// 表里没找到时退到内置的 7 / 8 —— 由 <see cref="SetTurning"/> 已经算好。
    /// </summary>
    private ushort TurnTimelineForCurrentDirection => _turnTimeline;

    /// <summary>按实际移动速度换算动画播放速度。</summary>
    private void ApplyPlaybackSpeed(float speed, Gait gait)
    {
        if (_npc is not { IsAlive: true })
            return;

        var reference = gait == Gait.奔跑 ? ReferenceRunSpeed : ReferenceWalkSpeed;
        var character = (Character*)_npc.Address;

        // 跑动时允许更大幅度的播放速度缩放：跑的时间轴本身就比走的长，
        // 缩放不足会显得她在"快走"而不是"跑"。
        //
        // ⚠️ 下限的取值经过两轮修正（v0.17.2 定稿）：
        //   · 0.35：减速时腿还在大步迈 → "间歇性平移"（脚在动、人不走）；
        //   · 0.08：修了减速，却把**起步**也压死了 —— 起步那 0.3 秒动画几乎定格，
        //     而位移已经在发生 → "做完动作再走会平移"（用户实测反馈）。
        // 现在改成 0.60 / 0.75，并且起步那一帧由调用方直接喂"参考步速"（见 SetWalking），
        // 于是真正被这个 floor 兜住的只有"低速但没停"的中间态，两边都不挨打。
        var floor = gait == Gait.奔跑 ? 0.75f : 0.60f;
        var scale = Math.Clamp(speed / reference, floor, gait == Gait.奔跑 ? 2.2f : 1.8f) * WalkSpeedScale;
        character->Timeline.OverallSpeed = scale;
    }

    /// <summary>
    /// 自动找一条"行走"时间轴。
    ///
    /// 我们不知道哪个 ActionTimeline 行 id 是走路，所以去 <c>ActionTimeline</c> 表里
    /// 按 Key 关键字找（Walk / Run）。找不到就留 0 —— 走路时没有动画，但别乱填一个 id，
    /// 那比重播一段奇怪动作更糟。
    /// </summary>
    /// <summary>
    /// 发现玩家角色的**走 / 跑**基础动作时间轴。
    ///
    /// <para><b>为什么要精确匹配而不是"第一个含 run 的行"</b>：
    /// <c>ActionTimeline</c> 表有几千行，里面绝大多数是职业技能、表情、坐骑动作。
    /// 原来的写法 <c>runs[0]</c> 取的是<b>表里第一个名字带 run 的</b>，
    /// 那个几乎必然是某个技能动作 —— 于是"跑起来"会播成一套莫名其妙的动作。
    /// 走路之所以看起来对，只是因为 <c>normal/walk</c> 恰好排在前面。</para>
    ///
    /// <para>现在先找<b>精确等于</b> <c>normal/walk</c> / <c>normal/run</c> 的行
    /// —— 这两个就是 FFXIV 玩家角色自己的基础走跑动作（诊断里 13 就是 normal/walk）。
    /// 精确找不到时才退回模糊候选，并在诊断里注明来源。</para>
    /// </summary>
    //
    // 自动发现全挂时的兜底行号。
    // 来源：AQuestReborn（做自定义 NPC 跟随的上游项目）直接写死 走=13 / 跑=22 /
    // 疾跑=30，注释里写的是 "speed < 3.5 ? 13u : 22u"。和我们按 Key 精确匹配到的
    // 是同一批行 —— 两边对得上，说明映射没问题。
    // ActionTimeline 的 Key 列在部分行是空的，那种情况下自动发现会整个失败；
    // 与其"跑起来没动画"，不如用这两个兜底。
    private const ushort KnownWalkTimeline = 13;
    private const ushort KnownRunTimeline  = 22;

    /// <summary>
    /// 原地转身动作的时间轴 id（用户实测指定：8）。
    /// 表里按 Contains("left"/"right") 猜出来的那两个既可能找不到、也可能猜错，
    /// 所以统一用这个兜底。
    /// </summary>
    /// <summary>原地<b>左</b>转的时间轴 id（用户实测指定：7）。</summary>
    private const ushort KnownTurnLeftTimeline  = 7;

    /// <summary>原地<b>右</b>转的时间轴 id（用户实测指定：8）。</summary>
    private const ushort KnownTurnRightTimeline = 8;

    private void DiscoverWalkTimeline(IDataManager data)
    {
        var sheet = data.GetExcelSheet<ActionTimeline>();
        if (sheet is null)
            return;

        var samples   = new List<string>();
        var walks     = new List<string>();
        var runs      = new List<string>();
        string? exactWalk = null;
        string? exactRun  = null;

        foreach (var row in sheet)
        {
            var key = row.Key.ExtractText();
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var norm = key.Replace('\\', '/').Trim().ToLowerInvariant();

            if (samples.Count < 8)
                samples.Add($"{row.RowId}={key}");

            // ★ 精确匹配优先：玩家角色的基础走 / 跑动作。
            if (norm == "normal/walk") { exactWalk ??= $"{row.RowId}={key}"; continue; }
            if (norm == "normal/run")  { exactRun  ??= $"{row.RowId}={key}"; continue; }

            // 跳跃（v0.17.8）：避障"卡住尝试跳跃"要用。
            // 精确 normal/jump 优先；模糊候选**只认 normal/ 前缀**——
            // 整个表里含 "jump" 的行大多是坐骑/职业技能跳，播出来完全不对
            // （用户反馈"跳一下没有动作"：要么 0，要么播了个不相干的东西）。
            if (norm == "normal/jump") { JumpTimeline = (ushort)row.RowId; continue; }
            if (JumpTimeline == 0 && norm.StartsWith("normal/") && norm.Contains("jump"))
            {
                JumpTimeline = (ushort)row.RowId;
                continue;
            }

            // 模糊候选留作兜底（顺序：先 run 再 walk，避免 "walk_run" 被 walk 抢走）。
            if (norm.Contains("run"))
            {
                if (runs.Count < 8) runs.Add($"{row.RowId}={key}");
            }
            else if (norm.Contains("walk"))
            {
                if (walks.Count < 8) walks.Add($"{row.RowId}={key}");
            }
        }

        if (exactWalk is not null)
        {
            WalkTimeline       = ParseRowId(exactWalk);
            WalkTimelineSource = $"normal/walk「{exactWalk}」";
        }
        else if (walks.Count > 0)
        {
            WalkTimeline       = ParseRowId(walks[0]);
            WalkTimelineSource = $"（无 normal/walk）退回模糊候选「{walks[0]}」";
        }

        if (exactRun is not null)
        {
            RunTimeline       = ParseRowId(exactRun);
            RunTimelineSource = $"normal/run「{exactRun}」";
        }
        else if (runs.Count > 0)
        {
            RunTimeline       = ParseRowId(runs[0]);
            RunTimelineSource = $"（无 normal/run）退回模糊候选「{runs[0]}」";
        }

        //
        // ── 待机 / 原地转身 ──
        //
        // 用户说得对：**一个"动作"在游戏里往往不止一条时间轴**。
        // FFXIVClientStructs 里 PlayActionTimeline 的签名就是证据：
        //   PlayActionTimeline(ushort introId, ushort loopId)
        // 起步（intro）+ 循环（loop）两段 —— 这就是"连贯动作"。
        // 除了走/跑，这里再把待机循环和原地转身找出来，
        // 并把所有候选 Key 打进日志，后面要接真正的"起步动作"时直接照着这份清单挑。
        //
        var idleCand = new List<string>();
        var turnCand = new List<string>();

        foreach (var row in sheet)
        {
            var key = row.Key.ExtractText();
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var norm = key.Replace('\\', '/').Trim().ToLowerInvariant();

            if (IdleTimeline == 0 && norm == "normal/idle")
            {
                IdleTimeline = (ushort)row.RowId;
                continue;
            }

            if (norm.Contains("turn"))
            {
                if (turnCand.Count < 24) turnCand.Add($"{row.RowId}={key}");

                // 只认 normal/* 里的左右转，别把坐骑/技能的 turn 当成原地转身。
                if (!norm.StartsWith("normal/")) continue;

                if (TurnLeftTimeline == 0 && norm.Contains("left"))
                    TurnLeftTimeline = (ushort)row.RowId;
                else if (TurnRightTimeline == 0 && norm.Contains("right"))
                    TurnRightTimeline = (ushort)row.RowId;

                continue;
            }

            if (norm.Contains("idle") && idleCand.Count < 24)
                idleCand.Add($"{row.RowId}={key}");
        }

        _log.Information(
            "[SmartNpc] 待机时间轴=#{0}｜转身 左=#{1} 右=#{2}｜idle 候选：{3}｜turn 候选：{4}",
            IdleTimeline, TurnLeftTimeline, TurnRightTimeline,
            string.Join("、", idleCand), string.Join("、", turnCand));

        // 兜底：表里没匹配到就用已知行号。宁可动作平庸，也不要"跑起来没动画"。
        if (WalkTimeline == 0)
        {
            WalkTimeline       = KnownWalkTimeline;
            WalkTimelineSource = $"内置兜底 #{KnownWalkTimeline}（表里没找到 normal/walk）";
        }

        if (RunTimeline == 0)
        {
            RunTimeline       = KnownRunTimeline;
            RunTimelineSource = $"内置兜底 #{KnownRunTimeline}（表里没找到 normal/run）";
        }

        // 兜底（v0.18.9）：用户实测起跳时间轴 = 31（空中 32 / 落地 33 为常量，见属性默认值）。
        if (JumpTimeline == 0)
            JumpTimeline = 31;

        _log.Information(
            "[SmartNpc] 行走时间轴：{0}｜奔跑时间轴：{1}｜精确命中 walk={2} run={3}｜Key 样本：{4}",
            WalkTimeline == 0 ? "**没找到**" : $"#{WalkTimeline}（{WalkTimelineSource}）",
            RunTimeline == 0  ? "**没找到**，奔跑会沿用走路动画" : $"#{RunTimeline}（{RunTimelineSource}）",
            exactWalk is not null, exactRun is not null, string.Join("、", samples));
    }

    private bool TryResolve(string nameOrAlias, out ushort timelineId)
    {
        // 直接给行号也认。
        if (ushort.TryParse(nameOrAlias, out timelineId) && timelineId != 0)
            return true;

        return _lookup.TryGetValue(Normalize(nameOrAlias), out timelineId) && timelineId != 0;
    }

    private string Sample() => string.Join("、", _actions.Take(20).Select(a => a.Name));

    /// <summary>归一化：只保留字母与数字并转小写（中文会原样保留）。</summary>
    private static string Normalize(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        Span<char> buffer = raw.Length <= 256 ? stackalloc char[raw.Length] : new char[raw.Length];
        var length = 0;

        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch))
                buffer[length++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..length]);
    }
}
