#!/usr/bin/env bash
set -euo pipefail

TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$TOOLS_DIR/.." && pwd)"

# Explicit process/shell values override machine-local defaults from hatifect.env.
# Keep this list aligned with tools/hatifect.env.example so one-off invocations are
# deterministic without rewriting a developer's ignored configuration file.
_hatifect_configurable_variables=(
  HATIFECT_SOLUTION
  HATIFECT_CONFIGURATION
  HATIFECT_SMAPI_PATH
  HATIFECT_SMAPI_TEST_ROOT
  HATIFECT_TEST_TIMEOUT_SECONDS
  HATIFECT_TEST_SEED
  HATIFECT_LIVE_DOTNET
  HATIFECT_LIVE_TEST_DOTNET
  HATIFECT_LIVE_RUN_HOST_FREE_TESTS
  HATIFECT_VALIDATOR
  HATIFECT_WARNINGS_AS_ERRORS
)
_hatifect_external_variable_names=()
_hatifect_external_variable_values=()
for _hatifect_variable_name in "${_hatifect_configurable_variables[@]}"; do
  if [[ -n "${!_hatifect_variable_name+x}" ]]; then
    _hatifect_external_variable_names+=("$_hatifect_variable_name")
    _hatifect_external_variable_values+=("${!_hatifect_variable_name}")
  fi
done

if [[ -f "$TOOLS_DIR/hatifect.env" ]]; then
  # shellcheck disable=SC1091
  source "$TOOLS_DIR/hatifect.env"
fi
for _hatifect_variable_index in "${!_hatifect_external_variable_names[@]}"; do
  printf -v "${_hatifect_external_variable_names[$_hatifect_variable_index]}" '%s' \
    "${_hatifect_external_variable_values[$_hatifect_variable_index]}"
done
unset _hatifect_configurable_variables
unset _hatifect_external_variable_names
unset _hatifect_external_variable_values
unset _hatifect_variable_index
unset _hatifect_variable_name

HATIFECT_CONFIGURATION="${HATIFECT_CONFIGURATION:-Debug}"
HATIFECT_WARNINGS_AS_ERRORS="${HATIFECT_WARNINGS_AS_ERRORS:-0}"

say() {
  printf '%s\n' "$*"
}

die() {
  printf 'ERROR: %s\n' "$*" >&2
  exit 2
}

need_cmd() {
  command -v "$1" >/dev/null 2>&1 || die "Required command not found: $1"
}

resolve_smapi_test_root() {
  need_cmd python3

  local configured="${HATIFECT_SMAPI_TEST_ROOT:-$REPO_ROOT/.smapi-test/isolated}"
  local resolved=""
  local repository_harness_root=""
  local temporary_root=""

  resolved="$(python3 -c 'import os, sys; print(os.path.realpath(sys.argv[1]))' "$configured")"
  repository_harness_root="$(python3 -c 'import os, sys; print(os.path.realpath(sys.argv[1]))' "$REPO_ROOT/.smapi-test")"
  temporary_root="$(python3 -c 'import os, sys; print(os.path.realpath(sys.argv[1]))' "${TMPDIR:-/tmp}")"

  case "$resolved" in
    "$repository_harness_root"/*|"$temporary_root"/hatifect-smapi-test.*) ;;
    *)
      die "HATIFECT_SMAPI_TEST_ROOT must stay below .smapi-test or use a hatifect-smapi-test.* directory below the temporary root: $resolved"
      ;;
  esac

  printf '%s\n' "$resolved"
}

has_supported_build_toolchain() {
  local candidate="$1"
  [[ -x "$candidate" ]] || return 1
  "$candidate" --list-sdks 2>/dev/null | grep -Eq '^8\.0\.'
}

has_supported_test_toolchain() {
  local candidate="$1"
  has_supported_build_toolchain "$candidate" || return 1
  "$candidate" --list-runtimes 2>/dev/null | grep -Eq '^Microsoft\.NETCore\.App 6\.0\.'
}

resolve_build_dotnet() {
  if [[ -n "${HATIFECT_DOTNET:-}" ]]; then
    [[ -x "$HATIFECT_DOTNET" ]] || die "HATIFECT_DOTNET is not executable: $HATIFECT_DOTNET"
    has_supported_build_toolchain "$HATIFECT_DOTNET" || \
      die "HATIFECT_DOTNET must provide the supported .NET 8 SDK."
    printf '%s\n' "$HATIFECT_DOTNET"
    return
  fi

  local candidate=""
  local candidates=(
    "$(command -v dotnet 2>/dev/null || true)"
    "${HOME}/.dotnet/hatifect-x64-8/dotnet"
    "/usr/local/share/dotnet/x64/dotnet"
  )
  for candidate in "${candidates[@]}"; do
    if [[ -n "$candidate" ]] && has_supported_build_toolchain "$candidate"; then
      printf '%s\n' "$candidate"
      return
    fi
  done
  die "Builds require .NET 8 SDK; set HATIFECT_DOTNET to a supported dotnet executable."
}

resolve_test_dotnet() {
  if [[ -n "${HATIFECT_TEST_DOTNET:-}" ]]; then
    [[ -x "$HATIFECT_TEST_DOTNET" ]] || \
      die "Compatible test runner is not executable: $HATIFECT_TEST_DOTNET"
    has_supported_test_toolchain "$HATIFECT_TEST_DOTNET" || \
      die "HATIFECT_TEST_DOTNET must provide x64 .NET 8 SDK and .NET 6 runtime."
    printf '%s\n' "$HATIFECT_TEST_DOTNET"
    return
  fi

  if [[ "$(uname -s)" == "Darwin" && "$(uname -m)" == "arm64" ]]; then
    local candidate=""
    local candidates=(
      "${HOME}/.dotnet/hatifect-x64-8/dotnet"
      "/usr/local/share/dotnet/x64/dotnet"
    )
    for candidate in "${candidates[@]}"; do
      if has_supported_test_toolchain "$candidate"; then
        printf '%s\n' "$candidate"
        return
      fi
    done
    die "Apple Silicon tests require x64 .NET 8 SDK plus .NET 6 runtime; set HATIFECT_TEST_DOTNET."
  fi

  local resolved=""
  resolved="$(resolve_build_dotnet)"
  has_supported_test_toolchain "$resolved" || \
    die "Tests require .NET 8 SDK plus .NET 6 runtime."
  printf '%s\n' "$resolved"
}

resolve_solution() {
  # 1. Explicitly configured solution/project always wins.
  if [[ -n "${HATIFECT_SOLUTION:-}" ]]; then
    if [[ -f "$REPO_ROOT/$HATIFECT_SOLUTION" ]]; then
      printf '%s\n' "$REPO_ROOT/$HATIFECT_SOLUTION"
      return
    fi

    if [[ -f "$HATIFECT_SOLUTION" ]]; then
      printf '%s\n' "$HATIFECT_SOLUTION"
      return
    fi

    die "HATIFECT_SOLUTION does not exist: $HATIFECT_SOLUTION"
  fi

  local matches=()

  # 2. Prefer modern .slnx solutions.
  while IFS= read -r f; do
    matches+=("$f")
  done < <(
    find "$REPO_ROOT" \
      -maxdepth 5 \
      -type f \
      -name '*.slnx' \
      -not -path '*/bin/*' \
      -not -path '*/obj/*' \
      -print
  )

  if [[ ${#matches[@]} -eq 1 ]]; then
    printf '%s\n' "${matches[0]}"
    return
  fi

  if [[ ${#matches[@]} -gt 1 ]]; then
    die "Multiple *.slnx files found. Set HATIFECT_SOLUTION in tools/hatifect.env."
  fi

  matches=()

  # 3. Fall back to traditional .sln.
  while IFS= read -r f; do
    matches+=("$f")
  done < <(
    find "$REPO_ROOT" \
      -maxdepth 5 \
      -type f \
      -name '*.sln' \
      -not -path '*/bin/*' \
      -not -path '*/obj/*' \
      -print
  )

  if [[ ${#matches[@]} -eq 1 ]]; then
    printf '%s\n' "${matches[0]}"
    return
  fi

  if [[ ${#matches[@]} -gt 1 ]]; then
    die "Multiple *.sln files found. Set HATIFECT_SOLUTION in tools/hatifect.env."
  fi

  matches=()

  # 4. Last-resort support for repositories with one main project.
  while IFS= read -r f; do
    matches+=("$f")
  done < <(
    find "$REPO_ROOT" \
      -maxdepth 5 \
      -type f \
      -name '*.csproj' \
      -not -path '*/bin/*' \
      -not -path '*/obj/*' \
      -not -path '*/tests/*' \
      -not -path '*/Tests/*' \
      -not -path '*/dev/*' \
      -print
  )

  if [[ ${#matches[@]} -eq 1 ]]; then
    printf '%s\n' "${matches[0]}"
    return
  fi

  if [[ ${#matches[@]} -gt 1 ]]; then
    die "No solution found and multiple *.csproj files exist. Set HATIFECT_SOLUTION explicitly in tools/hatifect.env."
  fi

  die "No *.slnx, *.sln, or suitable *.csproj found. Set HATIFECT_SOLUTION in tools/hatifect.env."
}

prepare_build_input() {
  local source="$1"

  HATIFECT_BUILD_INPUT="$source"
  HATIFECT_BUILD_INPUT_TEMP_DIR=""

  if [[ "$source" != *.slnx ]]; then
    return
  fi

  need_cmd python3

  HATIFECT_BUILD_INPUT_TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/hatifect-sln.XXXXXX")"
  HATIFECT_BUILD_INPUT="$HATIFECT_BUILD_INPUT_TEMP_DIR/Hatifect.sln"

  say "Materializing SDK 8 build input from: $source"
  python3 - "$source" "$REPO_ROOT" "$HATIFECT_BUILD_INPUT" <<'PY'
import os
import sys
import xml.etree.ElementTree as ET
import uuid
from pathlib import Path

source = Path(sys.argv[1])
repository_root = Path(sys.argv[2])
output = Path(sys.argv[3])
document = ET.parse(source)

projects = []
for project in document.iter("Project"):
    relative_path = project.get("Path")
    if relative_path:
        project_path = repository_root / relative_path
        if not project_path.is_file():
            raise SystemExit(f"Project listed by {source} does not exist: {project_path}")
        projects.append((relative_path, project_path))

if not projects:
    raise SystemExit(f"No projects found in solution: {source}")

project_type = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"
entries = []
configurations = []
seen_paths = set()

for relative_path, project_path in projects:
    identity = Path(relative_path).as_posix()
    if identity in seen_paths:
        raise SystemExit(f"Duplicate project in solution: {relative_path}")
    seen_paths.add(identity)

    project_guid = "{" + str(
        uuid.uuid5(uuid.NAMESPACE_URL, f"hatifect-project:{identity}")
    ).upper() + "}"
    solution_path = os.path.relpath(project_path, output.parent).replace("/", "\\")
    if any(character in solution_path for character in ('"', "\r", "\n")):
        raise SystemExit(f"Unsupported project path in solution: {project_path}")

    entries.extend([
        f'Project("{project_type}") = "{project_path.stem}", "{solution_path}", "{project_guid}"',
        "EndProject",
    ])
    for configuration in ("Debug", "Release"):
        configurations.extend([
            f"\t\t{project_guid}.{configuration}|Any CPU.ActiveCfg = {configuration}|Any CPU",
            f"\t\t{project_guid}.{configuration}|Any CPU.Build.0 = {configuration}|Any CPU",
        ])

lines = [
    "Microsoft Visual Studio Solution File, Format Version 12.00",
    "# Visual Studio Version 17",
    "VisualStudioVersion = 17.0.31903.59",
    "MinimumVisualStudioVersion = 10.0.40219.1",
    *entries,
    "Global",
    "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution",
    "\t\tDebug|Any CPU = Debug|Any CPU",
    "\t\tRelease|Any CPU = Release|Any CPU",
    "\tEndGlobalSection",
    "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution",
    *configurations,
    "\tEndGlobalSection",
    "\tGlobalSection(SolutionProperties) = preSolution",
    "\t\tHideSolutionNode = FALSE",
    "\tEndGlobalSection",
    "EndGlobal",
]
output.write_text("\n".join(lines) + "\n", encoding="utf-8")
PY

  [[ -f "$HATIFECT_BUILD_INPUT" ]] || \
    die "Failed to materialize SDK 8 build input: $HATIFECT_BUILD_INPUT"
}

cleanup_build_input() {
  local temp_dir="${HATIFECT_BUILD_INPUT_TEMP_DIR:-}"
  if [[ -z "$temp_dir" ]]; then
    return
  fi

  rm -f -- "$temp_dir/Hatifect.sln"
  rmdir -- "$temp_dir" 2>/dev/null || true
  HATIFECT_BUILD_INPUT_TEMP_DIR=""
}

new_run_dir() {
  local kind="$1"
  local stamp

  stamp="$(date '+%Y%m%d-%H%M%S')"
  local root="$REPO_ROOT/artifacts/$kind"
  mkdir -p "$root"
  mktemp -d "$root/$stamp.XXXXXX"
}

new_runtime_run_dir() {
  need_cmd python3
  local root="$REPO_ROOT/artifacts/runtime"
  local request_id
  request_id="$(python3 -c 'import uuid; print(uuid.uuid4())')"
  mkdir -p "$root"
  mkdir -m 700 "$root/$request_id"
  printf '%s\n' "$root/$request_id"
}

validate_result_json() {
  local path="$1"
  local expected_scenario="${2:-}"

  [[ -f "$path" ]] || die "Required result file missing: $path"

  need_cmd python3

  python3 "$TOOLS_DIR/live-harness/validate.py" \
    validate-result "$path" "$expected_scenario"
}
