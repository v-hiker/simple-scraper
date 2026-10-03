#!/usr/bin/env python3
"""Compare the release source with an explicitly supplied reference checkout.

Findings are review aids. No original source text, credential values or secret
fingerprints are written to the report, and similarity is not a legal opinion.
Only Git's committed HEAD is read from the reference project.
"""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import difflib
import hashlib
from io import BytesIO
import json
from pathlib import Path
import re
import subprocess


EXCLUDED = {".git", ".vs", "bin", "obj", "artifacts", ".tools", "node_modules", "packages", "__pycache__"}
TEXT_EXTENSIONS = {".cs", ".xaml", ".xml", ".json", ".md", ".ps1", ".py", ".yml", ".yaml", ".props", ".csproj", ".svg", ".manifest", ".iss"}
TOKEN = re.compile(
    r'(?P<comment>//[^\r\n]*|/\*[\s\S]*?\*/)|'
    r'(?P<string>@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\')|'
    r'(?P<word>[A-Za-z_]\w*|\d+(?:\.\d+)?)|(?P<punct>[^\s])'
)
SECRET_ASSIGNMENT = re.compile(
    r'(?i)\b\w*(?:api[_-]?key|access[_-]?token|bearer[_-]?token|password|secret)\s*[=:]\s*["\']([^"\'\r\n]{16,})["\']'
)
RAW_KEY = re.compile(r'["\']([a-fA-F0-9]{32})["\']|\b(eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+)\b|\b(AIza[A-Za-z0-9_-]{35})\b')
OLD_MARKERS = ("CineLibraryEssentials", "aungkokomm", "MediaScraper/1.9", "33e6332", "DefaultTmdbApiKey")


def git(upstream: Path, *args: str) -> bytes:
    completed = subprocess.run(["git", "-C", str(upstream), *args], check=True, capture_output=True)
    return completed.stdout


def source_files(root: Path) -> list[Path]:
    return sorted(path for path in root.rglob("*") if path.is_file() and not EXCLUDED.intersection(path.relative_to(root).parts))


def decode(path: str, value: bytes) -> str | None:
    if Path(path).suffix.lower() not in TEXT_EXTENSIONS and Path(path).name != "LICENSE":
        return None
    try:
        return value.decode("utf-8-sig")
    except UnicodeDecodeError:
        return None


def tokens(text: str) -> tuple[list[str], list[int]]:
    values: list[str] = []
    lines: list[int] = []
    line, position = 1, 0
    for match in TOKEN.finditer(text):
        line += text.count("\n", position, match.start())
        position = match.start()
        if match.lastgroup == "comment":
            continue
        value = match.group()
        # Renaming a namespace alone must not hide an implementation match.
        values.append("SimpleScraper" if value == "CineLibraryEssentials" else value)
        lines.append(line)
    return values, lines


def compare_rasters(originals: dict[str, bytes], current: dict[str, bytes]) -> dict:
    try:
        from PIL import Image
    except ImportError:
        return {"available": False, "reason": "Optional Pillow package unavailable; byte hashes still checked."}
    raster_extensions = {".ico", ".png", ".jpg", ".jpeg", ".webp", ".bmp"}
    comparisons = []
    for old_name, old_bytes in originals.items():
        if Path(old_name).suffix.lower() not in raster_extensions:
            continue
        with Image.open(BytesIO(old_bytes)) as original:
            old = original.convert("RGBA").resize((256, 256), Image.Resampling.LANCZOS)
        for name, value in current.items():
            if Path(name).suffix.lower() not in raster_extensions:
                continue
            with Image.open(BytesIO(value)) as image:
                new = image.convert("RGBA").resize((256, 256), Image.Resampling.LANCZOS)
            old_data, new_data = old.tobytes(), new.tobytes()
            visible, error = 0, 0
            for offset in range(0, len(old_data), 4):
                if old_data[offset + 3] == 0 and new_data[offset + 3] == 0:
                    continue
                visible += 1
                error += sum(abs(old_data[offset + channel] - new_data[offset + channel]) for channel in range(4))
            comparisons.append({
                "current": name,
                "upstream": old_name,
                "same_decoded_pixels": old_data == new_data,
                "visible_mean_channel_difference": round(error / (visible * 4), 3) if visible else 0,
            })
    return {"available": True, "normalized_size": [256, 256], "comparisons": comparisons}


def audit(root: Path, upstream: Path, minimum: int) -> dict:
    committed = git(upstream, "ls-tree", "-r", "--name-only", "-z", "HEAD").decode("utf-8").split("\0")
    originals = {name: git(upstream, "show", "HEAD:" + name) for name in committed if name}
    current = {path.relative_to(root).as_posix(): path.read_bytes() for path in source_files(root)}
    original_hashes: dict[bytes, list[str]] = defaultdict(list)
    for name, data in originals.items():
        original_hashes[hashlib.sha256(data).digest()].append(name)
    identical = [
        {"current": name, "upstream": original}
        for name, data in current.items()
        for original in original_hashes.get(hashlib.sha256(data).digest(), [])
    ]
    old_text = {name: text for name, data in originals.items() if (text := decode(name, data)) is not None}
    new_text = {name: text for name, data in current.items() if (text := decode(name, data)) is not None}
    old_tokens = {name: tokens(text) for name, text in old_text.items()}
    new_tokens = {name: tokens(text) for name, text in new_text.items()}
    window = min(24, minimum)
    index: dict[int, set[str]] = defaultdict(set)
    for name, (values, _) in old_tokens.items():
        for start in range(len(values) - window + 1):
            index[hash(tuple(values[start:start + window]))].add(name)
    blocks = []
    for current_name, (new_values, new_lines) in new_tokens.items():
        candidates: Counter[str] = Counter()
        for start in range(len(new_values) - window + 1):
            candidates.update(index.get(hash(tuple(new_values[start:start + window])), ()))
        for original, count in candidates.items():
            if count < minimum - window + 1:
                continue
            old_values, old_lines = old_tokens[original]
            for block in difflib.SequenceMatcher(None, old_values, new_values, autojunk=False).get_matching_blocks():
                if block.size < minimum:
                    continue
                schema = original == "Models/MovieMetadata.cs" and current_name.endswith("/Models/TmdbMetadata.cs")
                blocks.append({
                    "current": current_name,
                    "upstream": original,
                    "tokens": block.size,
                    "current_lines": [new_lines[block.b], new_lines[block.b + block.size - 1]],
                    "upstream_lines": [old_lines[block.a], old_lines[block.a + block.size - 1]],
                    "review_category": "tmdb_data_contract" if schema else "review_implementation_or_framework_boilerplate",
                })
    markers, credentials = [], []
    own_script = "scripts/audit-provenance.py"
    for name, text in new_text.items():
        if name == own_script:
            continue
        for number, line in enumerate(text.splitlines(), 1):
            for marker in OLD_MARKERS:
                if marker.casefold() in line.casefold():
                    markers.append({"path": name, "line": number, "marker": marker, "documentation": name.startswith("docs/")})
            possible = list(SECRET_ASSIGNMENT.finditer(line)) + list(RAW_KEY.finditer(line))
            for candidate in possible:
                literal = next((item for item in candidate.groups() if item), "")
                if any(word in literal.lower() for word in ("test", "placeholder", "your-", "example")):
                    continue
                credentials.append({"path": name, "line": number, "finding": "possible embedded credential; value omitted"})
    return {
        "scope": "release source, tests, scripts and documentation; excludes generated builds and artifacts",
        "reference": "committed Git HEAD, excluding uncommitted local library additions",
        "reference_files": len(originals),
        "current_files": len(current),
        "token_threshold": minimum,
        "utf8_bom_files": [name for name, data in current.items() if data.startswith(b"\xef\xbb\xbf")],
        "identical_files": identical,
        "raster_comparison": compare_rasters(originals, current),
        "matching_blocks": sorted(blocks, key=lambda item: (-item["tokens"], item["current"])),
        "old_project_markers": markers,
        "possible_credentials": credentials,
        "limitation": "Heuristic evidence only. Data-contract fields and framework boilerplate need human review; absence of matches does not establish authorship or license clearance.",
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--upstream", type=Path, required=True, help="Reference checkout; committed HEAD is read without modifying it.")
    parser.add_argument("--output", type=Path, help="Optional JSON findings path; source text and credential values are omitted.")
    parser.add_argument("--min-tokens", type=int, default=70, help="Minimum contiguous noncomment token match (default: 70).")
    options = parser.parse_args()
    if options.min_tokens < 24:
        parser.error("--min-tokens must be at least 24")
    root = Path(__file__).resolve().parent.parent
    result = audit(root, options.upstream.resolve(), options.min_tokens)
    serialized = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
    if options.output:
        options.output.parent.mkdir(parents=True, exist_ok=True)
        options.output.write_text(serialized, encoding="utf-8")
    print(serialized, end="")
    # Findings require classification, not an automatic claim of legal status.
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
