#!/usr/bin/env python3
"""Remove factions from the bot's factions file, in place, by name.

    python3 scripts/remove-faction.py /path/to/factions.json Kings Followers

WHY NOT jq. The file is hand-edited config: FactionsFile.Load allows // comments
and trailing commas, and no JSON writer round-trips either. Parsing and
re-emitting would silently strip every comment in the file. So the faction
objects are cut out by brace matching and every other byte - comments,
alignment, blank lines - is left exactly as it was.

THE OBJECTS ARE FOUND STRUCTURALLY, not by searching for the name. A rank or a
sub-class may share a faction's name ("Elder" is both a Brotherhood rank and a
plausible faction), and a search that took the first match would cut the
enclosing object out of the middle of a different faction. Only the top-level
elements of the "factions" array are considered.

The previous file is kept alongside as <name>.bak.
"""
import re
import shutil
import sys


def _match_brace(text, start):
    """Index just past the '}' closing the '{' at start, ignoring braces in strings."""
    depth, i, in_string, escaped = 0, start, False, False
    while i < len(text):
        c = text[i]
        if in_string:
            if escaped:
                escaped = False
            elif c == "\\":
                escaped = True
            elif c == '"':
                in_string = False
        elif c == '"':
            in_string = True
        elif c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    raise ValueError("unbalanced braces - is this the right file?")


def _elements(text):
    """(start, end, name) for each top-level object in the "factions" array."""
    array = re.search(r'"factions"\s*:\s*\[', text)
    if not array:
        raise ValueError('no "factions" array in this file')

    found, i = [], array.end()
    while i < len(text):
        c = text[i]
        if c == "]":
            break
        if c != "{":
            i += 1
            continue

        end = _match_brace(text, i)
        # The element's OWN name is its first "name" key, before any NESTED object -
        # searched from 1, because index 0 is this element's own opening brace.
        body = text[i:end]
        nested = body.find("{", 1)
        name = re.search(r'"name"\s*:\s*"([^"]*)"', body if nested < 0 else body[:nested])
        found.append((i, end, name.group(1) if name else None))
        i = end

    return found


def strip(text, names):
    wanted = {n.casefold() for n in names}
    seen = set()

    # Removed back to front, so an earlier element's offsets stay valid.
    for start, end, name in reversed(_elements(text)):
        if name is None or name.casefold() not in wanted:
            continue
        seen.add(name.casefold())

        # Take the separating comma with it, whichever side it is on, so the array
        # stays well formed whether this was the last element or not.
        tail = text[end:]
        comma = re.match(r"\s*,", tail)
        if comma:
            end += comma.end()
        else:
            head = text[:start].rstrip()
            if head.endswith(","):
                start = len(head) - 1

        text = text[:start].rstrip(" \t") + text[end:].lstrip(" \t")
        text = re.sub(r"\n[ \t]*\n[ \t]*\n", "\n\n", text)

    for missing in sorted(wanted - seen):
        print(f'  "{missing}" is not a faction in this file - nothing to remove', file=sys.stderr)

    return text


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)

    path, names = sys.argv[1], sys.argv[2:]
    with open(path, encoding="utf-8") as handle:
        original = handle.read()

    updated = strip(original, names)
    if updated == original:
        print("nothing changed")
        return 1

    shutil.copyfile(path, path + ".bak")
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(updated)

    remaining = [n for _, _, n in _elements(updated)]
    print(f"{path} now has {len(remaining)} faction(s): {', '.join(remaining)}")
    print(f"previous file kept at {path}.bak")
    print("restart the bot for it to re-read the file and re-register its commands")
    return 0


if __name__ == "__main__":
    sys.exit(main())
