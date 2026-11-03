using System;
using System.IO;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using SceneObjectType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType;

namespace SmartNpc.Core;

/// <summary>
/// 外观：把 NPC 写成人类，并支持随时换装。
///
/// 沿用 Minion Humanizer 里验证过的两条关键做法：
///
/// 1. <b>能刷新就不重建</b>。参照 Brio：只有"模型或骨架真的变了"才重建绘制对象，
///    否则直接 <c>Human.UpdateDrawData</c> 让它按新的 DrawData 重装配件。
///    重建是重操作、有失败面（超时 → 回滚 → 看起来像"没生效"）。
///
/// 2. <b>写完内存里的 DrawData 之后，"刷新缓冲区"才发给绘制对象</b>。
///    只写不刷，画面不变；只刷不写，下次游戏自己重绘会把旧外观带回来。
/// </summary>
public sealed unsafe class NpcAppearance(IPluginLog log)
{
    /// <summary>CustomizeData 字节数（FFXIVClientStructs: Size = 0x1A）。</summary>
    public const int CustomizeLength = 0x1A;

    public const int EquipmentSlots     = 10;
    public const int EquipmentSlotSize  = 8;
    public const int EquipmentLength    = EquipmentSlots * EquipmentSlotSize;

    /// <summary>Human.DrawData 的缓冲区大小：32 字节 customize + 80 字节装备，留足余量。</summary>
    private const int UpdateBufferSize    = 128;
    private const int UpdateCustomizeSize = 32;

    private NpcHandle? _npc;
    private byte[]?    _pendingCustomize;
    private byte[]?    _pendingEquipment;

    public void Bind(NpcHandle handle)
    {
        _npc              = handle;
        _pendingCustomize = null;
        _pendingEquipment = null;
        _rebuildPhase     = -1;
    }

    /// <summary>
    /// 造人之后、请求绘制之前<b>必须</b>先做的一步：把模型写上去。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这是整个流程里最关键的一步，也是之前一直失败的原因。
    ///
    /// 刚从 <c>CreateBattleCharacter</c> 出来的 actor 是一个<b>空壳</b>：没有任何模型。
    /// 游戏不会为一个没有模型的 actor 构建绘制对象，于是
    /// <c>gameObject-&gt;DrawObject</c> 永远是空、<c>IsReadyToDraw()</c> 永远是 false。
    /// 先等绘制、后写模型 = 死锁。
    ///
    /// 所以顺序必须是：创建 → <b>写模型（本方法）</b> → 请求绘制 → 等绘制对象出现。
    /// Brio 的 <c>CloneCharacter</c> 也是先 <c>CharacterSetup.CopyFromCharacter</c>
    /// 再 <c>DrawWhenReady</c>，同一条道理。
    /// </remarks>
    /// <param name="playerAddress">本地玩家 <c>Character*</c> 地址；给 0 则用默认人类形象。</param>
    /// <param name="fromPlayer">是否照抄玩家。</param>
    public bool PrepareModel(nint playerAddress, bool fromPlayer)
    {
        if (_npc is not { IsAlive: true })
            return false;

        var character = (Character*)_npc.Address;
        var usePlayer = fromPlayer && playerAddress != nint.Zero;
        var player    = usePlayer ? (Character*)playerAddress : null;

        // ── 1) 原生整体复制 ──
        //
        // 只写 DrawData 不够：游戏判定"这个 actor 有没有模型"看的是 CharacterSetup。
        // 这一步把玩家的整套 CharacterSetup 抄过来，游戏才认得她有模型可以加载。
        // 第二句"自己抄自己"是上游为了 Penumbra/Glamourer 之类的工具能正确接管而做的。
        if (player is not null)
        {
            character->CharacterSetup.CopyFromCharacter(player, CharacterSetupContainer.CopyFlags.WeaponHiding);
            character->CharacterSetup.CopyFromCharacter(character, default);
        }

        // ── 2) 模型来源归一化 ──
        //
        // ModelCharaId = 0 → 人形骨架。不这么做的话，造出来的 actor 会带着一个
        // 不存在的模型 ID，游戏加载失败 → 依然没有绘制对象。
        character->ModelContainer.ModelCharaId      = 0;
        character->ModelContainer.ModelSkeletonId   = 0;
        character->ModelContainer.ModelCharaId_2    = -1;
        character->ModelContainer.ModelSkeletonId_2 = 0;

        // ── 3) DrawData —— 真正决定"长什么样"的那份数据 ──
        var (customize, equipment) = player is not null
            ? CopyFromPlayer(player)
            : (DefaultCustomize(), new byte[EquipmentLength]);

        WriteCustomize(character, customize);
        WriteEquipment(character, equipment);

        character->DrawData.IsHatHidden     = false;
        character->DrawData.IsWeaponHidden  = false;
        character->DrawData.IsVisorToggled  = false;
        character->DrawData.VieraEarsHidden = false;

        _npc.StructureKey = StructureKey(customize);

        log.Debug("[SmartNpc] 模型已就位：来源={0} 骨架指纹=0x{1:X} 模型={2}/{3}",
                  player is not null ? "玩家" : "默认人类",
                  _npc.StructureKey,
                  character->ModelContainer.ModelCharaId,
                  character->ModelContainer.ModelSkeletonId);

        return true;
    }

    /// <summary>
    /// 套一套来自 <b>Glamourer 设计</b> 的完整外观（含武器与元开关）。
    ///
    /// 这是"能自己定义她长什么样"的正路：不用手抄装备表，
    /// 直接复用玩家在 Glamourer 里 designs 目录里存好的设计文件。
    /// </summary>
    public bool ApplyDesign(DesignAppearance design)
    {
        if (_npc is not { IsAlive: true })
            return false;

        if (design.Customize.Length < CustomizeLength || design.Equipment.Length < EquipmentLength)
            return false;

        var character = (Character*)_npc.Address;

        // 设计自带骨架信息（种族/性别/体型），骨架变了必须重建绘制对象。
        var skeletonChanged = !IsHuman(_npc)
                           || _npc.StructureKey != StructureKey(design.Customize)
                           || !IsHumanDrawObject(_npc);

        character->ModelContainer.ModelCharaId      = 0;
        character->ModelContainer.ModelSkeletonId   = 0;
        character->ModelContainer.ModelCharaId_2    = -1;
        character->ModelContainer.ModelSkeletonId_2 = 0;

        WriteCustomize(character, design.Customize);
        WriteEquipment(character, design.Equipment);

        // 武器必须走游戏自己的加载入口：直接写字段不会触发重装（会看到"数据对但手上没东西"）。
        if (design.MainHand is { } mainHand)
            WriteWeapon(character, DrawDataContainer.WeaponSlot.MainHand, mainHand);

        if (design.OffHand is { } offHand)
            WriteWeapon(character, DrawDataContainer.WeaponSlot.OffHand, offHand);

        character->DrawData.IsHatHidden     = !(design.HatVisible ?? true);
        character->DrawData.IsWeaponHidden  = !(design.WeaponVisible ?? true);
        character->DrawData.IsVisorToggled  = design.VisorToggled ?? false;
        character->DrawData.VieraEarsHidden = !(design.VieraEarsVisible ?? true);

        _npc.StructureKey = StructureKey(design.Customize);

        if (!skeletonChanged && TryUpdateDrawData(character, design.Customize, design.Equipment))
        {
            log.Debug("[SmartNpc] 已就地套用 Glamourer 设计。");
            return true;
        }

        Rebuild();
        return true;
    }

    private static void WriteWeapon(Character* character, DrawDataContainer.WeaponSlot slot, ulong weaponData)
    {
        var model = default(WeaponModelId);
        model.Value = weaponData;
        character->DrawData.LoadWeapon(slot, model, 0, 0, 0, 0, false);
    }

    /// <summary>默认形象：人族女性、裸装。用于"不抄玩家"时的兜底。</summary>
    public static byte[] DefaultCustomize()
    {
        var customize = new byte[CustomizeLength];
        customize[0] = 1;   // 种族：人族
        customize[1] = 1;   // 性别：女
        customize[2] = 0;   // 体型
        customize[4] = 0;   // 部族
        return customize;
    }

    /// <summary>当前是否已经是人类外观。</summary>
    public static bool IsHuman(NpcHandle npc)
    {
        if (npc.Address == nint.Zero)
            return false;

        return ((Character*)npc.Address)->ModelContainer.ModelCharaId == 0;
    }

    /// <summary>绘制对象是否真的是人形（用来发现"写了但没换"的假成功）。</summary>
    public static bool IsHumanDrawObject(NpcHandle npc)
    {
        var draw = NpcFactory.GetDrawObject(npc);
        return draw is not null && draw->GetModelType() == CharacterBase.ModelType.Human;
    }

    /// <summary>
    /// 套一套外观。返回 false 表示数据长度不对。
    /// 内部会自己判断该"刷新"还是该"重建"。
    /// </summary>
    public bool Apply(byte[] customize, byte[] equipment)
    {
        if (_npc is not { IsAlive: true })
            return false;

        if (customize.Length < CustomizeLength || equipment.Length < EquipmentLength)
            return false;

        _pendingCustomize = customize;
        _pendingEquipment = equipment;

        var character = (Character*)_npc.Address;

        var skeletonChanged =
               !IsHuman(_npc)                                          // 还不是人 → 必须重建
            || _npc.StructureKey != StructureKey(customize)           // 骨架变了 → 必须重建
            || !IsHumanDrawObject(_npc);                              // 画面没跟上 → 重建更稳

        // ── 1) 永远先写内存里的 ModelContainer + DrawData ──
        character->ModelContainer.ModelCharaId      = 0;
        character->ModelContainer.ModelSkeletonId   = 0;
        character->ModelContainer.ModelCharaId_2    = -1;
        character->ModelContainer.ModelSkeletonId_2 = 0;

        WriteCustomize(character, customize);
        WriteEquipment(character, equipment);

        // 视觉开关归一为"正常显示"。
        character->DrawData.IsHatHidden     = false;
        character->DrawData.IsWeaponHidden  = false;
        character->DrawData.IsVisorToggled  = false;
        character->DrawData.VieraEarsHidden = false;

        // ── 2) 已是人形且骨架没变 → 就地刷新，不碰绘制对象生命周期 ──
        if (!skeletonChanged)
        {
            if (TryUpdateDrawData(character, customize, equipment))
            {
                log.Debug("[SmartNpc] 已就地刷新外观。");
                return true;
            }

            log.Debug("[SmartNpc] 就地刷新未成功，改为重建绘制对象。");
        }

        // ── 3) 需要重建 ──
        Rebuild();
        return true;
    }

    /// <summary>
    /// 就地刷新：让 Human 按新的 DrawData 重装配件，不撤下绘制对象。
    /// 任何状态下都安全、零失败面。
    /// </summary>
    private static bool TryUpdateDrawData(Character* character, byte[] customize, byte[] equipment)
    {
        var draw = character->DrawObject;
        if (draw is null || draw->Object.GetObjectType() != SceneObjectType.CharacterBase)
            return false;

        var characterBase = (CharacterBase*)draw;
        if (characterBase->GetModelType() != CharacterBase.ModelType.Human)
            return false;

        var buffer = new byte[UpdateBufferSize];

        for (var i = 0; i < CustomizeLength && i < UpdateCustomizeSize; ++i)
            buffer[i] = customize[i];

        for (var i = 0; i < EquipmentLength; ++i)
            buffer[UpdateCustomizeSize + i] = equipment[i];

        var human = (Human*)characterBase;

        fixed (byte* pointer = buffer)
            return human->UpdateDrawData((Human.DrawData*)pointer, false);
    }

    /// <summary>
    /// 重建绘制对象。分帧进行：撤下 → 等就绪 → 开启 → 等出现且确实是人类模型。
    /// </summary>
    private void Rebuild()
    {
        if (_npc is not { IsAlive: true })
            return;

        var gameObject = (GameObject*)_npc.Address;
        gameObject->DisableDraw();

        _npc.StructureKey = _pendingCustomize is null ? 0 : StructureKey(_pendingCustomize);

        // 这里不自己驱动重建 —— 交给 NpcController 每帧推进阶段，
        // 这样"等就绪"不会阻塞，也不依赖某个一次性回调。
        _rebuildPhase = 0;
        _rebuildTicks = 0;
    }

    // ⚠️ 初值必须是 -1（"没有待重建的事"）。默认的 0 会被 TickRebuild 当成
    // "正在重建的第 0 阶段"，于是 NPC 一绑定就开始对绘制对象动手。
    private int  _rebuildPhase = -1;
    private int  _rebuildTicks;

    /// <summary>由 NpcController 每帧调用，推进重建流程。</summary>
    public void TickRebuild()
    {
        if (_npc is not { IsAlive: true } || _rebuildPhase < 0)
            return;

        var gameObject = (GameObject*)_npc.Address;

        switch (_rebuildPhase)
        {
            case 0:
                // 撤下之后先给游戏两帧清空旧对象，否则会读到"还没清空"的旧指针 → 假成功。
                if (++_rebuildTicks < 2)
                    return;

                // ⚠️ 这里**同样不能先等 IsReadyToDraw**：DisableDraw 之后绘制对象已经被销毁，
                // IsReadyToDraw() 必然为 false，先等它就是死锁（和造人时踩的是同一个坑）。
                // 正确顺序：先请求绘制，再等绘制对象出现。
                gameObject->EnableDraw();
                _rebuildPhase = 1;
                _rebuildTicks = 0;
                return;

            case 1:
                var draw = gameObject->DrawObject;
                if (draw is null || !draw->IsVisible)
                {
                    // 每隔约半秒重申一次"请绘制"，防止标志被游戏翻回去。
                    if (_rebuildTicks % 30 == 0)
                        gameObject->EnableDraw();

                    if (++_rebuildTicks > 240) _rebuildPhase = -1;
                    return;
                }

                // 必须是人类模型，否则说明游戏没换掉绘制对象 —— 不能算成功。
                if (draw->Object.GetObjectType() != SceneObjectType.CharacterBase ||
                    ((CharacterBase*)draw)->GetModelType() != CharacterBase.ModelType.Human)
                {
                    if (++_rebuildTicks > 240) _rebuildPhase = -1;
                    return;
                }

                log.Debug("[SmartNpc] 绘制对象重建完成。");
                _rebuildPhase = -1;
                return;
        }
    }

    /// <summary>骨架指纹：种族/性别/体型/部族。这几个变了才需要换骨架资源。</summary>
    public static int StructureKey(byte[] customize)
    {
        if (customize is null || customize.Length < 5)
            return 0;

        return customize[0] | (customize[1] << 8) | (customize[2] << 16) | (customize[4] << 24);
    }

    private static void WriteCustomize(Character* character, byte[] source)
    {
        var destination = character->DrawData.CustomizeData.Data;
        var count       = Math.Min(destination.Length, Math.Min(source.Length, CustomizeLength));

        for (var i = 0; i < count; ++i)
            destination[i] = source[i];
    }

    private static void WriteEquipment(Character* character, byte[] source)
    {
        var destination = character->DrawData.EquipmentModelIds;
        var count       = Math.Min(destination.Length, EquipmentSlots);

        for (var slot = 0; slot < count; ++slot)
        {
            var offset = slot * EquipmentSlotSize;

            // 从 default 起步，确保 padding / union 部分也是干净的 0。
            var model = default(EquipmentModelId);
            model.Id      = (ushort)(source[offset] | (source[offset + 1] << 8));
            model.Variant = source[offset + 2];
            model.Stain0  = source[offset + 3];
            model.Stain1  = source[offset + 4];

            destination[slot] = model;
        }
    }

    /// <summary>从本地玩家"照抄"一套外观——最省心的默认来源。</summary>
    public static (byte[] Customize, byte[] Equipment) CopyFromPlayer(Character* player)
    {
        var customize = new byte[CustomizeLength];
        var equipment = new byte[EquipmentLength];

        var source = player->DrawData.CustomizeData.Data;
        var count  = Math.Min(source.Length, CustomizeLength);
        for (var i = 0; i < count; ++i)
            customize[i] = source[i];

        var gear = player->DrawData.EquipmentModelIds;
        for (var slot = 0; slot < Math.Min(gear.Length, EquipmentSlots); ++slot)
        {
            var model  = gear[slot];
            var offset = slot * EquipmentSlotSize;

            equipment[offset]     = (byte)(model.Id & 0xFF);
            equipment[offset + 1] = (byte)((model.Id >> 8) & 0xFF);
            equipment[offset + 2] = model.Variant;
            equipment[offset + 3] = model.Stain0;
            equipment[offset + 4] = model.Stain1;
        }

        return (customize, equipment);
    }
}