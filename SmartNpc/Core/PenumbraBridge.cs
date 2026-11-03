using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace SmartNpc.Core;

/// <summary>
/// Penumbra 集成层（v0.18.55）—— 给她挂「个人分组」（collection）。
///
/// <para><b>为什么直接引用 Penumbra.Api.dll，而不是像 GlamourerBridge 那样手写通道名：</b>
/// Dalamud IPC 的订阅端泛型类型必须与提供端<b>完全一致</b>。
/// Glamourer 那几条读接口的签名全是 System 类型（Dictionary&lt;Guid,string&gt;），手写没问题；
/// 而 Penumbra 所有会改状态的接口返回值里都带自定义枚举 <c>PenumbraApiEc</c>
/// —— 手写的枚举是两个不同的类型标识，运行时直接 InvalidCastException。
/// 所以这条路必须引用官方 Api 程序集（本机 Penumbra 1.7.2.3 的 Api 引用
/// Dalamud 15.0.3.5，与本项目编译用的 15.0.3.9 同为 API15，兼容）。</para>
///
/// <para><b>用法：</b>
/// 1. 用户先在 Penumbra 里建好（或挑好）一个 collection，把想要的 mod 放进去；
/// 2. 面板下拉框选它 → 记进配置；
/// 3. 召唤时在模型准备<b>之前</b>调用 <see cref="AssignToObject"/> —— 她的资源从第一次加载
///    起就按该分组解析，不需要补 redraw；
/// 4. 送走时 <see cref="ClearObject"/> 解绑（趁对象还在对象表里）。</para>
///
/// <para><b>通道与签名</b>（本机 Penumbra.Api.dll 1.7.2.3 逐一解码核实）：</para>
/// <list type="bullet">
/// <item><c>Penumbra.GetCollections</c> → <c>Dictionary&lt;Guid, string&gt; ()</c>（id → 名字）</item>
/// <item><c>Penumbra.SetCollectionForObject</c> →
///   <c>(PenumbraApiEc, (Guid, string)?) (int 对象表索引, Guid? collectionId, bool allowCreateNew, bool allowDelete)</c>
///   —— 文档原话：索引即"对象表里的索引"；collectionId 传 null 即解绑</item>
/// <item><c>Penumbra.GetCollectionForObject</c> → <c>(bool, bool, (Guid, string)) (int)</c></item>
/// <item><c>Penumbra.RedrawObject</c> → <c>void (int, RedrawType)</c></item>
/// </list>
///
/// <para>只调用、不注册：对 Penumbra 来说我们只是「另一个挂 collection 的客户端」，
/// 它的对象面板里会把她显示成一条个人指派 —— 这正是这个功能的目的。</para>
/// </summary>
public sealed class PenumbraBridge
{
    private readonly IPluginLog _log;

    private readonly GetCollections?        _getCollections;
    private readonly SetCollectionForObject _setForObject;
    private readonly GetCollectionForObject _getForObject;
    private readonly RedrawObject?          _redrawObject;

    private List<(Guid Id, string Name)> _collectionCache = [];
    private DateTime                     _cacheTime;
    private bool?                        _availableCache;
    private DateTime                     _availableCacheTime;

    public PenumbraBridge(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log = log;

        // 构造只存引用，永远不会抛（真正会抛的是 Invoke —— Penumbra 没开时）。
        _getCollections  = new GetCollections(pi);
        _setForObject    = new SetCollectionForObject(pi);
        _getForObject    = new GetCollectionForObject(pi);
        _redrawObject    = new RedrawObject(pi);
    }

    /// <summary>最近一次操作的结果描述（UI 显示用）。</summary>
    public string LastStatus { get; private set; } = string.Empty;

    /// <summary>最近一次失败的直接原因（UI 显示用）。成功时清空。</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>Penumbra 是否在运行且 IPC 可用（缓存 5 秒，UI 可放心每帧调用）。</summary>
    public bool IsPenumbraRunning
    {
        get
        {
            if (_availableCache.HasValue && (DateTime.UtcNow - _availableCacheTime).TotalSeconds < 5)
                return _availableCache.Value;

            bool running;
            try
            {
                _ = _getCollections!.Invoke();
                running   = true;
                LastError = string.Empty;
            }
            catch (Exception ex)
            {
                running   = false;
                LastError = $"Penumbra IPC 不可用：{ex.GetBaseException().Message}";
            }

            _availableCache     = running;
            _availableCacheTime = DateTime.UtcNow;
            return running;
        }
    }

    /// <summary>所有 collection（缓存 10 秒，避免 UI 每帧打 IPC）。按名字排序。</summary>
    public List<(Guid Id, string Name)> GetCollections()
    {
        if (_collectionCache.Count > 0 && (DateTime.UtcNow - _cacheTime).TotalSeconds < 10)
            return _collectionCache;

        try
        {
            var raw = _getCollections!.Invoke();
            _collectionCache = raw.Select(kv => (kv.Key, kv.Value))
                                  .OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase)
                                  .ToList();
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            // Penumbra 没开 —— 给空列表，UI 显示提示。
            _log.Debug(ex, "[SmartNpc] Penumbra GetCollections 失败。");
            if (_collectionCache.Count == 0)
                LastError = $"Penumbra IPC 不可用：{ex.GetBaseException().Message}";
        }

        _cacheTime = DateTime.UtcNow;
        return _collectionCache;
    }

    /// <summary>
    /// 把一个 collection 挂到指定对象表索引上。
    /// allowCreateNew=true（首次指派要新建个体条目）、allowDelete=false（这里绝不删）。
    /// </summary>
    public (bool Ok, string Message) AssignToObject(int objectIndex, Guid collectionId)
    {
        try
        {
            var (ec, previous) = _setForObject.Invoke(objectIndex, collectionId, true, false);
            var ok  = ec == PenumbraApiEc.Success || ec == PenumbraApiEc.NothingChanged;
            var msg = ec == PenumbraApiEc.Success
                ? $"已挂上（原先：{Describe(previous)}）"
                : ec == PenumbraApiEc.NothingChanged
                    ? "本来就是这个分组"
                    : $"失败：{ec}";
            LastStatus = $"索引 {objectIndex}：{msg}";
            LastError  = ok ? string.Empty : msg;
            _log.Information("[SmartNpc] Penumbra 挂分组到索引 {0} → {1}（ec={2}）", objectIndex, msg, ec);
            return (ok, msg);
        }
        catch (Exception ex)
        {
            var msg = $"Penumbra 调用失败：{ex.GetBaseException().Message}";
            LastError = msg;
            _log.Warning(ex, "[SmartNpc] {0}", msg);
            return (false, msg);
        }
    }

    /// <summary>解绑（collectionId 传 null 即移除个人指派）。趁对象还在时调用。</summary>
    public (bool Ok, string Message) ClearObject(int objectIndex)
    {
        try
        {
            var (ec, _) = _setForObject.Invoke(objectIndex, null, false, true);
            var ok  = ec == PenumbraApiEc.Success || ec == PenumbraApiEc.NothingChanged;
            var msg = ok ? "已解绑" : $"解绑失败：{ec}";
            LastStatus = $"索引 {objectIndex}：{msg}";
            if (ok) LastError = string.Empty;
            _log.Information("[SmartNpc] Penumbra 解绑索引 {0}（ec={1}）", objectIndex, ec);
            return (ok, msg);
        }
        catch (Exception ex)
        {
            var msg = $"Penumbra 解绑失败：{ex.GetBaseException().Message}";
            LastError = msg;
            _log.Debug(ex, "[SmartNpc] {0}", msg);
            return (false, msg);
        }
    }

    /// <summary>她当前挂的分组名（返回 null = 查不到/没挂）。UI 状态行用。</summary>
    public string? GetAssignedName(int objectIndex)
    {
        try
        {
            var (_, _, current) = _getForObject.Invoke(objectIndex);
            return current.Item2.Length > 0 ? current.Item2 : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>补一个 redraw（面板上换分组、她已经在场时用 —— 召唤路径不需要）。</summary>
    public void Redraw(int objectIndex)
    {
        try
        {
            _redrawObject?.Invoke(objectIndex, RedrawType.Redraw);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[SmartNpc] Penumbra redraw 失败（不影响挂载结果）。");
        }
    }

    private static string Describe((Guid, string)? previous)
        => previous is { } p && !string.IsNullOrEmpty(p.Item2) ? p.Item2 : "无";
}
