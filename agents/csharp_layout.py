"""Make LLM-written C# readable when it comes back squeezed onto a few very long lines.

reflow() adds a line break after ; { and } outside string literals and
parentheses. That never changes what the code means. `dotnet format` then fixes
the indentation.
"""

import os
import subprocess
from pathlib import Path

LONG_LINE = 400


def _skip_string(text, i):
    """Return the index just after the string or char literal that starts at i."""
    verbatim = interpolated = False
    j = i
    while text[j] in "@$":
        verbatim |= text[j] == "@"
        interpolated |= text[j] == "$"
        j += 1
    quote = text[j]
    j += 1
    depth = 0
    while j < len(text):
        c = text[j]
        if interpolated and c == "{":
            if text[j + 1 : j + 2] == "{" and depth == 0:
                j += 2
                continue
            depth += 1
        elif interpolated and c == "}" and depth > 0:
            depth -= 1
        elif depth > 0 and c in "\"'":
            j = _skip_string(text, j)
            continue
        elif c == "\\" and not verbatim:
            j += 2
            continue
        elif c == quote and depth == 0:
            if verbatim and text[j + 1 : j + 2] == quote:
                j += 2
                continue
            return j + 1
        j += 1
    return j


def reflow(text):
    out, i, parens = [], 0, 0
    while i < len(text):
        c = text[i]
        if c in "\"'" or (c in "@$" and text[i + 1 : i + 3].lstrip("@$")[:1] == '"'):
            end = _skip_string(text, i)
            out.append(text[i:end])
            i = end
            continue
        if text.startswith("//", i):
            end = text.find("\n", i)
            end = len(text) if end < 0 else end
            out.append(text[i:end])
            i = end
            continue
        if c == "(":
            parens += 1
        elif c == ")":
            parens = max(0, parens - 1)
        if c == "}" and parens == 0:
            out.append("\n}")
        else:
            out.append(c)
        if parens == 0 and c in ";{}":
            out.append("\n")
        i += 1
    return "\n".join(line.rstrip() for line in "".join(out).splitlines() if line.strip()) + "\n"


def tidy(project_dir, files):
    """Reflow squeezed files, then let dotnet format fix whitespace in all of them."""
    for name in files:
        path = Path(project_dir) / name
        text = path.read_text()
        if max((len(line) for line in text.splitlines()), default=0) > LONG_LINE:
            path.write_text(reflow(text))
    env = dict(os.environ, PATH=os.environ.get("PATH", "") + ":/usr/local/share/dotnet")
    for name in files:
        subprocess.run(
            ["dotnet", "format", "whitespace", "--folder", str(project_dir), "--include", name],
            capture_output=True,
            env=env,
            timeout=300,
        )
