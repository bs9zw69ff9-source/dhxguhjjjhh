#!/usr/bin/env python3
"""Remove factions from the bot's factions file, in place, by name.

    python3 scripts/remove-faction.py Kings Followers          # finds the file from .env
    python3 scripts/remove-faction.py --file f.json Kings      # or name it outright
    python3 scripts/remove-faction.py --dry-run Kings          # say what would change

IT FINDS THE FILE ITSELF, because the alternative was a shell one-liner to dig
FACTIONS_PATH out of .env, and that one-liner got the path wrong - which is the
exact failure this whole tool exists to stop. FACTIONS_PATH is resolved the way
the BOT resolves it, using dotenv 16's grammar as ported in
DotEnvConfiguration.cs: `export` prefixes ignored, `KEY=value` and `KEY: value`
both bind, quotes stripped, an unquoted value ending at the first `#`, and the
last assignment of a key winning. A real environment variable beats the file,
as it does for the bot. A relative path resolves against the directory holding
.env, which is the bot's working directory.

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
import argparse
import os
import re
import shutil
import sys


# dotenv 16's LINE regex, the same one DotEnvConfiguration.cs mirrors. Kept as one
# expression so the correspondence to the bot's parser stays checkable.
_LINE = re.compile(
    r"""^\s*(?:export\s+)?([\w.-]+)(?:\s*=\s*?|:\s+?)"""
    r"""(\s*'(?:\\'|[^'])*'|\s*"(?:\\"|[^"])*"|\s*`(?:\\`|[^`])*`|[^#\r\n]+)?\s*(?:#.*)?$""",
    re.MULTILINE,
)


def _unquote(raw):
    """Strip matching quotes. Escapes expand only inside double quotes, as dotenv does."""
    if len(raw) < 2:
        return raw
    first = raw[0]
    if first in "\"'`" and raw[-1] == first:
        inner = raw[1:-1]
        return inner.replace("\\n", "\n").replace("\\r", "\r") if first == '"' else inner
    return raw


def parse_env(text):
    """A .env file as a dict. Last assignment of a key wins, as it does for the bot."""
    found = {}
    for match in _LINE.finditer(text.replace("\r\n", "\n")):
        key = match.group(1)
        if key:
            found[key] = _unquote((match.group(2) or "").strip())
    return found


def find_factions_file(env_path):
    """FACTIONS_PATH as the bot would resolve it, or None."""
    # A real environment variable beats the file - the bot registers .env first so that
    # AddEnvironmentVariables can override it, and a container override has to work.
    value = os.environ.get("FACTIONS_PATH")
    source = "the FACTIONS_PATH environment variable"

    if not value:
        if not os.path.isfile(env_path):
            raise Failure(
                f"no {env_path} to read FACTIONS_PATH from.",
                "Run this from the bot's directory, or pass --file with the factions file.",
            )
        with open(env_path, encoding="utf-8") as handle:
            value = parse_env(handle.read()).get("FACTIONS_PATH")
        source = env_path

    if not value:
        raise Failure(
            f"FACTIONS_PATH is not set in {source}.",
            "Without it the bot runs the BUILT-IN factions (Gambino, Colombo, NYPD) and there",
            "is no file to edit. Set FACTIONS_PATH, or pass --file.",
        )

    # A relative path resolves against the directory holding .env, which is the bot's
    # working directory - not wherever this script happens to be run from.
    resolved = os.path.join(os.path.dirname(os.path.abspath(env_path)), value)
    return os.path.abspath(resolved), source


class Failure(Exception):
    """An operator error. Printed as lines, never as a traceback."""



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
    parser = argparse.ArgumentParser(
        description="Remove factions from the bot's factions file, by name.",
        epilog="With no --file, FACTIONS_PATH is read from .env the same way the bot reads it.")
    parser.add_argument("factions", nargs="+", metavar="FACTION", help="faction names to remove")
    parser.add_argument("--file", help="the factions JSON file (default: from FACTIONS_PATH)")
    parser.add_argument("--env", default=".env", help="the .env to read FACTIONS_PATH from")
    parser.add_argument("--dry-run", action="store_true", help="say what would change, write nothing")
    args = parser.parse_args()

    names = list(args.factions)
    path = args.file

    # Back compatibility: the first positional used to be the file.
    if path is None and os.path.isfile(names[0]) and len(names) > 1:
        path, names = names[0], names[1:]

    source = "--file"
    if path is None:
        path, source = find_factions_file(args.env)

    if not os.path.isfile(path):
        raise Failure(
            f"{path} does not exist.",
            f"That is what {source} points at. Check the path, or pass --file.",
        )

    with open(path, encoding="utf-8") as handle:
        original = handle.read()

    print(f"reading {path}")
    print(f"  currently: {', '.join(n for _, _, n in _elements(original)) or '(none)'}")

    updated = strip(original, names)
    if updated == original:
        print("nothing changed")
        return 1

    remaining = ", ".join(n for _, _, n in _elements(updated)) or "(none)"
    if args.dry_run:
        print(f"  would leave: {remaining}")
        print("dry run - nothing written")
        return 0

    shutil.copyfile(path, path + ".bak")
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(updated)

    print(f"  now: {remaining}")
    print(f"previous file kept at {path}.bak")
    print("restart the bot for it to re-read the file and re-register its commands")
    return 0


if __name__ == "__main__":
    # An operator error prints as lines, not as a traceback: this edits a live config and
    # a stack trace is the least useful thing to hand somebody who mistyped a path.
    try:
        sys.exit(main())
    except Failure as failure:
        for line in failure.args:
            print(line, file=sys.stderr)
        sys.exit(2)
    except OSError as error:
        print(f"could not read or write that file: {error}", file=sys.stderr)
        sys.exit(2)
