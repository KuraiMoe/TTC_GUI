# -*- coding: utf-8 -*-
"""Generates data/card_pinyin.json: card id -> [initials, full pinyin] for picker search."""
import csv
import json
import re
import sys

from pypinyin import lazy_pinyin

ROOT = r"C:\Users\zewux\Documents\TTC_GUI"
CSV_PATH = ROOT + r"\data\幻卡数据库.csv"
OUT_PATH = ROOT + r"\data\card_pinyin.json"

CJK = re.compile(r"[一-鿿]")


def to_pinyin(name: str):
    chars = [ch for ch in name if CJK.match(ch)]
    if not chars:
        return None
    parts = lazy_pinyin(chars)
    parts = [p.lower() for p in parts if p]
    if not parts:
        return None
    initials = "".join(p[0] for p in parts)
    full = "".join(parts)
    return [initials, full]


def main():
    result = {}
    with open(CSV_PATH, encoding="utf-8-sig") as f:
        for row in csv.DictReader(f):
            cid = int(row["序号"])
            py = to_pinyin(row["名称"].strip())
            if py:
                result[str(cid)] = py
    with open(OUT_PATH, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, separators=(",", ":"))
    print("wrote", OUT_PATH, "entries:", len(result))
    for cid in ("1", "2", "4"):
        print(cid, result.get(cid))


if __name__ == "__main__":
    sys.exit(main())
