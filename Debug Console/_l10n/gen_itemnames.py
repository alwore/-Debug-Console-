# -*- coding: utf-8 -*-
"""
从 item_catalog.json 生成 ItemNames.cs（物品英文名对照表）。

中文名不用我们管 —— 游戏自己给（GeneralHelper.GetDisplayNameFromID）；
英文名游戏没有现成接口，于是把 441 条本地化文本抄成一张只读表。
物品名是运行时数据，不在 L10n 那张「界面文案」表里。
"""
import io
import json
import os

SRC = r"E:\a_开发\拆解的别的模组的dll\item_catalog.json"
DST = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "ItemNames.cs")


def lit(s):
    """转成 C# 字面量：反斜杠和引号转义，其余原样。"""
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def main():
    with io.open(SRC, encoding="utf-8") as f:
        rows = json.load(f)

    pairs = []
    seen = set()
    for r in rows:
        i = (r.get("id") or "").strip()
        e = (r.get("english") or "").strip()
        if not i or not e or i in seen:
            continue
        seen.add(i)
        pairs.append((i, e))
    pairs.sort(key=lambda p: p[0])

    out = []
    out.append("// 由 _l10n/gen_itemnames.py 生成，别手改。")
    out.append("// 数据来源：item_catalog.json（441 条，抄自游戏的本地化表）。")
    out.append("using System;")
    out.append("using System.Collections.Generic;")
    out.append("")
    out.append("namespace DebugConsole")
    out.append("{")
    out.append("    /// <summary>")
    out.append("    /// 物品的英文显示名。中文名由游戏自己给，英文名游戏没现成接口，")
    out.append("    /// 所以在这儿放一张只读对照表；查不到就让调用方回落中文名。")
    out.append("    /// </summary>")
    out.append("    internal static class ItemNames")
    out.append("    {")
    out.append("        private static readonly Dictionary<string, string> Map =")
    out.append("            new Dictionary<string, string>(StringComparer.Ordinal)")
    out.append("        {")
    for i, e in pairs:
        out.append("            { %s, %s }," % (lit(i), lit(e)))
    out.append("        };")
    out.append("")
    out.append("        /// <summary>英文名。查不到返回 null（调用方自己回落）。</summary>")
    out.append("        public static string En(string id)")
    out.append("        {")
    out.append("            if (string.IsNullOrEmpty(id)) return null;")
    out.append("            string v;")
    out.append("            return Map.TryGetValue(id, out v) ? v : null;")
    out.append("        }")
    out.append("    }")
    out.append("}")
    out.append("")

    path = os.path.normpath(DST)
    with io.open(path, "w", encoding="utf-8", newline="\r\n") as f:
        f.write("\n".join(out))
    print("写好了 %s，%d 条。" % (path, len(pairs)))


if __name__ == "__main__":
    main()