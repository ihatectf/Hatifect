#!/usr/bin/env bash
set -euo pipefail

release_root="$(cd "$(dirname "$0")" && pwd)"
verifier="$release_root/tools/release.py"
skip_tests="${HATIFECT_SKIP_TESTS:-0}"
skip_deploy="${HATIFECT_SKIP_DEPLOY:-1}"

[[ "$skip_tests" == "0" || "$skip_tests" == "1" ]] || {
  printf 'HATIFECT_SKIP_TESTS must be 0 or 1.\n' >&2
  exit 2
}
[[ "$skip_deploy" == "0" || "$skip_deploy" == "1" ]] || {
  printf 'HATIFECT_SKIP_DEPLOY must be 0 or 1.\n' >&2
  exit 2
}
release_base="$(python3 "$verifier" check-output "${HATIFECT_RELEASE_BASE:-$release_root/artifacts/package}")"
if [[ "$skip_deploy" == "0" ]]; then
  [[ -n "${HATIFECT_MODS_DIR:-}" ]] || {
    printf 'Explicit isolated deployment requires HATIFECT_MODS_DIR.\n' >&2
    exit 2
  }
  python3 "$verifier" check-isolated "$HATIFECT_MODS_DIR"
fi

python3 "$verifier" verify-source
python3 "$release_root/tools/validation.py" build --platform
if [[ "$skip_tests" == "0" ]]; then
  "$release_root/tools/hatifect-test" all --platform --no-build
else
  printf 'Tests were not run; this package has no test-completion evidence.\n'
fi

package_root="$(python3 "$verifier" assemble "$release_base")"
artifacts="$release_base/artifacts"
if [[ "$skip_tests" == "0" ]]; then
  python3 "$release_root/tools/rc_readiness.py" record-build \
    "$package_root" "$artifacts/rc-build-evidence.json" --tests-passed
else
  python3 "$release_root/tools/rc_readiness.py" record-build \
    "$package_root" "$artifacts/rc-build-evidence.json"
fi
python3 "$verifier" make-archive "$package_root" "$artifacts"

if [[ "$skip_deploy" == "0" ]]; then
  python3 "$verifier" deploy-isolated "$package_root" "$HATIFECT_MODS_DIR"
else
  printf 'Deployment is disabled.\n'
fi
printf 'Hatifect package assembled: %s\nRC promotion still requires current runtime evidence.\n' "$package_root"
