#!/usr/bin/env python3
"""Validate Hatifect agent guidance; optionally audit actual local Codex discovery."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import selectors
import shutil
import subprocess
import sys
import time

try:
    import tomllib
except ModuleNotFoundError:
    raise SystemExit("RESULT: BLOCKED — Python 3.11+ is required for project TOML validation")

ROOT = Path(__file__).resolve().parents[1]
SKILL = ".agents/skills/hatifect-development/SKILL.md"
ROUTING = ".agents/skills/hatifect-development/routing.json"
SUBAGENT_MODELS = ("gpt-5.6-luna", "gpt-5.6-sol")
SUBAGENT_EFFORTS = ("none", "low", "medium", "high", "xhigh")


class AgentSetupError(ValueError):
    def __init__(self, message: str, status: str = "FAIL"):
        super().__init__(message)
        self.status = status


def repository_file(root: Path, relative: str) -> Path:
    if not isinstance(relative, str) or not relative.strip() or Path(relative).is_absolute():
        raise AgentSetupError("instruction/config path must be repository-relative")
    path = (root / relative).resolve()
    if not path.is_relative_to(root.resolve()) or not path.is_file():
        raise AgentSetupError(f"missing or escaping repository file: {relative}")
    return path


def read_toml(root: Path, relative: str) -> dict:
    try:
        return tomllib.loads(repository_file(root, relative).read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        raise AgentSetupError(f"invalid TOML: {relative}") from error


def string_list(value: object, field: str) -> list[str]:
    if (not isinstance(value, list) or not value
            or any(not isinstance(item, str) or not item.strip() for item in value)
            or len(value) != len(set(value))):
        raise AgentSetupError(f"{field} must contain unique nonempty strings")
    return value


def check_repository(root: Path = ROOT) -> dict:
    """Portable integrity checks; no Codex binary, credentials or personal skills needed."""
    root = root.resolve()
    config = read_toml(root, ".codex/config.toml")
    if set(config) != {"project_doc_max_bytes", "agents"}:
        raise AgentSetupError("project config must declare only instruction budget and agents; inherit personal settings")
    budget = config["project_doc_max_bytes"]
    agents = config["agents"]
    if type(budget) is not int or budget <= 0 or budget > 32768:
        raise AgentSetupError("instruction budget must be a positive integer up to 32768")
    if (not isinstance(agents, dict) or set(agents) != {
            "max_concurrent_threads_per_session", "default_subagent_model", "default_subagent_reasoning_effort"}
            or type(agents["max_concurrent_threads_per_session"]) is not int
            or not 1 <= agents["max_concurrent_threads_per_session"] <= 3):
        raise AgentSetupError("agents must declare model/reasoning defaults and cap concurrency between 1 and 3")
    if (agents["default_subagent_model"] not in SUBAGENT_MODELS
            or agents["default_subagent_reasoning_effort"] not in SUBAGENT_EFFORTS):
        raise AgentSetupError("subagent defaults must use Luna/Sol with reasoning no higher than xhigh")
    roles = []
    role_models = {}
    for path in sorted((root / ".codex/agents").glob("*.toml")):
        role = read_toml(root, str(path.relative_to(root)))
        if (set(role) != {"name", "description", "sandbox_mode", "developer_instructions",
                         "model", "model_reasoning_effort"}
                or any(not isinstance(value, str) or not value.strip() for value in role.values())
                or not re.fullmatch(r"[a-z][a-z0-9_]*", role["name"])
                or role["sandbox_mode"] != "read-only"
                or role["model"] not in SUBAGENT_MODELS
                or role["model_reasoning_effort"] not in SUBAGENT_EFFORTS):
            raise AgentSetupError(f"invalid read-only role: {path.name}")
        if role["name"] in roles:
            raise AgentSetupError(f"duplicate role name: {role['name']}")
        roles.append(role["name"])
        role_models[role["name"]] = {"model": role["model"], "reasoningEffort": role["model_reasoning_effort"]}
    if not roles:
        raise AgentSetupError("no project agent roles found")
    repository_file(root, SKILL)
    try:
        registry = json.loads(repository_file(root, ROUTING).read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        raise AgentSetupError("invalid routing JSON") from error
    if (not isinstance(registry, dict) or set(registry) != {"version", "routes"}
            or type(registry["version"]) is not int or registry["version"] != 1
            or not isinstance(registry["routes"], list) or not registry["routes"]):
        raise AgentSetupError("routing registry must contain version 1 and nonempty routes")
    ids = set()
    instruction_bytes = {}
    root_instructions = repository_file(root, "AGENTS.md")
    for route in registry["routes"]:
        if (not isinstance(route, dict) or set(route) != {"id", "when", "instructions", "skills"}
                or not isinstance(route["id"], str) or not re.fullmatch(r"[a-z][a-z0-9-]*", route["id"])
                or not isinstance(route["when"], str) or not route["when"].strip()):
            raise AgentSetupError("invalid route identity or description")
        if route["id"] in ids:
            raise AgentSetupError(f"duplicate route: {route['id']}")
        ids.add(route["id"])
        string_list(route["skills"], "route skills")
        paths = {root_instructions}
        for relative in string_list(route["instructions"], "route instructions"):
            path = repository_file(root, relative)
            paths.add(path)
            # Include intervening AGENTS files even if a route names a deeper document.
            for parent in path.parents:
                if parent == root:
                    break
                guide = parent / "AGENTS.md"
                if guide.exists():
                    paths.add(repository_file(root, str(guide.relative_to(root))))
        size = sum(path.stat().st_size for path in paths)
        if size > budget:
            raise AgentSetupError(f"instruction budget exceeded for route {route['id']}: {size} > {budget}")
        instruction_bytes[route["id"]] = size
    return {"status": "PASS", "roles": roles, "routes": registry["routes"],
            "projectSkill": SKILL, "instructionBytes": instruction_bytes,
            "subagentPolicy": {"defaultModel": agents["default_subagent_model"],
                               "defaultReasoningEffort": agents["default_subagent_reasoning_effort"],
                               "allowedModels": list(SUBAGENT_MODELS), "maxReasoningEffort": "xhigh",
                               "roles": role_models}}


def audit_host(root: Path, config: dict, skills: dict, route: str | None = None) -> dict:
    """Project loading and skill availability, never raw effective config or MCP data."""
    repository = check_repository(root)
    root = root.resolve()
    routes = repository["routes"]
    if route is not None and route not in {item["id"] for item in routes}:
        raise AgentSetupError(f"unknown skill route: {route}")
    try:
        if (not isinstance(config["layers"], list)
                or any(not isinstance(item, dict) or not isinstance(item.get("name"), dict)
                       for item in config["layers"])):
            raise TypeError("invalid configuration layers")
        layers = [item for item in config["layers"]
                  if item["name"].get("type") == "project"
                  and Path(item["name"]["dotCodexFolder"]).resolve() == (root / ".codex").resolve()]
        if len(layers) != 1 or layers[0].get("disabledReason") is not None:
            raise AgentSetupError("project configuration is missing or disabled; trust this exact checkout in Codex", "BLOCKED")
        effective_agents = config["config"]["agents"]
        policy = repository["subagentPolicy"]
        if (not isinstance(effective_agents, dict)
                or effective_agents.get("default_subagent_model") != policy["defaultModel"]
                or effective_agents.get("default_subagent_reasoning_effort") != policy["defaultReasoningEffort"]):
            raise AgentSetupError("effective subagent defaults do not match project policy", "BLOCKED")
        catalogs = [item for item in skills["data"] if Path(item["cwd"]).resolve() == root]
        if len(catalogs) != 1 or catalogs[0].get("errors") != []:
            raise AgentSetupError("skill discovery is missing, duplicated or contains errors", "BLOCKED")
        entries = catalogs[0]["skills"]
        if not isinstance(entries, list):
            raise TypeError("skills must be a list")
        by_name: dict[str, list[dict]] = {}
        for entry in entries:
            if (not isinstance(entry["name"], str) or not isinstance(entry["path"], str)
                    or type(entry["enabled"]) is not bool):
                raise TypeError("invalid skill metadata")
            by_name.setdefault(entry["name"], []).append(entry)
        own = by_name.get("hatifect-development", [])
        if (len(own) != 1 or not own[0]["enabled"]
                or Path(own[0]["path"]).resolve() != (root / SKILL).resolve()):
            raise AgentSetupError("repository skill is missing, disabled, ambiguous or from another checkout", "BLOCKED")
        availability = {}
        for item in routes:
            unavailable = [name for name in item["skills"]
                           if len(by_name.get(name, [])) != 1 or not by_name[name][0]["enabled"]]
            availability[item["id"]] = {"available": not unavailable, "unavailable": unavailable}
        if route and availability[route]["unavailable"]:
            raise AgentSetupError(f"selected route {route} has unavailable or ambiguous skills: "
                                  + ", ".join(availability[route]["unavailable"]), "BLOCKED")
        return {"status": "PASS", "projectConfigLoaded": True, "projectSkillLoaded": True,
                "subagentDefaultsLoaded": True,
                "subagentDefaults": {"model": policy["defaultModel"],
                                     "reasoningEffort": policy["defaultReasoningEffort"]},
                "discoveredSkills": len(entries), "enabledSkills": sum(item["enabled"] for item in entries),
                "routes": availability, "selectedRoute": route,
                "note": "Discovery proves availability, not that a skill was read or applied."}
    except (KeyError, TypeError, OSError, ValueError) as error:
        if isinstance(error, AgentSetupError):
            raise
        raise AgentSetupError("malformed Codex discovery response", "BLOCKED") from error


def read_host(root: Path, executable: str = "codex", timeout: float = 25) -> tuple[dict, dict]:
    """Own one bounded stdio server; never start a thread/turn or edit configuration."""
    binary = shutil.which(executable)
    if binary is None:
        raise AgentSetupError("Codex CLI is unavailable; install/configure it for --host", "BLOCKED")
    if not 0 < timeout <= 60:
        raise AgentSetupError("host audit timeout must be between 0 and 60 seconds")
    try:
        process = subprocess.Popen([binary, "app-server", "--listen", "stdio://"], cwd=root,
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    except OSError as error:
        raise AgentSetupError("cannot start local Codex app-server", "BLOCKED") from error
    selector = selectors.DefaultSelector()
    buffer = b""
    deadline = time.monotonic() + timeout

    def send(message: dict) -> None:
        process.stdin.write((json.dumps(message) + "\n").encode())
        process.stdin.flush()

    def request(identity: int, method: str, params: dict) -> dict:
        nonlocal buffer
        send({"id": identity, "method": method, "params": params})
        while time.monotonic() < deadline:
            while b"\n" in buffer:
                line, buffer = buffer.split(b"\n", 1)
                message = json.loads(line)
                if not isinstance(message, dict):
                    raise AgentSetupError("Codex returned a non-object protocol message", "BLOCKED")
                if message.get("id") == identity:
                    if "error" in message or not isinstance(message.get("result"), dict):
                        raise AgentSetupError(f"Codex rejected {method}", "BLOCKED")
                    return message["result"]
            if selector.select(min(1, max(0, deadline - time.monotonic()))):
                data = os.read(process.stdout.fileno(), 65536)
                if not data:
                    raise AgentSetupError("Codex exited before audit completed; inspect local CLI startup/configuration", "BLOCKED")
                buffer += data
                if len(buffer) > 8 * 1024 * 1024:
                    raise AgentSetupError("Codex discovery response exceeded 8 MiB", "BLOCKED")
        raise AgentSetupError("Codex discovery timed out", "BLOCKED")

    try:
        selector.register(process.stdout, selectors.EVENT_READ)
        request(0, "initialize", {"clientInfo": {"name": "hatifect-agent-audit", "version": "1.0"}})
        send({"method": "initialized", "params": {}})
        config = request(1, "config/read", {"cwd": str(root.resolve()), "includeLayers": True})
        skills = request(2, "skills/list", {"cwds": [str(root.resolve())], "forceReload": True})
        return config, skills
    except (OSError, ValueError) as error:
        if isinstance(error, AgentSetupError):
            raise
        raise AgentSetupError("Codex discovery could not complete", "BLOCKED") from error
    finally:
        selector.close()
        if process.poll() is None:
            process.terminate()
        try:
            process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
        process.stdin.close()
        process.stdout.close()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--host", action="store_true")
    parser.add_argument("--route", help="require all named skills for this route; implies --host")
    parser.add_argument("--codex", default="codex", help="Codex executable for optional host audit")
    args = parser.parse_args()
    try:
        result = {"repository": check_repository(args.root)}
        if args.host or args.route:
            result["host"] = audit_host(args.root, *read_host(args.root, args.codex), route=args.route)
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    except AgentSetupError as error:
        print(json.dumps({"status": error.status, "error": str(error)}, ensure_ascii=False), file=sys.stderr)
        return 2 if error.status == "BLOCKED" else 1


if __name__ == "__main__":
    raise SystemExit(main())
