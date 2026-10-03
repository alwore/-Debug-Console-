# -*- coding: utf-8 -*-
"""
把 map*.tsv 里的中英对照合成 L10n.cs，并检查覆盖率。

覆盖率判据：拿 need.txt 里的每一条字面量（和 EXTRA 里那批运行时拼出来的句子）
走一遍 L10n.S 的模拟实现，如果翻完还剩汉字，就说明对照表缺条目 —— 缺哪条直接补进 map4.tsv。
"""
import io
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "L10n.cs")
RAW = "\u0001"


def read_text(p):
    with io.open(p, "r", encoding="utf-8") as f:
        return f.read()


def han(c):
    o = ord(c)
    return 0x4E00 <= o <= 0x9FFF or 0x3400 <= o <= 0x4DBF or 0xF900 <= o <= 0xFAFF


def has_han(s):
    for c in s:
        if han(c):
            return True
    return False


def load_map():
    m = {}
    for name in sorted(os.listdir(HERE)):
        if not name.startswith("map") or not name.endswith(".tsv"):
            continue
        for line in read_text(os.path.join(HERE, name)).splitlines():
            line = line.rstrip("\r")
            if not line.strip() or line.startswith("#"):
                continue
            parts = line.split("\t")
            if len(parts) < 2:
                continue
            k, v = parts[0], parts[1]
            if k == "":
                continue
            m[k] = v
    return m


def load_keep():
    p = os.path.join(HERE, "keep.txt")
    if not os.path.exists(p):
        return set()
    out = set()
    for line in read_text(p).splitlines():
        line = line.rstrip("\r")
        if not line.strip() or line.startswith("#"):
            continue
        out.add(line.split("\t")[0])
    return out


def load_need():
    out = []
    for line in read_text(os.path.join(HERE, "need.txt")).splitlines():
        if not line.strip():
            continue
        parts = line.split("\t")
        if len(parts) < 3:
            continue
        out.append((parts[0], parts[1], parts[2]))
    return out


def buckets(m):
    b = {}
    for k in m:
        b.setdefault(k[0], []).append(k)
    for c in b:
        b[c].sort(key=lambda k: (-len(k), k))
    return b


def conv(s, m, b):
    """L10n.S 的模拟：最长优先 + 汉字邻接保护。"""
    out = []
    i = 0
    n = len(s)
    while i < n:
        cand = b.get(s[i])
        hit = None
        if cand:
            for k in cand:
                L = len(k)
                if L > n - i:
                    continue
                if s[i:i + L] != k:
                    continue
                if han(k[0]) and i > 0 and han(s[i - 1]):
                    continue
                if han(k[-1]) and i + L < n and han(s[i + L]):
                    continue
                hit = k
                break
        if hit:
            out.append(m[hit])
            i += len(hit)
            continue
        out.append(s[i])
        i += 1
    return "".join(out)


# 运行时拼出来的句子：字面量一条条都对上，拼起来也未必顺，这里挑常见的几句一起验
EXTRA = [
    # 面板里真正会出现的拼句（<c> 代表 Palette.Tag* 插进来的颜色标签）
    "界面已切到 简单模式。",
    "界面已切到亮色风格。",
    "界面已切到 开发者模式。",
    "匹配 12 个类型：",
    "共 340 个类型，加个关键词再搜（列表太长没意义）。",
    "<c>在 PlayerStore 上找不到名叫「Foo」、参数个数为 2 的方法。</c>",
    "第 1 个参数：abc",
    "<c>候选 </c><c>5 个</c><c>：</c>",
    "<c>　分类：</c><c>物品</c>",
    "主菜单 · 未进存档<c>　物品目录 120 件</c>",
    "存档 · 店铺就绪<c>　物品目录 120 件</c>",
    "存档 · 库存未就绪<c>　物品目录 0 件</c>",
    "全部命令",
    "金钱　钱和租金，改多改少都在这儿",
    "已删除 <c>apple</c><c>　(右键悬停)</c>",
    "已选中 <c>apple</c><c>　(右键悬停)</c>，按 Ctrl+Shift+X 删掉。",
    "<c>ID apple</c>\n<c>估价 </c><c>12 元</c>",
    "1 / 3<c>　共 42 件</c>",
    "硬核模式已打开。",
    "硬核模式已关闭。",
    "电闸已合上。",
    "电闸已断开。",
    "电闸本来就是断电的。",
    "下层区 声望 12 → 50。",
    "顾客的货换了一批：<c>3 件 → 5 件</c>。",
    "已清空 <c>12 件</c> 物品。",
    "连跳中，还剩 3 天",
    "读不到当前是第几天，改成「推一把就算跳过」的方式，准头差一点。",
    "编号要落在 1 到 12 之间。",
    "手臂：<c>举着</c><c>（还剩 1.2 秒）</c>",
    "手臂：<c>收着</c>",
    "举枪失败：<c>还没进存档，没有可举的手臂。</c>",
    "还没进存档。先开一局或读一个档，这条命令才有东西可改。",
]


def cs(s):
    r = []
    for ch in s:
        if ch == "\\":
            r.append("\\\\")
        elif ch == '"':
            r.append('\\"')
        elif ch == "\n":
            r.append("\\n")
        elif ch == "\r":
            r.append("\\r")
        elif ch == "\t":
            r.append("\\t")
        elif ord(ch) < 0x20:
            r.append("\\u%04x" % ord(ch))
        else:
            r.append(ch)
    return '"' + "".join(r) + '"'


HEAD = u'''using System;
using System.Collections.Generic;
using System.Text;

namespace DebugConsole
{
    /// <summary>
    /// 界面语言：中文原文只写一份，英文在「显示的那一刻」靠这张对照表翻过去。
    /// 命令表、卡片、日志、状态栏、按钮都走同一条路，所以不用在几百处源码里各写一遍双语。
    ///
    /// 两条规矩：
    /// 1) 长的优先 —— 「物品控制台」不会被「物品」抢先切走；
    /// 2) 汉字挨着汉字就不翻 —— 物品名、顾客名、事件名是游戏运行时拼进来的，
    ///    谁也不知道下一条叫什么，只能靠邻接保护兜住它们。
    ///    代价是「汉字+汉字」的固定搭配得整句列一条，缺哪条会被覆盖率脚本报出来。
    /// </summary>
    internal static class L10n
    {
        /// <summary>true = 英文界面。</summary>
        public static bool En;

        /// <summary>原样显示的记号：夹在这对记号之间的文字不翻译（命令 token、补全候选这类要照抄的东西）。</summary>
        private const char RawMark = '\\u0001';

        public static string Raw(string s)
        {
            return string.IsNullOrEmpty(s) ? s : RawMark + s + RawMark;
        }

        /// <summary>把中文原文翻成当前语言。没汉字、或者中文模式，都原样返回。</summary>
        public static string S(string zh)
        {
            if (string.IsNullOrEmpty(zh)) return zh;
            if (zh.IndexOf(RawMark) < 0) return En ? Tr(zh) : zh;

            StringBuilder sb = new StringBuilder(zh.Length);
            int i = 0;
            while (i < zh.Length)
            {
                int a = zh.IndexOf(RawMark, i);
                if (a < 0)
                {
                    sb.Append(En ? Tr(zh.Substring(i)) : zh.Substring(i));
                    break;
                }
                if (a > i) sb.Append(En ? Tr(zh.Substring(i, a - i)) : zh.Substring(i, a - i));
                int b = zh.IndexOf(RawMark, a + 1);
                if (b < 0)
                {
                    sb.Append(zh.Substring(a + 1));
                    break;
                }
                sb.Append(zh.Substring(a + 1, b - a - 1));
                i = b + 1;
            }
            return sb.ToString();
        }

        private static readonly Dictionary<string, string> _memo = new Dictionary<string, string>(StringComparer.Ordinal);

        private static string Tr(string s)
        {
            if (!HasHan(s)) return s;
            string hit;
            if (_memo.TryGetValue(s, out hit)) return hit;
            string outp = Convert(s);
            if (_memo.Count > 4096) _memo.Clear();
            _memo[s] = outp;
            return outp;
        }

        private static Dictionary<char, List<string>> _buckets;

        private static Dictionary<char, List<string>> Buckets()
        {
            if (_buckets != null) return _buckets;
            Dictionary<char, List<string>> b = new Dictionary<char, List<string>>();
            foreach (KeyValuePair<string, string> kv in Map)
            {
                string k = kv.Key;
                if (k.Length == 0) continue;
                List<string> l;
                if (!b.TryGetValue(k[0], out l))
                {
                    l = new List<string>();
                    b[k[0]] = l;
                }
                l.Add(k);
            }
            foreach (KeyValuePair<char, List<string>> kv in b)
            {
                kv.Value.Sort((x, y) => y.Length != x.Length ? y.Length - x.Length : string.CompareOrdinal(x, y));
            }
            _buckets = b;
            return b;
        }

        private static string Convert(string s)
        {
            Dictionary<char, List<string>> b = Buckets();
            StringBuilder sb = new StringBuilder(s.Length + 8);
            int i = 0;
            while (i < s.Length)
            {
                List<string> cand;
                if (b.TryGetValue(s[i], out cand))
                {
                    for (int k = 0; k < cand.Count; k++)
                    {
                        string key = cand[k];
                        int n = key.Length;
                        if (n > s.Length - i) continue;
                        if (string.CompareOrdinal(s, i, key, 0, n) != 0) continue;
                        // 汉字挨着汉字 → 多半是游戏数据，别动
                        if (Han(key[0]) && i > 0 && Han(s[i - 1])) continue;
                        if (Han(key[n - 1]) && i + n < s.Length && Han(s[i + n])) continue;

                        string rep;
                        if (Map.TryGetValue(key, out rep)) sb.Append(rep);
                        i += n;
                        goto next;
                    }
                }
                sb.Append(s[i]);
                i++;
                next: ;
            }
            return sb.ToString();
        }

        private static bool HasHan(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (Han(s[i])) return true;
            }
            return false;
        }

        private static bool Han(char c)
        {
            return (c >= '\\u4E00' && c <= '\\u9FFF')
                || (c >= '\\u3400' && c <= '\\u4DBF')
                || (c >= '\\uF900' && c <= '\\uFAFF');
        }

        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
'''

TAIL = u'''        };
    }
}
'''


def main():
    m = load_map()
    keep = load_keep()
    need = load_need()
    b = buckets(m)

    miss = []
    for f, ln, lit in need:
        if lit in keep:
            continue
        r = conv(lit, m, b)
        if has_han(r):
            miss.append((f, ln, lit, r))

    ex_miss = []
    for s in EXTRA:
        r = conv(s, m, b)
        if has_han(r):
            ex_miss.append((s, r))

    print("对照表 %d 条，待译字面量 %d 条（豁免 %d 条）" % (len(m), len(need), len(keep)))
    print("缺条目：%d 条" % len(miss))
    for f, ln, lit, r in miss:
        print("  MISS %s:%s  %s   ==>   %s" % (f, ln, lit, r))
    if ex_miss:
        print("拼句检查不过：%d 条" % len(ex_miss))
        for s, r in ex_miss:
            print("  COMP %s   ==>   %s" % (s, r))

    lines = [HEAD]
    for k in sorted(m.keys()):
        lines.append("            { %s, %s },\n" % (cs(k), cs(m[k])))
    lines.append(TAIL)
    with io.open(OUT, "w", encoding="utf-8", newline="\r\n") as f:
        f.write(u"".join(lines).replace(u"\n", u"\r\n"))
    print("已写出 %s" % OUT)
    return 0


if __name__ == "__main__":
    sys.exit(main())