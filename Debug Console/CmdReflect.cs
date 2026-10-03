using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace DebugConsole
{
    /// <summary>
    /// 反射调试命令：直接读写游戏里的字段、调它的方法。
    ///
    /// 走的是一般 .NET 反射（AppDomain 里那批 Il2Cpp 代理程序集）。
    /// Il2CppInterop 生成的代理类型，属性读值/方法调用都会转成 il2cpp 的运行时调用，
    /// 所以 FieldInfo.GetValue / MethodInfo.Invoke 拿到的就是游戏里的真值。
    ///
    /// 路径写法：<c>类型.成员.成员…</c>，例如
    ///   get PlayerStore.instance.playerCash
    ///   set PlayerStore.instance.playerCash 99999
    ///   call PlayerStore.instance.RefreshCounterItem
    /// 中间任何一段是实例成员时，会自动去拿它所在类型的 instance / Instance / current 单例。
    /// </summary>
    internal static class CmdReflect
    {
        private const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private const int ListLimit = 30;

        private static List<Type> _index;
        private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();

        public static void Register()
        {
            Cmds.Add(new Cmd
            {
                Name = "types",
                Usage = "types <关键词>",
                Help = "在游戏程序集里按名字找类型。写空串会列出全部（很慢）。",
                Group = "reflect",
                Hints = new[] { "PlayerStore GameItem EmporiumEntry StoreReputation OverlayHandler" },
                Run = a => ListTypes(a.Rest(0))
            });

            Cmds.Add(new Cmd
            {
                Name = "fields",
                Usage = "fields <类型> [关键词]",
                Help = "列出某个类型的所有字段/属性；静态的会连当前值一起打出来。",
                Group = "reflect",
                Preset = "fields PlayerStore",
                Run = a =>
                {
                    Type t = NeedType(a.At(0));
                    if (t == null) return;
                    ListMembers(t, a.At(1), false);
                }
            });

            Cmds.Add(new Cmd
            {
                Name = "methods",
                Usage = "methods <类型> [关键词]",
                Help = "列出某个类型的方法（只列名字和参数，不带返回值）。",
                Group = "reflect",
                Run = a =>
                {
                    Type t = NeedType(a.At(0));
                    if (t == null) return;
                    ListMembers(t, a.At(1), true);
                }
            });

            Cmds.Add(new Cmd
            {
                Name = "get",
                Usage = "get <类型.成员[.成员…]>",
                Help = "读一个字段/属性的值。例：get PlayerStore.instance.playerCash",
                Group = "reflect",
                Preset = "get PlayerStore.instance.playerCash",
                Run = a =>
                {
                    if (!a.Has(0)) { Out.Err("用法：" + Palette.TagKey("get <类型.成员>")); return; }
                    string err;
                    object v = Resolve(a.At(0), out err);
                    if (err != null) { Out.Err(err); return; }
                    Out.Line(Palette.TagKey(a.At(0)) + " = " + Palette.TagVal(Show(v)));
                }
            });

            Cmds.Add(new Cmd
            {
                Name = "set",
                Usage = "set <类型.成员> <值>",
                Help = "写一个字段/属性。值支持数字、true/false、字符串、枚举名、null。",
                Group = "reflect",
                Run = a =>
                {
                    if (a.Count < 2) { Out.Err("用法：" + Palette.TagKey("set <类型.成员> <值>")); return; }
                    string path = a.At(0);
                    string raw = a.Rest(1);
                    string err = Write(path, raw);
                    if (err != null) { Out.Err(err); return; }
                    Out.Ok(Palette.TagKey(path) + " 已设为 " + Palette.TagVal(Show(Resolve(path, out err))));
                }
            });

            Cmds.Add(new Cmd
            {
                Name = "call",
                Usage = "call <类型.方法> [参数…]",
                Help = "调一个方法。例：call PlayerStore.instance.RefreshCounterItem",
                Group = "reflect",
                Run = a =>
                {
                    if (!a.Has(0)) { Out.Err("用法：" + Palette.TagKey("call <类型.方法> [参数…]")); return; }
                    string err = Call(a.At(0), a);
                    if (err != null) { Out.Err(err); return; }
                }
            });
        }

        // ══════════════════════════════════════════════════════════════
        //  类型索引
        // ══════════════════════════════════════════════════════════════

        private static bool SkipAssembly(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            return name.StartsWith("System", StringComparison.Ordinal)
                || name.StartsWith("Microsoft", StringComparison.Ordinal)
                || name.StartsWith("netstandard", StringComparison.Ordinal)
                || name == "mscorlib"
                || name.StartsWith("MelonLoader", StringComparison.Ordinal)
                || name.StartsWith("0Harmony", StringComparison.Ordinal)
                || name.StartsWith("Il2CppInterop", StringComparison.Ordinal)
                || name.StartsWith("Newtonsoft", StringComparison.Ordinal)
                || name.StartsWith("Il2CppNewtonsoft", StringComparison.Ordinal);
        }

        private static List<Type> TypeIndex()
        {
            if (_index != null) return _index;
            List<Type> list = new List<Type>();
            Assembly[] asms;
            try { asms = AppDomain.CurrentDomain.GetAssemblies(); }
            catch { asms = new Assembly[0]; }

            for (int i = 0; i < asms.Length; i++)
            {
                Assembly asm = asms[i];
                string n;
                try { n = asm.GetName().Name; } catch { continue; }
                if (SkipAssembly(n)) continue;
                try
                {
                    Type[] ts = asm.GetTypes();
                    for (int k = 0; k < ts.Length; k++)
                    {
                        if (ts[k] != null) list.Add(ts[k]);
                    }
                }
                catch { }
            }
            _index = list;
            Core.Log.Msg("[反射] 类型索引已建立，" + list.Count + " 个类型。");
            return list;
        }

        private static Type FindType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Type cached;
            if (_typeCache.TryGetValue(name, out cached)) return cached;

            List<Type> all = TypeIndex();
            Type exact = null, shortExact = null, ignoreCase = null;
            List<Type> fuzzy = new List<Type>();

            for (int i = 0; i < all.Count; i++)
            {
                Type t = all[i];
                string full, sn;
                try { full = t.FullName ?? t.Name; } catch { continue; }
                sn = t.Name;

                if (exact == null && full == name) exact = t;
                if (shortExact == null && sn == name) shortExact = t;
                if (ignoreCase == null && string.Equals(sn, name, StringComparison.OrdinalIgnoreCase)) ignoreCase = t;
                if (fuzzy.Count < ListLimit && sn.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) fuzzy.Add(t);
            }

            Type hit = exact ?? shortExact ?? ignoreCase;
            if (hit == null && fuzzy.Count == 1) hit = fuzzy[0];

            if (hit == null && fuzzy.Count > 1)
            {
                Out.Warn("「" + name + "」匹配到多个类型，把名字写全：");
                for (int i = 0; i < fuzzy.Count && i < 12; i++) Out.Line(Palette.TagKey(FullName(fuzzy[i])));
                return null;
            }
            if (hit == null)
            {
                Out.Err("找不到类型「" + name + "」。用 " + Palette.TagKey("types " + name) + " 搜搜看。");
                return null;
            }

            _typeCache[name] = hit;
            return hit;
        }

        private static string FullName(Type t)
        {
            try { return t.FullName ?? t.Name; } catch { return t.Name; }
        }

        private static Type NeedType(string name)
        {
            if (string.IsNullOrEmpty(name)) { Out.Err("要先给一个类型名，用 " + Palette.TagKey("types 关键词") + " 找。"); return null; }
            return FindType(name);
        }

        private static void ListTypes(string kw)
        {
            List<Type> all = TypeIndex();
            if (all.Count == 0) { Out.Warn("没扫到任何游戏程序集。"); return; }

            if (string.IsNullOrEmpty(kw))
            {
                Out.Line("共 " + Palette.TagVal(all.Count + " 个类型") + "，加个关键词再搜（列表太长没意义）。");
                return;
            }

            List<Type> hits = new List<Type>();
            for (int i = 0; i < all.Count && hits.Count < 60; i++)
            {
                Type t = all[i];
                string sn = t.Name;
                if (sn.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(t);
            }
            if (hits.Count == 0) { Out.Warn("没有匹配「" + kw + "」的类型。"); return; }

            Out.Line("匹配 " + Palette.TagVal(hits.Count + " 个类型") + "：");
            for (int i = 0; i < hits.Count; i++) Out.Line("· " + Palette.TagKey(FullName(hits[i])));
        }

        private static void ListMembers(Type t, string kw, bool methods)
        {
            int shown = 0;
            if (methods)
            {
                MethodInfo[] ms;
                try { ms = t.GetMethods(Flags); } catch { Out.Err("枚举方法失败。"); return; }
                for (int i = 0; i < ms.Length && shown < 40; i++)
                {
                    MethodInfo m = ms[i];
                    if (m.IsSpecialName) continue;
                    if (!string.IsNullOrEmpty(kw) && m.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    StringBuilder sb = new StringBuilder();
                    sb.Append("· ").Append(Palette.TagKey(m.Name)).Append('(');
                    ParameterInfo[] ps = m.GetParameters();
                    for (int k = 0; k < ps.Length; k++)
                    {
                        if (k > 0) sb.Append(", ");
                        sb.Append(SimpleName(ps[k].ParameterType.Name)).Append(' ').Append(ps[k].Name);
                    }
                    sb.Append(')');
                    if (m.IsStatic) sb.Append(Palette.TagMuted("  static"));
                    Out.Line(sb.ToString());
                    shown++;
                }
                if (shown == 0) Out.Warn("没有匹配的方法。");
                return;
            }

            MemberInfo[] all;
            try { all = t.GetMembers(Flags); } catch { Out.Err("枚举字段失败。"); return; }
            for (int i = 0; i < all.Length && shown < 50; i++)
            {
                MemberInfo mi = all[i];
                if (!(mi is PropertyInfo) && !(mi is FieldInfo)) continue;
                string name = mi.Name;
                if (name.StartsWith("get_") || name.StartsWith("set_") || name.StartsWith("add_") || name.StartsWith("remove_")) continue;
                if (!string.IsNullOrEmpty(kw) && name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;

                Type mt = MemberType(mi);
                bool isStatic = (mi is FieldInfo) ? ((FieldInfo)mi).IsStatic : IsStaticProperty((PropertyInfo)mi);
                string line = "· " + Palette.TagKey(name) + Palette.TagMuted(" : " + SimpleName(mt != null ? mt.Name : "?"))
                    + (isStatic ? Palette.TagMuted("  static") : "");

                if (isStatic)
                {
                    object v;
                    string err;
                    if (TryRead(null, mi, out v, out err)) line += "  = " + Palette.TagVal(Show(v));
                    else line += Palette.TagMuted("  = <" + err + ">");
                }
                else if (IsDefaultInstanceName(name) && mt != null)
                {
                    // instance / current 这类单例入口，把对象也顺手打出来，省得再 get 一次
                    object v;
                    string err;
                    if (TryRead(null, mi, out v, out err)) line += "  = " + Palette.TagVal(Show(v));
                }
                Out.Line(line);
                shown++;
            }
            if (shown == 0) Out.Warn("没有匹配的字段。");
        }

        private static string SimpleName(string n)
        {
            if (string.IsNullOrEmpty(n)) return "?";
            int i = n.IndexOf('`');
            return i > 0 ? n.Substring(0, i) : n;
        }

        private static Type MemberType(MemberInfo mi)
        {
            try
            {
                FieldInfo f = mi as FieldInfo;
                if (f != null) return f.FieldType;
                PropertyInfo p = mi as PropertyInfo;
                if (p != null) return p.PropertyType;
            }
            catch { }
            return null;
        }

        private static bool IsStaticProperty(PropertyInfo p)
        {
            try
            {
                MethodInfo g = p.GetGetMethod(true);
                if (g != null) return g.IsStatic;
                MethodInfo s = p.GetSetMethod(true);
                if (s != null) return s.IsStatic;
            }
            catch { }
            return false;
        }

        private static bool IsDefaultInstanceName(string n)
        {
            return n == "instance" || n == "Instance" || n == "current" || n == "singletonInstance";
        }

        // ══════════════════════════════════════════════════════════════
        //  路径解析
        // ══════════════════════════════════════════════════════════════

        /// <summary>沿路径一路读下去，返回最后一个成员的值。</summary>
        private static object Resolve(string path, out string err)
        {
            err = null;
            string[] parts = path.Split('.');
            if (parts.Length < 2) { err = "路径要写成「类型.成员」，例如 PlayerStore.instance.playerCash"; return null; }

            Type t = FindType(parts[0]);
            if (t == null) { err = "找不到类型「" + parts[0] + "」。"; return null; }

            object cur = null;
            for (int i = 1; i < parts.Length; i++)
            {
                string want = parts[i];
                MemberInfo mi = FindMember(t, want);
                if (mi == null)
                {
                    err = "类型 " + t.Name + " 上没有成员「" + want + "」。用 " + Palette.TagKey("fields " + t.Name) + " 看看。";
                    return null;
                }

                bool isStatic = (mi is FieldInfo) ? ((FieldInfo)mi).IsStatic : IsStaticProperty((PropertyInfo)mi);
                if (!isStatic)
                {
                    if (cur == null)
                    {
                        cur = DefaultInstance(t);
                        if (cur == null)
                        {
                            err = t.Name + " 的「" + want + "」是实例成员，但找不到它的单例入口，得自己先拿到对象。";
                            return null;
                        }
                    }
                }

                object v;
                string e;
                if (!TryRead(isStatic ? null : cur, mi, out v, out e))
                {
                    err = "读 " + t.Name + "." + want + " 失败：" + e;
                    return null;
                }

                Type nt = MemberType(mi);
                cur = v;
                t = nt ?? (v != null ? v.GetType() : null);
                if (t == null)
                {
                    if (i == parts.Length - 1) return cur;
                    err = "路径在「" + want + "」之后走不下去了（值为空）。";
                    return null;
                }
            }
            return cur;
        }

        private static MemberInfo FindMember(Type t, string name)
        {
            try
            {
                PropertyInfo p = t.GetProperty(name, Flags);
                if (p != null) return p;
            }
            catch { }
            try
            {
                FieldInfo f = t.GetField(name, Flags);
                if (f != null) return f;
            }
            catch { }
            return null;
        }

        private static bool TryRead(object target, MemberInfo mi, out object value, out string err)
        {
            value = null;
            err = null;
            try
            {
                FieldInfo f = mi as FieldInfo;
                if (f != null) { value = f.GetValue(target); return true; }
                PropertyInfo p = mi as PropertyInfo;
                if (p != null)
                {
                    MethodInfo g = p.GetGetMethod(true);
                    if (g == null) { err = "只有 setter，没有 getter"; return false; }
                    value = g.Invoke(target, null);
                    return true;
                }
                err = "不是字段也不是属性";
                return false;
            }
            catch (Exception ex)
            {
                err = (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        /// <summary>找这个类型的单例入口（instance / Instance / current / singletonInstance）。</summary>
        private static object DefaultInstance(Type t)
        {
            string[] names = { "instance", "Instance", "current", "singletonInstance" };
            for (int i = 0; i < names.Length; i++)
            {
                MemberInfo mi = FindMember(t, names[i]);
                if (mi == null) continue;
                bool isStatic = (mi is FieldInfo) ? ((FieldInfo)mi).IsStatic : IsStaticProperty((PropertyInfo)mi);
                if (!isStatic) continue;
                object v;
                string err;
                if (TryRead(null, mi, out v, out err) && v != null) return v;
            }
            return null;
        }

        // ══════════════════════════════════════════════════════════════
        //  写入 / 调用
        // ══════════════════════════════════════════════════════════════

        private static string Write(string path, string raw)
        {
            string[] parts = path.Split('.');
            if (parts.Length < 2) return "路径要写成「类型.成员」，例如 PlayerStore.instance.playerCash";

            string parentPath = string.Join(".", parts, 0, parts.Length - 1);
            string leaf = parts[parts.Length - 1];

            string err;
            object parent = Resolve(parentPath, out err);
            if (err != null) return err;

            Type t;
            if (parts.Length == 2)
            {
                t = FindType(parts[0]);
                if (t == null) return "找不到类型「" + parts[0] + "」。";
            }
            else
            {
                if (parent == null) return parentPath + " 的值是空的，写不进去。";
                t = parent.GetType();
            }

            MemberInfo mi = FindMember(t, leaf);
            if (mi == null) return t.Name + " 上没有成员「" + leaf + "」。";

            bool isStatic = (mi is FieldInfo) ? ((FieldInfo)mi).IsStatic : IsStaticProperty((PropertyInfo)mi);
            object target = isStatic ? null : parent;
            if (!isStatic && target == null) return leaf + " 是实例成员，但 " + parentPath + " 是空值。";

            Type mt = MemberType(mi);
            object val;
            if (!TryConvert(raw, mt, out val, out err)) return err;

            try
            {
                FieldInfo f = mi as FieldInfo;
                if (f != null) { f.SetValue(target, val); return null; }
                PropertyInfo p = mi as PropertyInfo;
                MethodInfo s = p.GetSetMethod(true);
                if (s == null) return leaf + " 只有 getter，是只读的。";
                s.Invoke(target, new object[] { val });
                return null;
            }
            catch (Exception ex)
            {
                return "写入失败：" + (ex.InnerException ?? ex).Message;
            }
        }

        private static string Call(string path, CmdArgs a)
        {
            string[] parts = path.Split('.');
            if (parts.Length < 2) return "路径要写成「类型.方法」，例如 PlayerStore.instance.RefreshCounterItem";

            string parentPath = string.Join(".", parts, 0, parts.Length - 1);
            string leaf = parts[parts.Length - 1];

            string err;
            object parent = Resolve(parentPath, out err);
            if (err != null) return err;

            Type t;
            if (parts.Length == 2)
            {
                t = FindType(parts[0]);
                if (t == null) return "找不到类型「" + parts[0] + "」。";
            }
            else
            {
                if (parent == null) return parentPath + " 是空值，没法调它的方法。";
                t = parent.GetType();
            }

            int argc = a.Count - 1;
            MethodInfo[] ms;
            try { ms = t.GetMethods(Flags); } catch { ms = new MethodInfo[0]; }

            MethodInfo pick = null;
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name != leaf) continue;
                if (ms[i].GetParameters().Length != argc) continue;
                pick = ms[i];
                break;
            }
            if (pick == null)
            {
                return "在 " + t.Name + " 上找不到名叫「" + leaf + "」、参数个数为 " + argc + " 的方法。";
            }

            bool isStatic = pick.IsStatic;
            object target = isStatic ? null : parent;
            if (!isStatic && target == null) return leaf + " 是实例方法，但 " + parentPath + " 是空值。";

            ParameterInfo[] ps = pick.GetParameters();
            object[] args = new object[argc];
            for (int i = 0; i < argc; i++)
            {
                object v;
                string e;
                if (!TryConvert(a.At(i + 1), ps[i].ParameterType, out v, out e)) return "第 " + (i + 1) + " 个参数：" + e;
                args[i] = v;
            }

            object ret;
            try
            {
                ret = pick.Invoke(target, args);
            }
            catch (Exception ex)
            {
                return "调用失败：" + (ex.InnerException ?? ex).Message;
            }

            if (pick.ReturnType == typeof(void))
            {
                Out.Ok(Palette.TagKey(path) + " 已调用（无返回值）。");
            }
            else
            {
                Out.Ok(Palette.TagKey(path) + " 返回 " + Palette.TagVal(Show(ret)));
            }
            return null;
        }

        /// <summary>字符串 → 目标类型的值。支持数字、bool、字符串、枚举名、null、以及同名类型的单例。</summary>
        private static bool TryConvert(string raw, Type want, out object value, out string err)
        {
            value = null;
            err = null;

            if (string.IsNullOrEmpty(raw)) raw = "";
            if (want == null) want = typeof(string);

            try
            {
                if (want == typeof(string)) { value = raw; return true; }
                if (want == typeof(bool)) { value = raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "on"; return true; }
                if (want == typeof(int)) { value = int.Parse(raw); return true; }
                if (want == typeof(long)) { value = long.Parse(raw); return true; }
                if (want == typeof(short)) { value = short.Parse(raw); return true; }
                if (want == typeof(byte)) { value = byte.Parse(raw); return true; }
                if (want == typeof(uint)) { value = uint.Parse(raw); return true; }
                if (want == typeof(float)) { value = float.Parse(raw); return true; }
                if (want == typeof(double)) { value = double.Parse(raw); return true; }
                if (want == typeof(char)) { value = raw.Length > 0 ? raw[0] : '\0'; return true; }
                if (want.IsEnum) { value = Enum.Parse(want, raw, true); return true; }
                if (raw == "null" || raw == "nil") { value = null; return true; }

                // 想传一个游戏对象：写它的类型名就行，会去拿这个类型的单例
                Type ot = FindType(raw);
                if (ot != null)
                {
                    object inst = DefaultInstance(ot);
                    if (inst == null) { err = raw + " 有类型但没找到单例，换个参数写法。"; return false; }
                    value = inst;
                    return true;
                }
            }
            catch (Exception ex)
            {
                err = "「" + raw + "」转不成 " + want.Name + "：" + ex.Message;
                return false;
            }

            err = "不认识的值「" + raw + "」（目标类型 " + want.Name + "）。";
            return false;
        }

        // ══════════════════════════════════════════════════════════════
        //  值的显示
        // ══════════════════════════════════════════════════════════════

        private static string Show(object v)
        {
            if (v == null) return "null";
            try
            {
                string s = v as string;
                if (s != null) return s.Length == 0 ? "(空字符串)" : s;

                if (v is bool) return ((bool)v) ? "true" : "false";

                Type t = v.GetType();
                if (IsNumber(t)) return v.ToString();

                // 集合：打个数量 + 前几项，比一长串 ToString 有用得多
                int n;
                if (TryCount(v, out n))
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append('[').Append(n).Append(" 项] ");
                    PropertyInfo idx = t.GetProperty("Item", new[] { typeof(int) });
                    if (idx == null) { try { idx = t.GetProperty("Item"); } catch { } }
                    int limit = Math.Min(n, 8);
                    for (int i = 0; i < limit; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        try
                        {
                            object item = idx != null && idx.GetIndexParameters().Length == 1
                                ? idx.GetValue(v, new object[] { i })
                                : null;
                            sb.Append(item == null ? "null" : item.ToString());
                        }
                        catch { sb.Append("<取不到>"); }
                    }
                    if (n > limit) sb.Append(", …");
                    return sb.ToString();
                }

                string str = v.ToString();
                if (str.Length > 220) str = str.Substring(0, 220) + "…";
                return str;
            }
            catch (Exception ex)
            {
                return "<显示失败：" + ex.Message + ">";
            }
        }

        private static bool IsNumber(Type t)
        {
            return t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
                || t == typeof(uint) || t == typeof(ulong) || t == typeof(float) || t == typeof(double);
        }

        private static bool TryCount(object v, out int n)
        {
            n = 0;
            try
            {
                Type t = v.GetType();
                if (t.IsPrimitive || v is string) return false;
                PropertyInfo c = t.GetProperty("Count", Flags);
                if (c == null) return false;
                object raw = c.GetValue(v);
                if (raw == null) return false;
                n = Convert.ToInt32(raw);
                return true;
            }
            catch { return false; }
        }
    }
}