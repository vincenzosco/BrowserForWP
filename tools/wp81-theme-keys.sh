#!/usr/bin/env bash
# ───────────────────────────────────────────────────────────────────────────
#  Extract the theme-resource keys that Windows Phone 8.1 actually defines.
#
#  Why this exists
#  ---------------
#  A `{ThemeResource X}` in XAML is resolved at *load* time, not at compile
#  time, so nothing in the build pipeline rejects an unknown X. The VS2013 XAML
#  compiler does notice, but only as an internal error:
#
#      WMC9999: La chiave specificata non era presente nel dizionario
#               ("the given key was not present in the dictionary")
#
#  which tools/vm-build.cmd has always allow-listed as non-fatal noise. The
#  XAML *designer* says the same thing in English, with the key named:
#
#      The resource "TextControlBackground" could not be resolved.
#
#  Both are symptoms of one defect: a key used by the app that the platform does
#  not define. The page still compiles, still ships, and then fails to load on
#  the phone. That is the failure this file lets an off-Windows checker catch.
#
#  THE ORACLE IS THE PHONE'S DICTIONARY, NOT THE DESKTOP'S
#  ------------------------------------------------------
#  Windows 8.1 ships its own design dictionaries beside the phone's, and they
#  are NOT the same set. A key that exists only on the desktop (many UWP-era
#  names, `TextControlBackground` among them) compiles, ships and then throws on
#  the handset. Only Windows Phone Kits\8.1 is read, on purpose.
#
#  Usage:  bash tools/wp81-theme-keys.sh
#  Writes: tools/wp81-theme-keys.txt   (regenerate, do not hand-edit)
#  Exit:   0 if the snapshot was written and is plausibly complete, 1 otherwise.
# ───────────────────────────────────────────────────────────────────────────
set -uo pipefail

VM="{66a2f493-162c-4b3f-ba40-0a26020cc818}"
DESIGN='C:\Program Files (x86)\Windows Phone Kits\8.1\Include\abi\Xaml\Design'
ROOT="$(cd "$(dirname "$0")" && pwd)"
OUT="$ROOT/wp81-theme-keys.txt"

# both files: themeresources.xaml holds the brushes/colours, generic.xaml the
# control styles, and each defines keys the other references.
dump() {
  for f in themeresources.xaml generic.xaml; do
    prlctl exec "$VM" "cmd.exe" "/c" "type \"$DESIGN\\$f\""
  done
}

tmp="$(mktemp)"
trap 'rm -f "$tmp"' EXIT

dump | LC_ALL=C tr -d '\r' \
     | grep -o 'x:Key="[^"]*"' \
     | sed 's/^x:Key="//; s/"$//' \
     | LC_ALL=C sort -u > "$tmp"

count="$(wc -l < "$tmp" | tr -d ' ')"
# A short list means the guest did not answer, or answered with an error page.
# Silently writing 3 keys would turn the checker into a rubber stamp.
if [ "$count" -lt 200 ]; then
  echo "extraction failed: only $count keys read from $DESIGN" >&2
  exit 1
fi

{
  echo "# Theme-resource keys defined by Windows Phone 8.1."
  echo "#"
  echo "# GENERATED — do not hand-edit. Regenerate with:  bash tools/wp81-theme-keys.sh"
  echo "# Sources, inside the VS2013 guest:"
  echo "#   C:\\Program Files (x86)\\Windows Phone Kits\\8.1\\Include\\abi\\Xaml\\Design\\themeresources.xaml"
  echo "#   C:\\Program Files (x86)\\Windows Phone Kits\\8.1\\Include\\abi\\Xaml\\Design\\generic.xaml"
  echo "#"
  echo "# Consumers: the XAML theme-resource group in tools/check-vb.mjs._"
  echo "# Keys: $count"
  cat "$tmp"
} | sed 's/\._$//' > "$OUT"

echo "wrote $OUT ($count keys)"
