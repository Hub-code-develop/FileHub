#!/usr/bin/env bash
# Generate a markdown changelog from the latest stable vX.Y.Z tag to HEAD.
# When no stable tag exists yet, fall back to the last 20 commits.
set -euo pipefail

REPO_URL="${1:-https://github.com/Hub-code-develop/FileHub}"
STABLE_TAG="$(git tag -l --sort=-v:refname 'v*' | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | head -n1 || true)"

if [ -n "$STABLE_TAG" ]; then
  RANGE="${STABLE_TAG}..HEAD"
else
  RANGE="-20"
fi

git log --no-merges --pretty=tformat:"- %s ([%h](${REPO_URL}/commit/%H))" "$RANGE"
