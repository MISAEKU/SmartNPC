using System;
using System.Globalization;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Timer;

namespace SmartNpc.Core;

/// <summary>
/// 游戏内<b>天气</b>与<b>时段</b>（v0.18.50）。
///
/// <para>⚠️ 用户明确要求「时间和天气要求游戏内为主」—— 所以这里<b>不碰现实时钟</b>，
/// 全部读游戏自己的状态：
///   · 天气 = <c>WeatherManager.WeatherId</c>（+0x64，byte，当前区域实际天气的 Weather 行号）；
///   · 时间 = <c>ClientTime.EorzeaTimeMilliseconds</c>（+0x10，艾欧泽亚时间，毫秒）。</para>
///
/// <para>取样频率 1 秒一次：天气每 8 个艾欧泽亚小时（≈23 分钟真实时间）才换一次，
/// 逐帧读纯属浪费；而且 Lumina 查表只在<b>行号真的变了</b>的时候才做一次。</para>
///
/// <para>⚠️ 兜底（"必须有条无条件的出路"）：
///   · 结构体指针拿不到 / 读出来是 0 → 保持 <see cref="NpcLines.Weather.未知"/>，
///     <b>不触发天气台词</b>（总比在副本里喊"下雨了"强）；
///   · 时段算不出来 → 保持上一次的结果，不会来回跳；
///   · 小时算错的唯一风险是 <c>EorzeaTimeMilliseconds</c> 的单位，
///     所以面板会显示原始值，并且配置里留了 <c>EorzeaHourOverride</c> 手动钉死。</para>
/// </summary>
internal static unsafe class NpcEnvironment
{
    /// <summary>当前天气（归类后）。</summary>
    public static NpcLines.Weather Weather { get; private set; } = NpcLines.Weather.未知;

    /// <summary>当前时段。</summary>
    public static NpcLines.DayPhase Phase { get; private set; } = NpcLines.DayPhase.白天;

    /// <summary>上一次取样时的天气（用来判断"刚变了"）。</summary>
    public static NpcLines.Weather LastWeather { get; private set; } = NpcLines.Weather.未知;

    /// <summary>上一次取样时的时段（用来判断"刚变了"）。</summary>
    public static NpcLines.DayPhase LastPhase { get; private set; } = NpcLines.DayPhase.白天;

    // ── 原始读数（面板 / 诊断显示，出了偏差一眼能看出来）──

    /// <summary>Weather 表行号。</summary>
    public static uint RawWeatherId { get; private set; }

    /// <summary>Weather 表里的名字（取得到才有）。</summary>
    public static string RawWeatherName { get; private set; } = string.Empty;

    /// <summary>艾欧泽亚时间原始毫秒读数。</summary>
    public static long RawEorzeaMs { get; private set; }

    /// <summary>艾欧泽亚时间原始秒读数（long，见 <see cref="PickDivisor"/> 的单位说明）。</summary>
    public static long RawEorzeaSec { get; private set; }

    /// <summary>算出来的艾欧泽亚小时（0~23；-1 = 还没取到）。</summary>
    public static int EorzeaHour { get; private set; } = -1;

    /// <summary>算出来的艾欧泽亚分钟（0~59；-1 = 还没取到）。面板/诊断用它和游戏内时钟对表。</summary>
    public static int EorzeaMinute { get; private set; } = -1;

    /// <summary>最近一次取样的错误（空 = 正常）。</summary>
    public static string LastError { get; private set; } = string.Empty;

    /// <summary>
    /// 手动钉死小时（-1 = 自动）。
    /// 万一单位推错了，用户不用等改代码就能自己校准。
    /// </summary>
    public static int HourOverride = -1;

    /// <summary>
    /// 时段以什么为准（v0.18.51）。
    ///
    /// <para><b>0 = 游戏内艾欧泽亚时间（默认）</b> —— 用户明确要求"时间和天气以游戏内为主"。</para>
    ///
    /// <para><b>1 = 现实本地时间</b> —— 之所以留这个开关：艾时一天只有 70 分钟真实时间，
    /// 和现实时钟<b>完全不同步</b>（现实凌晨一点，游戏里完全可能是黄昏）。
    /// 用户实测"她在凌晨一点说天边泛黄"，第一反应就是"时段算错了"，
    /// 其实那是艾时的真实读数。想要"跟着现实作息说话"的话，切到 1 即可。</para>
    /// </summary>
    public static int DayPhaseSource;

    /// <summary>是不是以现实时间为准。</summary>
    public static bool UseRealTime => DayPhaseSource == 1;

    private static float _sampleTimer;
    private static bool _primed;

    /// <summary>取样间隔（秒）。天气 8 艾小时一换，1 秒足够。</summary>
    private const float SampleInterval = 1f;

    /// <summary>
    /// 每帧调用；内部按 <see cref="SampleInterval"/> 节流。
    /// </summary>
    public static void Update(IDataManager? data, float deltaTime)
    {
        _sampleTimer -= deltaTime;
        if (_primed && _sampleTimer > 0f)
            return;

        _sampleTimer = SampleInterval;
        _primed      = true;

        try
        {
            // ── 天气 ──
            var wm = WeatherManager.Instance();
            if (wm is not null)
            {
                uint id = wm->WeatherId;
                if (id != RawWeatherId || RawWeatherName.Length == 0)
                {
                    RawWeatherId   = id;
                    RawWeatherName = LookupWeatherName(data, id);
                }
            }

            // ── 时间 ──
            //
            // ⚠️ ClientTime **没有** Instance()（本机实测：那个类型里一个方法都没有），
            // 它只有字段。实例挂在 Framework 的 +0x1770（**内嵌结构体，不是指针** ——
            // 字段签名 06 11 95 F8 = FIELD + ELEMENT_TYPE_VALUETYPE + TypeDef#1406，
            // 是值类型不是 PTR）。所以走 Framework.Instance()->ClientTime。
            var fw = Framework.Instance();
            if (fw is not null)
            {
                // ⚠️ 这两个字段类型不一样：EorzeaTimeMilliseconds 是 **float**，
                // EorzeaTime 是 long（本机编译实测）。所以小时一律用 long 那个算，
                // 毫秒那个只用于显示 / 兜底。
                RawEorzeaMs  = (long)fw->ClientTime.EorzeaTimeMilliseconds;
                RawEorzeaSec = fw->ClientTime.EorzeaTime;
            }

            LastWeather = Weather;
            LastPhase   = Phase;

            Weather = ClassifyWeather(RawWeatherId, RawWeatherName);
            Phase   = ClassifyHour(ResolveHour());

            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            // 读不到就保持上一次的结果，绝不更新成"未知"再触发一堆台词。
            LastError = ex.Message;
        }
    }

    /// <summary>换区时叫：天气/时段都重新取，并且不算"变化"（避免换区瞬间连环说两句）。</summary>
    public static void ResetZone()
    {
        _sampleTimer = 0f;
        LastWeather  = NpcLines.Weather.未知;
        LastPhase    = NpcLines.DayPhase.白天;
        Weather      = NpcLines.Weather.未知;
        Phase        = NpcLines.DayPhase.白天;
        EorzeaHour   = -1;
        EorzeaMinute = -1;
        _primed      = false;
    }

    // ── 单位：⭐ v0.18.52 彻底推翻了 v0.18.50 的假设 ────────────────
    //
    // 【用户反馈】"艾欧泽亚时间（ET）确认有很大的误差，完全不准确"。
    //
    // 【真值】从诊断里取到一次实锤读数：EorzeaTime = 36,860,666,843（那一刻北京时间 2026-10-13 04:46:10）。
    //   36,860,666,843 × 4200 ÷ 86400 = 1,791,837,971.5
    //   那一刻的 Unix 秒（UTC）      = 1,791,837,970
    //   → **只差 1.5 秒**。
    //
    // 结论：**EorzeaTime 存的是「自 Unix 纪元起的艾欧泽亚秒」**。
    // 一个艾日 = 86400 艾秒 = 4200 真实秒 ⇒ 它每秒真实时间自增 **86400/4200 ≈ 20.571**，
    // 既不是 1（真实秒）也不是 1000（毫秒）。
    //
    // 【旧代码为什么错】自校准只认两档：`rate > 100 ? 175000 : 175`。
    // 20.571 落进 ≤100 那一档 ⇒ 除数取了 **175**（那是"真实秒"才该用的），
    // 比正确答案 3600 慢了整整 20.571 倍 —— 小时每 8.5 秒就跳一格，
    // 24 小时只要 3.4 分钟就转完一圈。所以她说出来的时段完全是随机的。
    //
    // 【修法】不再硬猜：**用当前 Unix 时间去反算哪个除数能对得上**。
    // 若除数 D 正确，则 `E * 175 / D` 应该刚好等于现在的 Unix 秒数（因为 1 艾小时 = 175 真实秒）。
    // 四个候选里谁最贴近真实 Unix 秒，谁就是对的 —— 这一步既能选出单位，又能顺带验证，
    // 以后 SE 要是换了单位也能自动跟上。
    private static readonly long[] CandidateDivisors = [175L, 3600L, 175000L, 3600000L];

    /// <summary>当前判定出的除数（0 = 还没定）。</summary>
    private static long _unitDivisor;

    /// <summary>
    /// 选出正确的除数：拿 <c>E * 175 / D</c> 跟<b>真实 Unix 秒</b>比，谁最接近谁就是答案。
    /// </summary>
    private static long PickDivisor(long e)
    {
        if (e <= 0)
            return 0;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        long best = 3600L;                 // 这一版的正确答案，兜底用它
        long bestErr = long.MaxValue;

        foreach (var d in CandidateDivisors)
        {
            var guess = e * 175L / d;      // 用这个除数反推出来的 Unix 秒
            var err = Math.Abs(guess - now);
            if (err < bestErr)
            {
                bestErr = err;
                best = d;
            }
        }

        // 差得离谱（>3 分钟）说明不是"自 Unix 纪元的艾秒"这套，退回 rate 也没意义，
        // 直接用这一版的实测值 —— 至少比乱跳强，面板会显示原始值方便再校准。
        _divisorError = bestErr;
        return bestErr <= 180 ? best : 3600L;
    }

    /// <summary>反推出来的 Unix 秒与真实值的偏差（诊断用；差得越小说明单位判得越准）。</summary>
    private static long _divisorError;

    /// <summary>
    /// 艾欧泽亚小时（0~23；-1 = 还没取到）。
    /// </summary>
    private static int ResolveHour()
    {
        if (HourOverride >= 0)
            return HourOverride % 24;

        // v0.18.51：切到现实本地时间时直接读系统时钟（见 DayPhaseSource 的说明）。
        if (UseRealTime)
            return DateTime.Now.Hour;

        if (RawEorzeaSec <= 0)
            return EorzeaHour >= 0 ? EorzeaHour : -1;

        _unitDivisor = PickDivisor(RawEorzeaSec);
        if (_unitDivisor <= 0)
            return EorzeaHour >= 0 ? EorzeaHour : -1;

        // ⚠️ 先模一次再算分钟：直接对 3.7e10 这种量级做除法，
        // 除数哪怕差千分之一，小时的余数就会被放大到完全无关的另一个值。
        var hours = RawEorzeaSec / _unitDivisor;
        EorzeaMinute = (int)((RawEorzeaSec % _unitDivisor) * 60L / _unitDivisor);

        return (int)(hours % 24);
    }

    /// <summary>
    /// 小时 → 时段（v0.18.51：3 档细分到 5 档）。
    ///
    /// <para>清晨 5~7 / 白天 8~16 / 黄昏 17~18 / 夜晚 19~23 / 深夜 0~4。</para>
    ///
    /// <para>为什么要细分：原来"夜晚"含 19~4 共 10 个小时，
    /// 于是 19 点刚天黑和凌晨 3 点说的是同一批台词 —— 后者明显应该是"深夜"那种
    /// 「都睡了，只有我们还醒着」的语气。用户反馈的"凌晨一点说黄昏台词"，
    /// 一部分观感问题就来自档位太粗。</para>
    /// </summary>
    private static NpcLines.DayPhase ClassifyHour(int hour)
    {
        if (hour < 0)
            return Phase;   // 还没取到：保持上一个，别乱跳

        EorzeaHour = hour;

        return hour switch
        {
            >= 5 and <= 7  => NpcLines.DayPhase.清晨,
            >= 8 and < 17  => NpcLines.DayPhase.白天,
            >= 17 and < 19 => NpcLines.DayPhase.黄昏,
            >= 19 and <= 23 => NpcLines.DayPhase.夜晚,
            _ => NpcLines.DayPhase.深夜,        // 0~4
        };
    }

    /// <summary>
    /// 天气行号 → 晴 / 雨 / 雪 / 其他。
    ///
    /// <para>先看名字（多语言关键词，最稳），名字取不到再按<b>行号</b>兜底。
    /// Weather 的行号与图标号同源，常见那 18 个是固定的：
    /// 1 碧空 / 2 晴朗 / 3 阴云 / 4 薄雾 / 5 微风 / 6 强风 / 7 小雨 / 8 暴雨 /
    /// 9 打雷 / 10 雷雨 / 11 扬沙 / 13 热浪 / 14 灼热波 / 15 小雪 / 16 暴雪 / 17 妖雾 / 18 极光。</para>
    ///
    /// <para>⚠️ 阴云 / 薄雾 / 风 / 热浪这些"没在下雨雪"的都归到<b>晴</b>桶 ——
    /// 晴桶的台词写的是"天气不错 / 适合赶路 / 没有下雨"，套在阴天身上也不违和。</para>
    /// </summary>
    private static NpcLines.Weather ClassifyWeather(uint id, string name)
    {
        if (name.Length > 0)
        {
            if (ContainsAny(name, "雪", "Snow", "Blizzard", "吹雪"))
                return NpcLines.Weather.雪;

            if (ContainsAny(name, "雨", "Rain", "Shower", "Thunder", "雷"))
                return NpcLines.Weather.雨;

            if (ContainsAny(name, "晴", "Clear", "Fair", "碧空"))
                return NpcLines.Weather.晴;
        }

        return id switch
        {
            1 or 2 or 3 or 4 or 5 or 6 or 13 or 14 or 18 => NpcLines.Weather.晴,
            7 or 8 or 9 or 10 => NpcLines.Weather.雨,
            15 or 16 => NpcLines.Weather.雪,
            0 => NpcLines.Weather.未知,
            _ => NpcLines.Weather.其他,
        };
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var n in needles)
        {
            if (text.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>查 Weather 表的名字（只是为了归类更准；取不到不影响功能）。</summary>
    private static string LookupWeatherName(IDataManager? data, uint id)
    {
        try
        {
            if (data is null || id == 0)
                return string.Empty;

            var sheet = data.GetExcelSheet<Lumina.Excel.Sheets.Weather>();
            if (sheet is null)
                return string.Empty;

            foreach (var row in sheet)
            {
                if (row.RowId != id)
                    continue;

                var s = row.Name.ToString();
                return s ?? string.Empty;
            }
        }
        catch
        {
            // 查表失败只是少了名字，行号兜底照样能归类。
        }

        return string.Empty;
    }

    /// <summary>
    /// 天气的<b>真实名词</b>（v0.18.51）。
    ///
    /// <para>用户要求不要只归成晴/雨/雪，而是直接用游戏里那个名词组词：
    /// 「碧空」就说"今天真是碧空如洗呢"，「薄雾」就说"今天起了点薄雾呢"。</para>
    ///
    /// <para>优先用 Lumina 查到的名字；查不到就退回下面的行号对照表
    /// （总比在句子里留一个空位强）。</para>
    /// </summary>
    public static string WeatherNoun
        => RawWeatherName.Length > 0 ? RawWeatherName : FallbackWeatherName(RawWeatherId);

    /// <summary>
    /// Lumina 查不到时的行号 → 名词对照（常见那 18 个）。
    /// 行号与图标号同源，国服/国际服一致。
    /// </summary>
    private static string FallbackWeatherName(uint id) => id switch
    {
        1  => "碧空",
        2  => "晴朗",
        3  => "阴云",
        4  => "薄雾",
        5  => "微风",
        6  => "强风",
        7  => "小雨",
        8  => "暴雨",
        9  => "打雷",
        10 => "雷雨",
        11 => "扬沙",
        12 => "霞",
        13 => "热浪",
        14 => "灼热波",
        15 => "小雪",
        16 => "暴雪",
        17 => "妖雾",
        18 => "极光",
        _  => "好天气",
    };

    /// <summary>
    /// 面板 / 诊断用的一句话（含原始值，便于跟游戏内的艾欧泽亚时钟对表）。
    ///
    /// <para>写成 "HH:MM" 是为了让用户<b>一眼能对上游戏里那个 ET 钟</b> ——
    /// 单位要是判错了，这里立刻就对不齐。</para>
    /// </summary>
    public static string Describe()
    {
        // ⚠️ ET 与现实时钟无关，别拿 DateTime.Now 去比；要跟游戏里的艾欧泽亚时钟比。
        var clock = EorzeaHour < 0
            ? "--:--"
            : string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}", EorzeaHour, Math.Max(0, EorzeaMinute));

        return string.Format(
            CultureInfo.InvariantCulture,
            "天气={0}({1} {2})｜时段={3}({4} ET {5})｜原始 s={6} ms={7} 除数={8} 与真实时差={9}s",
            Name(Weather),
            RawWeatherId,
            WeatherNoun,
            Name(Phase),
            UseRealTime ? "现实" : "艾时",
            clock,
            RawEorzeaSec,
            RawEorzeaMs,
            _unitDivisor == 0 ? "未定" : _unitDivisor.ToString(CultureInfo.InvariantCulture),
            _divisorError < long.MaxValue ? _divisorError.ToString(CultureInfo.InvariantCulture) : "--");
    }

    public static string Name(NpcLines.Weather w) => w switch
    {
        NpcLines.Weather.晴 => "晴",
        NpcLines.Weather.雨 => "雨",
        NpcLines.Weather.雪 => "雪",
        NpcLines.Weather.其他 => "特殊",
        _ => "未知",
    };

    public static string Name(NpcLines.DayPhase p) => p switch
    {
        NpcLines.DayPhase.清晨 => "清晨",
        NpcLines.DayPhase.黄昏 => "黄昏",
        NpcLines.DayPhase.夜晚 => "夜晚",
        NpcLines.DayPhase.深夜 => "深夜",
        _ => "白天",
    };
}
