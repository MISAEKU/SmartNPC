using System;
using System.IO;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SmartNpc.Core;
using SmartNpc.UI;
using Dalamud.Configuration;
using Dalamud.Interface.Windowing;

namespace SmartNpc;

/// <summary>
/// 插件入口：生命周期、每帧驱动、指令。
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public const string CommandName = "/smartnpc";

    // ⚠️ 这三个字段**故意不带 readonly、也不写 `= null!`**。
    // 构造函数里它们是在 Try 内部赋值的（赋值失败就降级成默认值），
    // 写成 readonly 就没法在 lambda 里赋值，写成 `= null!` 则会让编译器
    // 以为它们一定非空，从而在后面埋下"某处 null 解引用"的雷。
    private NpcController? _controller;
    private Configuration? _config;
    private ConfigWindow? _configWindow;
    private SubNpcWindow[]? _subWindows;
    private readonly WindowSystem _windows = new("SmartNpcWindows");
    private Dalamud.Game.Gui.ContextMenu.MenuItem[]? _contextItems;

    /// <summary>
    /// 自建诊断文件（不依赖 Dalamud 日志 —— 那个日志到 100MB 就停写了）。
    /// 构造函数一进来就落盘，所以"这个文件存不存在"本身就是一个结论：
    /// 存在 = 本插件确实被 Dalamud 实例化了；不存在 = 根本没构造（去 /xlplugins 里启用）。
    /// </summary>
    private DiagFile _diag = null!;
    private readonly System.Text.StringBuilder _initTrace = new();

    // ⚠️⚠️ 这些服务属性**必须是 static**，这是本机反复验证过的硬要求（v0.12.3 修复）。
    //
    // 症状：写成实例属性（`private X Y { get; set; }`）时，Dalamud **一个都不注入**，
    //       全是 null → 初始化控制器 / 建窗口 / 注册右键菜单 / 注册指令 四处一起 NPE，
    //       表现为「插件加载了但 /smartnpc 指令消失、面板打不开」。
    //       而同一台机器上 MinionHumanizer 用 `internal static ... { get; private set; }`
    //       一直正常工作 —— 两者的唯一形式差别就是 static。
    //
    // 为什么会这样：Dalamud 的 [PluginService] 注入走的是「程序集里静态成员的服务表」，
    // 实例属性不在那张表里，于是被安静地留成 null（不抛异常，所以毫无征兆）。
    // 静态属性用法和实例属性**语法完全一致**（`Log.Information(...)`），所以业务代码不用动。
    [PluginService] internal static ICommandManager Commands       { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log            { get; private set; } = null!;
    [PluginService] internal static IFramework Framework            { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable          { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState          { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat              { get; private set; } = null!;
    [PluginService] internal static IDataManager Data          { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu          { get; private set; } = null!;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    /// <summary>战斗 / 采集 / 制作状态。行为层靠它决定"该不该凑过来搭话"。
    /// ⚠️ <c>ICondition</c> 在 <c>Dalamud.Plugin.Services</c>；同名的 <c>ConditionFlag</c> 才在
    /// <c>Dalamud.Game.ClientState.Conditions</c>。两个别搞混（写全限定名会 CS0234）。</summary>
    [PluginService] internal static ICondition Condition            { get; private set; } = null!;

    /// <summary>v0.18.43：签名扫描器（定位 ObjectArrays 注册表静态地址用）。</summary>
    [PluginService] internal static ISigScanner SigScanner        { get; private set; } = null!;

    /// <summary>v0.18.48：UI 访问（目标信息栏文本节点取证用，只读）。</summary>
    [PluginService] internal static IGameGui GameGui          { get; private set; } = null!;

    /// <summary>
    /// v0.18.50：当前目标（判断"玩家是不是把她选成目标"用）。
    ///
    /// <para>⚠️ 用 Dalamud 的 <c>ITargetManager</c> 而不是自己去读
    /// <c>TargetSystem.Target</c> 的偏移：那是托管 API，不会因为版本更新换偏移而读错，
    /// 也不存在野指针风险（本项目 v0.18.42 的 C0000005 教训）。</para>
    /// </summary>
    [PluginService] internal static ITargetManager TargetManager   { get; private set; } = null!;

    public Plugin()
    {
        // ⚠️ 每一段都单独 try：任何一段失败都<b>不影响其余功能</b>，
        // 并且失败原因一定进日志。之前把所有事塞在一个构造函数里，
        // 任何一处抛异常都是「插件整个加载失败 → 指令没反应、界面打不开」，
        // 什么都看不到。现在即使界面建不起来，指令也照样能用。
        // ⚠️ **构造函数里绝对不能有裸代码。**
        // 构造函数抛异常 = Dalamud 报 "Failed to create SmartNpc.Plugin (ctor invocation)"
        // = 插件整个加载不出来，而且堆栈只会指向构造函数，看不出真实原因。
        // 所以下面除了「记一行日志」，**所有操作都必须包在 Try 里**——
        // 一段坏了就让它坏，绝不让它连坐整个插件。

        // 诊断文件要最先建：后面每一段的失败都要靠它记下来。
        // 它刻意不依赖任何 [PluginService]，自己算配置目录。
        TryBuildDiagEarly();
        _diag?.Append("构造开始｜版本=" + DisplayVersion);

        // 配置读不出来也得继续：没有配置照样能跑，只是全用默认值。
        Try("读取配置", () => _config = LoadConfiguration());
        var cfg = _config ??= new Configuration();

        // 控制器同理：建不出来就真的什么都做不了，但**仍然要让构造函数正常返回**，
        // 这样至少指令能响应、诊断文件能写出来。
        Try("创建控制器", () => _controller = new NpcController(Log));
        var ctl = _controller ??= new NpcController();   // 无日志版的空壳
        ctl.Diag = _diag;

        Try("初始化控制器", () => ctl.Attach(Framework, ObjectTable, ClientState, Chat, PluginInterface, Data, cfg, Condition, Log));

        // v0.18.43.2：定位 ObjectArrays（GID 排序注册表）静态实例 —— #5「选不中/选中被自动取消」根因探针。
        // 静态分析见 _re/（⚠️ _re 产物地址全部比真实 RVA 小 0x400 —— 扫描索引被当文件偏移喂给 fo2rva 的系统性 bug，
        // 已用 .pdata 异常目录核实：解析函数真实 RVA = 0x842690）。
        // 函数头 20 字节唯一：40 53(REX push rbx); 48 83 EC 20(sub rsp,0x20); B8 00 00 00 E0(mov eax,0xE0000000);
        // 0F B6 DA(movzx ebx,dl); 48 3B C8(cmp rcx,rax —— ⚠️ 真实编码是 3B 不是 39，v0.18.43.1 的 sig 因此没命中);
        // 74 ??(je); 48 8B D1(mov rdx,rcx)。
        // 先 ScanText 定位解析函数，再读其内部 lea rcx,[rip+disp32]（+0x16 处，disp 在 +0x19）得到实例地址（真实 RVA 0x2A89DD0）。
        Try("定位 ObjectArrays 注册表", () =>
        {
            if (SigScanner is null)
            {
                NpcRegistryProbe.Initialize(nint.Zero, "SigScanner 服务为空");
                return;
            }

            try
            {
                var resolver = SigScanner.ScanText(
                    "40 53 48 83 EC 20 B8 00 00 00 E0 0F B6 DA 48 3B C8 74 ?? 48 8B D1");
                NpcRegistryProbe.InitializeFromResolver(resolver, "解析器签名命中");
            }
            catch (Exception ex)
            {
                NpcRegistryProbe.Initialize(nint.Zero, "解析器签名未命中: " + ex.Message);
            }
        });

        Try("接上配置保存", () => ctl.ConfigSaveRequested = () => PluginInterface.SavePluginConfig(cfg));

        Try("创建设置窗口", () =>
        {
            var win = new ConfigWindow(cfg, ctl);
            _configWindow = win;
            _windows.AddWindow(win);
            ctl.OpenPanelRequested = win.Toggle;

            PluginInterface.UiBuilder.Draw         += _windows.Draw;
            PluginInterface.UiBuilder.OpenConfigUi += win.Toggle;
            PluginInterface.UiBuilder.OpenMainUi   += win.Toggle;
        });

        // v0.18.69.23：每个副 NPC 一个独立设置窗口（主面板只留横栏，详细设置进这里）。
        Try("创建副 NPC 窗口", () =>
        {
            _subWindows = new SubNpcWindow[Configuration.SubNpcSlotCount];
            for (var i = 0; i < Configuration.SubNpcSlotCount; ++i)
            {
                var slot = i;   // 闭包捕获（foreach 变量在 C# 5+ 已安全，这里显式一份更稳）
                var sub = new SubNpcWindow(slot, cfg, ctl);
                _subWindows[slot] = sub;
                _windows.AddWindow(sub);

                if (slot == 0)
                    ctl.OpenSubNpcRequested = s =>
                    {
                        if (s >= 0 && s < _subWindows.Length)
                            _subWindows[s].Toggle();
                    };
            }
        });

        Try("注册右键菜单", () =>
        {
            // v0.18.51：不再往根菜单里塞两行 —— 合成**一个父级「智能 NPC」**，
            // 点开再选。Dalamud 的做法是 IsSubmenu=true + 点击时 args.OpenSubmenu(子项)。
            _contextItems =
            [
                new Dalamud.Game.Gui.ContextMenu.MenuItem
                {
                    Name      = "智能 NPC",
                    IsSubmenu = true,
                    OnClicked = args => args.OpenSubmenu(BuildNpcSubmenu(ctl)),
                },
            ];

            foreach (var item in _contextItems)
                ContextMenu.AddMenuItem(Dalamud.Game.Gui.ContextMenu.ContextMenuType.Default, item);
        });

        Try("注册指令", () => Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "智能 NPC。参数：probe / config / summon / dismiss / look / naked / say <内容> / emote <动作> / persona <人格> / status；不带参数则打开面板。",
            ShowInHelp  = true,
        }));

        // ⚠️ 收尾这三行同样必须绝对安全。
        // 走到这里说明前面都处理完了，结果最后一行 Log 抛异常把整个插件带崩，
        // 是最冤的一种失败 —— 而且诊断文件也已经写完了，看堆栈只会以为是别的问题。
        // 所以：每一步各自 try，谁失败都不影响别人，更不影响构造函数返回。
        try { Log?.Information("[SmartNpc] v{0} 已加载。指令：/smartnpc（不带参数打开面板）", DisplayVersion); }
        catch { }

        // 加载过程整份落盘：以后排查"到底加载到哪一步"看这个文件就行。
        SafeWrite("加载完成｜版本=" + DisplayVersion + Environment.NewLine + _initTrace);

        // 加载问候走日志。聊天栏只在它可用时才打印——构造早期它可能还不可用。
        try { Chat.Print($"[SmartNpc] v{DisplayVersion} 已加载。输入 /smartnpc 打开面板。"); }
        catch { /* 聊天服务还不可用就算了 */ }

        // ⚠️ 最后一道保险：就算上面有什么漏网的，**也绝不让异常逃出构造函数**。
        // 走到这里插件已经基本可用了，让它因为一句日志加载失败是最蠢的失败方式。
        // 注意 catch 之后不能再有会抛异常的代码。
    }

    /// <summary>
    /// 右键父菜单「智能 NPC」展开后的子项（v0.18.51）。
    ///
    /// <para>用户要求：原来只有「打个招呼」「打开面板」两项，太少 ——
    /// 现在把主动互动都放进来（问天气 / 问时间 / 问心情 / 夸她 / 谢她 / 摸头 / 让她歇会儿），
    /// 并全部收进一个父级里，免得根菜单被我们占满。</para>
    ///
    /// <para>⚠️ 每次展开都<b>新建</b>一份：这些 MenuItem 是 record，
    /// 复用的话 Dalamud 那边的引用计数 / 相等比较会出问题（它按值相等来去重）。</para>
    /// </summary>
    private static Dalamud.Game.Gui.ContextMenu.MenuItem[] BuildNpcSubmenu(NpcController ctl) =>
    [
        new() { Name = "打个招呼",   OnClicked = _ => ctl.Greet() },
        new() { Name = "询问天气",   OnClicked = _ => ctl.AskWeather() },
        new() { Name = "询问时间",   OnClicked = _ => ctl.AskTime() },
        new() { Name = "询问心情",   OnClicked = _ => ctl.AskMood() },
        new() { Name = "夸夸她",     OnClicked = _ => ctl.Praise() },
        new() { Name = "谢谢她",     OnClicked = _ => ctl.Thank() },
        new() { Name = "摸摸头",     OnClicked = _ => ctl.PatHead() },
        new() { Name = "让她歇一会儿", OnClicked = _ => ctl.Rest() },
        new() { Name = "道别",       OnClicked = _ => ctl.Farewell() },
        new() { Name = "打开面板",   OnClicked = _ => ctl.OpenPanel() },
    ];

    /// <summary>
    /// 尽早建好诊断文件，而且**刻意不依赖任何 [PluginService]**。
    ///
    /// 为什么不用 <c>PluginInterface.ConfigDirectory</c>：它是注入服务，
    /// 而这个方法要保证"在任何可能出错的地方之前就能用"。
    /// 自己算路径（%APPDATA%\XIVLauncherCN\pluginConfigs）零依赖、零风险。
    ///
    /// ⚠️ 整个方法体都在 try 里，**包括算路径那几行**：
    /// 它是构造函数里第一个被调用的方法，它抛异常的话插件照样起不来，
    /// 而且那时诊断文件还不存在 → 连个记录都没有。
    /// </summary>
    private void TryBuildDiagEarly()
    {
        try
        {
            // ⚠️ 必须是 pluginConfigs\SmartNpc 这一层。v0.12.2 传的是**父目录** pluginConfigs，
            // 而 DiagFile 不会再自己补子目录 → last-diag.txt 直接落在所有插件公用的目录里，
            // 既难找，也可能和别的插件撞名。
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncherCN", "pluginConfigs");

            var dir = Path.Combine(root, "SmartNpc");
            if (!Directory.Exists(root))
                dir = Path.Combine(Path.GetTempPath(), "SmartNpcDiag");

            _diag = new DiagFile(dir);
        }
        catch
        {
            // 连文件都建不了就算了 —— 不影响插件本身，只是没有诊断信息。
        }
    }

    /// <summary>
    /// 读配置。
    ///
    /// ⚠️ 排查记录（v0.12.1 → v0.12.2，别再改回原来的写法）：
    /// 我曾以为"构造函数期 [PluginService] 全是 null"，在这里加了 null 判空。
    /// **那是错的** —— v0.10.1 的构造函数和现在几乎逐行相同（同样裸调
    /// <c>PluginInterface.GetPluginConfig()</c>、同样在 <c>Try</c> 的 catch 里裸调
    /// <c>Log.Error</c>），而它是**能正常加载运行**的（诊断文件里有 NPC 实体和坐标）。
    ///
    /// 所以服务注入是正常的，不该在这里拦。
    /// 但保留一段**只记录不断言**的探测：万一注入真的出问题，
    /// diag 文件里会直接写明「哪个服务是 null」，不用再靠堆栈猜。
    /// </summary>
    private Configuration LoadConfiguration()
    {
        _diag?.Append("服务探测｜PluginInterface=" + (PluginInterface is null ? "null" : "OK")
                   + "｜Log=" + (Log is null ? "null" : "OK")
                   + "｜Framework=" + (Framework is null ? "null" : "OK")
                   + "｜ObjectTable=" + (ObjectTable is null ? "null" : "OK")
                   + "｜Commands=" + (Commands is null ? "null" : "OK"));

        try
        {
            // 局部变量先取出来判空：下面的 GetPluginConfig / SavePluginConfig
            // 都要用它，逐个判空反而更容易漏。
            var pi = PluginInterface;
            if (pi is null)
            {
                _diag?.Append("⚠ PluginInterface 为 null（服务探测已记录），改用默认配置。");
                return new Configuration();
            }

            var config = pi.GetPluginConfig() as Configuration ?? new Configuration();
            config.Sanitize();
            pi.SavePluginConfig(config);
            return config;
        }
        catch (Exception ex)
        {
            // ⚠️ 这里**不能**裸调 Log.Error：catch 里再抛异常会盖掉真正的堆栈，
            // 而构造函数抛异常 = 整个插件加载不出来（实测踩过）。
            // 先写 diag（文件 IO 不依赖注入服务），日志只作补充。
            _diag?.Append("⚠ 读取配置失败，改用默认值：" + Environment.NewLine + ex);

            try { Log?.Error(ex, "[SmartNpc] 读取配置失败，改用默认值。"); }
            catch { /* 日志服务不可用就算了，diag 里已经记全了 */ }

            return new Configuration();
        }
    }

    /// <summary>
    /// 跑一段初始化代码并把异常记进日志。
    /// 目的是「一部分坏了不要连坐」——插件加载失败是完全查不到原因的。
    /// </summary>
    private void Try(string what, Action action)
    {
        // ⚠️ 这个方法本身**绝不能抛异常**。
        // 它是构造函数的"防火墙"：所有初始化都走它，一旦它自己抛出去，
        // 防火墙就变成引爆点，插件照样加载失败 —— 而堆栈只会指向这里。
        // 所以最外层再包一层 try，连"记日志失败"都不许外泄。
        try
        {
            try
            {
                action();
                _initTrace.Append("✔ ").AppendLine(what);
            }
            catch (Exception ex)
            {
                // ⚠️ **绝对不能在这里裸调 Log.Error。**
                // catch 里抛出的异常会一路逃出构造函数 → 整个插件加载不出来，
                // 而堆栈里只剩「Try 里的这一行 NRE」，**真正出错的那一行完全看不见**
                // （实测踩过：Attach 里的真实异常就是这样被吃掉的，
                //   堆栈只指向 Try 的 catch，看起来像"Try 自己坏了"）。
                //
                // 顺序很重要：**先把异常写进 diag 文件**（文件 IO，不依赖注入服务），
                // 再尝试写日志（写不写无所谓）。
                SafeWrite("✘ 初始化「" + what + "」失败：" + Environment.NewLine + ex);
                _initTrace.Append("✘ ").Append(what).Append(" 失败：")
                          .AppendLine(ex.GetType().Name + ": " + ex.Message);

                try { Log?.Error(ex, $"[SmartNpc] 初始化「{what}」失败，其余功能不受影响。"); }
                catch { /* 日志服务不可用就算了，diag 里已经记全了 */ }
            }

            // 每一步都落盘，避免"加载中途整体崩掉"时什么都看不到。
            SafeWrite("加载中｜版本=" + DisplayVersion + Environment.NewLine + _initTrace);
        }
        catch
        {
            // 连记日志都失败了，那就真的什么都不做——但绝不让异常逃出去。
        }
    }

    /// <summary>
    /// 写诊断文件的绝对安全版本：<see cref="DiagFile"/> 自己会吞掉 IO 异常，
    /// 这里再包一层是因为调用点遍布构造函数，必须确保**写日志本身永不影响主流程**。
    /// </summary>
    private void SafeWrite(string text)
    {
        try { _diag?.Append(text); }
        catch { }
    }

    private void OnCommand(string command, string arguments)
    {
        var rest = arguments.Trim();

        // ⚠️ _controller / _configWindow 现在都是**可空**的：
        // 构造函数里它们是在 Try 内部创建的，万一失败就是 null。
        // 所以指令入口必须先确认它们还在，否则用户敲 /smartnpc 会把异常
        // 抛进 Dalamud 的指令分发里 —— 那会连带影响其他插件。
        if (_controller is null)
        {
            Chat.Print("[SmartNpc] 控制器初始化失败，功能不可用。诊断见 last-diag.txt。");
            return;
        }

        // 不带参数 = 打开控制面板。
        // 主入口必须是界面而不是"切换召唤"——召唤/离开在面板上有大按钮。
        if (rest.Length == 0)
        {
            TogglePanel();
            return;
        }

        Log?.Information("[SmartNpc] 收到指令：{0}", rest);

        var verb = rest.Split(' ', 2);
        switch (verb[0].ToLowerInvariant())
        {
            case "summon":  _controller.ToggleSummon(); break;
            case "dismiss": _controller.Dismiss(); break;
            case "status":  _controller.PrintStatus(); break;
            case "look":    _controller.ApplyAppearanceFromPlayer(); break;
            case "naked":   _controller.ApplyAppearanceNaked(); break;
            case "persona":
                if (verb.Length > 1) _controller.SetPersona(verb[1]);
                break;
            case "say":
                if (verb.Length > 1) _controller.Say(verb[1]);
                break;
            case "emote":
                if (verb.Length > 1) _controller.Emote(verb[1]);
                break;
            case "stop":     _controller.StopAction(); break;
            case "diag":     _controller.LogDiagnostics(); break;

            case "config":
                TogglePanel();
                break;

            default:
                Chat.Print($"[SmartNpc] 未知子命令「{verb[0]}」。可用：probe / config / summon / dismiss / look / naked / say / emote / persona / status");
                break;
        }
    }

    /// <summary>开关设置面板。没建起来就明确告诉用户，别静默失败。</summary>
    private void TogglePanel()
    {
        if (_configWindow is null)
        {
            Chat.Print("[SmartNpc] 设置面板初始化失败。诊断见 last-diag.txt。");
            return;
        }

        _configWindow.Toggle();
    }

    /// <summary>版本号（界面与指令里显示用），与 csproj 的 &lt;Version&gt; 同源。</summary>
    public static string DisplayVersion
        => typeof(Plugin).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "unknown";

    public void Dispose()
    {
        // ⚠️ Dispose 也必须全程判空：卸载时任何一处抛异常，
        // Dalamud 会认为插件没卸干净，可能留下僵尸 NPC 或崩在卸载流程里。
        // 这里宁可什么都做不了，也不能抛。

        try { Commands?.RemoveHandler(CommandName); } catch { }

        try
        {
            if (_configWindow is not null)
            {
                PluginInterface?.UiBuilder.Draw         -= _windows.Draw;
                PluginInterface?.UiBuilder.OpenConfigUi -= _configWindow.Toggle;
                PluginInterface?.UiBuilder.OpenMainUi   -= _configWindow.Toggle;
            }
        }
        catch { }

        try { if (_config is not null) PluginInterface?.SavePluginConfig(_config); } catch { }

        if (_contextItems is not null)
        {
            foreach (var item in _contextItems)
            {
                try { ContextMenu?.RemoveMenuItem(Dalamud.Game.Gui.ContextMenu.ContextMenuType.Default, item); }
                catch { }
            }
        }

        // ⚠️ 必须同步收尾：IFramework 是插件作用域的，
        // 卸载时挂在它上面的 RunOnTick 回调会被 Dalamud 处置掉，再也不会执行。
        // 所以 NPC 必须在 Dispose 里同步销毁，否则会留下一个僵尸 NPC。
        try { _controller?.Dispose(); } catch { }
    }
}