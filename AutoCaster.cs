namespace AutoCastSpell;

public class AutoCaster : MonoBehaviour
{
    // ============================================================
    //  Fields & Config
    // ============================================================

    private static readonly Dictionary<int, string> ModeNames = new()
    {
        { 1, "Cast all" },
        { 2, "Cheapest cost first" },
        { 3, "Shortest CD first" },
        { 4, "Buff Sync" }
    };

    public static bool GlobalEnabled = true;

    private int mode;
    private float delay;
    private float previewTime = 10f;

    // --- State tracking ---
    private readonly Dictionary<Guid, float> _lastCastTime = new();
    private readonly Dictionary<Guid, float> _cooldownStartTime = new();
    private readonly HashSet<Guid> _activeToggledBuffs = [];
    private readonly HashSet<Guid> _wasActiveLastTick = [];

    // --- Buff Sync state ---
    private bool _syncing; // 正在逐个释放同批 buff
    private static AutoCaster _instance;

    // ============================================================
    //  Unity Lifecycle
    // ============================================================

    private void Awake() => _instance = this;

    public void Update()
    {
        if (SceneManager.GetActiveScene().name != "Main") return;

        RefreshState();

        if (Plugin.Keybind.Of(Plugin.Instance.CycleAutoCastModeKeybind.Value).IsPressed())
        { mode = (mode + 1) % (ModeNames.Count + 1); delay = 5f; _syncing = false; }

        if (Plugin.Keybind.Of(Plugin.Instance.CycleAutoCastModeReverseKeybind.Value).IsPressed())
        { mode = (mode + ModeNames.Count) % (ModeNames.Count + 1); delay = 5f; _syncing = false; }

        if (previewTime > 0) previewTime -= Time.deltaTime;
        if (mode == 0 || !GlobalEnabled) return;
        if (delay > 0) { delay -= Time.deltaTime; return; }

        OnTick();
    }

    // ============================================================
    //  State Management
    // ============================================================

    /// <summary>每帧刷新：清理过期记录、检测冷却开始</summary>
    private void RefreshState()
    {
        var spells = SpellManager.instance?.activeSpells;
        if (spells == null) return;

        var activeIds = new HashSet<Guid>();
        for (var i = 0; i < spells.Count; i++)
        {
            var s = spells.Get(i);
            if (s == null || s.IsEmpty()) continue;
            var id = s.GetId();
            activeIds.Add(id);

            // Toggled buff 关闭检测
            if (s.IsToggledSpell())
            {
                if (s.IsCasting())
                    _activeToggledBuffs.Add(id);
                else
                    _activeToggledBuffs.Remove(id);
            }

            // 冷却开始检测（仅 toggled buff）
            if (!s.IsDurationSpell() || !s.IsToggledSpell()) continue;
            var nowActive = s.IsCasting();
            if (_wasActiveLastTick.Contains(id) && !nowActive && !s.CanFire())
                _cooldownStartTime[id] = Time.time;
            if (nowActive) _wasActiveLastTick.Add(id);
            else _wasActiveLastTick.Remove(id);
        }

        // 清理已移出 loadout 的记录
        RemoveStale(_lastCastTime, activeIds);
        RemoveStale(_cooldownStartTime, activeIds);
        _wasActiveLastTick.RemoveWhere(k => !activeIds.Contains(k));
        _activeToggledBuffs.RemoveWhere(k => !activeIds.Contains(k));
    }

    private static void RemoveStale(Dictionary<Guid, float> dict, HashSet<Guid> keep)
    {
        foreach (var k in dict.Keys.Where(k => !keep.Contains(k)).ToList())
            dict.Remove(k);
    }

    /// <summary>排队施法并记录状态</summary>
    private static void QueueSpell(Spell spell)
    {
        if (spell == null || spell.IsEmpty()) return;
        _instance._lastCastTime[spell.GetId()] = Time.time;
        _instance._cooldownStartTime.Remove(spell.GetId());
        if (spell.IsToggledSpell())
            _instance._activeToggledBuffs.Add(spell.GetId());
        SpellManager.QueueSpell(spell);
    }

    // ============================================================
    //  Spell Queries
    // ============================================================

    /// <summary>该魔法是否应参与 Buff Sync 管理（可开关的 toggled buff）</summary>
    private static bool IsManagedBuff(Spell s)
        => s.IsDurationSpell() && s.IsToggledSpell();

    /// <summary>buff 是否已开启（Aura/Channel 正在施法中）</summary>
    private static bool IsBuffActive(Spell s)
        => s.IsCasting();

    /// <summary>buff 是否可释放（冷却完毕 + 未打开 + 未被锁定）</summary>
    private static bool CanCastBuff(Spell s)
        => s.CanFire() && !s.IsCasting() && !_instance._activeToggledBuffs.Contains(s.GetId());

    /// <summary>是否有 Channeled 魔法正在引导</summary>
    private static bool IsAnyChanneling(SpellListVariable spells)
    {
        for (var i = 0; i < spells.Count; i++)
        {
            var s = spells.Get(i);
            if (s != null && !s.IsEmpty() && s.IsChanneled() && s.IsCasting())
                return true;
        }
        return false;
    }

    /// <summary>收集可施放的普通魔法（排除 excluded + 已激活 toggled）</summary>
    private static List<Spell> CollectReadySpells(SpellListVariable spells)
    {
        var list = new List<Spell>();
        for (var i = 0; i < spells.Count; i++)
        {
            var s = spells.Get(i);
            if (s == null || s.IsEmpty()) continue;
            if (Plugin.IsSpellExcluded(s)) continue;
            if (!s.CanFire()) continue;
            if (s.IsCasting() || s.IsReadyingCast()) continue;
            if (s.IsToggledSpell() && _instance._activeToggledBuffs.Contains(s.GetId())) continue;
            list.Add(s);
        }
        return list;
    }

    /// <summary>Channeled 排最后</summary>
    private static int ByChanneledLast(Spell a, Spell b)
    {
        if (a.IsChanneled() == b.IsChanneled()) return 0;
        return a.IsChanneled() ? 1 : -1;
    }

    // ============================================================
    //  Main Tick
    // ============================================================

    private void OnTick()
    {
        if (!SpellManager.CanCastASpell()) return;
        var spells = SpellManager.instance?.activeSpells;
        if (spells == null) return;

        // Channeled 引导中：阻塞所有自动施法
        if (IsAnyChanneling(spells))
            return;

        if (mode == 4)
            TickBuffSync(spells);
        else
            TickNormal(spells);
    }

    // ============================================================
    //  Normal Modes (1 / 2 / 3)
    // ============================================================

    private void TickNormal(SpellListVariable spells)
    {
        var candidates = CollectReadySpells(spells);
        if (candidates.Count == 0) return;
        SortCandidates(candidates, mode);
        QueueSpell(candidates[0]);
    }

    private static void SortCandidates(List<Spell> list, int mode)
    {
        if (mode == 2) list.Sort((a, b) => MaxCostRatio(a).CompareTo(MaxCostRatio(b)));
        if (mode == 3) list.Sort((a, b) => a.GetCooldownTime().CompareTo(b.GetCooldownTime()));
        list.Sort(ByChanneledLast);
    }

    // ============================================================
    //  Buff Sync Mode (4)
    // ============================================================

    private void TickBuffSync(SpellListVariable spells)
    {
        // --- 收集 managed buffs（可开关的 toggled 型） ---
        var managedBuffs = new List<Spell>();
        for (var i = 0; i < spells.Count; i++)
        {
            var s = spells.Get(i);
            if (s == null || s.IsEmpty() || Plugin.IsSpellExcluded(s) || !IsManagedBuff(s)) continue;
            if (!s.CanFire() && !s.IsCasting() && !_lastCastTime.ContainsKey(s.GetId())) continue;
            managedBuffs.Add(s);
        }

        // 排序：持续时间最长的优先，Channeled 排同组最后
        var readyBuffs = managedBuffs.Where(CanCastBuff).ToList();
        readyBuffs.Sort((a, b) =>
        {
            var durCmp = b.GetCooldownTime().CompareTo(a.GetCooldownTime()); // 长的优先
            if (durCmp != 0) return durCmp;
            return ByChanneledLast(a, b);
        });

        var activeCount = managedBuffs.Count(IsBuffActive);
        var missingCount = managedBuffs.Count - activeCount;

        var otherSpells = CollectReadySpells(spells).Where(s => !IsManagedBuff(s)).ToList();

        // --- 逐个释放续接 ---
        if (_syncing)
        {
            if (readyBuffs.Count > 0)
            {
                QueueSpell(readyBuffs[0]);
                return;
            }
            _syncing = false;
            return;
        }

        // --- Buff 决策 ---
        if (readyBuffs.Count > 0)
        {
            if (readyBuffs.Count == managedBuffs.Count)
            {
                _syncing = true;
                QueueSpell(readyBuffs[0]);
            }
            else if (missingCount == 1)
            {
                QueueSpell(readyBuffs[0]);
            }
            return;
        }

        // --- 非 Buff 决策：仅在无 Channeled buff 可用时才考虑 ---
        var hasChanneledBuff = managedBuffs.Any(b => b.IsChanneled());
        var channeledReady = managedBuffs.Any(b => b.IsChanneled() && CanCastBuff(b));
        if (hasChanneledBuff && !channeledReady && managedBuffs.Count > 0)
            return; // Channeled buff 不可用 → 等它

        if (otherSpells.Count == 0) return;
        if (managedBuffs.Count == 0 || missingCount == 0)
        {
            SortCandidates(otherSpells, 2);
            QueueSpell(otherSpells[0]);
            return;
        }

        if (ShouldReleaseDamageAnyway(managedBuffs, otherSpells))
        {
            SortCandidates(otherSpells, 2);
            QueueSpell(otherSpells[0]);
        }
    }

    /// <summary>判断是否"等太久不值得"，直接释放伤害</summary>
    private bool ShouldReleaseDamageAnyway(List<Spell> buffs, List<Spell> dmgList)
    {
        var now = Time.time;
        BigDouble maxRemaining = -1;

        foreach (var b in buffs)
        {
            if (CanCastBuff(b) || IsBuffActive(b)) continue;
            BigDouble remaining;
            if (_cooldownStartTime.TryGetValue(b.GetId(), out var cdStart))
                remaining = b.GetCooldownTime() - (BigDouble)(now - cdStart);
            else if (_lastCastTime.TryGetValue(b.GetId(), out var lastT))
                remaining = b.GetCooldownTime() * 2 - (BigDouble)(now - lastT);
            else continue;
            if (remaining < 0) remaining = 0;
            if (remaining > maxRemaining) maxRemaining = remaining;
        }

        if (maxRemaining < 0) return true; // 无法计算 → 直接放

        SortCandidates(dmgList, 2);
        return maxRemaining > dmgList[0].GetCooldownTime();
    }

    // ============================================================
    //  Sort Helpers
    // ============================================================

    private static BigDouble MaxCostRatio(Spell spell)
    {
        BigDouble max = 0;
        foreach (var e in spell.GetUsageCost().GetEntries())
        {
            if (e.resource.quantity <= 0) { max = BigDouble.MaxValue; break; }
            var r = e.GetValue() / e.resource.quantity;
            if (r > max) max = r;
        }
        return max;
    }

    // ============================================================
    //  OnGUI
    // ============================================================

    public void OnGUI()
    {
        if (SceneManager.GetActiveScene().name != "Main") return;
        if (mode == 0 && previewTime <= 0) return;

        var (w, h) = (Screen.width, Screen.height);

        var opacity = 1f;
        if (mode == 0 && previewTime < 5) opacity = previewTime / 5f;

        string text;
        if (!GlobalEnabled)
            text = "AutoCastSpell: OFF";
        else if (mode == 0)
            text = $"AutoCastSpell loaded! {Plugin.Instance.CycleAutoCastModeKeybind.Value}/{Plugin.Instance.CycleAutoCastModeReverseKeybind.Value} to cycle | {Plugin.Instance.ToggleExcludeSpellKeybind.Value} to exclude | F2 to toggle";
        else
            text = $"AutoCastSpell: {ModeNames[mode]}{(delay > 0 ? $" [{delay:F1}]" : "")}";

        var style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 18,
            fontStyle = FontStyle.Bold
        };
        style.normal.textColor = new Color(1, 1, 1, opacity);

        var outline = new GUIStyle(style) { normal = { textColor = new Color(0, 0, 0, opacity) } };
        Vector2[] off = { new(-1, -1), new(-1, 0), new(-1, 1), new(0, -1), new(0, 1), new(1, -1), new(1, 0), new(1, 1) };
        var rect = mode == 0
            ? new Rect(0, h - 28, w, 28)
            : new Rect(w * 0.5f, h - 28, w * 0.48f, 28);
        foreach (var o in off) GUI.Label(new Rect(rect.x + o.x, rect.y + o.y, rect.width, rect.height), text, outline);
        GUI.Label(rect, text, style);
    }
}
