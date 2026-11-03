using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace SmartNpc.Core;

/// <summary>
/// 人格与台词。
///
/// 阶段一：规则驱动的"反应式"人格——她看玩家在做什么，然后给出对应反应与台词。
/// 不联网、不调任何 AI 服务，纯本地就能跑，行为可预测。
///
/// 阶段二（预留）：接入 LLM，把「当前情境 + 人格设定」喂进去生成台词。
/// 接口已经留好——只要替换 <see cref="NextLine"/> 的实现，对话层不用动。
/// </summary>
public sealed unsafe class NpcBrain(IPluginLog log, IChatGui? chat, NpcAnimator animator)
{
    /// <summary>
    /// 人格档位。
    ///
    /// ⚠️ v0.18.50：原来的「毒舌」「学者」两档**整档重做**成「傲娇」「弱气」（用户要求）。
    /// 枚举的<b>数值顺序绝对不能变</b> —— 配置里存的是整数，
    /// 换顺序会让老存档点一个人格却选中另一个。
    /// </summary>
    public enum Persona
    {
        /// <summary>话少、省略号、观察细、温柔克制。爱意是"我一直在这儿"。</summary>
        安静,

        /// <summary>感叹号、语气词、直率黏人，喜欢明晃晃写在脸上。</summary>
        元气,

        /// <summary>嘴硬心软：才不是 / 别误会 / 谁、谁要……。壳是硬的，芯子里全是关心。</summary>
        傲娇,

        /// <summary>小心翼翼：那个…… / 对不起 / 可以吗。怕给人添麻烦，但很依赖你。</summary>
        弱气,
    }

    public Persona CurrentPersona { get; set; } = Persona.元气;

    /// <summary>她的名字（显示在气泡与名牌上）。</summary>
    public string Name { get; set; } = "缇娅";

    /// <summary>
    /// 玩家称谓（v0.18.50）。台词里写 <c>{{称谓}}</c>，出口替换成这个值。
    /// 默认「主人」，面板可改；留空则回退 <see cref="NpcLines.DefaultTitle"/>。
    /// </summary>
    public string PlayerTitle { get; set; } = NpcLines.DefaultTitle;

    /// <summary>是否自言自语。</summary>
    public bool AutoChatter { get; set; } = true;

    public int MinIdleSeconds { get; set; } = 18;
    public int MaxIdleSeconds { get; set; } = 40;

    /// <summary>是否在聊天栏显示（关掉只写日志）。</summary>
    public bool ShowInChat { get; set; } = true;

    /// <summary>
    /// 两句台词之间的<b>全局最短间隔</b>（秒，v0.18.51）。
    ///
    /// <para>用户反馈「现在台词太频繁了」——光靠各类话题自己的冷却不够，
    /// 因为话题之间互不认识：天气刚说完、战斗又开始、再被选中一次，
    /// 三句话可以在十秒内连着冒出来。这里加一道<b>所有台词共用的闸门</b>。</para>
    ///
    /// <para>⚠️ 玩家主动触发的（打招呼、右键菜单、面板试说）走 <c>force</c>=true 跳过这道闸
    /// —— 那是你点名要她说话，不该被冷却挡住。</para>
    /// </summary>
    public float LineCooldownSeconds { get; set; } = 12f;

    private float _lineCooldown;

    // ── v0.18.69.22：按 NPC 的呈现覆盖（副 NPC 用）──
    //
    // 气泡 / 配音 / 动作概率原本读的是**全局静态**（BalloonTuning / VoiceTuning /
    // NpcGesture.ChancePercent）—— 主 NPC 面板推全局，副 NPC 各槽要有自己的值。
    // 这些覆盖属性为 null = 沿用全局（主 NPC 从不设置它们，行为不变）。

    /// <summary>头顶气泡开关覆盖（null = 沿用 BalloonTuning.Enabled）。</summary>
    public bool?  BalloonEnabledOverride { get; set; }

    /// <summary>头顶气泡时长覆盖（null = 沿用 BalloonTuning.Seconds）。</summary>
    public float? BalloonSecondsOverride { get; set; }

    /// <summary>说话配音开关覆盖（null = 沿用 VoiceTuning.Enabled）。</summary>
    public bool?  VoiceEnabledOverride   { get; set; }

    /// <summary>语音行号覆盖（null = 沿用 VoiceTuning.LineId；嗓音音色走每帧
    /// NpcVoice.ApplyPreset，由控制器按槽位喂）。</summary>
    public int?   VoiceLineIdOverride    { get; set; }

    /// <summary>台词配动作的概率覆盖（**%**，null = 沿用 NpcGesture.ChancePercent；
    /// 最小间隔仍是全局节奏 —— 多个 NPC 共用一道闸防动作刷屏）。</summary>
    public float? GestureChanceOverride  { get; set; }

    /// <summary>
    /// 每种人格在三种距离下的台词，外加"话多密"系数。
    ///
    /// ⚠️ 之前这里是一份**和人格无关的固定池**，只有打招呼才看人格 ——
    /// 于是"人格"这个选项实际上只是个换开场白的按钮。现在按人格分池。
    ///
    /// v0.18.50：池子搬到 <see cref="NpcLines"/>（那边每种人格 80+ 条，
    /// 按天气 / 时段 / 战斗 / 互动分桶）。这里只在启动时把距离池抓过来缓存，
    /// 结构和字段格式保持原样，下游代码一行都不用改。
    /// </summary>
    private sealed record PersonaPool(string[] Idle, string[] Near, string[] Far, float Chatter);

    private static readonly Dictionary<Persona, PersonaPool> PersonaPools = BuildPersonaPools();

    private static Dictionary<Persona, PersonaPool> BuildPersonaPools()
    {
        var map = new Dictionary<Persona, PersonaPool>();

        foreach (var persona in Enum.GetValues<Persona>())
        {
            var (idle, near, far, chatter) = NpcLines.Pool(persona);
            map[persona] = new PersonaPool(idle, near, far, chatter);
        }

        return map;
    }

    private NpcHandle? _npc;

    /// <summary>
    /// v0.17.4：「移动中不做动作」开关的执行信号，由控制器每帧喂入。
    ///
    /// true = 她<b>此刻在移动且开关是开的</b> → 自动闲聊推迟、说话不配肢体动作。
    /// 注意这只决定"**要不要发起新动作**"；一旦动作真的播了，
    /// "做动作时人必须停下"是 NpcController 里的铁律（Paused），不受此开关影响 ——
    /// 用户定义的语义是"可以在移动时开始播动作，但不允许边走边做"。
    /// </summary>
    public bool SuppressGestureAndChatter { get; set; }

    /// <summary>
    /// v0.18.66：闲聊距离池的「贴身」档阈值（米）—— 由控制器从配置
    /// <c>CloseTouchDistance</c>（面板「贴近触发距离」）喂入。
    /// ⚠️ v0.18.65 以前这里硬编码 2.2f，面板滑条完全管不到闲聊贴身档
    /// （用户实测"不论选多近/远她都会触发贴近台词"的根因）。
    /// </summary>
    public float NearChatDistance { get; set; } = 2.2f;

    /// <summary>刚说完话的一小段时间——用来让她停下来，而不是边走边念。</summary>
    public bool IsSpeaking { get; private set; }

    private float _speakingFor;

    /// <summary>被甩开了（瞬移跟上）时立刻说一句抱怨，而不是等下一次闲聊计时。</summary>
    public void NoticeTeleported()
    {
        var persona = PersonaPools.TryGetValue(CurrentPersona, out var found) ? found : PersonaPools[Persona.元气];
        Speak(Pick(persona.Far));
    }

    private float _nextIdleLine = 10f;
    private string _lastLine     = string.Empty;

    /// <summary>
    /// 「跟不上你」之类的远距台词冷却（秒）。
    /// v0.18.1：被你甩开、她正在狂奔追的时候，distance-pool 会一直落在 Far 池，
    /// 于是"我跟不上 / 等等我"每隔一次闲聊就刷一句（用户反馈"跟不上的时候聊天框刷屏"）。
    /// 这里给 Far 池单独设一个更长的冷却，避免追上之前反复念叨；
    /// 真正的"落后"抱怨仍由 NoticeTeleported（瞬移追上）与 落后 情境反应负责，不会漏。
    /// </summary>
    private float _farCooldown;

    public void Bind(NpcHandle handle)
    {
        _npc          = handle;
        _nextIdleLine = 10f;
        _lastLine     = string.Empty;
        _farCooldown  = 0f;

        // 重新召唤 = 重新认识一次：情境记忆要清掉，
        // 否则"初见"这种一次性的场景会被当成"已经说过"而永远不再出现。
        _sceneCooldown    = 0f;
        _lastReactedScene = Scene.平静;
        CurrentScene      = Scene.平静;
        _topicCooldowns.Clear();   // 重新召唤 = 重新认识，话题冷却一并清掉
    }

    /// <param name="speak">false = 只切换不说话（用于从配置恢复，不要一召唤就让她叨叨）。</param>
    public void SetPersona(Persona persona, bool speak = true)
    {
        CurrentPersona = persona;

        if (speak)
            Speak(OpeningLine(persona), force: true);

        // 换人格后重排下一次闲聊的计时，否则还要等上一段的计时走完才看得出变化。
        var scale = PersonaPools.TryGetValue(persona, out var found) ? found.Chatter : 1f;
        _nextIdleLine = Math.Max(2f, 6f * scale);
    }

    // ── 对外 ──────────────────────────────────────────────────────

    /// <summary>
    /// 让她说一句话（带气泡 + 一个小动作）。
    /// </summary>
    /// <param name="line">台词原文（可以带 <c>{{称谓}}</c> / <c>{天气}</c> / <c>{艾时}</c>）。</param>
    /// <param name="force">
    /// true = <b>跳过全局说话冷却</b>。玩家主动触发的（打招呼、右键菜单、面板试说）必须传 true，
    /// 否则你点名要她说话却被冷却挡掉，手感非常怪。
    /// </param>
    /// <param name="weatherName">替换 <c>{天气}</c>（右键「询问天气」用）。</param>
    /// <param name="hour">替换 <c>{艾时}</c>（右键「询问时间」用；-1 = 不替换）。</param>
    public void Speak(string line, bool force = false, string? weatherName = null, int hour = -1,
                      string? actionName = null)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        // v0.18.51：全局说话冷却（玩家主动触发的除外）。
        if (!force && _lineCooldown > 0f)
            return;

        // v0.18.50：出口统一替换 {{称谓}}（玩家称谓可配置，默认"主人"）。
        // v0.18.51：再加 {天气} / {艾时}。v0.18.64：{{动作}}（玩家刚做的动作名）。
        line = NpcLines.Resolve(line, PlayerTitle, weatherName, hour, actionName);

        _lineCooldown = Math.Max(0f, LineCooldownSeconds);

        _lastLine    = line;
        _speakingFor = 2.2f;
        IsSpeaking   = true;
        log.Debug("[SmartNpc] {0}：{1}", Name, line);

        // 呈现走游戏自己的聊天栏：最稳，不构造 AtkUnit、不和原生 UI 打架。
        if (ShowInChat)
            chat?.Print($"「{Name}」{line}");

        // v0.18.49 头顶气泡：走她自己 Character 上的 YellBalloon（游戏原生 NPC 喊话气泡），
        // 和肢体动作/配音同一拍出，观感上"她正在说这句"。
        // ⚠️ 放在聊天栏之后、配音之前：文字先出来，声音与开口动作跟上。
        if ((BalloonEnabledOverride ?? BalloonTuning.Enabled) && _npc is { } bubbleNpc && bubbleNpc.Address != nint.Zero)
            NpcBalloon.Show((Character*)bubbleNpc.Address, line, BalloonSecondsOverride ?? BalloonTuning.Seconds);

        // 说话配音（v0.18.37：真正的角色语音 —— 走游戏自己的
        // VfxContainer.LoadCharacterSound，音色由 Character.Vfx.VoiceId 决定）。
        // ⚠️ 放在肢体动作之前 —— 声音先出、"开口"的动作同步跟上，观感一致。
        if ((VoiceEnabledOverride ?? VoiceTuning.Enabled) && _npc is { } voiceNpc && voiceNpc.Address != nint.Zero)
            NpcVoice.TryPlay((Character*)voiceNpc.Address, VoiceLineIdOverride ?? VoiceTuning.LineId, VoiceTuning.MinInterval);

        // 说话时配一个肢体语言，让台词不悬着。
        //
        // ⚠️⚠️ v0.18.51 重写：**不再每句话都配动作**。
        // 旧版这里无条件 PlayIdleGesture(人格) —— 从人格关键字池里**随机**挑一个动作，
        // 于是安静打招呼会连摇两次头、弱气会做「用触控板记录」（自拍动作），
        // 用户点名说"许多对应都十分怪异"。
        //
        // 现在走 NpcGesture：按**台词的含义**挑最贴切的动作；
        // 匹配不上（大多数台词）就**只说话、不做动作**；
        // 匹配上了还要再过"命中概率"与"最小间隔"两道闸，不会句句比划。
        //
        // ⚠️ v0.17.4 沿用：「移动中不做动作」开着且她正在走 → 只说话不比划（推迟到停下）。
        if (!SuppressGestureAndChatter)
        {
            var gesture = NpcGesture.Decide(CurrentPersona, line, GestureChanceOverride);
            if (gesture is not null)
                // ⚠️ v0.18.53：时长**按时长分档**，不再一律 2.2 秒 ——
                // 表情走的是 BaseOverride（基底动画，会循环），摇头那条约 1 秒摇一次，
                // 给 2.2 秒正好摇满两遍（用户点名："摇头不要重复两遍"）。
                animator.Play(gesture, NpcGesture.DurationFor(gesture));
        }
    }

    /// <summary>
    /// 立刻结束"说话中"状态（v0.17.8）。
    ///
    /// ⚠️ 这是「移动打断动作」此前不生效的隐藏元凶：
    /// Speak 会设 2.2 秒的 IsSpeaking（说话时停走的墙），打断只停了动作、
    /// 这堵墙还在 —— 玩家一动她照样站满 2.2 秒才走，观感就是"没打断"。
    /// </summary>
    public void CancelSpeaking()
    {
        _speakingFor = 0f;
        IsSpeaking   = false;
    }

    /// <summary>打招呼：随机一句开场白，配一个动作。</summary>
    public void Greet()
    {
        // v0.18.50：开场白并入台词库的「初见」桶（每种人格 4 条）。
        var greetings = NpcLines.SceneLines(CurrentPersona, Scene.初见)
                     ?? ["……嗯。", "你好。", "来了。", "在呢。"];

        // ⚠️ 玩家点名打招呼 → force：不该被全局说话冷却挡掉。
        Speak(PickLine(greetings), force: true);

        // 开场白已经算作对「初见」的反应了，标记掉，
        // 否则同一帧的行为层再跑一次情境反应，等于开场白说完又接一句。
        _lastReactedScene = Scene.初见;
        _sceneCooldown    = 8f;
    }

    /// <summary>让她重复一句话（/smartnpc say）。</summary>
    public void Say(string line) => Speak(line);

    // ── 每帧驱动 ──────────────────────────────────────────────────

    public void Update(NpcHandle npc, Vector3 playerPosition, float deltaTime)
    {
        _npc = npc;

        var delta = Math.Clamp(deltaTime, 0f, 0.25f);

        // ⚠️ 话题冷却必须最先走：下面有好几个提前 return（不自言自语 / 移动中不说话）。
        TickTopicCooldowns(delta);

        // v0.18.51：全局说话冷却 + 动作间隔计时 —— 同样必须放在所有提前 return 之前。
        if (_lineCooldown > 0f)
            _lineCooldown -= delta;

        NpcGesture.Tick(delta);

        if (_farCooldown > 0f)
            _farCooldown -= delta;
        animator.Update(delta);

        if (_speakingFor > 0f)
        {
            _speakingFor -= delta;
            if (_speakingFor <= 0f)
                IsSpeaking = false;
        }

        if (!AutoChatter)
            return;

        // ⚠️ v0.17.4：「移动中不做动作」开着且她正在走 → 自动闲聊推迟，别边走边说。
        if (SuppressGestureAndChatter)
            return;

        _nextIdleLine -= delta;
        if (_nextIdleLine > 0f)
            return;

        // 说完一句就安静一会儿——话太密会很吵。间隔再按人格缩放：
        // 元气话多、安静话少。
        var scale = PersonaPools.TryGetValue(CurrentPersona, out var found) ? found.Chatter : 1f;
        var min   = Math.Max(3, (int)(MinIdleSeconds * scale));
        var max   = Math.Max(min + 1, (int)(MaxIdleSeconds * scale));
        _nextIdleLine = Random.Shared.Next(min, max + 1);

        var line = NextLine(npc, playerPosition);
        if (!string.IsNullOrEmpty(line))
            Speak(line);
    }

    // ── 台词生成（接 LLM 时只替换这里）─────────────────────────────

    /// <summary>
    /// 玩家所处的情境。行为层每帧更新，台词层据此挑反应。
    ///
    /// 这正是 TRUST 队友那种"对周围有反应"的关键：她不是只按距离选台词，
    /// 而是<b>看玩家在干什么</b>——你在打怪她说"小心后面"，你在采集她说"采到了什么"，
    /// 这比任何闲聊都像"一个在场的人"。
    /// </summary>
    public enum Scene
    {
        /// <summary>平常无事。</summary>
        平静,

        /// <summary>玩家在战斗。</summary>
        战斗,

        /// <summary>玩家在采集 / 制作。</summary>
        劳作,

        /// <summary>玩家站着很久没动。</summary>
        久站,

        /// <summary>刚被拉下距离、瞬移跟上。</summary>
        落后,

        /// <summary>刚召唤出来。</summary>
        初见,
    }

    /// <summary>当前情境；由行为层每帧写入。</summary>
    public Scene CurrentScene { get; set; } = Scene.平静;

    // 各情境的<b>反应</b>台词（按人格分池）→ v0.18.50 起统一放在 NpcLines.ScenePools，
    // 见 UpdateSceneReaction 里的 NpcLines.SceneLines 调用。
    //
    // ⚠️ Scene.战斗 故意**不再**在这里挂池：战斗那几句现在由 CombatPools 分阶段
    // （进入 / 攻击 / 受击 / 胜利 / 撤退）负责，见 OnCombat。

    /// <summary>
    /// 情境变化时给一句反应。<b>去重 + 节流</b>：同一个情境不会每帧都冒一句。
    /// 返回 true 表示这次真的开口了。
    /// </summary>
    private Scene _lastReactedScene = Scene.平静;
    private float _sceneCooldown;

    /// <summary>每帧调用：推进情境反应。返回 true = 这一帧该说点什么。</summary>
    public bool UpdateSceneReaction(Scene scene, float deltaTime)
    {
        if (_sceneCooldown > 0f)
        {
            _sceneCooldown -= Math.Clamp(deltaTime, 0f, 0.25f);
            return false;
        }

        // 初见只认一次，之后别反复念叨。
        if (scene == _lastReactedScene)
            return false;

        _lastReactedScene = scene;

        //「初见」已经由 Greet() 处理过了，这里不再重复。
        if (scene == Scene.初见)
            return false;

        var pool = NpcLines.SceneLines(CurrentPersona, scene);
        if (pool is null || pool.Length == 0)
            return false;

        Speak(PickLine(pool));

        // 冷却比普通闲聊长一点：情境切换不该连着说三句。
        _sceneCooldown = 8f;
        return true;
    }

    /// <summary>重置情境记忆（重新召唤 / 换区时叫）。</summary>
    public void ResetScene() => _lastReactedScene = Scene.平静;

    // ── v0.18.50：分情境开口（战斗 / 天气 / 时段 / 玩家互动）────────────
    //
    // 入口都很薄：控制器负责**判定发生了什么**，这里只负责"这句话该怎么说"。
    // 每个话题自带冷却，避免同一类事件连着刷（玩家在战场里反复进出战斗时尤其明显）。

    /// <summary>话题冷却剩余秒数。键 = 话题标识（见下面四个入口）。</summary>
    private readonly Dictionary<string, float> _topicCooldowns = new();

    /// <summary>
    /// 每帧推进话题冷却。
    ///
    /// ⚠️ 必须在 <see cref="Update"/> 的**最前面**调用 —— 那里面有好几个提前 return
    /// （不自言自语 / 移动中不说话），冷却要是放在后面就永远走不到，等于没冷却。
    /// </summary>
    private void TickTopicCooldowns(float delta)
    {
        if (_topicCooldowns.Count == 0)
            return;

        foreach (var key in _topicCooldowns.Keys.ToArray())
        {
            var left = _topicCooldowns[key] - delta;
            if (left <= 0f)
                _topicCooldowns.Remove(key);
            else
                _topicCooldowns[key] = left;
        }
    }

    /// <summary>战斗阶段：进入 / 攻击 / 受击 / 胜利 / 撤退。返回 true = 开口了。</summary>
    public bool OnCombat(NpcLines.Combat phase, float cooldown = 10f)
        => SpeakTopic("combat" + (int)phase, NpcLines.CombatLines(CurrentPersona, phase), cooldown);

    /// <summary>天气变了（控制器只在真的变了时才叫）。</summary>
    public bool OnWeather(NpcLines.Weather kind, float cooldown = 120f)
        => SpeakTopic("weather" + (int)kind, NpcLines.WeatherLines(CurrentPersona, kind), cooldown);

    /// <summary>时段变了（白天 / 黄昏 / 夜晚）。</summary>
    public bool OnDayPhase(NpcLines.DayPhase phase, float cooldown = 300f)
        => SpeakTopic("phase" + (int)phase, NpcLines.PhaseLines(CurrentPersona, phase), cooldown);

    /// <summary>玩家主动互动（被选中 / 被注视 / 被靠近）与她的关心 / 称赞。</summary>
    public bool OnTouch(NpcLines.Touch touch, float cooldown = 20f)
        => SpeakTopic("touch" + (int)touch, NpcLines.TouchLines(CurrentPersona, touch), cooldown);

    /// <summary>
    /// 天气变了，说一句<b>带真实天气名词</b>的话（v0.18.51）。
    ///
    /// <para>例：游戏里现在是「碧空」→"今天真是碧空如洗呢~！"；是「薄雾」→"今天起了点薄雾呢。"
    /// 名词由 <see cref="NpcEnvironment"/> 从 Lumina Weather 表读出来，不是我们编的。</para>
    /// </summary>
    public bool OnWeatherName(string weatherName, float cooldown = 180f)
    {
        if (string.IsNullOrWhiteSpace(weatherName))
            return false;

        return SpeakTopic("weathername", NpcLines.WeatherNameLines(CurrentPersona), cooldown, weatherName);
    }

    /// <summary>
    /// 玩家<b>换了职业</b>（v0.18.51）。
    ///
    /// <para>⚠️ 判重由控制器负责 —— 它每 0.4 秒看一次 <c>ICharacter.ClassJob</c>，
    /// 只有<b>切到了与现在不同的职业</b>才调这里一次。所以：
    /// "骑士→武士"说一次、"武士→骑士"再说一次，而"骑士→骑士"永远一句都不说。</para>
    ///
    /// <para>走 <c>force</c>：换职业是你主动做的事，不该被全局冷却挡掉。</para>
    /// </summary>
    public bool OnJob(string? jobName, string[]? pool)
    {
        if (pool is null || pool.Length == 0)
            return false;

        // ⚠️⚠️ v0.18.52：**这里一个冷却都不设**。
        //
        // 用户实测"有时候触发不到切换职业台词"，怀疑和台词冷却打架 ——
        // 而且他的要求很明确：**换职业台词必须无视冷却**。
        //
        // 去重**完全交给控制器**：它比对 ClassJob 行号，只有"切到了与现在不同的职业"
        // 才调过来一次（骑士→骑士 一句都不说）。这里再加话题冷却属于重复设卡，
        // 只会把真的换职业那一次吃掉。
        var line = NpcJobLines.Decorate(PickLine(pool), CurrentPersona, jobName);
        Speak(line, force: true);

        return true;
    }

    /// <summary>
    /// 玩家<b>选中她</b>并对她做了一个表情动作（v0.18.53）。
    ///
    /// <para>⚠️ 判重由控制器负责 —— 它比对 <c>EmoteController.EmoteId</c>，
    /// 只有"新起了一个动作"且"当前目标就是她"才调这里一次。</para>
    ///
    /// <para>⚠️ 走 <c>force</c>：这是你主动对她做的事，不该被全局台词冷却吃掉。
    /// 控制器那边另有一个 3 秒的防连击闸。</para>
    /// </summary>
    public bool OnEmote(string? emoteName, string[]? pool)
    {
        if (pool is null || pool.Length == 0)
            return false;

        // v0.18.64：把玩家刚做的动作名传进去 —— 台词里的 {{动作}} 会被替换成它，
        // "（看了你一眼）"这种敷衍话升级成"「张望」？……嗯，我看见了"。
        Speak(PickLine(pool), force: true, actionName: emoteName);
        return true;
    }

    /// <summary>
    /// 玩家在右键菜单里点了某项（v0.18.51）。
    ///
    /// <para>⚠️ 走 <c>force</c>：这是你点名要她说话，不该被全局冷却挡掉；
    /// 但保留一个很短的话题冷却，防止连点两次连说两句。</para>
    /// </summary>
    public bool OnAsk(NpcLines.Ask topic, float cooldown = 3f)
    {
        var pool = NpcLines.AskLines(CurrentPersona, topic, NpcEnvironment.EorzeaHour);
        if (pool is null || pool.Length == 0)
            return false;

        var key = "ask" + (int)topic;
        if (_topicCooldowns.ContainsKey(key))
            return false;

        _topicCooldowns[key] = cooldown;

        Speak(PickLine(pool), force: true,
              weatherName: NpcEnvironment.WeatherNoun,
              hour: NpcEnvironment.EorzeaHour);

        return true;
    }

    /// <summary>面板「试说一句」用：无视开关与冷却，立刻来一句。</summary>
    public void ForceSample(NpcLines.Weather kind)
        => ForceTopic("weather" + (int)kind, NpcLines.WeatherLines(CurrentPersona, kind));

    /// <inheritdoc cref="ForceSample(NpcLines.Weather)"/>
    public void ForceSample(NpcLines.DayPhase phase)
        => ForceTopic("phase" + (int)phase, NpcLines.PhaseLines(CurrentPersona, phase));

    /// <inheritdoc cref="ForceSample(NpcLines.Weather)"/>
    public void ForceSample(NpcLines.Combat phase)
        => ForceTopic("combat" + (int)phase, NpcLines.CombatLines(CurrentPersona, phase));

    /// <inheritdoc cref="ForceSample(NpcLines.Weather)"/>
    public void ForceSample(NpcLines.Touch touch)
        => ForceTopic("touch" + (int)touch, NpcLines.TouchLines(CurrentPersona, touch));

    private void ForceTopic(string key, string[]? pool)
    {
        if (pool is null || pool.Length == 0)
            return;

        _topicCooldowns[key] = 0f;   // 试说不留冷却（下一帧就被 TickTopicCooldowns 清掉）

        // ⚠️ force = 面板试说是玩家点的，必须立刻说。
        Speak(PickLine(pool), force: true);
    }

    /// <summary>话题冷却里就闭嘴；不在冷却里就说一句并起冷却。</summary>
    private bool SpeakTopic(string key, string[]? pool, float cooldown, string? weatherName = null)
    {
        if (pool is null || pool.Length == 0)
            return false;

        if (_topicCooldowns.ContainsKey(key))
            return false;

        _topicCooldowns[key] = cooldown;

        // ⚠️ 这里**不加** Decorate：台词库里每条都是完整的句子（自带语气与标点），
        // 再套一层语气词会变成"才不是啦，……哦，是你。"这种怪句子。
        Speak(PickLine(pool), weatherName: weatherName);
        return true;
    }

    private string NextLine(NpcHandle npc, Vector3 playerPosition)
    {
        var here    = PositionOf(npc);
        var offset  = here - playerPosition;
        var distance = new Vector3(offset.X, 0f, offset.Z).Length();

        var persona = PersonaPools.TryGetValue(CurrentPersona, out var found) ? found : PersonaPools[Persona.元气];

        // v0.18.66：贴身档阈值走配置（原硬编码 2.2f）。
        // ⚠️ switch 关系模式要求编译期常量 —— MathF.Max(...) 会 CS9135，必须用 `_ when` 臂。
        var nearCut = MathF.Max(0.3f, NearChatDistance);
        var (pool, isFar) = distance switch
        {
            > 8f    => (persona.Far,  true),
            _ when distance < nearCut => (persona.Near, false),
            _        => (persona.Idle, false),
        };

        // ⚠️ v0.18.1：跟不上你、正在狂奔追的这段时间里，Far 池台词必须限频，
        // 否则"我跟不上 / 等等我"会一直刷（用户反馈"跟不上的时候聊天框刷屏"）。
        // 冷却期间直接不开口；追上之后冷却自然衰减完，再回到正常闲聊节奏。
        // 瞬移追上时的那句抱怨走 NoticeTeleported，不受这里限制（不会漏）。
        if (isFar && _farCooldown > 0f)
            return string.Empty;

        // 尽量别重复上一句。
        var chosen = pool[Random.Shared.Next(pool.Length)];
        for (var attempt = 0; attempt < 4; ++attempt)
        {
            var candidate = pool[Random.Shared.Next(pool.Length)];
            if (candidate != _lastLine)
            {
                chosen = candidate;
                break;
            }
        }

        if (isFar)
            _farCooldown = 30f;   // 一句"跟不上"够用了，30 秒后再考虑下一句

        // ⚠️ v0.18.50：不再套 Decorate —— 台词库里每条都是自带语气标点的完整句子。
        return chosen;
    }

    /// <summary>
    /// 挑一句：尽量别重复上一句。
    ///
    /// ⚠️ 台词库 v0.18.50 之后每格都有 80+ 条完整句子（自带语气与标点），
    /// 所以不再额外套语气词（旧的 <c>Decorate</c> 已删 —— 套上去会变成
    /// "才不是啦，……哦，是你。"这种怪句子）。
    /// </summary>
    private string PickLine(string[] pool)
    {
        if (pool.Length == 0)
            return string.Empty;

        var chosen = pool[Random.Shared.Next(pool.Length)];

        for (var attempt = 0; attempt < 4; ++attempt)
        {
            var candidate = pool[Random.Shared.Next(pool.Length)];
            if (candidate != _lastLine)
                return candidate;
        }

        return chosen;
    }

    /// <summary>
    /// 换人格时那一句开场白。
    ///
    /// <para>⚠️ v0.18.51：傲娇那句「……是你啊。行吧，那就跟着你好了——别误会，只是顺路」
    /// 被用户点名 OOC —— 她是<b>被玩家叫出来的</b>，不是自己路过顺便跟上的。
    /// "顺路"直接和这个底层设定冲突，已改成"被叫来的"语义（傲娇味保留：
    /// 嘴上嫌弃、脚下已经站过来了）。</para>
    ///
    /// <para>⚠️ 同理，台词库里凡是"顺路 / 路过 / 刚好在这儿"的说法也一并清掉了
    /// （见 <see cref="NpcLines"/> 傲娇各池）。</para>
    /// </summary>
    private string OpeningLine(Persona persona)
        => persona switch
        {
            Persona.元气 => "你来啦！今天也一起走吧。",
            Persona.傲娇 => "……叫我出来就直说。行了，我来了——别误会，只是正好有空。",
            Persona.弱气 => "那、那个……我是来跟着你的……可、可以吗？",
            _            => "……嗯。你好。",
        };

    private static string Pick(params string[] options) => options[Random.Shared.Next(options.Length)];

    private static Vector3 PositionOf(NpcHandle npc)
    {
        if (npc.Address == nint.Zero)
            return Vector3.Zero;

        var p = ((GameObject*)npc.Address)->Position;
        return new Vector3(p.X, p.Y, p.Z);
    }
}