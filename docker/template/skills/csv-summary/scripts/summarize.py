#!/usr/bin/env python3
"""Summarise a CSV file as Markdown: row count, and per column its type, blanks and either
numeric statistics or the most common values.

Usage: python3 summarize.py FILE.csv [--top N]

Standard library only, reads the file once (streaming), never touches the network and never
writes anything: the summary goes to stdout.
"""

import argparse
import csv
import math
import sys
from collections import Counter

MAX_DISTINCT_TRACKED = 10_000  # stop counting distinct text values beyond this (memory cap)


def parse_number(text):
    """A float for numeric-looking text (allows thousands separators, %, and a leading currency sign)."""
    t = text.strip().replace(",", "")
    if t.endswith("%"):
        t = t[:-1]
    if t[:1] in "$£€":
        t = t[1:]
    try:
        value = float(t)
    except ValueError:
        return None
    return value if math.isfinite(value) else None


class Column:
    def __init__(self, name):
        self.name = name
        self.blank = 0
        self.numbers = 0
        self.texts = 0
        self.total = 0.0
        self.minimum = None
        self.maximum = None
        self.values = Counter()
        self.overflow = False

    def add(self, raw):
        text = raw.strip()
        if not text:
            self.blank += 1
            return
        number = parse_number(text)
        if number is None:
            self.texts += 1
        else:
            self.numbers += 1
            self.total += number
            self.minimum = number if self.minimum is None else min(self.minimum, number)
            self.maximum = number if self.maximum is None else max(self.maximum, number)
        if text in self.values or len(self.values) < MAX_DISTINCT_TRACKED:
            self.values[text] += 1
        else:
            self.overflow = True

    @property
    def numeric(self):
        return self.numbers > 0 and self.texts == 0


def fmt(value):
    if float(value).is_integer():
        return f"{value:,.0f}"
    return f"{value:,.2f}" if abs(value) >= 1 else f"{value:.4g}"


def cell(text):
    """Escape a value for a Markdown table cell."""
    return text.replace("|", "\\|").replace("\n", " ")[:60]


def summarise(path, top):
    with open(path, newline="", encoding="utf-8-sig", errors="replace") as handle:
        sample = handle.read(64 * 1024)
        handle.seek(0)
        try:
            dialect = csv.Sniffer().sniff(sample, delimiters=",;\t|")
        except csv.Error:
            dialect = csv.excel
        reader = csv.reader(handle, dialect)
        header = next(reader, None)
        if header is None:
            return f"# Summary of {path}\n\nThe file is empty.\n"
        columns = [Column(name.strip() or f"column {i + 1}") for i, name in enumerate(header)]
        rows = ragged = 0
        for row in reader:
            if not any(field.strip() for field in row):
                continue
            rows += 1
            if len(row) != len(columns):
                ragged += 1
            for column, value in zip(columns, row):
                column.add(value)

    out = [f"# Summary of {path}", "", f"- Rows: {rows:,}", f"- Columns: {len(columns)}"]
    if ragged:
        out.append(f"- Rows with a different number of fields than the header: {ragged:,}")
    out += ["", "## Columns", "", "| Column | Type | Blank | Summary |", "|---|---|---|---|"]
    for c in columns:
        if c.numeric:
            mean = c.total / c.numbers
            summary = f"min {fmt(c.minimum)}, max {fmt(c.maximum)}, mean {fmt(mean)}, sum {fmt(c.total)}"
            kind = "number"
        else:
            distinct = f"{len(c.values):,}{'+' if c.overflow else ''} distinct"
            common = ", ".join(f"{cell(v)} ({n:,})" for v, n in c.values.most_common(top))
            summary = f"{distinct}; most common: {common}" if common else distinct
            kind = "text" if c.numbers == 0 else "mixed"
        out.append(f"| {cell(c.name)} | {kind} | {c.blank:,} | {summary} |")
    return "\n".join(out) + "\n"


def main():
    parser = argparse.ArgumentParser(description="Summarise a CSV file as Markdown.")
    parser.add_argument("file", help="path to the CSV file")
    parser.add_argument("--top", type=int, default=3, help="most common values to show per text column (default 3)")
    args = parser.parse_args()
    try:
        sys.stdout.write(summarise(args.file, max(1, min(args.top, 10))))
    except FileNotFoundError:
        sys.exit(f"summarize.py: no such file: {args.file}")
    except IsADirectoryError:
        sys.exit(f"summarize.py: {args.file} is a folder, not a CSV file")


if __name__ == "__main__":
    main()
