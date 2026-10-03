# -*- coding: utf-8 -*-
"""扫描项目里的 C# 源码，把所有含中文的字符串字面量抽出来（跳过注释）。
输出 _l10n/need.txt：唯一字面量 + 条数 + 首次出现的位置。"""
import os, re, sys, io

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "_l10n")
CJK = re.compile(r'[\u4e00-\u9fff\u3000-\u303f\uff00-\uffef]')
HAN = re.compile(r'[\u4e00-\u9fff]')

def literals(src):
    i, n = 0, len(src)
    line = 1
    while i < n:
        c = src[i]
        if c == '\n':
            line += 1; i += 1; continue
        if c == '/' and i + 1 < n and src[i + 1] == '/':
            j = src.find('\n', i)
            i = n if j < 0 else j
            continue
        if c == '/' and i + 1 < n and src[i + 1] == '*':
            j = src.find('*/', i + 2)
            if j < 0:
                i = n
            else:
                line += src.count('\n', i, j)
                i = j + 2
            continue
        if c == '@' and i + 1 < n and src[i + 1] == '"':
            j, buf = i + 2, []
            while j < n:
                if src[j] == '"':
                    if j + 1 < n and src[j + 1] == '"':
                        buf.append('"'); j += 2; continue
                    break
                if src[j] == '\n': line += 1
                buf.append(src[j]); j += 1
            yield ('v', line, ''.join(buf))
            i = j + 1; continue
        if c == '"':
            j, buf = i + 1, []
            while j < n:
                d = src[j]
                if d == '\\':
                    e = src[j + 1] if j + 1 < n else ''
                    buf.append({'n': '\n', 't': '\t', 'r': '\r', '"': '"', '\\': '\\'}.get(e, e))
                    j += 2; continue
                if d == '"':
                    break
                if d == '\n': line += 1
                buf.append(d); j += 1
            yield ('s', line, ''.join(buf))
            i = j + 1; continue
        if c == "'":
            j = i + 1
            while j < n:
                if src[j] == '\\':
                    j += 2; continue
                if src[j] == "'":
                    break
                j += 1
            i = j + 1; continue
        i += 1

# 生成出来的、不走 L10n 的文件：里面的中文是游戏数据（物品名的兜底），不用翻
SKIP = {'L10n.cs', 'ItemNames.cs'}

def main():
    counts, where = {}, {}
    for fn in sorted(os.listdir(ROOT)):
        if not fn.endswith('.cs') or fn in SKIP:
            continue
        path = os.path.join(ROOT, fn)
        with io.open(path, encoding='utf-8-sig') as f:
            src = f.read()
        for kind, line, text in literals(src):
            if not CJK.search(text):
                continue
            if text not in counts:
                counts[text] = 0
                where[text] = '%s:%d' % (fn, line)
            counts[text] += 1

    items = sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))
    with io.open(os.path.join(OUT, 'need.txt'), 'w', encoding='utf-8') as f:
        for text, cnt in items:
            f.write('%s\t%d\t%s\n' % (where[text], cnt, text.replace('\n', '\\n').replace('\t', '\\t')))
    han = sum(1 for t, _ in items if HAN.search(t))
    print('unique literals with CJK: %d (with hanzi: %d, total occurrences: %d)'
          % (len(items), han, sum(counts.values())))

main()