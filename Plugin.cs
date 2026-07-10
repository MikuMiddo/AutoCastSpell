namespace AutoCastSpell;

[BepInPlugin(PluginGuid, PluginName, PluginVer)]
[HarmonyPatch]
public class Plugin : BaseUnityPlugin
{
    public enum KeyModifier
    {
        Ctrl,
        Shift,
        Alt,
        LCtrl,
        RCtrl,
        LShift,
        RShift,
        LAlt,
        RAlt
    }

    public const string PluginGuid = "IngoH.OrbOfCreation.AutoCastSpell";
    public const string PluginName = "AutoCastSpell";
    public const string PluginVer = "1.2.0";

    internal static ManualLogSource Log;
    internal static readonly Harmony Harmony = new(PluginGuid);
    internal static string PluginPath;

    private readonly KeyCode[] modifierKeys =
    [
        KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftAlt,
        KeyCode.RightAlt
    ];

    private int _listening;

    // --- 鼠标悬停检测 ---
    internal static Spell HoveredSpell;

    // --- 被排除自动施法的魔法 GUID 集合 ---
    internal static HashSet<Guid> ExcludedSpells = [];
    internal static Dictionary<Guid, int> Priorities = new();

    // --- 设置面板窗口 ---
    private Rect _settingsWindowRect;
    private bool _settingsRectInit;

    // --- Config entries ---
    public static Plugin Instance { get; private set; }

    public ConfigEntry<string> ToggleAutoCastKeybind { get; private set; }
    public ConfigEntry<string> CycleAutoCastModeKeybind { get; private set; }
    public ConfigEntry<string> CycleAutoCastModeReverseKeybind { get; private set; }
    public ConfigEntry<string> ToggleExcludeSpellKeybind { get; private set; }
    public ConfigEntry<string> ExcludedSpellsJson { get; private set; }
    public ConfigEntry<string> PrioritiesJson { get; private set; }

    private void Awake()
    {
        Log = Logger;
        Instance = this;
        PluginPath = Path.GetDirectoryName(Info.Location);
        gameObject.AddComponent<AutoCaster>();
        DefineConfig();
        LoadExcludedSpells();
        LoadPriorities();
    }

    private void OnEnable()
    {
        Harmony.PatchAll();
        Logger.LogInfo($"Loaded {PluginName}!");
    }

    private void OnDisable()
    {
        SaveExcludedSpells();
        SavePriorities();
        Harmony.UnpatchSelf();
        Logger.LogInfo($"Unloaded {PluginName}!");
    }

    private void Update()
    {
        if (Keybind.Of(ToggleAutoCastKeybind.Value).IsPressed())
        {
            AutoCaster.GlobalEnabled = !AutoCaster.GlobalEnabled;
            Logger.LogInfo($"Auto-cast globally {(AutoCaster.GlobalEnabled ? "enabled" : "disabled")}");
        }

        if (Keybind.Of(ToggleExcludeSpellKeybind.Value).IsPressed())
        {
            ToggleHoveredSpellExclusion();
        }

        // 每帧检测鼠标悬停的魔法
        HoveredSpell = GetSpellUnderMouse();

        // 滚轮调整悬停魔法的优先级
        var scroll = Input.mouseScrollDelta.y;
        if (scroll != 0 && HoveredSpell != null && !HoveredSpell.IsEmpty())
            AdjustPriority(HoveredSpell, scroll > 0 ? 1 : -1);
    }

    /// <summary>获取鼠标悬停的魔法</summary>
    private static Spell GetSpellUnderMouse()
    {
        var eventSystem = UnityEngine.EventSystems.EventSystem.current;
        if (eventSystem == null) return null;

        var pointerData = new UnityEngine.EventSystems.PointerEventData(eventSystem)
        {
            position = Input.mousePosition
        };

        var results = new List<UnityEngine.EventSystems.RaycastResult>();
        eventSystem.RaycastAll(pointerData, results);

        foreach (var result in results)
        {
            var spellButton = result.gameObject.GetComponent<UISpellButton>();
            if (spellButton != null && spellButton.item != null && !spellButton.item.IsEmpty())
                return spellButton.item;
        }
        return null;
    }

    /// <summary>切换鼠标悬停魔法的排除状态</summary>
    private static void ToggleHoveredSpellExclusion()
    {
        var spell = HoveredSpell;
        if (spell == null || spell.IsEmpty()) return;

        var guid = spell.GetId();
        if (ExcludedSpells.Contains(guid))
        {
            ExcludedSpells.Remove(guid);
            Log.LogInfo($"Auto-cast enabled for: {spell.GetName()}");
        }
        else
        {
            ExcludedSpells.Add(guid);
            Log.LogInfo($"Auto-cast disabled for: {spell.GetName()}");
        }
        SaveExcludedSpells();
    }

    /// <summary>检查指定魔法是否被排除</summary>
    public static bool IsSpellExcluded(Spell spell)
    {
        if (spell == null || spell.IsEmpty()) return true;
        return ExcludedSpells.Contains(spell.GetId());
    }

    /// <summary>获取魔法优先级（默认 1，0 = 总是释放）</summary>
    public static int GetPriority(Spell spell)
    {
        if (spell == null || spell.IsEmpty()) return 1;
        return Priorities.TryGetValue(spell.GetId(), out var p) ? p : 1;
    }

    private static void AdjustPriority(Spell spell, int delta)
    {
        var id = spell.GetId();
        var current = GetPriority(spell);
        var next = Math.Max(0, current + delta);
        if (next == current) return;
        Priorities[id] = next;
        SavePriorities();
        Log.LogInfo($"Priority for {spell.GetName()}: {current} → {next}");
    }

    private static void SavePriorities()
    {
        var dict = Priorities.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
        Instance.PrioritiesJson.Value = JsonConvert.SerializeObject(dict);
    }

    private static void LoadPriorities()
    {
        try
        {
            var json = Instance.PrioritiesJson.Value;
            if (!string.IsNullOrEmpty(json))
            {
                var dict = JsonConvert.DeserializeObject<Dictionary<string, int>>(json);
                Priorities = dict?.ToDictionary(kv => Guid.Parse(kv.Key), kv => kv.Value) ?? new();
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed to load priorities: {ex.Message}");
            Priorities = new();
        }
    }

    private static void SaveExcludedSpells()
    {
        var json = JsonConvert.SerializeObject(ExcludedSpells.Select(g => g.ToString()).ToList());
        Instance.ExcludedSpellsJson.Value = json;
    }

    private static void LoadExcludedSpells()
    {
        try
        {
            var json = Instance.ExcludedSpellsJson.Value;
            if (!string.IsNullOrEmpty(json))
            {
                var list = JsonConvert.DeserializeObject<List<string>>(json);
                if (list != null)
                {
                    ExcludedSpells = new HashSet<Guid>(list.Select(Guid.Parse));
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed to load excluded spells: {ex.Message}");
            ExcludedSpells = [];
        }
    }

    // ====== GUI 设置面板 ======

    private void OnGUI()
    {
        if (SceneManager.GetActiveScene().name != "Start") return;

        var (w, h) = (Screen.width, Screen.height);
        if (!_settingsRectInit)
        {
            _settingsWindowRect = new Rect(w * 0.75f, h * 0.05f, w * 0.2f, h * 0.35f);
            _settingsRectInit = true;
        }

        var ratio = w / 2560f;
        GUI.skin.label.fontSize = (int)(16 * ratio);
        GUI.skin.button.fontSize = (int)(12 * ratio);
        GUI.skin.textField.fontSize = (int)(12 * ratio);
        GUI.skin.toggle.fontSize = (int)(12 * ratio);
        GUI.skin.window.fontSize = (int)(16 * ratio);

        _settingsWindowRect = GUI.Window(GetHashCode(), _settingsWindowRect, DrawSettingsWindow, "AutoCastSpell Settings");
    }

    private void DrawSettingsWindow(int windowId)
    {
        var rw = _settingsWindowRect.width;
        var ratio = Screen.width / 2560f;

        GUI.DragWindow(new Rect(0, 0, rw, 22 * ratio));

        if (_listening == 0)
        {
            var yOff = 26 * ratio;
            var lineH = 55 * ratio;

            GUI.Label(new Rect(10, yOff, rw - 20, 22 * ratio),
                $"Toggle: {ToggleAutoCastKeybind.Value}");
            if (GUI.Button(new Rect(10, yOff + 22 * ratio, rw - 20, 20 * ratio), "Set Keybind"))
                _listening = 1;
            yOff += lineH;

            GUI.Label(new Rect(10, yOff, rw - 20, 22 * ratio),
                $"Cycle: {CycleAutoCastModeKeybind.Value}");
            if (GUI.Button(new Rect(10, yOff + 22 * ratio, rw - 20, 20 * ratio), "Set Keybind"))
                _listening = 2;
            yOff += lineH;

            GUI.Label(new Rect(10, yOff, rw - 20, 22 * ratio),
                $"Cycle Rev: {CycleAutoCastModeReverseKeybind.Value}");
            if (GUI.Button(new Rect(10, yOff + 22 * ratio, rw - 20, 20 * ratio), "Set Keybind"))
                _listening = 3;
            yOff += lineH;

            GUI.Label(new Rect(10, yOff, rw - 20, 22 * ratio),
                $"Exclude: {ToggleExcludeSpellKeybind.Value}");
            if (GUI.Button(new Rect(10, yOff + 22 * ratio, rw - 20, 20 * ratio), "Set Keybind"))
                _listening = 4;
        }
        else
        {
            GUI.Label(new Rect(8, 32 * ratio, rw - 16, 20 * ratio), "Press a key...");
            var key = Event.current.keyCode;
            if (key != KeyCode.None && !modifierKeys.Contains(key))
            {
                var modifiers = new List<KeyModifier>();
                if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                    modifiers.Add(KeyModifier.Ctrl);
                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                    modifiers.Add(KeyModifier.Shift);
                if (Input.GetKey(KeyCode.LeftAlt))
                    modifiers.Add(KeyModifier.LAlt);
                if (Input.GetKey(KeyCode.RightAlt))
                    modifiers.Add(KeyModifier.RAlt);

                var newKeybind = new Keybind(key, modifiers);
                switch (_listening)
                {
                    case 1: ToggleAutoCastKeybind.Value = newKeybind; break;
                    case 2: CycleAutoCastModeKeybind.Value = newKeybind; break;
                    case 3: CycleAutoCastModeReverseKeybind.Value = newKeybind; break;
                    case 4: ToggleExcludeSpellKeybind.Value = newKeybind; break;
                }
                _listening = 0;
            }
        }
    }

    // ====== Config ======

    private void DefineConfig()
    {
        ToggleAutoCastKeybind = Config.Bind("Keybinds", "ToggleAutoCast",
            new Keybind(KeyCode.F2).ToString(),
            "Toggle auto-casting on/off globally.");
        CycleAutoCastModeKeybind = Config.Bind("Keybinds", "CycleAutoCastMode",
            new Keybind(KeyCode.RightBracket, [KeyModifier.LAlt]).ToString(),
            "Cycle through auto-cast modes.");
        CycleAutoCastModeReverseKeybind = Config.Bind("Keybinds", "CycleAutoCastModeReverse",
            new Keybind(KeyCode.LeftBracket, [KeyModifier.LAlt]).ToString(),
            "Cycle through auto-cast modes in reverse.");
        ToggleExcludeSpellKeybind = Config.Bind("Keybinds", "ToggleExcludeSpell",
            new Keybind(KeyCode.X, [KeyModifier.LAlt]).ToString(),
            "Toggle auto-cast exclusion for the spell under the mouse cursor.");
        ExcludedSpellsJson = Config.Bind("Internal", "ExcludedSpellsJson", "[]",
            "JSON array of excluded spell GUIDs (managed automatically).");
        PrioritiesJson = Config.Bind("Internal", "PrioritiesJson", "{}",
            "JSON map of spell GUID → priority (managed automatically).");
    }

    // ====== Harmony Patch: UISpellButton 显示自动施法状态 ======

    [HarmonyPatch(typeof(UISpellButton), nameof(UISpellButton.RenderContent))]
    [HarmonyPostfix]
    public static void RenderAutoCastIndicator(UISpellButton __instance)
    {
        if (!AutoCaster.GlobalEnabled) return;

        var spell = __instance.item;
        if (spell == null || spell.IsEmpty()) return;

        // 在名字后面追加自动施法指示器
        if (__instance.namePlate != null)
        {
            var isExcluded = IsSpellExcluded(spell);
            var prio = GetPriority(spell);
            var isHovered = spell == HoveredSpell;
            string indicator;
            if (isExcluded)
                indicator = " <color=#888888>[×]</color>";
            else if (prio == 0)
                indicator = isHovered ? " <color=#FFDD44>[A]</color>" : " <color=#66FF66>[A]</color>";
            else
                indicator = isHovered ? $" <color=#FFDD44>[{prio}]</color>" : $" <color=#66FF66>[{prio}]</color>";
            __instance.namePlate.text += indicator;
        }
    }

    // ====== Keybind 类 ======

    public class Keybind
    {
        private readonly Tuple<List<KeyModifier>, KeyCode> _keybind;

        public Keybind(KeyCode key, List<KeyModifier> modifiers = null)
        {
            modifiers ??= [];
            _keybind = new Tuple<List<KeyModifier>, KeyCode>(modifiers, key);
        }

        public bool IsPressed()
        {
            foreach (var mod in _keybind.Item1)
                switch (mod)
                {
                    case KeyModifier.Ctrl:
                        if (!Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl)) return false;
                        break;
                    case KeyModifier.Shift:
                        if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift)) return false;
                        break;
                    case KeyModifier.Alt:
                        if (!Input.GetKey(KeyCode.LeftAlt) && !Input.GetKey(KeyCode.RightAlt)) return false;
                        break;
                    case KeyModifier.LCtrl:
                        if (!Input.GetKey(KeyCode.LeftControl)) return false;
                        break;
                    case KeyModifier.RCtrl:
                        if (!Input.GetKey(KeyCode.RightControl)) return false;
                        break;
                    case KeyModifier.LShift:
                        if (!Input.GetKey(KeyCode.LeftShift)) return false;
                        break;
                    case KeyModifier.RShift:
                        if (!Input.GetKey(KeyCode.RightShift)) return false;
                        break;
                    case KeyModifier.LAlt:
                        if (!Input.GetKey(KeyCode.LeftAlt)) return false;
                        break;
                    case KeyModifier.RAlt:
                        if (!Input.GetKey(KeyCode.RightAlt)) return false;
                        break;
                }

            return Input.GetKeyDown(_keybind.Item2);
        }

        public override string ToString()
        {
            if (_keybind.Item1.Count == 0)
                return _keybind.Item2.ToString();
            return string.Join("+", _keybind.Item1) + "+" + _keybind.Item2;
        }

        public static Keybind Of(string str) => str;

        public static implicit operator Keybind(KeyCode key) => new(key);

        public static implicit operator Keybind(Tuple<List<KeyModifier>, KeyCode> tuple) =>
            new(tuple.Item2, tuple.Item1);

        public static implicit operator Keybind(string str)
        {
            var parts = str.Split('+');
            var modifiers = new List<KeyModifier>();
            var key = KeyCode.None;
            foreach (var part in parts)
                if (Enum.TryParse(part, out KeyModifier mod))
                    modifiers.Add(mod);
                else if (Enum.TryParse(part, out KeyCode k))
                {
                    if (key != KeyCode.None)
                        throw new ArgumentException($"Invalid keybind string: {str}. Multiple keys specified.");
                    key = k;
                }
                else
                    throw new ArgumentException($"Invalid keybind string: {str}. Unknown part: {part}");

            if (key == KeyCode.None)
                throw new ArgumentException($"Invalid keybind string: {str}");
            return new Keybind(key, modifiers);
        }

        public static implicit operator string(Keybind keybind) => keybind.ToString();
    }
}
