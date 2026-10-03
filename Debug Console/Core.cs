using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(DebugConsole.Core), "Debug Console", "1.0.0", "ButterLab")]
[assembly: MelonGame("Questing Goose Studio", "Probably Stolen")]

namespace DebugConsole
{
    /// <summary>
    /// 模组入口。负责生命周期、热键、把命令表挂上、以及几个必要的流程钩子。
    /// </summary>
    public sealed class Core : MelonMod
    {
        public const string Version = "1.0.0";

        public static MelonLogger.Instance Log;
        public static Core Instance { get; private set; }

        private MelonPreferences_Category _cfg;
        private MelonPreferences_Entry<string> _hotkey;
        private MelonPreferences_Entry<bool> _verbose;
        private MelonPreferences_Entry<bool> _lightTheme;
        private MelonPreferences_Entry<string> _mode;
        private MelonPreferences_Entry<string> _favs;
        private MelonPreferences_Entry<string> _lang;

        /// <summary>
        /// 热键。用带修饰键的组合而不是单键 —— 单键（原来的 F8）在装了别的模组的
        /// 环境里几乎必然撞车：Pure Ice、Custom_Rent 之类都占着 F8，按一下连它们一起触发。
        /// 组合键在游戏里没人用，撞不上。
        /// </summary>
        private const string DefaultHotkey = "LeftControl+LeftShift+I";

        private static KeyCode[] _hotKeys = new KeyCode[0];
        private static string _hotkeyText = "Ctrl+Shift+I";

        /// <summary>给人看的热键写法，状态栏和日志用。</summary>
        public static string HotkeyText { get { return _hotkeyText; } }

        /// <summary>界面风格偏好（纯外观，跟存档无关）。面板里那个「亮色/深色」按钮改的就是它。</summary>
        public static bool LightThemePref
        {
            get { return Instance != null && Instance._lightTheme != null && Instance._lightTheme.Value; }
            set
            {
                if (Instance == null || Instance._lightTheme == null) return;
                if (Instance._lightTheme.Value == value) return;
                Instance._lightTheme.Value = value;
                try { MelonPreferences.Save(); } catch { }
            }
        }

        /// <summary>物品控制台里收藏的物品 ID（'|' 分隔）。</summary>
        public static string FavoritesPref
        {
            get { return Instance != null && Instance._favs != null ? Instance._favs.Value : ""; }
            set
            {
                if (Instance == null || Instance._favs == null) return;
                if (Instance._favs.Value == value) return;
                Instance._favs.Value = value;
                try { MelonPreferences.Save(); } catch { }
            }
        }

        /// <summary>界面模式偏好：simple / hybrid / developer。</summary>
        public static string ModePref
        {
            get { return Instance != null && Instance._mode != null ? Instance._mode.Value : null; }
            set
            {
                if (Instance == null || Instance._mode == null) return;
                if (Instance._mode.Value == value) return;
                Instance._mode.Value = value;
                try { MelonPreferences.Save(); } catch { }
            }
        }

        /// <summary>界面语言偏好：zh / en。顶栏那个中英按钮改的就是它。</summary>
        public static string LangPref
        {
            get { return Instance != null && Instance._lang != null ? Instance._lang.Value : "zh"; }
            set
            {
                if (Instance == null || Instance._lang == null) return;
                if (Instance._lang.Value == value) return;
                Instance._lang.Value = value;
                try { MelonPreferences.Save(); } catch { }
            }
        }

        public override void OnInitializeMelon()
        {
            Instance = this;
            Log = LoggerInstance;

            _cfg = MelonPreferences.CreateCategory("DebugConsole", "调试控制台");
            _hotkey = _cfg.CreateEntry<string>("Hotkey", DefaultHotkey,
                "打开/关闭调试控制台的热键。+ 连接，最后一个是主键，前面的是要一起按住的修饰键");
            _verbose = _cfg.CreateEntry<bool>("Verbose", false, "输出调试日志");
            _lightTheme = _cfg.CreateEntry<bool>("LightTheme", false, "界面用亮色风格（false = 深色）");
            _mode = _cfg.CreateEntry<string>("Mode", "hybrid", "界面模式：simple / hybrid / developer");
            _favs = _cfg.CreateEntry<string>("Favorites", "", "物品控制台里收藏的物品 ID，用 | 分隔");
            _lang = _cfg.CreateEntry<string>("Language", "zh", "界面语言：zh / en");

            ApplyHotkey(_hotkey.Value);

            // 语言要在建任何界面之前定下来：文本是显示的那一刻查表的
            L10n.En = LangPref == "en";

            // 建面板之前先把上次选的风格刷进去，否则第一眼看到的是深色
            Palette.SetTheme(LightThemePref ? Palette.Theme.Light : Palette.Theme.Dark);
            ConsoleUI.LoadMode();

            CmdGame.Register();
            CmdReflect.Register();
            CmdSys.Register();

            try
            {
                PatchAll();
            }
            catch (Exception ex)
            {
                Log.Error("Harmony 补丁挂载失败：" + ex);
            }

            Log.Msg("调试控制台 v" + Version + " 已加载，共 " + Cmds.All.Count
                + " 条命令。默认按 " + _hotkeyText + " 打开。");
        }

        // ── 热键 ──────────────────────────────────────────────────────

        /// <summary>
        /// 把 "LeftControl+LeftShift+I" 这样的写法解析成键位数组（最后一个才是主键）。
        /// 顺带拼一句给人看的短名（Ctrl+Shift+I）。
        /// </summary>
        private void ApplyHotkey(string text)
        {
            string[] parts = (text ?? "").Split(new[] { '+', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            List<KeyCode> keys = new List<KeyCode>();
            List<string> names = new List<string>();

            for (int i = 0; i < parts.Length; i++)
            {
                KeyCode k;
                try
                {
                    k = (KeyCode)Enum.Parse(typeof(KeyCode), parts[i].Trim(), true);
                }
                catch
                {
                    Log.Warning("热键里认不出「" + parts[i] + "」，这一段忽略了。");
                    continue;
                }
                keys.Add(k);
                names.Add(ShortKeyName(k.ToString()));
            }

            if (keys.Count == 0)
            {
                // 配置写坏了就退回默认，不然玩家会连控制台都打不开
                Log.Warning("热键「" + text + "」解析不出键位，改用默认 " + DefaultHotkey + "。");
                ApplyHotkey(DefaultHotkey);
                return;
            }
            if (keys.Count == 1)
            {
                Log.Warning("热键只有一个键（" + names[0] + "），容易和别的模组撞车，建议写成 "
                    + DefaultHotkey + " 这种组合。");
            }

            _hotKeys = keys.ToArray();
            _hotkeyText = string.Join("+", names.ToArray());
        }

        private static string ShortKeyName(string code)
        {
            switch (code)
            {
                case "LeftControl": case "RightControl": return "Ctrl";
                case "LeftShift": case "RightShift": return "Shift";
                case "LeftAlt": case "RightAlt": return "Alt";
                case "LeftCommand": case "RightCommand": return "Win";
            }
            if (code.StartsWith("Alpha", StringComparison.Ordinal)) return code.Substring(5);
            if (code.StartsWith("Keypad", StringComparison.Ordinal)) return "小键盘" + code.Substring(6);
            return code;
        }

        /// <summary>
        /// 组合键判定：所有修饰键都按住不放，同时主键在这一帧被按下。
        /// 用 Input.GetKeyDown 逐个查（KeyCode 里没有「Ctrl+Shift+I」这种枚举）。
        /// </summary>
        private static bool HotkeyPressed()
        {
            int n = _hotKeys.Length;
            if (n == 0) return false;
            for (int i = 0; i < n - 1; i++)
            {
                if (!Input.GetKey(_hotKeys[i])) return false;
            }
            return Input.GetKeyDown(_hotKeys[n - 1]);
        }

        /// <summary>
        /// 逐个补丁类挂载。PatchAll 是一锤子买卖 —— 只要有一个目标方法对不上
        /// （游戏版本差异、方法改名），整批补丁都会失效；分开挂就互不牵连。
        /// </summary>
        private void PatchAll()
        {
            Type[] patches =
            {
                typeof(GameInputSuppressPatch),
                typeof(GameInputKeysSuppressPatch),
                typeof(GameInputHandlerSuppressPatch),
                typeof(LoadGamePatch),
                typeof(StartNewGamePatch),
                typeof(QuitToMenuPatch),
            };
            for (int i = 0; i < patches.Length; i++)
            {
                try
                {
                    HarmonyInstance.CreateClassProcessor(patches[i]).Patch();
                }
                catch (Exception ex)
                {
                    Log.Warning("补丁 " + patches[i].Name + " 挂载失败：" + ex.Message);
                }
            }
        }

        public override void OnDeinitializeMelon()
        {
            try
            {
                HarmonyInstance.UnpatchSelf();
                GameApi.ReleaseWorldMouse();
                GameApi.BlockUiNav(false);
                GameApi.BlockGameInput(false);
            }
            catch { }
        }

        public override void OnUpdate()
        {
            try
            {
                ConsoleUI.Tick();
                // 连跳天数得在面板关着的时候也能走，所以驱动放在这儿而不是界面里
                GameApi.TickSkip();
                // 举枪的手臂：非射击模式下游戏每帧都会自己把它藏起来，得跟着按回去
                GameApi.TickArmShow();
                // 快捷删除只在游戏画面里生效（面板开着时鼠标归面板管）：
                // 先右键点一下选中，再按 Ctrl+Shift+X 删
                if (!ConsoleUI.IsOpen)
                {
                    GameApi.TickItemPick();
                    GameApi.TickItemDelete();
                    // 属性刷子：按住 Ctrl+Shift+A 划过物品就批量改
                    GameApi.TickPropBrush();
                }
                if (HotkeyPressed())
                {
                    ConsoleUI.Toggle();
                }
            }
            catch (Exception ex)
            {
                Log.Warning("控制台更新失败：" + ex.Message);
            }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            ConsoleUI.OnSceneLoaded();
            // 场景换了，OverlayHandler 也重建了，把可能残留的屏蔽栈还回去
            GameApi.ReleaseWorldMouse();
            GameApi.BlockUiNav(false);
            GameApi.BlockGameInput(false);
        }

        public static void Debug(string message)
        {
            if (Instance != null && Instance._verbose != null && Instance._verbose.Value)
            {
                Log.Msg("[调试] " + message);
            }
        }
    }

    /// <summary>
    /// 控制台开着的时候，把游戏自己的键鼠处理掐掉。
    ///
    /// 游戏所有按键派发都走 InputActionManager.Update → 各个 InputActionHandler，
    /// 掐这一处就等于「世界不再响应按键和点击」，但 Unity 的 EventSystem 完全不受影响，
    /// 所以我们的输入框照样能打字、按钮照样能点。
    /// 早先那种「把 EventSystem 禁掉自己模拟点击」的做法（NotEnoughItems）会把输入法、
    /// 回车、Tab 全变成手工活，不值得。
    ///
    /// 注意这里拦不住「别的模组自己在 OnUpdate 里读 Input」—— 那是模组各自的循环，
    /// 拦不了也不该拦。所以热键从 F8 换成了组合键：装了 Pure Ice / Custom_Rent 这些
    /// 占用 F8 的模组时，按一下 F8 会连它们一起触发。
    /// </summary>
    [HarmonyPatch(typeof(InputActionManager), "Update")]
    internal static class GameInputSuppressPatch
    {
        private static bool Prefix()
        {
            return !ConsoleUI.IsOpen;
        }
    }

    /// <summary>
    /// 管理器里那个按 KeyCode 派发的入口，单独再掐一道 —— 有些流程不经过 Update。
    /// </summary>
    [HarmonyPatch(typeof(InputActionManager), "UpdateKeys")]
    internal static class GameInputKeysSuppressPatch
    {
        private static bool Prefix()
        {
            return !ConsoleUI.IsOpen;
        }
    }

    /// <summary>
    /// 处理器自己那一层也补一刀。这是打字还会触发游戏快捷键的漏网处：
    /// 部分处理器自己带 Update / 按键回调，绕开了管理器的 Update。
    /// </summary>
    [HarmonyPatch(typeof(InputActionHandler), "UpdateUpdate")]
    internal static class GameInputHandlerSuppressPatch
    {
        private static bool Prefix()
        {
            return !ConsoleUI.IsOpen;
        }
    }

    /// <summary>换档/新开一局：物品目录缓存作废，重新读一遍（装了别的模组会加新物品）。</summary>
    [HarmonyPatch(typeof(PlayerStore), "LoadGame")]
    internal static class LoadGamePatch
    {
        private static void Postfix()
        {
            try { GameApi.DropItemCache(); } catch { }
        }
    }

    [HarmonyPatch(typeof(PlayerStore), "StartNewGame")]
    internal static class StartNewGamePatch
    {
        private static void Postfix()
        {
            try { GameApi.DropItemCache(); } catch { }
        }
    }

    /// <summary>退回主菜单：把面板收掉，并把鼠标交互还给游戏。</summary>
    [HarmonyPatch(typeof(GameMaster), "QuitToMenu")]
    internal static class QuitToMenuPatch
    {
        private static void Postfix()
        {
            try { ConsoleUI.Close(); } catch { }
        }
    }
}