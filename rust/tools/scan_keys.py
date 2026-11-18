#!/usr/bin/env python3
"""Find stored JSON property names the Rust models would not read.

The C# reads stored keys case-INSENSITIVELY (System.Text.Json, PropertyNameCaseInsensitive);
the Rust serde models read exactly the camelCase the Node bot and the C# both WRITE. A document
holding "ScreenHits" where "screenHits" is expected reads fine in C# and silently as the default
in Rust. This lists every key that could be one of those, so they are checked before cutover.

Run it against a COPY of the live data, never the live file:

    cp /root/pavlov-bot/data/bot.db /tmp/bot-copy.db
    python3 rust/tools/scan_keys.py /tmp/bot-copy.db          # the SQLite kv table
    python3 rust/tools/scan_keys.py /root/pavlov-bot/data     # or a directory of <dataset>.json

A key is reported when it starts with an upper-case letter and is shaped like an identifier.
Map keys that are DATA (player names, IPs, ids) can look the same, so each finding prints its
path and a sample: "vpn_checks: {*}.ScreenHits (12 times)" is a property; a player called
"Holosight" in "{*}" position is not. Exit status 1 when anything is reported.
"""

import json
import os
import re
import sqlite3
import sys
from collections import Counter

IDENTIFIER = re.compile(r"^[A-Z][A-Za-z0-9_]*$")
# An object with more keys than this, none of them identifier-shaped property names we know,
# is treated as a map: its keys are data, and its values are walked under "{*}".
MAP_THRESHOLD = 24


def looks_like_map(obj):
    if len(obj) > MAP_THRESHOLD:
        return True
    keys = list(obj)
    # Ids, addresses and names with spaces are data, not properties.
    data_like = sum(1 for k in keys if not re.match(r"^[A-Za-z_][A-Za-z0-9_]*$", k))
    return bool(keys) and data_like * 2 >= len(keys)


def walk(value, path, found):
    if isinstance(value, list):
        for item in value:
            walk(item, path + "[]", found)
    elif isinstance(value, dict):
        as_map = looks_like_map(value)
        for key, child in value.items():
            if as_map:
                walk(child, path + ".{*}" if path else "{*}", found)
                continue
            if IDENTIFIER.match(key):
                found[(path + "." + key) if path else key] += 1
            walk(child, (path + "." + key) if path else key, found)


def documents(source):
    if os.path.isdir(source):
        for name in sorted(os.listdir(source)):
            if name.endswith(".json"):
                with open(os.path.join(source, name), encoding="utf-8") as handle:
                    yield name[: -len(".json")], handle.read()
        return
    connection = sqlite3.connect(f"file:{source}?mode=ro", uri=True)
    try:
        for dataset, data in connection.execute("SELECT file, data FROM kv ORDER BY file"):
            yield dataset, data
    finally:
        connection.close()


def main():
    if len(sys.argv) != 2:
        print(__doc__.strip().split("\n\n")[0])
        print(f"usage: {sys.argv[0]} <bot.db copy | data directory>")
        return 2
    reported = 0
    for dataset, text in documents(sys.argv[1]):
        try:
            document = json.loads(text) if text and text.strip() else None
        except json.JSONDecodeError as error:
            print(f"{dataset}: NOT JSON ({error}) - the Rust store will report it unreadable")
            reported += 1
            continue
        found = Counter()
        walk(document, "", found)
        for path, count in sorted(found.items()):
            print(f"{dataset}: {path} ({count} time{'s' if count != 1 else ''})")
            reported += 1
    if reported == 0:
        print("No upper-case property names found: every stored key is in the shape the Rust models read.")
        return 0
    print(f"\n{reported} finding(s). Each property above is read by the C# and IGNORED by the Rust build.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
