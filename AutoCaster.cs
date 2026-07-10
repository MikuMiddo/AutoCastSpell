namespace AutoCastSpell;

public class AutoCaster : MonoBehaviour
{
    // ============================================================
    //  Fields & Config
    // ============================================================

    private static readonly Dictionary<int, string> ModeNames = new()
    {
        { 1, "Cast all" },
        { 2, "Buff Sync" }
    };

    public static bool GlobalEnabled = true;

    private int mode;
    private float delay;
    private float previewTime = 10f;

    // --- State tracking ---
    private readonly Dictionary<Guid, float> _lastCastTime = new();
    private readonly HashSet<Guid> _activeToggledBuffs = [];
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
            mode = (mode + 1) % (ModeNames.Count + 1);

        if (Plugin.Keybind.Of(Plugin.Instance.CycleAutoCastModeReverseKeybind.Value).IsPressed())
            mode = (mode + ModeNames.Count) % (ModeNames.Count + 1);

        if (previewTime > 0) previewTime -= Time.deltaTime;
        if (mode == 0 || !GlobalEnabled) return;
        if (delay > 0) { delay -= Time.deltaTime; return; }

        OnTick();
    }

    // ============================================================
    //  State Management
    // ============================================================

    private void RefreshState()
    {
        var spells = SpellManager.instance?.activeSpells;
        if (spells == null) return;

        var activeIds = new HashSet<Guid>();
        for (var i = 0; i < spells.Count; i++)
        {
            var s = spells.Get(i);
            if (s == null || s.IsEmpty()) continue;
            activeIds.Add(s.GetId());
            if (s.IsToggledSpell())
            {
                if (s.IsCasting()) _activeToggledBuffs.Add(s.GetId());
                else _activeToggledBuffs.Remove(s.GetId());
            }
        }

        foreach (var k in _lastCastTime.Keys.Where(k => !activeIds.Contains(k)).ToList())
            _lastCastTime.Remove(k);
        _activeToggledBuffs.RemoveWhere(k => !activeIds.Contains(k));
    }

    private static void QueueSpell(Spell spell)
    {
        if (spell == null || spell.IsEmpty()) return;
        _instance._lastCastTime[spell.GetId()] = Time.time;
        SpellManager.QueueSpell(spell);
    }

    // ============================================================
    //  Spell Queries
    // ============================================================

    private static bool IsManagedBuff(Spell s)
        => s.IsDurationSpell() && s.IsToggledSpell();

    private static bool IsBuffActive(Spell s)
        => s.IsCasting();

    private static bool CanCastSpell(Spell s)
        => s.CanFire() && !s.IsCasting() && !s.IsReadyingCast()
           && !(s.IsToggledSpell() && _instance._activeToggledBuffs.Contains(s.GetId()));

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

    private static List<Spell> CollectReady(SpellListVariable spells)
    {
        var list = new List<Spell>();
        for (var i = 0; i < spells.Count; i++)
        {
            var s = spells.Get(i);
            if (s == null || s.IsEmpty()) continue;
            if (Plugin.IsSpellExcluded(s)) continue;
            if (!CanCastSpell(s)) continue;
            list.Add(s);
        }
        return list;
    }

    /// <summary>按优先级排序：0 [A] 最前 → 高优先级 → 低优先级 → Channeled 最后</summary>
    private static void SortByPriority(List<Spell> list)
    {
        list.Sort((a, b) =>
        {
            var pa = Plugin.GetPriority(a);
            var pb = Plugin.GetPriority(b);
            if (pa == 0 && pb != 0) return -1;
            if (pb == 0 && pa != 0) return 1;
            if (pa != pb) return pb.CompareTo(pa); // 高的优先
            if (a.IsChanneled() != b.IsChanneled()) return a.IsChanneled() ? 1 : -1;
            return 0;
        });
    }

    // ============================================================
    //  Main Tick
    // ============================================================

    private void OnTick()
    {
        if (!SpellManager.CanCastASpell()) return;
        var spells = SpellManager.instance?.activeSpells;
        if (spells == null) return;

        if (IsAnyChanneling(spells)) return;

        if (mode == 2) TickBuffSync(spells);
        else           TickCastAll(spells);
    }

    // ============================================================
    //  Mode 1: Cast all（按优先级释放）
    // ============================================================

    private void TickCastAll(SpellListVariable spells)
    {
        var candidates = CollectReady(spells);
        if (candidates.Count == 0) return;
        SortByPriority(candidates);
        QueueSpell(candidates[0]);
    }

    // ============================================================
    //  Mode 2: Buff Sync（优先级高的 buff 活跃才释放）
    // ============================================================

    private void TickBuffSync(SpellListVariable spells)
    {
        var candidates = CollectReady(spells);
        if (candidates.Count == 0) return;
        SortByPriority(candidates);

        foreach (var s in candidates)
        {
            var prio = Plugin.GetPriority(s);

            // 优先级 0 = 总是释放
            if (prio == 0) { QueueSpell(s); return; }

            // 检查：所有更高优先级的 managed buff 是否都已活跃
            if (HigherPriorityBuffsAllActive(spells, prio))
                { QueueSpell(s); return; }
        }
    }

    /// <summary>所有优先级 > minPrio 的 managed buff 是否都处于活跃状态</summary>
    private static bool HigherPriorityBuffsAllActive(SpellListVariable spells, int minPrio)
    {
        for (var i = 0; i < spells.Count; i++)
        {
            var s = spells.Get(i);
            if (s == null || s.IsEmpty() || Plugin.IsSpellExcluded(s)) continue;
            if (!IsManagedBuff(s)) continue;
            var p = Plugin.GetPriority(s);
            if (p > minPrio && !IsBuffActive(s))
                return false;
        }
        return true;
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
            text = $"AutoCastSpell: {ModeNames[mode]}";

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
