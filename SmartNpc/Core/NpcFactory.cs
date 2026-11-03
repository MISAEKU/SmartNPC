using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using SceneObjectType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType;
using NativeObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;
using FFXIVClientStructs.FFXIV.Common.Math;

namespace SmartNpc.Core;

/// <summary>
/// 凭空造一个 NPC actor，并负责销毁。
///
/// 用的是游戏自己的 <c>ClientObjectManager::CreateBattleCharacter</c>——
/// 它会在对象表里新建一个 NPC 槽位并实例化 actor。所以：
///
///   · 我们<b>不自己 new 任何原生对象</b>，也<b>不 free</b>；
///   · 对象的生命周期始终由游戏的 ClientObjectManager 持有；
///   · 销毁只调用游戏自己的 <c>DeleteObjectByIndex</c>。
///
/// 这跟"自己构造 Character 再塞进场景"是完全不同的风险等级：
/// 前者如果漏了某个注册步骤就是野指针崩溃，后者是官方留出来的正规入口。
/// </summary>
public static unsafe class NpcFactory
{
    /// <summary>游戏用这个返回值表示"没造出来"。</summary>
    private const uint InvalidIndex = 0xFFFFFFFF;

    /// <summary>对象表索引 0 恒定是本地玩家，任何情况下都不许操作它。</summary>
    private const ushort LocalPlayerIndex = 0;

    /// <summary>
    /// 野外区段的索引上界。<b>200 及以上是 GPose 专用块</b>
    /// （Brio 的 <c>ActorTableHelpers</c>：野外 0..199、GPose 201..439）。
    ///
    /// 为什么必须落在野外块：GPose 块的 actor 由 GPose 事件控制器管理
    /// （<c>EventGPoseController::AddCharacterToGPose</c>），而那个注册在<b>非 GPose 时是空操作</b>。
    /// 落在 GPose 块又不注册，游戏就永远不会给它构建绘制对象 ——
    /// 症状正是我们踩的坑：日志说"创建成功"，但 DrawObject 永远是空。
    /// </summary>
    private const ushort OverworldEnd = 199;

    /// <summary>让游戏自己挑索引（Brio 的用法）。</summary>
    private const uint AutoIndex = 0xFFFFFFFF;

    /// <summary>
    /// 队伍区段上界（含）。索引 0 是玩家、1..8 是队友——这些槽位由队伍系统管理，
    /// 而我们造出来的东西 <c>CopyFromCharacter</c> 之后对外就是 <c>ObjectKind.Pc</c>，
    /// 落在这段里等于交给队伍系统处置。实测它就落在 1 号槽。
    /// </summary>
    private const ushort PartySlotEnd = 16;

    /// <summary>
    /// <c>GameObject.TargetableStatus</c> 的字节偏移（1 字节，紧邻 <c>UpdateFlags</c>）。
    /// 用裸字节读写是因为该字段的枚举类型在 FFXIVClientStructs 里没有公开命名。
    /// </summary>
    private const int TargetableStatusOffset = 0x9A;

    /// <summary>本次创建的 <b>创建瞬间</b>（套外观之前）的对象类型。</summary>
    public static NativeObjectKind CreatedKind { get; private set; }

    /// <summary>本地玩家地址（由 NpcController 注入；取不到时闸 ③ 跳过，但闸 ①②仍然有效）。</summary>
    internal static nint LocalPlayerAddress { get; set; }

    /// <summary>上一次实际拿到的对象表索引（诊断用）。</summary>
    public static uint LastSpawnIndex { get; private set; }

    /// <summary>创建前游戏自己算出来的"下一个可用索引"（诊断用）。</summary>
    public static uint LastNextAvailableIndex { get; private set; }

    /// <summary>
    /// 造一个 NPC。
    /// </summary>
    /// <param name="reserveCompanionSlot">
    /// true = 占用一个随从槽位；false = 不占。
    /// 我们默认<b>不占</b>：这个 NPC 只是视觉 companion，抢玩家的随从位会影响真实随从。
    /// </param>
    public static bool TrySpawn(bool reserveCompanionSlot, out NpcHandle handle, out string error)
    {
        handle = default;
        error   = string.Empty;

        nint playerAddress = nint.Zero;
        try { playerAddress = LocalPlayerAddress; }
        catch { /* 取不到就跳过第③道闸 */ }

        try
        {
            var com = ClientObjectManager.Instance();
            if (com is null)
            {
                error = "拿不到 ClientObjectManager（游戏可能还在加载）";
                return false;
            }

            // ── 参数：真实签名已从本机 FFXIVClientStructs.dll 解出来核实 ──
            //
            //     uint CreateBattleCharacter(uint index, byte param)
            //
            // ⚠️ 第一个参数是**对象表索引**，不是"kind"！
            //   之前把它当 kind 传了固定值 1，等于每次强行占用 1 号槽：
            //   对象确实建出来了，但那是"派对成员槽位"，不是游戏给我们挑的空位。
            //   索引传 0xFFFFFFFF 时参数名才叫 index 的默认值 = 让游戏自己挑。
            //
            // ⚠️ 但"让游戏自己挑"会挑到 GPose 块（>=200，见 OverworldEnd 注释），
            //   非 GPose 时那块不会被处理。所以野外同伴要显式要一个野外索引。
            //
            // param：1 = 占一个随从槽位，0 = 不占。
            var wantedIndex = PickOverworldIndex(com);
            var rawIndex    = com->CreateBattleCharacter(wantedIndex, (byte)(reserveCompanionSlot ? 1 : 0));
            LastSpawnIndex  = rawIndex;

            if (rawIndex == InvalidIndex)
            {
                error = $"CreateBattleCharacter 没有返回有效索引（要的是 {wantedIndex}）";
                return false;
            }

            // ── 闸 ①：索引 0 恒定是本地玩家，绝不能当成新建的 NPC ──
            //
            // ⚠️ 这是实测踩出来的：CreateBattleCharacter 失败时返回的是 0（不是 0xFFFFFFFF），
            // 于是 GetObjectByIndex(0) 拿到的是**玩家自己**——插件会开始对玩家
            // EnableDraw / 改外观 / 移动位置，甚至 DeleteObjectByIndex(0) 去删玩家。
            if (rawIndex == LocalPlayerIndex)
            {
                error = "CreateBattleCharacter 返回 0（本地玩家槽位）——创建失败。"
                      + "绝不会把玩家当成新 NPC 操作。";
                return false;
            }

            var objectIndex = (ushort)rawIndex;
            var gameObject  = com->GetObjectByIndex(objectIndex);

            // ── 闸 ②：取到的必须是个 actor ──
            //
            // ⚠️ 实测：CreateBattleCharacter 造出来的是 **EventNpc**，不是 BattleNpc。
            // 之前只放行 BattleNpc，等于把造出来的东西自己拒掉了——这是"造不出人"的直接原因。
            if (gameObject is null)
            {
                error = $"索引 {objectIndex} 上没有对象。";
                return false;
            }

            if (gameObject->ObjectKind is not (NativeObjectKind.BattleNpc or NativeObjectKind.EventNpc))
            {
                error = $"索引 {objectIndex} 的对象类型是 {gameObject->ObjectKind}，既不是 BattleNpc 也不是 EventNpc——放弃。";
                return false;
            }

            // ── 闸 ③：绝不能是玩家本人 ──
            if (playerAddress != nint.Zero && (nint)gameObject == playerAddress)
            {
                error = "取到的对象是玩家本人——放弃。";
                return false;
            }

            // ⚠️ 这里**故意什么都不做**。
            //
            // 刚创建出来的 actor 是一个"空壳"：它没有任何模型，所以游戏不会为它
            // 构建绘制对象。此时无论立刻 EnableDraw() 还是死等 IsReadyToDraw()，
            // 都拿不到绘制对象（实测：前者毫无反应，后者卡满超时）。
            //
            // 正确顺序是：
            //   创建 → **先写模型（NpcAppearance.PrepareModel）** → 再请求绘制并等它出现。
            // 模型由调用方负责，工厂这边不越权。
            // 记录"创建瞬间"的对象类型。
            // 套完外观之后快照里显示的是 Pc —— 那就说明是 <c>CharacterSetup.CopyFromCharacter</c>
            // 之后才变的。先记下来，别再靠猜。
            CreatedKind = gameObject->ObjectKind;

            // ⚠️ v0.18.44 根因修复：**绝不能再改写 EntityId（= GameObjectId）**。
            //
            // 历史教训链：
            //   ① 最老版本拿索引当 EntityId（索引 1 → EntityId 1）会撞真实实体 → 废；
            //   ② 于是改成 0x7E000000|索引 的"不撞范围" → 引出 #5 的真正根因：
            //      CreateBattleCharacter 时游戏已按**原始 EntityId** 把她注册进
            //      ObjectArrays 的 GameObjectId 排序表（+0x1998，实锤见 _re/ 与探针）；
            //      创建后改写 EntityId，表里留的还是旧 Id 的位置 → 游戏点击选中走
            //      "按 GID 二分解析"，解析永远 NULL → SetHardTarget(NULL) →
            //      **刚召唤点不中 / 选中被自动取消**。
            //      探针实锤（registry-probe.txt）：指针在表 Y(i=85)、二分=NULL、
            //      全表仅 1 处排序违例 = 她那条；+0x190 的 bit4 干净（另一嫌疑排除）。
            //
            // 游戏自己分配的 EntityId 天然全局一致（注册表、目标系统、动画锁定都用它），
            // 保留原值即可；身份判断在插件里从不依赖 EntityId（只有日志/转头用玩家 Id）。
            var assignedEntityId = gameObject->EntityId;

            // v0.18.45：召唤瞬间抓拍游戏分配的默认名 —— 配置名留空时显示名回退到它。
            var gameAssignedName = NpcNaming.ReadName((nint)gameObject);

            DiagnosticLog?.Invoke(
                $"索引 {objectIndex}｜创建瞬间类型={CreatedKind}｜游戏分配实体=0x{assignedEntityId:X}（保留不改写）｜默认名=\"{gameAssignedName}\"");

            handle = new NpcHandle(objectIndex, (nint)gameObject, assignedEntityId, gameAssignedName);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 挑一个<b>野外块</b>的空闲索引。
    ///
    /// 先问游戏自己的 <c>CalculateNextAvailableIndex</c>；它给的答案若落在 GPose 块
    /// （或就是 0 号玩家位），就退回自己扫一遍 1..199。
    /// 两者都失败才交给游戏自动分配（AutoIndex），因为那至少有一个人形可见的概率。
    /// </summary>
    private static uint PickOverworldIndex(ClientObjectManager* com)
    {
        uint next = InvalidIndex;
        try
        {
            next              = (uint)com->CalculateNextAvailableIndex(false);
            LastNextAvailableIndex = next;
        }
        catch
        {
            // 拿不到就算了，走下面的扫描。
        }

        if (IsUsableOverworldIndex(next))
            return next;

        // 自己扫。注意从 PartySlotEnd 起，别再抢队伍槽位。
        for (ushort i = PartySlotEnd; i <= OverworldEnd; ++i)
        {
            if (com->GetObjectByIndex(i) is null)
                return i;
        }

        return AutoIndex;
    }

    private static bool IsUsableOverworldIndex(uint index)
        => index != InvalidIndex && index >= PartySlotEnd && index <= OverworldEnd;

    /// <summary>
    /// 让对象可以被鼠标选中。
    ///
    /// ⚠️⚠️ v0.18.37：**旧实现是错的，已废**。它直接把 <c>TargetableStatus</c>
    /// 那个字节写成 <c>1</c> —— 而 <c>ObjectTargetableFlags</c> 里
    /// <c>Unk0 = 0x01</c>、<c>IsTargetable = 0x02</c>，写 1 等于**把可选中位清掉**。
    /// 现在统一走 <see cref="NpcTargetable"/>（那里还带对照采样，见其注释）。
    /// </summary>
    public static void MakeTargetable(NpcHandle handle)
    {
        if (handle.Address == nint.Zero || handle.ObjectIndex == LocalPlayerIndex)
            return;

        NpcTargetable.Apply((GameObject*)handle.Address, null, true);
    }

    /// <summary>诊断输出回调（由 NpcController 接到插件日志）。</summary>
    public static Action<string>? DiagnosticLog { get; set; }

    /// <summary>
    /// 销毁 NPC。必须在插件卸载/ 功能关闭时<b>同步</b>调用
    /// （原因见 README：「IFramework 是插件作用域的，卸载时 RunOnTick 不会被执行」）。
    /// </summary>
    public static bool Despawn(NpcHandle handle)
    {
        // ⚠️ 索引 0 是本地玩家，任何情况下都不许对它调用 DeleteObjectByIndex。
        if (handle.ObjectIndex == LocalPlayerIndex)
            return false;

        try
        {
            var com = ClientObjectManager.Instance();
            if (com is null)
                return false;

            com->DeleteObjectByIndex(handle.ObjectIndex, 0);
            return true;
        }
        catch (Exception)
        {
            // 对象可能已经被游戏自己清掉了（例如切图）。这不是错误。
            return false;
        }
    }

    /// <summary>
    /// 「点亮绘制」的分帧状态机。由宿主的每帧回调推进。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>顺序是这套东西最容易搞错的地方，而且错了的表现完全一样</b>
    /// （日志说成功、画面上什么都没有）。
    ///
    /// 不能写成「先等 <c>IsReadyToDraw()</c>、再 <c>EnableDraw()</c>」：
    /// 刚造出来的对象本来就没有绘制对象，<c>IsReadyToDraw()</c> 于是一直为 false，
    /// 先等它就等于死锁（实测卡满 8 秒超时，日志：<c>绘制对象=空</c>）。
    ///
    /// 正确顺序和宠物插件、Brio 一致：
    ///   <c>EnableDraw()</c>（请求绘制）→ 再等绘制对象真的挂上来。
    /// 这里额外每隔一小段时间重申一次请求，防止游戏中途把标志翻回去。
    /// </remarks>
    public sealed class SpawnDrawState
    {
        private int _ticks;

        /// <summary>超时。模型加载通常一两秒内完成，10 秒是很宽的兜底。</summary>
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private readonly DateTime _deadline = DateTime.UtcNow + Timeout;

        /// <summary>推进一步；返回 true 表示已完成（成功与否看 <paramref name="succeeded"/>）。</summary>
        public bool Step(NpcHandle handle, out bool succeeded, out string message)
        {
            succeeded = false;
            message   = string.Empty;

            if (handle.Address == nint.Zero || handle.ObjectIndex == LocalPlayerIndex)
            {
                message   = "目标已失效";
                succeeded = false;
                return true;
            }

            var go = (GameObject*)handle.Address;
            ++_ticks;

            // 1) 先给对象两帧完成自身初始化。
            if (_ticks < 2)
                return false;

            // 2) 主动请求绘制（不是"等就绪再请求"，那会死锁）。
            //    前两帧各来一次，之后每约半秒重申。
            if (_ticks == 2 || _ticks % 30 == 0)
                go->EnableDraw();

            // 3) 等绘制对象真的挂上来，并且可见。
            var draw = go->DrawObject;
            if (draw is not null && draw->IsVisible)
            {
                message   = $"绘制对象已就绪：0x{(nint)draw:X}";
                succeeded = true;
                return true;
            }

            if (DateTime.UtcNow > _deadline)
            {
                message   = $"等了 {Timeout.TotalSeconds:F0} 秒还没画出人｜{Describe(handle)}";
                succeeded = false;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// 把对象当前的"可绘制性"拍一张快照，用于失败时定位。
    /// 一次就能看出是"没模型"、"没绘制对象"还是"有绘制对象但不可见"。
    /// </summary>
    public static string Describe(NpcHandle handle)
    {
        if (handle.Address == nint.Zero)
            return "对象已失效";

        try
        {
            var go    = (GameObject*)handle.Address;
            var chara = (Character*)handle.Address;
            var draw  = go->DrawObject;

            var model = chara->ModelContainer;

            return $"索引={handle.ObjectIndex}"
                 + $" 类型={go->ObjectKind}"
                 + $" 就绪={go->IsReadyToDraw()}"
                 + $" 绘制对象={(draw is null ? "空" : $"有(可见={draw->IsVisible})")}"
                 + $" 渲染标志=0x{(ulong)go->RenderFlags:X}"
                 + $" 模型={model.ModelCharaId}/{model.ModelSkeletonId}"
                 + $" 坐标=({go->Position.X:F1},{go->Position.Y:F1},{go->Position.Z:F1})";
        }
        catch (Exception ex)
        {
            return $"（快照失败：{ex.Message}）";
        }
    }

    /// <summary>取绘制对象（CharacterBase）；没有就返回 null。</summary>
    public static CharacterBase* GetDrawObject(NpcHandle handle)
    {
        if (handle.Address == nint.Zero)
            return null;

        var gameObject = (GameObject*)handle.Address;
        var draw       = gameObject->DrawObject;
        if (draw is null)
            return null;

        return draw->Object.GetObjectType() == SceneObjectType.CharacterBase ? (CharacterBase*)draw : null;
    }

    /// <summary>
    /// 把 NPC 放到某个位置并转向。
    /// </summary>
    /// <remarks>
    /// 用 <c>SetPosition</c> 而不是直接写 <c>Position</c>：前者是这个类型对外的正规入口，
    /// 会走游戏自己的位置更新流程（广播给 interested 实体），直接写字段可能出现"位置对但不移动"。
    /// </remarks>
    public static void TeleportTo(NpcHandle handle, Vector3 position, float rotation)
    {
        // 索引 0 是本地玩家，绝不移动它。
        if (handle.Address == nint.Zero || handle.ObjectIndex == LocalPlayerIndex)
            return;

        var gameObject = (GameObject*)handle.Address;

        // ⚠️⚠️ v0.17.5：**SetPosition 绝不能删** —— 这一行每帧都必须写。
        //
        // 血的教训（v0.17.3，用户实测"按召唤人没了"）：
        // 上一版改"每帧无条件 SetRotation 有副作用"时，手滑把 SetDrawOffset 和
        // **SetPosition 一起删了** —— 方法叫 TeleportTo 却不再写位置。
        // 于是召唤时她被留在 (0,0,0)，离玩家十万八千里 → 游戏把远处对象剔除，
        // 点亮流程干等 10 秒："等了 10 秒还没画出人｜可见=False 渲染标志=0x0 坐标=(0.0,0.0,0.0)"。
        //
        // 位置更新**没有**"重置动画"的副作用（v0.17.2 及之前每帧 SetPosition 一直正常），
        // 它是本方法的本职，无条件写。
        gameObject->SetDrawOffset(0f, 0f, 0f);
        gameObject->SetPosition(position.X, position.Y, position.Z);

        // ⚠️ 但 SetRotation **有**副作用（重置动画状态，v0.17.3 定位）——
        // 只在朝向真的变了时才写。
        if (MathF.Abs(rotation - gameObject->Rotation) > 0.0001f)
            gameObject->SetRotation(rotation);
    }
}
