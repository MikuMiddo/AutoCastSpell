namespace AutoCastSpell;

public class AutoCaster : MonoBehaviour
{
    private enum SyncPhase { Idle, ReleasingBuffs }

    private static readonly Dictionary<int, string> ModeNames = new()
    {
        { 1, "Cast all" },
        { 2, "Cheapest cost first" },
        { 3, "Shortest CD first" },
        { 4, "Buff Sync" }
    };

    /// <summary>全局自动施法开关（由快捷键 F2 切换）</summary>
    public static bool GlobalEnabled = true;

    private int mode;
    private float delay;
    private float previewTime = 10f;

    // --- Buff Sync 模式内部状态 ---
    private SyncPhase syncPhase;
    private List<Spell> pendingBuffReleases = [];
    private int buffReleaseIndex;
    private readonly Dictionary<Guid, float> lastCastTime = new(); // 所有魔法的最后施法时间
    private static readonly HashSet<Guid> ToggledActiveSpells = []; // 已激活的 buff 魔法（防重复触发取消）
    private readonly Dictionary<Guid, float> cooldownStartTime = new(); // 冷却实际开始时间
    private readonly HashSet<Guid> wasActiveLastTick = []; // 上一帧是否活跃
    private static AutoCaster _instance;

    private void Awake()
    {
        _instance = this;
    }

    public void Update()
    {
        if (SceneManager.GetActiveScene().name != "Main") return;

        // 清理已被移出 loadout 的魔法记录
        var activeIds = new HashSet<Guid>();
        var spellsRef = SpellManager.instance?.activeSpells;
        if (spellsRef != null)
            for (var i = 0; i < spellsRef.Count; i++)
            {
                var s = spellsRef.Get(i);
                if (s != null && !s.IsEmpty()) activeIds.Add(s.GetId());
            }
        CleanStaleRecords(activeIds);

        // 清理已关闭的 toggled spells（不再 casting = 已取消/到期）
        ToggledActiveSpells.RemoveWhere(gid =>
        {
            for (var i = 0; i < spellsRef?.Count; i++)
            {
                var s = spellsRef.Get(i);
                if (s != null && s.GetId() == gid)
                    return !s.IsCasting(); // 不再 casting → toggled 已结束
            }
            return true;
        });

        // 检测 buff 过期 → 记录冷却实际开始时间
        if (spellsRef != null)
        {
            for (var i = 0; i < spellsRef.Count; i++)
            {
                var s = spellsRef.Get(i);
                if (s == null || s.IsEmpty() || !s.IsDurationSpell() || !s.IsToggledSpell()) continue;
                var id = s.GetId();
                var isActive = IsBuffActuallyActive(s);
                if (wasActiveLastTick.Contains(id) && !isActive && !s.CanFire())
                    cooldownStartTime[id] = Time.time;
                if (isActive)
                    wasActiveLastTick.Add(id);
                else
                    wasActiveLastTick.Remove(id);
            }
        }

        if (Plugin.Keybind.Of(Plugin.Instance.CycleAutoCastModeKeybind.Value).IsPressed())
        {
            mode = (mode + 1) % (ModeNames.Count + 1);
            delay = 5f;
            syncPhase = SyncPhase.Idle;
            pendingBuffReleases.Clear();
        }

        if (Plugin.Keybind.Of(Plugin.Instance.CycleAutoCastModeReverseKeybind.Value).IsPressed())
        {
            mode = (mode + ModeNames.Count) % (ModeNames.Count + 1);
            delay = 5f;
            syncPhase = SyncPhase.Idle;
            pendingBuffReleases.Clear();
        }

        if (previewTime > 0)
            previewTime -= Time.deltaTime;

        if (mode == 0 || !GlobalEnabled) return;
        if (delay > 0)
        {
            delay -= Time.deltaTime;
            return;
        }

        AutoCastTick();
    }

    private void AutoCastTick()
    {
        if (!SpellManager.CanCastASpell()) return;

        var activeSpells = SpellManager.instance?.activeSpells;
        if (activeSpells == null) return;

        // 有 Channeled 魔法正在引导中 → 不释放任何魔法，等引导结束
        for (var i = 0; i < activeSpells.Count; i++)
        {
            var s = activeSpells.Get(i);
            if (s != null && !s.IsEmpty() && s.IsChanneled() && s.IsCasting())
                return;
        }

        if (mode == 4)
            AutoCastTick_BuffSync(activeSpells);
        else
            AutoCastTick_Normal(activeSpells);
    }

    // ====== 普通模式（1/2/3）======

    /// <summary>排队释放并追踪 toggled 状态和时间戳</summary>
    private static void QueueSpellInternal(Spell spell)
    {
        if (spell == null || spell.IsEmpty()) return;
        if (spell.IsToggledSpell())
            ToggledActiveSpells.Add(spell.GetId());
        _instance.lastCastTime[spell.GetId()] = Time.time;
        _instance.cooldownStartTime.Remove(spell.GetId()); // 新施放 → 清除旧冷却记录
        SpellManager.QueueSpell(spell);
    }

    private void AutoCastTick_Normal(SpellListVariable activeSpells)
    {
        var candidates = CollectCandidates(activeSpells);
        if (candidates.Count == 0) return;

        switch (mode)
        {
            case 2: candidates.Sort(CompareByCost); break;
            case 3: candidates.Sort(CompareByCooldown); break;
        }
        candidates.Sort(CompareByChanneled);

        QueueSpellInternal(candidates[0]);
    }

    /// <summary>Channeled 魔法排在最后</summary>
    private static int CompareByChanneled(Spell a, Spell b)
    {
        if (a.IsChanneled() == b.IsChanneled()) return 0;
        return a.IsChanneled() ? 1 : -1;
    }

    // ====== Buff Sync 模式（4）======

    private void AutoCastTick_BuffSync(SpellListVariable activeSpells)
    {
        // 如果正在批量释放 buff，继续释放下一个
        if (syncPhase == SyncPhase.ReleasingBuffs)
        {
            if (buffReleaseIndex < pendingBuffReleases.Count)
            {
                var next = pendingBuffReleases[buffReleaseIndex];
                if (next != null && !next.IsEmpty() && IsBuffReadyForSync(next))
                    QueueSpellInternal(next);
                buffReleaseIndex++;
                return;
            }
            syncPhase = SyncPhase.Idle;
            pendingBuffReleases.Clear();
            return;
        }

        // 收集所有 buff 魔法（排除 excluded 和永久锁死的）
        var allBuffSpells = new List<Spell>();
        for (var i = 0; i < activeSpells.Count; i++)
        {
            var s = activeSpells.Get(i);
            if (s == null || s.IsEmpty() || Plugin.IsSpellExcluded(s) || !s.IsDurationSpell() || !s.IsToggledSpell()) continue;
            // 跳过永久锁死的 toggled buff：从未施放过 + CanFire=false + 不在施法中 = 无法开启
            if (s.IsToggledSpell() && !s.CanFire() && !s.IsCasting() && !lastCastTime.ContainsKey(s.GetId()))
                continue;
            allBuffSpells.Add(s);
        }

        var readyBuffs = allBuffSpells.Where(IsBuffReadyForSync).ToList();
        var trulyActiveCount = allBuffSpells.Count(IsBuffActuallyActive);
        var missingCount = allBuffSpells.Count - trulyActiveCount;

        // --- Buff 魔法决策 ---
        if (readyBuffs.Count > 0)
        {
            if (readyBuffs.Count == allBuffSpells.Count)
            {
                // 全部就绪 → 批量一起释放
                pendingBuffReleases = new List<Spell>(readyBuffs);
                syncPhase = SyncPhase.ReleasingBuffs;
                buffReleaseIndex = 0;
                if (buffReleaseIndex < pendingBuffReleases.Count)
                {
                    var next = pendingBuffReleases[buffReleaseIndex];
                    if (next != null && !next.IsEmpty() && IsBuffReadyForSync(next))
                        QueueSpellInternal(next);
                    buffReleaseIndex++;
                }
            }
            else if (missingCount == 1)
            {
                // 仅缺失 1 个 buff（其余全部真正活跃）→ 补齐它
                QueueSpellInternal(readyBuffs[0]);
            }
            // missingCount > 1 → 缺失多个，等待同步
            return;
        }

        // --- 非 Buff 魔法决策 ---
        var dmgCandidates = CollectCandidates(activeSpells)
            .Where(s => !s.IsDurationSpell() || !s.IsToggledSpell()).ToList();
        if (dmgCandidates.Count == 0) return;

        if (allBuffSpells.Count == 0)
        {
            dmgCandidates.Sort(CompareByCost);
            dmgCandidates.Sort(CompareByChanneled);
            QueueSpellInternal(dmgCandidates[0]);
            return;
        }

        if (missingCount == 0)
        {
            // 所有 buff 真正活跃 → 放伤害
            dmgCandidates.Sort(CompareByCost);
            dmgCandidates.Sort(CompareByChanneled);
            QueueSpellInternal(dmgCandidates[0]);
            return;
        }

        // 有 buff 缺失 → 比较等待时间 vs 伤害冷却
        var now = Time.time;
        BigDouble maxRemainingCd = -1;
        foreach (var b in allBuffSpells)
        {
            if (IsBuffReadyForSync(b) || IsBuffActuallyActive(b)) continue;
            BigDouble remaining;
            if (cooldownStartTime.TryGetValue(b.GetId(), out var cdStart))
                remaining = b.GetCooldownTime() - (BigDouble)(now - cdStart); // 精确计算
            else if (lastCastTime.TryGetValue(b.GetId(), out var lastT))
                remaining = b.GetCooldownTime() * 2 - (BigDouble)(now - lastT); // 估算
            else continue;
            if (remaining < 0) remaining = 0;
            if (remaining > maxRemainingCd) maxRemainingCd = remaining;
        }

        dmgCandidates.Sort(CompareByCost);
        dmgCandidates.Sort(CompareByChanneled);
        var bestDmg = dmgCandidates[0];
        if (maxRemainingCd < 0 || maxRemainingCd > bestDmg.GetCooldownTime())
            QueueSpellInternal(bestDmg);
        // 否则等待 buff 就绪
    }

    /// <summary>判断 buff 魔法是否可释放（仅用于 toggled 型 buff）</summary>
    private static bool IsBuffReadyForSync(Spell s)
    {
        return s.CanFire() && !s.IsCasting() && !ToggledActiveSpells.Contains(s.GetId());
    }

    /// <summary>判断 toggled buff 是否真正活跃</summary>
    private bool IsBuffActuallyActive(Spell s)
    {
        return s.IsCasting();
    }

    /// <summary>清理已被移出 loadout 的魔法的追踪数据</summary>
    private void CleanStaleRecords(HashSet<Guid> activeIds)
    {
        foreach (var k in lastCastTime.Keys.Where(k => !activeIds.Contains(k)).ToList())
            lastCastTime.Remove(k);
        foreach (var k in cooldownStartTime.Keys.Where(k => !activeIds.Contains(k)).ToList())
            cooldownStartTime.Remove(k);
        wasActiveLastTick.RemoveWhere(k => !activeIds.Contains(k));
        ToggledActiveSpells.RemoveWhere(k => !activeIds.Contains(k));
    }

    // ====== 通用方法 ======

    /// <summary>收集所有可施放的魔法（排除施法中、已激活 toggled）</summary>
    private static List<Spell> CollectCandidates(SpellListVariable activeSpells)
    {
        var candidates = new List<Spell>();
        for (var i = 0; i < activeSpells.Count; i++)
        {
            var spell = activeSpells.Get(i);
            if (spell == null || spell.IsEmpty()) continue;
            if (Plugin.IsSpellExcluded(spell)) continue;
            if (!spell.CanFire()) continue;
            if (spell.IsCasting() || spell.IsReadyingCast()) continue;
            // 跳过已激活的 toggled 魔法（Aura 类：再次触发会取消）
            if (spell.IsToggledSpell() && ToggledActiveSpells.Contains(spell.GetId())) continue;
            candidates.Add(spell);
        }
        return candidates;
    }

    private static int CompareByCost(Spell a, Spell b)
    {
        return GetMaxCostRatio(a).CompareTo(GetMaxCostRatio(b));
    }

    private static BigDouble GetMaxCostRatio(Spell spell)
    {
        BigDouble maxRatio = 0;
        foreach (var entry in spell.GetUsageCost().GetEntries())
        {
            var cost = entry.GetValue();
            var available = entry.resource.quantity;
            if (available <= 0)
            {
                maxRatio = BigDouble.MaxValue;
                break;
            }
            var ratio = cost / available;
            if (ratio > maxRatio) maxRatio = ratio;
        }
        return maxRatio;
    }

    private static int CompareByCooldown(Spell a, Spell b)
    {
        return a.GetCooldownTime().CompareTo(b.GetCooldownTime());
    }

    // ====== OnGUI ======

    public void OnGUI()
    {
        if (SceneManager.GetActiveScene().name != "Main") return;
        if (mode == 0 && previewTime <= 0) return;

        var (w, h) = (Screen.width, Screen.height);

        // 渐变透明度（和 AutobuyOrb 一致）
        var opacity = 1f;
        if (mode == 0 && previewTime < 5)
            opacity = previewTime / 5f;

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

        // 黑色描边，和 AutobuyOrb 一样
        var outlineStyle = new GUIStyle(style) { normal = { textColor = new Color(0, 0, 0, opacity) } };
        Vector2[] off =
        [
            new(-1, -1), new(-1, 0), new(-1, 1), new(0, -1),
            new(0, 1), new(1, -1), new(1, 0), new(1, 1)
        ];
        var rect = mode == 0
            ? new Rect(0, h - 28, w, 28)           // 启动提示全宽
            : new Rect(w * 0.5f, h - 28, w * 0.48f, 28);
        foreach (var o in off)
            GUI.Label(new Rect(rect.x + o.x, rect.y + o.y, rect.width, rect.height), text, outlineStyle);
        GUI.Label(rect, text, style);
    }
}
