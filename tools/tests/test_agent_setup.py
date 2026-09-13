"""Behavioral contracts for portable guidance and read-only Codex discovery."""
from __future__ import annotations

from contextlib import contextmanager, redirect_stderr, redirect_stdout
from copy import deepcopy
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import agent_setup


SECRET = "fixture-secret-never-include-in-diagnostics"


class AgentSetupTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory(prefix="hatifect-agent-setup-test.")
        self.addCleanup(temporary.cleanup)
        self.base = Path(temporary.name).resolve()
        self.root = self.base / "checkout"
        self.root.mkdir()
        self.write("AGENTS.md", "root instructions\n")
        self.write("tools/AGENTS.md", "tool instructions\n")
        self.write("docs/flow.txt", "flow instructions\n")
        self.write(
            agent_setup.SKILL,
            "---\n"
            "name: hatifect-development\n"
            "description: Route work in the Hatifect repository.\n"
            "---\n",
        )
        self.write_config()
        self.roles = {
            "explorer": {"name": "hatifect_explorer", "description": "Explore assigned ownership",
                         "model": "gpt-5.6-luna", "model_reasoning_effort": "medium",
                         "sandbox_mode": "read-only", "developer_instructions": "Inspect without edits"},
            "reviewer": {"name": "hatifect_reviewer", "description": "Review assigned evidence",
                         "model": "gpt-5.6-sol", "model_reasoning_effort": "xhigh",
                         "sandbox_mode": "read-only", "developer_instructions": "Report without edits"},
        }
        for filename, role in self.roles.items():
            self.write_role(filename, role)
        self.registry = {"version": 1, "routes": [
            {"id": "tools", "when": "Change validation tooling", "instructions": ["tools/AGENTS.md"],
             "skills": ["code-testing-agent"]},
            {"id": "flowline", "when": "Change domain ownership", "instructions": ["docs/flow.txt"],
             "skills": ["architecture"]},
        ]}
        self.write_registry()

    def write(self, relative: str, text: str) -> Path:
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path

    def write_config(self, budget=32768, threads=2, extra=None, agent_overrides=None) -> None:
        entries = {"project_doc_max_bytes": budget, **(extra or {})}
        text = "".join(f"{key} = {json.dumps(value)}\n" for key, value in entries.items())
        agents = {"max_concurrent_threads_per_session": threads,
                  "default_subagent_model": "gpt-5.6-luna", "default_subagent_reasoning_effort": "medium",
                  **(agent_overrides or {})}
        self.write(".codex/config.toml", text + "\n[agents]\n"
                   + "".join(f"{key} = {json.dumps(value)}\n" for key, value in agents.items()))

    def write_role(self, name: str, role: dict) -> None:
        self.write(f".codex/agents/{name}.toml",
                   "".join(f"{key} = {json.dumps(value)}\n" for key, value in role.items()))

    def write_registry(self, registry=None) -> None:
        self.write(agent_setup.ROUTING, json.dumps(self.registry if registry is None else registry))

    def responses(self) -> tuple[dict, dict]:
        config = {"config": {"mcp_servers": {"private": {"env": {"TOKEN": SECRET}}},
                             "agents": {"default_subagent_model": "gpt-5.6-luna",
                                        "default_subagent_reasoning_effort": "medium"}}, "layers": [
            {"name": {"type": "user"}, "config": {"api_key": SECRET}},
            {"name": {"type": "project", "dotCodexFolder": str(self.root / ".codex")},
             "disabledReason": None},
        ]}
        skills = {"data": [{"cwd": str(self.root), "errors": [], "skills": [
            {"name": "hatifect-development", "path": str(self.root / agent_setup.SKILL), "enabled": True},
            {"name": "code-testing-agent", "path": str(self.base / "specialists/testing/SKILL.md"), "enabled": True},
            {"name": "architecture", "path": str(self.base / "specialists/architecture/SKILL.md"), "enabled": True},
            {"name": "unrelated", "path": str(self.base / "specialists/unused/SKILL.md"), "enabled": False},
        ]}]}
        return config, skills

    def assert_rejected(self, callback, *, status="FAIL", contains=None) -> agent_setup.AgentSetupError:
        with self.assertRaises(agent_setup.AgentSetupError) as caught:
            callback()
        self.assertEqual(status, caught.exception.status)
        if contains is not None:
            self.assertIn(contains, str(caught.exception))
        return caught.exception

    def test_valid_repository_reports_roles_routes_and_exact_instruction_bytes(self) -> None:
        before = {path.relative_to(self.root): path.read_bytes()
                  for path in self.root.rglob("*") if path.is_file()}
        result = agent_setup.check_repository(self.root)
        self.assertEqual("PASS", result["status"])
        self.assertEqual(["hatifect_explorer", "hatifect_reviewer"], result["roles"])
        self.assertEqual(["tools", "flowline"], [route["id"] for route in result["routes"]])
        self.assertEqual(agent_setup.SKILL, result["projectSkill"])
        self.assertEqual({"tools": 36, "flowline": 36}, result["instructionBytes"])
        self.assertEqual({"defaultModel": "gpt-5.6-luna", "defaultReasoningEffort": "medium",
                          "allowedModels": ["gpt-5.6-luna", "gpt-5.6-sol"], "maxReasoningEffort": "xhigh",
                          "roles": {"hatifect_explorer": {"model": "gpt-5.6-luna", "reasoningEffort": "medium"},
                                    "hatifect_reviewer": {"model": "gpt-5.6-sol", "reasoningEffort": "xhigh"}}},
                         result["subagentPolicy"])
        self.assertEqual(before, {path.relative_to(self.root): path.read_bytes()
                                  for path in self.root.rglob("*") if path.is_file()})

    def test_invalid_toml_is_rejected_without_disclosing_its_content(self) -> None:
        self.write(".codex/config.toml", f'credential = "{SECRET}\n')
        error = self.assert_rejected(lambda: agent_setup.check_repository(self.root), contains="invalid TOML")
        self.assertNotIn(SECRET, str(error))

    def test_project_config_cannot_override_personal_model_or_mcp(self) -> None:
        for extra in ({"model": "personal-model"}, {"model_reasoning_effort": "max"},
                      {"mcp_servers": {}}, {"api_key": SECRET}):
            with self.subTest(extra=next(iter(extra))):
                self.write_config(extra=extra)
                error = self.assert_rejected(lambda: agent_setup.check_repository(self.root))
                self.assertNotIn(SECRET, str(error))

    def test_subagent_defaults_accept_luna_sol_and_efforts_through_xhigh(self) -> None:
        for model in ("gpt-5.6-luna", "gpt-5.6-sol"):
            for effort in ("none", "low", "medium", "high", "xhigh"):
                with self.subTest(model=model, effort=effort):
                    self.write_config(agent_overrides={"default_subagent_model": model,
                                                       "default_subagent_reasoning_effort": effort})
                    result = agent_setup.check_repository(self.root)
                    self.assertEqual("PASS", result["status"])
                    self.assertEqual((model, effort), (result["subagentPolicy"]["defaultModel"],
                                                      result["subagentPolicy"]["defaultReasoningEffort"]))

    def test_subagent_defaults_reject_other_models_and_effort_above_xhigh(self) -> None:
        for field, values in (("default_subagent_model", ("gpt-6-astra", "gpt-5.6-terra", "", True, [])),
                              ("default_subagent_reasoning_effort", ("max", "ultra", "unknown", "", True, []))):
            for value in values:
                with self.subTest(field=field, value=value):
                    self.write_config(agent_overrides={field: value})
                    self.assert_rejected(lambda: agent_setup.check_repository(self.root), contains="subagent defaults")

    def test_subagent_defaults_require_model_and_reasoning(self) -> None:
        for field in ("default_subagent_model", "default_subagent_reasoning_effort"):
            with self.subTest(missing=field):
                self.write_config()
                path = self.root / ".codex/config.toml"
                path.write_text("\n".join(line for line in path.read_text().splitlines()
                                          if not line.startswith(field + " =")), encoding="utf-8")
                self.assert_rejected(lambda: agent_setup.check_repository(self.root), contains="model/reasoning defaults")

    def test_instruction_budget_and_concurrency_reject_invalid_types_and_ranges(self) -> None:
        for budget in (0, -1, 32769, True, "32768"):
            with self.subTest(budget=budget):
                self.write_config(budget=budget)
                self.assert_rejected(lambda: agent_setup.check_repository(self.root))
        for threads in (0, 5, True, "2"):
            with self.subTest(threads=threads):
                self.write_config(threads=threads)
                self.assert_rejected(lambda: agent_setup.check_repository(self.root))
        for threads in (1, 4):
            with self.subTest(valid_threads=threads):
                self.write_config(threads=threads)
                self.assertEqual("PASS", agent_setup.check_repository(self.root)["status"])

    def test_read_only_role_metadata_rejects_missing_extra_and_invalid_fields(self) -> None:
        original = self.roles["explorer"]
        cases = [original | {"sandbox_mode": "workspace-write"}, original | {"model": "override"},
                 original | {"name": "Invalid-Name"}, original | {"description": " "},
                 original | {"developer_instructions": False}, original | {"api_key": SECRET},
                 {key: value for key, value in original.items() if key != "description"}]
        for role in cases:
            with self.subTest(role=role):
                self.write_role("explorer", role)
                self.assert_rejected(lambda: agent_setup.check_repository(self.root), contains="read-only role")

    def test_roles_reject_other_models_or_reasoning_above_xhigh(self) -> None:
        for name, original in self.roles.items():
            cases = [original | {"model": "gpt-6-astra"}, original | {"model": "gpt-5.6-terra"},
                     original | {"model_reasoning_effort": "max"}, original | {"model_reasoning_effort": "ultra"},
                     {key: value for key, value in original.items() if key != "model"},
                     {key: value for key, value in original.items() if key != "model_reasoning_effort"}]
            for role in cases:
                with self.subTest(name=name, role=role):
                    self.write_role(name, role)
                    self.assert_rejected(lambda: agent_setup.check_repository(self.root), contains="read-only role")
            self.write_role(name, original)

    def test_duplicate_role_identity_is_rejected_across_different_files(self) -> None:
        self.write_role("copy", self.roles["explorer"])
        self.assert_rejected(lambda: agent_setup.check_repository(self.root), contains="duplicate role")

    def test_repository_requires_at_least_one_role_and_its_own_skill(self) -> None:
        for path in (self.root / ".codex/agents").glob("*.toml"):
            path.unlink()
        self.assert_rejected(lambda: agent_setup.check_repository(self.root), contains="no project agent roles")
        self.write_role("explorer", self.roles["explorer"])
        (self.root / agent_setup.SKILL).unlink()
        self.assert_rejected(lambda: agent_setup.check_repository(self.root), contains="missing")

    def test_routing_rejects_invalid_json_empty_registry_and_wrong_version(self) -> None:
        self.write(agent_setup.ROUTING, "{")
        self.assert_rejected(lambda: agent_setup.check_repository(self.root), contains="routing JSON")
        for registry in ([], {}, {"version": 1, "routes": []}, {"version": True, "routes": self.registry["routes"]},
                         self.registry | {"version": 2}, self.registry | {"extra": "field"}):
            with self.subTest(registry=registry):
                self.write_registry(registry)
                self.assert_rejected(lambda: agent_setup.check_repository(self.root))

    def test_route_identity_description_and_duplicates_are_rejected(self) -> None:
        original = self.registry["routes"][0]
        for route in (None, original | {"id": ""}, original | {"id": "Bad_Route"},
                      original | {"when": " "}, original | {"unexpected": True},
                      {key: value for key, value in original.items() if key != "when"}):
            with self.subTest(route=route):
                self.write_registry({"version": 1, "routes": [route]})
                self.assert_rejected(lambda: agent_setup.check_repository(self.root))
        self.write_registry({"version": 1, "routes": [original, deepcopy(original)]})
        self.assert_rejected(lambda: agent_setup.check_repository(self.root), contains="duplicate route")

    def test_route_skill_and_instruction_lists_require_unique_nonempty_strings(self) -> None:
        original = self.registry["routes"][0]
        for field, existing in (("skills", "code-testing-agent"), ("instructions", "tools/AGENTS.md")):
            for value in ([], [existing, existing], [""], [" "], [1], existing, None):
                with self.subTest(field=field, value=value):
                    self.write_registry({"version": 1, "routes": [original | {field: value}]})
                    self.assert_rejected(lambda: agent_setup.check_repository(self.root))

    def test_repository_owned_route_skills_are_validated(self) -> None:
        skill_name = "hatifect-diagnostics"
        relative = f".agents/skills/{skill_name}/SKILL.md"
        self.write(
            relative,
            "---\n"
            f"name: {skill_name}\n"
            "description: Diagnose bounded Hatifect harness evidence.\n"
            "---\n\n"
            "Read the canonical failure envelope.\n",
        )
        route = self.registry["routes"][0] | {
            "skills": ["code-testing-agent", skill_name]
        }
        self.write_registry({"version": 1, "routes": [route]})

        result = agent_setup.check_repository(self.root)

        self.assertEqual(
            {
                "hatifect-development": agent_setup.SKILL,
                skill_name: relative,
            },
            result["projectSkills"],
        )

    def test_repository_owned_route_skill_rejects_missing_or_mismatched_frontmatter(self) -> None:
        skill_name = "hatifect-diagnostics"
        route = self.registry["routes"][0] | {"skills": [skill_name]}
        self.write_registry({"version": 1, "routes": [route]})
        self.assert_rejected(
            lambda: agent_setup.check_repository(self.root),
            contains="missing",
        )

        self.write(
            f".agents/skills/{skill_name}/SKILL.md",
            "---\n"
            "name: hatifect-wrong-skill\n"
            "description: Wrong project skill identity.\n"
            "---\n",
        )
        self.assert_rejected(
            lambda: agent_setup.check_repository(self.root),
            contains="frontmatter",
        )

    def test_missing_absolute_and_escaping_instruction_paths_are_rejected(self) -> None:
        outside = self.base / "outside.txt"
        outside.write_text("outside fixture", encoding="utf-8")
        for relative in ("missing.txt", str(outside), "../outside.txt"):
            with self.subTest(path=relative):
                route = self.registry["routes"][0] | {"instructions": [relative]}
                self.write_registry({"version": 1, "routes": [route]})
                self.assert_rejected(lambda: agent_setup.check_repository(self.root))

    def test_escaping_symlinks_cannot_supply_config_skill_or_instructions(self) -> None:
        for relative in (".codex/config.toml", agent_setup.SKILL, "tools/AGENTS.md"):
            with self.subTest(path=relative):
                path = self.root / relative
                original = path.read_bytes()
                outside = self.base / "outside.txt"
                outside.write_bytes(original)
                path.unlink()
                path.symlink_to(outside)
                try:
                    self.assert_rejected(lambda: agent_setup.check_repository(self.root))
                finally:
                    path.unlink()
                    path.write_bytes(original)

    def test_internal_instruction_symlink_is_allowed_and_not_double_counted(self) -> None:
        alias = self.root / "tools/alias.md"
        alias.symlink_to("AGENTS.md")
        route = self.registry["routes"][0] | {"instructions": ["tools/AGENTS.md", "tools/alias.md"]}
        self.write_registry({"version": 1, "routes": [route]})
        result = agent_setup.check_repository(self.root)
        self.assertEqual("PASS", result["status"])
        self.assertEqual({"tools": 36}, result["instructionBytes"])

    def test_instruction_byte_budget_accepts_below_and_at_limit_but_rejects_above(self) -> None:
        self.write_registry({"version": 1, "routes": [self.registry["routes"][0]]})
        self.write_config(budget=32)
        self.write("AGENTS.md", "Ж")  # two UTF-8 bytes, not one character
        for size in (31, 32, 33):
            with self.subTest(instruction_bytes=size):
                self.write("tools/AGENTS.md", "x" * (size - 2))
                if size > 32:
                    self.assert_rejected(lambda: agent_setup.check_repository(self.root), contains="budget exceeded")
                else:
                    self.assertEqual({"tools": size}, agent_setup.check_repository(self.root)["instructionBytes"])

    def test_intervening_agents_contribute_once_to_the_route_budget(self) -> None:
        self.write("AGENTS.md", "ro")
        self.write("tools/AGENTS.md", "abc")
        self.write("tools/nested/AGENTS.md", "abcde")
        self.write("tools/nested/guide.txt", "abcdefg")
        route = self.registry["routes"][0] | {"instructions": ["tools/AGENTS.md", "tools/nested/guide.txt"]}
        self.write_registry({"version": 1, "routes": [route]})
        self.write_config(budget=17)
        self.assertEqual({"tools": 17}, agent_setup.check_repository(self.root)["instructionBytes"])
        self.write_config(budget=16)
        self.assert_rejected(lambda: agent_setup.check_repository(self.root), contains="budget exceeded")

    def test_host_reports_only_sanitized_availability_without_mutating_discovery(self) -> None:
        config, skills = self.responses()
        original = deepcopy((config, skills))
        result = agent_setup.audit_host(self.root, config, skills)
        self.assertEqual("PASS", result["status"])
        self.assertTrue(result["projectConfigLoaded"])
        self.assertTrue(result["projectSkillLoaded"])
        self.assertTrue(result["subagentDefaultsLoaded"])
        self.assertEqual({"model": "gpt-5.6-luna", "reasoningEffort": "medium"}, result["subagentDefaults"])
        self.assertEqual((4, 3), (result["discoveredSkills"], result["enabledSkills"]))
        self.assertEqual({"tools": {"available": True, "unavailable": []},
                          "flowline": {"available": True, "unavailable": []}}, result["routes"])
        self.assertIsNone(result["selectedRoute"])
        self.assertNotIn(SECRET, json.dumps(result))
        self.assertNotIn("mcp_servers", json.dumps(result))
        self.assertNotIn("api_key", json.dumps(result))
        self.assertEqual(original, (config, skills))

    def test_host_requires_effective_subagent_defaults_to_match_project(self) -> None:
        defaults = self.responses()[0]["config"]["agents"]
        for agents in (None, {}, defaults | {"default_subagent_model": "gpt-5.6-sol"},
                       defaults | {"default_subagent_model": "gpt-6-astra"},
                       defaults | {"default_subagent_reasoning_effort": "xhigh"},
                       defaults | {"default_subagent_reasoning_effort": "max"}):
            with self.subTest(agents=agents):
                config, skills = self.responses()
                config["config"]["agents"] = agents
                self.assert_rejected(lambda: agent_setup.audit_host(self.root, config, skills),
                                     status="BLOCKED", contains="subagent defaults")

    def test_host_requires_one_enabled_project_layer_for_exact_checkout(self) -> None:
        for state in ("missing", "disabled", "wrong-checkout", "duplicate"):
            with self.subTest(layer=state):
                config, skills = self.responses()
                project = config["layers"][1]
                if state == "missing":
                    config["layers"].pop()
                elif state == "disabled":
                    project["disabledReason"] = SECRET
                elif state == "wrong-checkout":
                    project["name"]["dotCodexFolder"] = str(self.base / "other/.codex")
                else:
                    config["layers"].append(deepcopy(project))
                error = self.assert_rejected(lambda: agent_setup.audit_host(self.root, config, skills), status="BLOCKED")
                self.assertNotIn(SECRET, str(error))

    def test_host_requires_unambiguous_enabled_repository_skill_at_own_path(self) -> None:
        for state in ("missing", "disabled", "ambiguous", "wrong-path"):
            with self.subTest(skill=state):
                config, skills = self.responses()
                entries = skills["data"][0]["skills"]
                if state == "missing":
                    entries.pop(0)
                elif state == "disabled":
                    entries[0]["enabled"] = False
                elif state == "ambiguous":
                    entries.append(deepcopy(entries[0]))
                else:
                    entries[0]["path"] = str(self.base / "other" / agent_setup.SKILL)
                self.assert_rejected(lambda: agent_setup.audit_host(self.root, config, skills), status="BLOCKED")

    def test_host_requires_every_repository_owned_route_skill_at_own_path(self) -> None:
        skill_name = "hatifect-diagnostics"
        relative = f".agents/skills/{skill_name}/SKILL.md"
        self.write(
            relative,
            "---\n"
            f"name: {skill_name}\n"
            "description: Diagnose bounded Hatifect harness evidence.\n"
            "---\n",
        )
        route = self.registry["routes"][0] | {"skills": [skill_name]}
        self.write_registry({"version": 1, "routes": [route]})

        for state in ("missing", "disabled", "ambiguous", "wrong-path"):
            with self.subTest(skill=state):
                config, skills = self.responses()
                entry = {"name": skill_name, "path": str(self.root / relative), "enabled": True}
                if state != "missing":
                    skills["data"][0]["skills"].append(entry)
                if state == "disabled":
                    entry["enabled"] = False
                elif state == "ambiguous":
                    skills["data"][0]["skills"].append(deepcopy(entry))
                elif state == "wrong-path":
                    entry["path"] = str(self.base / "other" / relative)
                self.assert_rejected(
                    lambda: agent_setup.audit_host(self.root, config, skills),
                    status="BLOCKED",
                    contains=skill_name,
                )

        config, skills = self.responses()
        skills["data"][0]["skills"].append(
            {"name": skill_name, "path": str(self.root / relative), "enabled": True}
        )
        self.assertEqual("PASS", agent_setup.audit_host(self.root, config, skills)["status"])

    def test_internal_skill_symlink_is_valid_in_portable_and_host_checks(self) -> None:
        skill = self.root / agent_setup.SKILL
        content = skill.read_text(encoding="utf-8")
        target = self.write(".agents/shared-guidance.md", content)
        skill.unlink()
        skill.symlink_to(target)
        self.assertEqual("PASS", agent_setup.check_repository(self.root)["status"])
        self.assertEqual("PASS", agent_setup.audit_host(self.root, *self.responses())["status"])

    def test_optional_route_reports_unavailable_specialists_but_selected_route_blocks(self) -> None:
        for state in ("missing", "disabled", "ambiguous"):
            with self.subTest(specialist=state):
                config, skills = self.responses()
                entries = skills["data"][0]["skills"]
                if state == "missing":
                    entries.pop(2)
                elif state == "disabled":
                    entries[2]["enabled"] = False
                else:
                    entries.append(deepcopy(entries[2]))
                result = agent_setup.audit_host(self.root, config, skills)
                self.assertEqual("PASS", result["status"])
                self.assertEqual({"available": False, "unavailable": ["architecture"]}, result["routes"]["flowline"])
                self.assertEqual("tools", agent_setup.audit_host(self.root, config, skills, route="tools")["selectedRoute"])
                self.assert_rejected(lambda: agent_setup.audit_host(self.root, config, skills, route="flowline"),
                                     status="BLOCKED", contains="architecture")

    def test_unknown_selected_route_is_configuration_failure(self) -> None:
        self.assert_rejected(lambda: agent_setup.audit_host(self.root, *self.responses(), route="missing"),
                             contains="unknown skill route")

    def test_skill_discovery_requires_one_error_free_catalog_for_exact_checkout(self) -> None:
        for state in ("missing", "wrong-checkout", "duplicate", "errors"):
            with self.subTest(catalog=state):
                config, skills = self.responses()
                if state == "missing":
                    skills["data"].clear()
                elif state == "wrong-checkout":
                    skills["data"][0]["cwd"] = str(self.base / "other")
                elif state == "duplicate":
                    skills["data"].append(deepcopy(skills["data"][0]))
                else:
                    skills["data"][0]["errors"] = [{"message": SECRET}]
                error = self.assert_rejected(lambda: agent_setup.audit_host(self.root, config, skills), status="BLOCKED")
                self.assertNotIn(SECRET, str(error))

    def test_malformed_discovery_is_blocked_instead_of_leaking_parser_errors(self) -> None:
        for malformed in ({}, {"layers": None}, {"layers": [{"name": None}]},
                          {"layers": [{"name": {"type": "project", "dotCodexFolder": "\0"}}]}):
            with self.subTest(config=malformed):
                self.assert_rejected(lambda: agent_setup.audit_host(self.root, malformed, self.responses()[1]), status="BLOCKED")
        for malformed in ({}, {"data": None}, {"data": [{"cwd": "\0"}]}):
            with self.subTest(skills=malformed):
                self.assert_rejected(lambda: agent_setup.audit_host(self.root, self.responses()[0], malformed), status="BLOCKED")
        for malformed in (None, {}, [None], [{}], [{"name": 1, "path": "a", "enabled": True}],
                          [{"name": "a", "path": False, "enabled": True}],
                          [{"name": "a", "path": "a", "enabled": "true"}]):
            with self.subTest(entries=malformed):
                config, skills = self.responses()
                if isinstance(malformed, list):
                    skills["data"][0]["skills"].extend(malformed)
                else:
                    skills["data"][0]["skills"] = malformed
                self.assert_rejected(lambda: agent_setup.audit_host(self.root, config, skills), status="BLOCKED")

    @contextmanager
    def fake_server(self, script: str):
        path = self.base / "fake-server.py"
        path.write_text(script, encoding="utf-8")
        original_popen = subprocess.Popen
        processes = []
        commands = []

        def start(command, **arguments):
            commands.append(command)
            process = original_popen([sys.executable, str(path), *command[1:]], **arguments)
            processes.append(process)
            return process

        try:
            with patch.object(agent_setup.shutil, "which", return_value=str(path)), \
                    patch.object(agent_setup.subprocess, "Popen", side_effect=start):
                yield processes, commands
        finally:
            for process in processes:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=5)

    def assert_process_closed(self, process) -> None:
        self.assertIsNotNone(process.poll(), "owned audit process must be reaped before returning")
        self.assertTrue(process.stdin.closed)
        self.assertTrue(process.stdout.closed)

    def test_stdio_audit_sends_only_read_methods_with_exact_checkout_and_reaps_server(self) -> None:
        config, skills = self.responses()
        transcript = self.base / "requests.jsonl"
        script = f'''import json, sys
from pathlib import Path
config = json.loads({json.dumps(config)!r})
skills = json.loads({json.dumps(skills)!r})
for line in sys.stdin:
    request = json.loads(line)
    with Path({str(transcript)!r}).open("a", encoding="utf-8") as output:
        output.write(json.dumps(request) + "\\n")
    method = request["method"]
    if method == "initialized":
        continue
    result = {{"initialize": {{}}, "config/read": config, "skills/list": skills}}[method]
    print(json.dumps({{"method": "notification", "params": {{}}}}), flush=True)
    print(json.dumps({{"id": request["id"], "result": result}}), flush=True)
'''
        with self.fake_server(script) as (processes, commands):
            self.assertEqual((config, skills), agent_setup.read_host(self.root, "fixture-codex", timeout=2))
            self.assertEqual([str(self.base / "fake-server.py"), "app-server", "--listen", "stdio://"], commands[0])
            self.assert_process_closed(processes[0])
        requests = [json.loads(line) for line in transcript.read_text(encoding="utf-8").splitlines()]
        self.assertEqual(["initialize", "initialized", "config/read", "skills/list"], [item["method"] for item in requests])
        self.assertEqual({"cwd": str(self.root), "includeLayers": True}, requests[2]["params"])
        self.assertEqual({"cwds": [str(self.root)], "forceReload": True}, requests[3]["params"])

    def test_stdio_timeout_blocks_and_reaps_unresponsive_server(self) -> None:
        with self.fake_server("import sys\nsys.stdin.read()\n") as (processes, _):
            self.assert_rejected(lambda: agent_setup.read_host(self.root, timeout=0.2), status="BLOCKED", contains="timed out")
            self.assert_process_closed(processes[0])

    def test_stdio_malformed_rejected_and_early_exit_responses_are_blocked_and_sanitized(self) -> None:
        rejection = json.dumps({"id": 0, "error": {"message": SECRET}})
        scripts = (
            "import sys\nsys.exit(0)\n",
            f"import sys\nsys.stdin.readline()\nprint({SECRET!r}, flush=True)\nsys.stdin.read()\n",
            "import sys\nsys.stdin.readline()\nprint('[]', flush=True)\nsys.stdin.read()\n",
            f"import sys\nsys.stdin.readline()\nprint({rejection!r}, flush=True)\nsys.stdin.read()\n",
            "import sys\nsys.stdin.readline()\nprint('{\"id\":0,\"result\":[]}', flush=True)\nsys.stdin.read()\n",
        )
        for script in scripts:
            with self.subTest(server=script), self.fake_server(script) as (processes, _):
                error = self.assert_rejected(lambda: agent_setup.read_host(self.root, timeout=2), status="BLOCKED")
                self.assertNotIn(SECRET, str(error))
                if repr(rejection) in script:
                    self.assertIn("rejected initialize", str(error))
                self.assert_process_closed(processes[0])

    def test_unavailable_cli_invalid_timeout_and_launch_error_do_not_start_an_audit(self) -> None:
        with patch.object(agent_setup.shutil, "which", return_value=None), patch.object(agent_setup.subprocess, "Popen") as launch:
            self.assert_rejected(lambda: agent_setup.read_host(self.root), status="BLOCKED")
            launch.assert_not_called()
        for timeout in (0, -1, 61):
            with self.subTest(timeout=timeout), patch.object(agent_setup.shutil, "which", return_value="fixture"), \
                    patch.object(agent_setup.subprocess, "Popen") as launch:
                self.assert_rejected(lambda: agent_setup.read_host(self.root, timeout=timeout))
                launch.assert_not_called()
        with patch.object(agent_setup.shutil, "which", return_value="fixture"), \
                patch.object(agent_setup.subprocess, "Popen", side_effect=OSError(SECRET)):
            error = self.assert_rejected(lambda: agent_setup.read_host(self.root), status="BLOCKED")
            self.assertNotIn(SECRET, str(error))

    def test_cli_host_output_is_sanitized_and_blocked_exit_is_distinct(self) -> None:
        for available in (True, False):
            with self.subTest(available=available):
                config, skills = self.responses()
                if not available:
                    config["layers"][1]["disabledReason"] = SECRET
                stdout, stderr = io.StringIO(), io.StringIO()
                arguments = ["agent-check", "--root", str(self.root), "--host"]
                with patch.object(sys, "argv", arguments), patch.object(agent_setup, "read_host", return_value=(config, skills)), \
                        redirect_stdout(stdout), redirect_stderr(stderr):
                    exit_code = agent_setup.main()
                self.assertEqual(0 if available else 2, exit_code)
                self.assertNotIn(SECRET, stdout.getvalue() + stderr.getvalue())
                self.assertNotIn("mcp_servers", stdout.getvalue() + stderr.getvalue())
                if available:
                    self.assertEqual("PASS", json.loads(stdout.getvalue())["host"]["status"])
                else:
                    self.assertEqual("BLOCKED", json.loads(stderr.getvalue())["status"])


if __name__ == "__main__":
    unittest.main()
