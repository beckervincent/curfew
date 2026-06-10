#!/bin/bash
# Bump version, tag and push. CI builds the installer and publishes the release.
# Usage: ./release.sh [new-version]   (default: bump patch)

set -e
cd "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ -n $(git status --porcelain | grep -v "Cargo.toml" | grep -v "Cargo.lock") ]]; then
    echo "Error: uncommitted changes. Commit or stash them first." >&2
    git status --short
    exit 1
fi

CURRENT=$(grep -m1 '^version' Cargo.toml | sed 's/.*"\(.*\)".*/\1/')

if [[ -n "$1" ]]; then
    NEW="$1"
else
    IFS='.' read -r MAJOR MINOR PATCH <<< "$CURRENT"
    NEW="${MAJOR}.${MINOR}.$((PATCH + 1))"
fi

echo "Version: $CURRENT -> $NEW"
read -p "Proceed with release v${NEW}? (y/N) " -n 1 -r
echo
[[ $REPLY =~ ^[Yy]$ ]] || { echo "Aborted."; exit 1; }

sed -i "s/^version = \"${CURRENT}\"/version = \"${NEW}\"/" Cargo.toml
cargo check --quiet 2>/dev/null || true   # refresh Cargo.lock if cargo is available

git add Cargo.toml Cargo.lock
git commit -m "Release v${NEW}"
git tag -a "v${NEW}" -m "Release v${NEW}"

BRANCH=$(git branch --show-current)
git push origin "$BRANCH" "v${NEW}"

echo "Done. The v${NEW} tag triggers the release build on GitHub Actions."
