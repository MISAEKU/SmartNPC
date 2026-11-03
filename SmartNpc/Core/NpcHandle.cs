using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Common.Math;

namespace SmartNpc.Core;

/// <summary>
/// 一个 NPC 的身份与运行时状态。
///
/// 全部是<b>托管</b>的：绝不持有原生指针的所有权，只记录地址用于每帧访问。
/// 原生对象的生灭由 <see cref="NpcFactory"/> 通过游戏自己的接口负责。
/// </summary>
public sealed class NpcHandle(ushort objectIndex, nint address, uint entityId, string gameAssignedName = "")
{
    /// <summary>对象表索引。销毁时按它调用 DeleteObjectByIndex。</summary>
    public ushort ObjectIndex { get; } = objectIndex;

    /// <summary>GameObject* 的地址（仅用于访问，不拥有它）。</summary>
    public nint Address { get; private set; } = address;

    /// <summary>实体 ID。换图后地址会变，用它确认"还是同一个 NPC"。</summary>
    public uint EntityId { get; private set; } = entityId;

    /// <summary>v0.18.45：游戏创建她时分配的名字（召唤瞬间抓拍）。
    /// 配置名留空时，显示名回退到它 —— 也就是"未命名时显示默认 ID"。</summary>
    public string GameAssignedName { get; } = gameAssignedName ?? string.Empty;

    public bool IsAlive => Address != nint.Zero;

    /// <summary>目标朝向（弧度）。跟随逻辑每帧更新它，实际朝向做平滑逼近。</summary>
    public float DesiredRotation { get; set; }

    /// <summary>当前所在区域。换区后需要重新确认对象是否还有效。</summary>
    public uint TerritoryId { get; set; }

    /// <summary>当前待播放的动作是否还在跑。</summary>
    public bool IsPlayingAction { get; set; }

    /// <summary>距离玩家过远时需要瞬移追上（不用平滑，否则会追不上）。</summary>
    public bool NeedsTeleport { get; set; }

    /// <summary>外貌指纹：换装时判断"骨架有没有变"，决定是刷新还是重建。</summary>
    public int StructureKey { get; set; }

    /// <summary>对象地址失效（换区/ 被销毁）。</summary>
    public void Invalidate() => Address = nint.Zero;

    public override string ToString() => $"NPC#{ObjectIndex}(0x{Address:X})";
}