#!/usr/bin/env bash
# Copies agent souls out of a SharpClaw install and back in, e.g. around a wipe and reinstall.
#   scripts/souls.sh backup  [-d dir] [-b folder]             (copies to <folder>/<agent>/soul.md)
#   scripts/souls.sh restore [-d dir] [-b folder] [agent...]  (default: every agent in the folder)
# dir is the install (default: ~/sharpclaw), folder the copy (default: ~/sharpclaw-backup).
# Restore only fills agents the install already has
# (create them first, e.g. with quickstart.sh); agents read their soul every turn, so no restart.
set -euo pipefail

usage() {
  sed -n '3,4s/^#  //p' "$0" >&2
  exit 2
}
die() { echo "error: $*" >&2; exit 1; }

[ $# -ge 1 ] || usage
CMD="$1"; shift

DIR="$HOME/sharpclaw"
BACKUP="$HOME/sharpclaw-backup"
while getopts "d:b:h" OPT; do
  case "$OPT" in
    d) DIR="$OPTARG" ;;
    b) BACKUP="$OPTARG" ;;
    *) usage ;;
  esac
done
shift $((OPTIND - 1))

[ -d "$DIR/agents" ] || die "$DIR is not a SharpClaw install (no agents/ directory)"
DIR="$(realpath "$DIR")"
# The gateway's agent name rule (Validation.IsValidAgentName).
NAME_RE='^[a-z0-9][a-z0-9-]{1,31}$'

backup() {
  [ $# -eq 0 ] || usage
  local out
  out="$(realpath -m "$BACKUP")"
  # A folder inside the install would be lost with the install it is meant to outlive.
  case "$out/" in "$DIR"/*) die "$out is inside the install $DIR; pick a folder outside it" ;; esac
  mkdir -p "$out"

  local count=0 f name
  for f in "$DIR"/agents/*/config/soul.md; do
    [ -f "$f" ] || continue
    name="${f#"$DIR"/agents/}"; name="${name%%/*}"
    if [ -e "$out/$name/soul.md" ] && ! cmp -s "$f" "$out/$name/soul.md"; then
      die "$out/$name/soul.md already exists and differs; remove it or pick another folder with -b"
    fi
    mkdir -p "$out/$name"
    cp "$f" "$out/$name/soul.md"
    echo "copied $name"
    count=$((count + 1))
  done
  [ "$count" -gt 0 ] || die "no agents/*/config/soul.md in $DIR"
  echo "$count soul(s) in $out"
}

restore() {
  local src="$BACKUP"
  [ -d "$src" ] || die "no such folder: $src"

  local found=() f name dest
  for f in "$src"/*/soul.md; do
    [ -f "$f" ] || continue
    name="${f%/soul.md}"; name="${name##*/}"
    [[ "$name" =~ $NAME_RE ]] || continue
    if [ $# -gt 0 ] && [[ " $* " != *" $name "* ]]; then continue; fi
    found+=("$name")
    dest="$DIR/agents/$name/config/soul.md"
    if [ ! -d "$DIR/agents/$name/config" ]; then
      echo "skipped $name: not in this install (create the agent first, e.g. with quickstart.sh)" >&2
      continue
    fi
    if [ -f "$dest" ] && cmp -s "$f" "$dest"; then
      echo "unchanged $name"; continue
    fi
    cp "$f" "$dest"
    echo "restored $name"
  done
  [ ${#found[@]} -gt 0 ] || [ $# -gt 0 ] || die "no <agent>/soul.md in $src"

  local a
  for a in "$@"; do
    [[ " ${found[*]-} " == *" $a "* ]] || echo "not in $src: $a" >&2
  done
}

case "$CMD" in
  backup) backup "$@" ;;
  restore) restore "$@" ;;
  *) usage ;;
esac
