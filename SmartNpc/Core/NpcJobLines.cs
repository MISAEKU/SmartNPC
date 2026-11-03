using System;
using System.Collections.Generic;

namespace SmartNpc.Core;

/// <summary>
/// 职业台词（v0.18.51）。
///
/// <para>玩家一切职业，她说一句跟<b>这个职业</b>有关、又<b>符合她自己性格</b>的话。
/// 例如同样是看到你换成骑士：安静说"……又要站在最前面了吗"，元气说"骑士！好帅！",
/// 傲娇说"哼，又去当肉盾，笨蛋"，弱气说"那个……会很危险吧……"。</para>
///
/// <para>⚠️⚠️ 触发纪律（用户明确要求）：
/// <b>切换后只触发一次</b> —— 记下当前职业，下次必须切到<b>与现在不同</b>的职业才会再说。
/// 所以"切了又切回来"会各说一次（因为每一次都是真的变了），
/// 而"没换职业"永远一句都不会冒出来。</para>
///
/// <para><b>为什么按名字而不是按行号索引</b>：ClassJob 的行号表我不打算硬背 ——
/// 万一记错一个，那整个职业就永远匹配不上。这里改成<b>按职业名匹配</b>：
/// 名字由 <c>NpcController</c> 从 Lumina 的 <c>ClassJob</c> 表里查出来（同一个行号），
/// 拿到中文名再来这张表查台词。名字对不上时退到<b>职能台词</b>（坦克 / 治疗 / …），
/// 职能也对不上就一句都不说 —— 绝不硬套。</para>
/// </summary>
public static class NpcJobLines
{
    /// <summary>职能名（用于兜底，也是面板显示用）。</summary>
    public static class Role
    {
        public const string 坦克 = "坦克";
        public const string 治疗 = "治疗";
        public const string 近战 = "近战";
        public const string 远敏 = "远敏";
        public const string 法系 = "法系";
        public const string 生产 = "生产";
        public const string 采集 = "采集";
    }

    /// <summary>职业名 → 职能。国服客户端 Lumina 读出来的名字就是这些。</summary>
    private static readonly Dictionary<string, string> JobRoles = new(StringComparer.Ordinal)
    {
        // ── 坦克 ──
        ["骑士"]     = Role.坦克,
        ["战士"]     = Role.坦克,
        ["暗黑骑士"] = Role.坦克,
        ["绝枪战士"] = Role.坦克,

        // ── 治疗 ──
        ["白魔法师"] = Role.治疗,
        ["学者"]     = Role.治疗,
        ["占星术士"] = Role.治疗,
        ["贤者"]     = Role.治疗,

        // ── 近战 ──
        ["武僧"]     = Role.近战,
        ["龙骑士"]   = Role.近战,
        ["忍者"]     = Role.近战,
        ["武士"]     = Role.近战,
        ["钐镰客"]   = Role.近战,
        ["蝰蛇剑士"] = Role.近战,

        // ── 远敏 ──
        ["吟游诗人"] = Role.远敏,
        ["机工士"]   = Role.远敏,
        ["舞者"]     = Role.远敏,

        // ── 法系 ──
        ["黑魔法师"] = Role.法系,
        ["召唤师"]   = Role.法系,
        ["赤魔法师"] = Role.法系,
        ["绘灵法师"] = Role.法系,
        ["青魔法师"] = Role.法系,

        // ── 生产 ──
        ["木工"]     = Role.生产,
        ["锻铁"]     = Role.生产,
        ["铸甲"]     = Role.生产,
        ["雕金"]     = Role.生产,
        ["制革"]     = Role.生产,
        ["裁缝"]     = Role.生产,
        ["炼金"]     = Role.生产,
        ["烹饪"]     = Role.生产,

        // ── 采集 ──
        ["采矿"]     = Role.采集,
        ["园艺"]     = Role.采集,
        ["捕鱼"]     = Role.采集,
    };

    /// <summary>职业名 → 人格 → 台词（每个职业每种人格 2 条）。</summary>
        private static readonly Dictionary<string, Dictionary<NpcBrain.Persona, string[]>> ByJob = new(StringComparer.Ordinal)
    {
        // ══════════ 坦克：挡在前面的人 ══════════
        ["骑士"] = new()
        {
            [NpcBrain.Persona.安静] = ["盾和誓言。……都在你身上。", "你站在最前面。我记住后面。"],
            [NpcBrain.Persona.元气] = ["骑士！举盾的样子超可靠！", "有你在前面，我就什么都不怕了！"],
            [NpcBrain.Persona.傲娇] = ["哼，又去挡在最前面。……别受伤啊。", "……我、我才不是担心你。只是你倒下的话，我就没人跟着了。"],
            [NpcBrain.Persona.弱气] = ["盾……好沉的样子……请、请小心……", "你会站在最前面吧……我、我会在后面看着的……"],
        },
        ["战士"] = new()
        {
            [NpcBrain.Persona.安静] = ["斧头很重。你拿得动。", "受了伤也不退。这一点我一直看着。"],
            [NpcBrain.Persona.元气] = ["战士！一斧头一个！好有魄力！", "哇——超有气势！"],
            [NpcBrain.Persona.傲娇] = ["……又选这个硬扛的。别、别逞强过头啊。", "哼，力气倒是不小。……血别掉太多。"],
            [NpcBrain.Persona.弱气] = ["看、看起来好痛……请、请小心……", "受伤了的话……请、请马上告诉我……"],
        },
        ["暗黑骑士"] = new()
        {
            [NpcBrain.Persona.安静] = ["黑衣很适合你。……不过别一个人扛。", "沉重的过去我分担不了，但我会站在看得到你的地方。"],
            [NpcBrain.Persona.元气] = ["暗黑骑士！大剑超——帅！", "好酷！那个黑色的气场！"],
            [NpcBrain.Persona.傲娇] = ["……装深沉。还挺像样。……不过别什么都自己吞下去。", "哼，一副很寂寞的样子。……想说的话，可以说给我听啊。"],
            [NpcBrain.Persona.弱气] = ["那身黑衣……看起来有点寂寞……", "我、我会一直在这儿的……"],
        },
        ["绝枪战士"] = new()
        {
            [NpcBrain.Persona.安静] = ["枪刃。拔刀和开火在同一双手里。", "……很利落。我看着就够了。"],
            [NpcBrain.Persona.元气] = ["枪刃！砰砰砰！超带感！", "又帅又利落！"],
            [NpcBrain.Persona.傲娇] = ["……倒是挺利落的。别、别割到自己。", "哼，耍帅倒是挺在行。……耍得还行吧。"],
            [NpcBrain.Persona.弱气] = ["我、我不太懂……但请小心使用……", "好、好大的刀……请、请小心……"],
        },

        // ══════════ 治疗：把人拉回来的人 ══════════
        ["白魔法师"] = new()
        {
            [NpcBrain.Persona.安静] = ["白色的光很暖。你会先救别人。", "你把光分给大家。我看着就好。"],
            [NpcBrain.Persona.元气] = ["白魔法师！治疗就拜托你啦！", "白白的好漂亮！光好温柔！"],
            [NpcBrain.Persona.傲娇] = ["……又要去照顾别人了。你自己也别忘了啊。", "哼，光给别人加血。……我、我不用你操心啦。"],
            [NpcBrain.Persona.弱气] = ["好温柔的颜色……我、我也想帮忙……", "你会把大家治好吧……我、我会为你加油的……"],
        },
        ["学者"] = new()
        {
            [NpcBrain.Persona.安静] = ["书和小妖精。……你总是先算好三步。", "你不是靠运气的人。这一点让我安心。"],
            [NpcBrain.Persona.元气] = ["学者！还有小妖精！好可爱！", "看书的样子好聪明！"],
            [NpcBrain.Persona.傲娇] = ["哼，就你心眼多。……不过算得准是好事。", "……别光顾着算别人，把自己也算进去啊。"],
            [NpcBrain.Persona.弱气] = ["好、好聪明的样子……我、我能跟着学吗……", "小妖精……好可爱……"],
        },
        ["占星术士"] = new()
        {
            [NpcBrain.Persona.安静] = ["星星会告诉你什么。……我有点想知道。", "命运这东西，我信你手里的牌。"],
            [NpcBrain.Persona.元气] = ["占星！星星好漂亮！", "今天会抽到好牌吗！"],
            [NpcBrain.Persona.傲娇] = ["哼，靠运气。……抽到好牌记得分我一点。", "……牌说得准不准，我看着呢。别、别出错啊。"],
            [NpcBrain.Persona.弱气] = ["星星……好漂亮……", "我的运气……会变好吗……"],
        },
        ["贤者"] = new()
        {
            [NpcBrain.Persona.安静] = ["切开，再缝合。……你的手很稳。", "冷静的手。我看着就安心。"],
            [NpcBrain.Persona.元气] = ["贤者！好厉害的手术！", "又冷静又帅！"],
            [NpcBrain.Persona.傲娇] = ["……手倒是挺稳的。别、别把自己也算进病人里啊。", "哼，一副很从容的样子。……累了要说。"],
            [NpcBrain.Persona.弱气] = ["看、看起来好难……你好厉害……", "我、我不太敢看……但是好佩服……"],
        },

        // ══════════ 近战：贴上去的人 ══════════
        ["武僧"] = new()
        {
            [NpcBrain.Persona.安静] = ["拳头的节奏，很好听。", "一直动着的职业。你也不喜欢停。"],
            [NpcBrain.Persona.元气] = ["武僧！打得好快！", "拳拳到肉！超帅！"],
            [NpcBrain.Persona.傲娇] = ["哼，动来动去的。……别、别脱力了。", "……手速倒是不慢。别把自己转晕。"],
            [NpcBrain.Persona.弱气] = ["好、好快……我看不清……", "一直动的话……会、会不会很累……"],
        },
        ["龙骑士"] = new()
        {
            [NpcBrain.Persona.安静] = ["跳得很高。……空中的那一瞬，我每次都看着。", "落下来的时候，我会在地上等你。"],
            [NpcBrain.Persona.元气] = ["龙骑士！跳得好高！", "飞起来啦——！超帅！"],
            [NpcBrain.Persona.傲娇] = ["又跳。……摔了我可要笑你了。才、才不是担心。", "……跳得还挺好看。落地记得站稳啊。"],
            [NpcBrain.Persona.弱气] = ["好高……我、我有点怕……", "请、请小心落地……"],
        },
        ["忍者"] = new()
        {
            [NpcBrain.Persona.安静] = ["影子很多。……你结印的手很快。", "忽然出现又忽然不见。我已经习惯了。"],
            [NpcBrain.Persona.元气] = ["忍者！手里剑！咻——！", "好帅！忽隐忽现的！"],
            [NpcBrain.Persona.傲娇] = ["哼，神出鬼没的。……别、别消失太久。", "……要消失也先说一声啊。我会去找的。"],
            [NpcBrain.Persona.弱气] = ["忽、忽然不见了……吓我一跳……", "不要突然消失好吗……我、我会不安的……"],
        },
        ["武士"] = new()
        {
            [NpcBrain.Persona.安静] = ["刀很快。……拔刀那一瞬，风都停了。", "一刀两断。你看得很准。"],
            [NpcBrain.Persona.元气] = ["武士！居合超——帅！", "一刀两断！好帅！"],
            [NpcBrain.Persona.傲娇] = ["……刀倒是耍得不错。别、别把手割了。", "哼，就知道耍刀。……不过，确实很好看。"],
            [NpcBrain.Persona.弱气] = ["好、好锋利……请、请小心手……", "那一刀……我、我看呆了……"],
        },
        ["钐镰客"] = new()
        {
            [NpcBrain.Persona.安静] = ["镰刀很长。……和什么东西做了交易吗。", "影子会替你出手。我有点在意，但不问。"],
            [NpcBrain.Persona.元气] = ["钐镰客！好大一把！", "挥起来超有气势！"],
            [NpcBrain.Persona.傲娇] = ["哼，搞得神神秘秘的。……别、别让那东西靠太近。", "……那把镰刀，别伤到自己就好。"],
            [NpcBrain.Persona.弱气] = ["那把镰刀……有点、有点吓人……", "我、我会乖乖待着的……请不要靠太近……"],
        },
        ["蝰蛇剑士"] = new()
        {
            [NpcBrain.Persona.安静] = ["两把刀。……像蛇一样缠在一起。", "你的手，比看上去更忙。"],
            [NpcBrain.Persona.元气] = ["双刀！好快好快！", "像蛇一样！超帅！"],
            [NpcBrain.Persona.傲娇] = ["哼，花里胡哨的。……不过，确实很利落。", "……别、别把自己缠进去啊。"],
            [NpcBrain.Persona.弱气] = ["两、两把……好厉害……", "会、会不会割到手……我有点担心……"],
        },

        // ══════════ 远敏：站得稍远的人 ══════════
        ["吟游诗人"] = new()
        {
            [NpcBrain.Persona.安静] = ["琴声很好听。……边拉边射，你总是分神。", "你的歌，我每次都听。"],
            [NpcBrain.Persona.元气] = ["诗人！唱歌！我也要跟着哼～", "弓和琴！又帅又好听！"],
            [NpcBrain.Persona.傲娇] = ["哼，又在那儿弹。……唱、唱得还行吧。", "……弹给我听也不是不行。才不是想听。"],
            [NpcBrain.Persona.弱气] = ["琴声……好好听……", "可、可以再弹一次吗……"],
        },
        ["机工士"] = new()
        {
            [NpcBrain.Persona.安静] = ["机械的声音。……蒸汽和齿轮都在你手里。", "你做得出来的东西，都很精巧。"],
            [NpcBrain.Persona.元气] = ["机工！机器人！砰砰砰！", "好热闹！超有趣！"],
            [NpcBrain.Persona.傲娇] = ["哼，玩具一堆。……坏了别、别硬修啊。", "……搞得叮当响。别炸到自己。"],
            [NpcBrain.Persona.弱气] = ["好厉害的机械……我、我不太懂……", "有、有点吵……但是好厉害……"],
        },
        ["舞者"] = new()
        {
            [NpcBrain.Persona.安静] = ["舞步很轻。……你的影子也在跳。", "看的人会跟着高兴起来。你的舞就是这样。"],
            [NpcBrain.Persona.元气] = ["舞者！转圈圈！我也要跳！", "好漂亮！我也想学！"],
            [NpcBrain.Persona.傲娇] = ["哼，就知道转。……跳、跳得还挺好看的。", "……要跳就好好跳。我会看着的。"],
            [NpcBrain.Persona.弱气] = ["好、好漂亮……", "我、我也想学……可以吗……"],
        },

        // ══════════ 法系：把魔力捏成形的人 ══════════
        ["黑魔法师"] = new()
        {
            [NpcBrain.Persona.安静] = ["火很大。……站着不动的那段时间，我在看着你。", "冰与火在你手里换班。很安静的强力。"],
            [NpcBrain.Persona.元气] = ["黑魔！大爆炸！轰——！", "好厉害的魔法！"],
            [NpcBrain.Persona.傲娇] = ["哼，又去玩火。……别、别把自己炸了。", "……咏唱别被打断啊。后面我帮你看着。"],
            [NpcBrain.Persona.弱气] = ["好、好大的火……", "我、我离远一点看就好……请小心……"],
        },
        ["召唤师"] = new()
        {
            [NpcBrain.Persona.安静] = ["它们都听你的。……书里住着很多东西。", "唤出来的伙伴，都愿意跟着你。"],
            [NpcBrain.Persona.元气] = ["召唤兽！好可爱！", "哇，又出来一只！"],
            [NpcBrain.Persona.傲娇] = ["哼，叫人帮忙。……那些家伙还挺听话的。", "……别、别一次叫太多把自己累着。"],
            [NpcBrain.Persona.弱气] = ["它、它们会咬人吗……有、有点怕……", "好厉害……能叫出那么多……"],
        },
        ["赤魔法师"] = new()
        {
            [NpcBrain.Persona.安静] = ["红与黑，很衬你。……你连挥剑都像在跳舞。", "既能打又能救人。你总是想得比别人多一步。"],
            [NpcBrain.Persona.元气] = ["赤魔！又帅又能奶！", "细剑超优雅！"],
            [NpcBrain.Persona.傲娇] = ["哼，就知道耍帅。……不过，确实很好看。", "……又能打又能救，累、累了要说啊。"],
            [NpcBrain.Persona.弱气] = ["好、好优雅……", "像、像画里的人……"],
        },
        ["绘灵法师"] = new()
        {
            [NpcBrain.Persona.安静] = ["你画的，会变成真的。……颜色在你手里活着。", "……要是能画我就好了。这句我没说出口。"],
            [NpcBrain.Persona.元气] = ["画画的！好厉害！", "哇，画出来啦！"],
            [NpcBrain.Persona.傲娇] = ["哼，涂涂画画的。……画、画得还行吧。", "……要画就画我啊。……才、才没说想让你画。"],
            [NpcBrain.Persona.弱气] = ["好漂亮……我、我也想学……", "可、可以画我吗……"],
        },
        ["青魔法师"] = new()
        {
            [NpcBrain.Persona.安静] = ["学别人的招式。……你收集的东西都很奇怪。", "你把见过的都变成了自己的。这很聪明。"],
            [NpcBrain.Persona.元气] = ["青魔！又学了新招！", "哇，这是什么招式！"],
            [NpcBrain.Persona.傲娇] = ["哼，尽学些乱七八糟的。……不、不过挺有意思。", "……又去跟怪物学东西了。别、别被咬到啊。"],
            [NpcBrain.Persona.弱气] = ["那招……好、好特别……", "我、我看不太懂……但好像很厉害……"],
        },

        // ══════════ 生产：把材料变成东西的人 ══════════
        ["木工"] = new()
        {
            [NpcBrain.Persona.安静] = ["木头的味道。……你手里的东西会长成形。", "做出来的东西，会带上你的手感。"],
            [NpcBrain.Persona.元气] = ["木工！做家具吗！", "敲敲打打！好好玩！"],
            [NpcBrain.Persona.傲娇] = ["哼，又在做木头活。……做、做完了给我看看。", "……别、别敲到手啊。"],
            [NpcBrain.Persona.弱气] = ["木头……香香的呢……", "我、我可以帮忙递工具吗……"],
        },
        ["锻铁"] = new()
        {
            [NpcBrain.Persona.安静] = ["火很旺。……铁在红的时候最听话。", "你敲出来的东西，会很结实。"],
            [NpcBrain.Persona.元气] = ["锻铁！好热好帅！", "铛铛铛！好有节奏！"],
            [NpcBrain.Persona.傲娇] = ["哼，一身汗。……别、别烫着了。", "……做好了先给自己用啊。别总是先给别人。"],
            [NpcBrain.Persona.弱气] = ["好、好热……请小心……", "火花……有点吓人……但是好厉害……"],
        },
        ["铸甲"] = new()
        {
            [NpcBrain.Persona.安静] = ["在做护甲吗。……一层一层敲出来的硬。", "你做的甲，会替谁挡下那一下。"],
            [NpcBrain.Persona.元气] = ["铸甲！做盔甲！", "铛铛！好帅！"],
            [NpcBrain.Persona.傲娇] = ["哼，敲个没完。……手、手别砸了。", "……做好了记得先给自己穿一件。"],
            [NpcBrain.Persona.弱气] = ["很、很重的样子……", "请、请别砸到手……"],
        },
        ["雕金"] = new()
        {
            [NpcBrain.Persona.安静] = ["细活。……小小的东西，最费神。", "亮的东西在你手里，会变得很好看。"],
            [NpcBrain.Persona.元气] = ["雕金！亮晶晶！", "哇——好漂亮！"],
            [NpcBrain.Persona.傲娇] = ["哼，尽弄些小玩意。……做、做得还挺精致。", "……眼睛别、别太累啊。"],
            [NpcBrain.Persona.弱气] = ["好、好细致……我怕弄坏……", "亮亮的……好漂亮……"],
        },
        ["制革"] = new()
        {
            [NpcBrain.Persona.安静] = ["皮子的味道。……软的东西，也要慢慢来。", "鞣过的皮会记得你的手。"],
            [NpcBrain.Persona.元气] = ["制革！做包包吗！", "摸起来软软的呢！"],
            [NpcBrain.Persona.傲娇] = ["哼，味道不太好闻。……做、做好了倒是挺好看。", "……做完记得洗手啊。"],
            [NpcBrain.Persona.弱气] = ["有点、有点味道……但摸起来很舒服……", "软软的……好厉害……"],
        },
        ["裁缝"] = new()
        {
            [NpcBrain.Persona.安静] = ["针线很细。……你做的衣服，我很想穿。", "一针一线都很慢。你在耐心这件事上很强。"],
            [NpcBrain.Persona.元气] = ["裁缝！做衣服！我也要一件！", "我也想穿你做的！"],
            [NpcBrain.Persona.傲娇] = ["哼，缝缝补补的。……要、要是给我做一件，我也不介意。", "……别、别熬夜啊。"],
            [NpcBrain.Persona.弱气] = ["可、可以帮我做一件吗……", "针线活……好厉害……"],
        },
        ["炼金"] = new()
        {
            [NpcBrain.Persona.安静] = ["瓶瓶罐罐。……气味有点冲，但你在就好。", "你调出来的东西会救人也会伤人。小心些。"],
            [NpcBrain.Persona.元气] = ["炼金！做药水！", "咕嘟咕嘟！像魔法！"],
            [NpcBrain.Persona.傲娇] = ["哼，又在那儿煮。……别、别炸了。", "……做好了记得先给自己备一份。"],
            [NpcBrain.Persona.弱气] = ["会不会、会不会有危险……", "气味……有点刺鼻……但是好厉害……"],
        },
        ["烹饪"] = new()
        {
            [NpcBrain.Persona.安静] = ["很香。……你做的饭，我想第一个吃。", "你把心思都煮进去了。尝得出来。"],
            [NpcBrain.Persona.元气] = ["烹饪！有好吃的吗！", "我也要吃我也要吃！"],
            [NpcBrain.Persona.傲娇] = ["哼，又在做吃的。……给、给我留一份。", "……别、别自己不吃光给别人啊。"],
            [NpcBrain.Persona.弱气] = ["好香……我、我可以吃吗……", "需要我帮忙吗……我、我可以洗菜……"],
        },

        // ══════════ 采集：肯等的人 ══════════
        ["采矿"] = new()
        {
            [NpcBrain.Persona.安静] = ["石头里有东西。……你敲得很稳。", "地下的东西不会跑。你有的是耐心。"],
            [NpcBrain.Persona.元气] = ["采矿！挖到宝了吗！", "铛铛铛！挖矿去咯！"],
            [NpcBrain.Persona.傲娇] = ["哼，一身灰。……挖、挖到好东西记得说一声。", "……别、别往太深的地方钻。"],
            [NpcBrain.Persona.弱气] = ["会不会、会不会塌下来……", "好、好辛苦的样子……我帮你拎着……"],
        },
        ["园艺"] = new()
        {
            [NpcBrain.Persona.安静] = ["草木的味道。……你蹲下去的时候，很温柔。", "你认得每一片叶子。我认得你就够了。"],
            [NpcBrain.Persona.元气] = ["园艺！采花花！", "哇，好漂亮的花！"],
            [NpcBrain.Persona.傲娇] = ["哼，采花惹草的。……采、采到了给我一朵。", "……别、别被树枝刮到。"],
            [NpcBrain.Persona.弱气] = ["花……好漂亮……", "我、我也想帮忙采……"],
        },
        ["捕鱼"] = new()
        {
            [NpcBrain.Persona.安静] = ["水很静。……等鱼上钩的时间，你不急。", "你等得起。这一点我很喜欢。"],
            [NpcBrain.Persona.元气] = ["钓鱼！钓到大鱼了吗！", "扑通扑通！好有趣！"],
            [NpcBrain.Persona.傲娇] = ["哼，又去坐着发呆。……钓、钓到了给我看看。", "……别、别靠水边太近。"],
            [NpcBrain.Persona.弱气] = ["水边……会、会不会滑……", "好安静啊……但是很舒服……"],
        },
    };

    /// <summary>
    /// 职能兜底台词（职业名对不上时用：比如客户端语言变了、或新版本加了新职业）。
    /// </summary>
        private static readonly Dictionary<string, Dictionary<NpcBrain.Persona, string[]>> ByRole = new(StringComparer.Ordinal)
    {
        [Role.坦克] = new()
        {
            [NpcBrain.Persona.安静] = ["站在最前面的人。……我记住后面。", "你挡在前面。我看着你的背。"],
            [NpcBrain.Persona.元气] = ["坦克！最前面交给你啦！", "好可靠！"],
            [NpcBrain.Persona.傲娇] = ["……又冲第一个。别、别受伤啊。", "哼，挡在前面就挡吧。……我、我会看着你的。"],
            [NpcBrain.Persona.弱气] = ["会、会很危险吧……", "请、请一定要小心……"],
        },
        [Role.治疗] = new()
        {
            [NpcBrain.Persona.安静] = ["你在看着所有人的血条。", "光很暖。……你总是先想到别人。"],
            [NpcBrain.Persona.元气] = ["治疗！拜托你啦！", "好温柔的光！"],
            [NpcBrain.Persona.傲娇] = ["……又要照顾别人了。你自己也别忘了啊。", "哼，光给别人加血。……累、累了要跟我说。"],
            [NpcBrain.Persona.弱气] = ["我、我也可以帮忙吗……", "好温柔……"],
        },
        [Role.近战] = new()
        {
            [NpcBrain.Persona.安静] = ["贴得很近。……你的动作我都能看清。", "近处的东西，你处理得很快。"],
            [NpcBrain.Persona.元气] = ["近战！冲上去！", "好快好帅！"],
            [NpcBrain.Persona.傲娇] = ["哼，贴那么近。……别、别冲太深。", "……要冲就冲吧。我跟着。"],
            [NpcBrain.Persona.弱气] = ["好近……我、我有点怕……", "会、会很危险吧……"],
        },
        [Role.远敏] = new()
        {
            [NpcBrain.Persona.安静] = ["站在稍远的地方。……你的箭，飞得很远。", "远处的事，你先看见。"],
            [NpcBrain.Persona.元气] = ["远程！射得准准的！", "咻——！好帅！"],
            [NpcBrain.Persona.傲娇] = ["哼，躲后面倒是舒服。……不、不过那样更安全。", "……射得还挺准。别、别太靠前。"],
            [NpcBrain.Persona.弱气] = ["站得远……安全一点……", "好厉害……那么远也打得到……"],
        },
        [Role.法系] = new()
        {
            [NpcBrain.Persona.安静] = ["咏唱的时候，很安静。", "魔法在你手里成形。"],
            [NpcBrain.Persona.元气] = ["法系！魔法好漂亮！", "轰——！好厉害！"],
            [NpcBrain.Persona.傲娇] = ["哼，又在那儿念。……咏、咏唱别被打断。", "……我帮你看着周围，你专心念。"],
            [NpcBrain.Persona.弱气] = ["魔法……好、好漂亮……", "我、我不太懂……但好厉害……"],
        },
        [Role.生产] = new()
        {
            [NpcBrain.Persona.安静] = ["手在动。……做出来的东西会有你的味道。", "你在慢慢把材料变成别的东西。"],
            [NpcBrain.Persona.元气] = ["做东西！我也要帮忙！", "你好厉害，什么都会做！"],
            [NpcBrain.Persona.傲娇] = ["哼，又开始干活。……做、做完了给我看看。", "……别、别太晚睡啊。"],
            [NpcBrain.Persona.弱气] = ["需要我帮忙吗……", "好厉害……我、我什么都不会……"],
        },
        [Role.采集] = new()
        {
            [NpcBrain.Persona.安静] = ["在找东西。……慢慢来，我陪你。", "你把时间花在等和找上。我很喜欢这样。"],
            [NpcBrain.Persona.元气] = ["采集！找到好东西了吗！", "我帮你一起找！"],
            [NpcBrain.Persona.傲娇] = ["哼，到处翻。……找、找到了叫我。", "……别、别走太远。"],
            [NpcBrain.Persona.弱气] = ["我、我也一起找……", "请、请别走太远……"],
        },
    };

    /// <summary>
    /// 名字没查出来时的<b>兜底</b>（v0.18.51）。
    ///
    /// <para>ClassJob 表读不出来（换语言客户端、数据没解压完）时用它 ——
    /// 至少她对"你换了职业"这件事有反应，而不是一声不吭。</para>
    /// </summary>
        private static readonly Dictionary<NpcBrain.Persona, string[]> GenericPools = new()
    {
        [NpcBrain.Persona.安静] = ["……换了吗。", "新的东西。我看着就好。"],
        [NpcBrain.Persona.元气] = ["换职业啦！换什么了！", "不管换成什么我都跟着你！"],
        [NpcBrain.Persona.傲娇] = ["哼，又换了。……不、不过我会跟着的。", "……换就换呗。别、别受伤就行。"],
        [NpcBrain.Persona.弱气] = ["换、换了吗……", "不管换成什么……我都会跟着的……"],
    };

    /// <summary>职业名 → 台词（找不到返回 null）。</summary>
    public static string[]? Lines(NpcBrain.Persona persona, string? jobName)
    {
        if (string.IsNullOrWhiteSpace(jobName))
            return null;

        return ByJob.TryGetValue(jobName!.Trim(), out var byPersona)
            && byPersona.TryGetValue(persona, out var lines)
                ? lines
                : null;
    }

    /// <summary>职能 → 台词（兜底；找不到返回 null）。</summary>
    public static string[]? RoleLines(NpcBrain.Persona persona, string role)
        => ByRole.TryGetValue(role, out var byPersona)
        && byPersona.TryGetValue(persona, out var lines)
            ? lines
            : null;

    /// <summary>职业名 → 职能名；不认识返回空串。</summary>
    public static string RoleOf(string? jobName)
        => !string.IsNullOrWhiteSpace(jobName)
        && JobRoles.TryGetValue(jobName!.Trim(), out var role)
            ? role
            : string.Empty;

    /// <summary>
    /// 取这个职业的台词：先按职业名，取不到就退到<b>职能</b>台词，再取不到返回 null（不说）。
    /// </summary>
    public static string[]? LinesWithFallback(NpcBrain.Persona persona, string? jobName)
    {
        var direct = Lines(persona, jobName);
        if (direct is not null)
            return direct;

        var role = RoleOf(jobName);
        return role.Length > 0 ? RoleLines(persona, role) : null;
    }

    /// <summary>名字没查出来时的兜底台词。</summary>
    public static string[]? GenericLines(NpcBrain.Persona persona)
        => GenericPools.TryGetValue(persona, out var lines) ? lines : null;

    // ── 行号兜底表（v0.18.52）───────────────────────────────────────
    //
    // 【用户反馈】"生产采集类的切换台词似乎有问题，总是只触发'XXX……换了吗。'这类无指向的"。
    // 那句正是 **GenericLines**（最外层兜底）⇒ 说明职业名根本没匹配上。
    //
    // 名字匹配失败有几种可能：客户端语言/译名和我写的 key 不一致、
    // RowRef 取不到 Name、或者行号落在表里没覆盖的职业上。
    // 所以再补一层**按行号**的兜底 —— ClassJob 的行号从 2.x 起就没变过，很稳。
    //
    // ⚠️ 这一层只在"名字没匹配上"时才用，不是主力路径。
    //    就算这里某行记错了，也只是那一个职业退到职能台词，不会像名字那样整体落空。
    private static readonly Dictionary<uint, string> ByRowId = new()
    {
        // 战斗职业（job）
        [19] = "骑士",   [20] = "武僧",   [21] = "战士",   [22] = "龙骑士",
        [23] = "吟游诗人", [24] = "白魔法师", [25] = "黑魔法师", [27] = "召唤师",
        [28] = "学者",   [30] = "忍者",   [31] = "机工士", [32] = "暗黑骑士",
        [33] = "占星术士", [34] = "武士",   [35] = "赤魔法师", [36] = "青魔法师",
        [37] = "绝枪战士", [38] = "舞者",   [39] = "钐镰客", [40] = "贤者",
        [41] = "蝰蛇剑士", [42] = "绘灵法师",

        // 基础职业（class，没转职时读到的就是这些）
        [1]  = "骑士",     // 剑术师 → 归到骑士那条线
        [2]  = "武僧",     // 格斗家
        [3]  = "战士",     // 斧术师
        [4]  = "龙骑士",   // 枪术师
        [5]  = "吟游诗人", // 弓术师
        [6]  = "白魔法师", // 幻术师
        [7]  = "黑魔法师", // 咒术师
        [26] = "召唤师",   // 秘术师
        [29] = "忍者",     // 双剑师

        // 生产
        [8]  = "木工", [9]  = "锻铁", [10] = "铸甲", [11] = "雕金",
        [12] = "制革", [13] = "裁缝", [14] = "炼金", [15] = "烹饪",

        // 采集
        [16] = "采矿", [17] = "园艺", [18] = "捕鱼",
    };

    /// <summary>
    /// 职业名的<b>别名</b>：万一客户端译名和我写的 key 差一点（多一个"师/匠"之类），
    /// 也能对上。键 = 客户端可能返回的名字，值 = 表里的标准 key。
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["木工师"] = "木工", ["锻铁师"] = "锻铁", ["铸甲师"] = "铸甲", ["雕金师"] = "雕金",
        ["制革师"] = "制革", ["裁缝师"] = "裁缝", ["炼金师"] = "炼金", ["烹饪师"] = "烹饪",
        ["采矿师"] = "采矿", ["园艺师"] = "园艺", ["捕鱼师"] = "捕鱼",
        ["锻铁匠"] = "锻铁", ["铸甲匠"] = "铸甲", ["雕金匠"] = "雕金",
        ["暗黑骑士"] = "暗黑骑士",
        ["白魔"] = "白魔法师", ["黑魔"] = "黑魔法师", ["赤魔"] = "赤魔法师",
        ["青魔"] = "青魔法师", ["召唤"] = "召唤师", ["占星"] = "占星术士",
        ["龙骑"] = "龙骑士", ["诗人"] = "吟游诗人", ["机工"] = "机工士",
        ["绝枪"] = "绝枪战士", ["钐镰"] = "钐镰客", ["蝰蛇"] = "蝰蛇剑士",
        ["绘灵"] = "绘灵法师",
    };

    /// <summary>上一次解析是从哪一层拿到的（诊断用：名字 / 别名 / 行号 / 未匹配）。</summary>
    public static string LastResolved { get; private set; } = string.Empty;

    /// <summary>
    /// 把"行号 + Lumina 名字"解析成表里的职业 key。三层兜底，绝不留空手：
    /// ① 名字精确匹配 → ② 别名 → ③ 行号表 → ④ 把名字原样返回（至少还能走职能兜底）。
    /// </summary>
    public static string ResolveJob(uint jobId, string? name)
    {
        LastResolved = "未匹配";

        if (!string.IsNullOrWhiteSpace(name))
        {
            var n = name!.Trim();

            if (ByJob.ContainsKey(n))
            {
                LastResolved = "名字";
                return n;
            }

            if (Aliases.TryGetValue(n, out var alias) && ByJob.ContainsKey(alias))
            {
                LastResolved = "别名";
                return alias;
            }

            // 反向包含：客户端返回的名字里含有我们的 key（"木工" 对 "木工师" 这种）
            foreach (var key in ByJob.Keys)
            {
                if (key.Length >= 2 && n.Contains(key, StringComparison.Ordinal))
                {
                    LastResolved = "名字含key";
                    return key;
                }
            }
        }

        if (jobId != 0 && ByRowId.TryGetValue(jobId, out var byRow))
        {
            LastResolved = "行号";
            return byRow;
        }

        return (name ?? string.Empty).Trim();
    }

    /// <summary>
    /// 把职业名<b>点</b>进句子里（v0.18.51）。
    ///
    /// <para>例：安静看到你换成骑士 → "……又要站在最前面了吗。"
    /// 变成"骑士……又要站在最前面了吗。"；弱气变成"骑士……那个……会很危险吧……"。</para>
    ///
    /// <para>⚠️ <b>只给安静和弱气点名</b>：元气那一档的台词<b>本来就喊了职业名</b>
    /// （"白魔！治疗交给你啦！""武士！一刀两断！"），再套一层会变成
    /// "白魔法师白魔！治疗交给你啦！"这种怪句子；傲娇那一档一律以"哼"起头，
    /// 硬塞名字会把那股别扭劲儿冲掉。所以：**已经喊过的、以及不适合喊的，都不动**。</para>
    /// </summary>
    public static string Decorate(string line, NpcBrain.Persona persona, string? jobName)
    {
        if (string.IsNullOrWhiteSpace(jobName) || string.IsNullOrEmpty(line))
            return line;

        var n = jobName!.Trim();

        // 句子里已经提到这个职业了（元气那批就是这么写的），别重复。
        if (line.Contains(n, StringComparison.Ordinal))
            return line;

        return persona switch
        {
            // 安静那一档多半以"……"起头 —— 名字直接接上去就是"骑士……又要站在最前面了吗。"；
            // 少数句子是直陈（"盾牌很沉。我帮你记着后面。"），那就补一个停顿变成
            // "骑士……盾牌很沉。我帮你记着后面。"
            NpcBrain.Persona.安静 => line.StartsWith("……", StringComparison.Ordinal) ? n + line : n + "……" + line,

            // ⚠️ v0.18.52：弱气那一档现在**不再**统一加"那个"（用户点名：每句都"那个"不行）。
            // 这里只保留职业名 + 一个停顿，把犹豫交给台词本身的省略号 / 结巴去表达。
            // 例："骑士……盾好沉的样子……请、请小心……"
            NpcBrain.Persona.弱气 => n + "……" + line,

            _ => line,
        };
    }

    /// <summary>这一套人格的职业台词总条数（自检 / 面板显示用）。</summary>
    public static int CountFor(NpcBrain.Persona persona)
    {
        var n = 0;

        foreach (var byPersona in ByJob.Values)
            if (byPersona.TryGetValue(persona, out var lines)) n += lines.Length;

        foreach (var byPersona in ByRole.Values)
            if (byPersona.TryGetValue(persona, out var lines)) n += lines.Length;

        return n;
    }

    /// <summary>一共支持多少个职业（面板显示用）。</summary>
    public static int JobCount => ByJob.Count;
}
