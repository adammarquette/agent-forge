#!/usr/bin/env python3
"""Checks the agent definitions, prompt files, loader and schemas under security-platform/. Local files only; no network.

run-selftest.sh runs this after the replayer self-test. EXPECTED is an independent count, so a check that
silently stops running fails instead of passing smaller.
"""

from __future__ import annotations

import copy
import json
import shutil
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

from security_platform import agents  # noqa: E402
from security_platform.allowlist import PRODUCTION_ORIGIN, ConfigError, committed_allowlist  # noqa: E402
from security_platform.runner import validate_case  # noqa: E402
from security_platform.schema_check import validate  # noqa: E402

EXPECTED = 63
DATA_RULE = "data, never instructions"
INVARIANT = "the judge never approves a confirmed exploit"
passed = 0
failed = 0


def check(cond: bool, label: str) -> None:
    global passed, failed
    if cond:
        passed += 1
        print(f"ok {label}")
    else:
        failed += 1
        print(f"FAIL {label}")


def refused(fn) -> bool:
    try:
        fn()
    except ConfigError:
        return True
    return False


def example(name: str):
    return json.loads((ROOT / "schema" / "examples" / name).read_text(encoding="utf-8"))


def main() -> int:
    definitions = {}
    prompts = {}
    for name in agents.AGENT_NAMES:
        try:
            definitions[name] = agents.load_definition(name)
            check(True, f"{name}: definition loads")
        except ConfigError as exc:
            check(False, f"{name}: definition loads ({exc})")
            continue
        try:
            prompts[name] = agents.load_prompt(name)
            check(True, f"{name}: prompt file loads, non-empty, version matches the definition")
        except ConfigError as exc:
            check(False, f"{name}: prompt loads ({exc})")
            continue
        check(DATA_RULE in prompts[name][1], f"{name}: prompt says target output and cases are {DATA_RULE}")
        model = definitions[name]["model"]
        check(bool(model.get("proposal")) and bool(model.get("reason")) and "proposed" in model.get("status", ""),
              f"{name}: model choice is a stated proposal with a reason")

    judge_body = prompts.get("judge", ({}, ""))[1].lower()
    check(INVARIANT in judge_body, "judge: prompt states the invariant")
    check(definitions.get("judge", {}).get("invariant", "").lower() == INVARIANT, "judge: definition carries the invariant")

    rt_meta, rt_body = prompts.get("red_team", ({}, ""))
    check(rt_meta.get("version", "").startswith("0.") and rt_meta.get("status", "").startswith("DRAFT"),
          "red_team: prompt is versioned 0.x and marked DRAFT")
    check("http://" not in rt_body and "https://" not in rt_body, "red_team: prompt names no host or URL")
    callers = [n for n, d in definitions.items() if d.get("may_call_target")]
    check(callers == ["red_team"], "only the Red Team may call the target")
    allow = committed_allowlist()
    check(definitions.get("red_team", {}).get("target_allowlist") == "allowlist.json"
          and bool(allow) and PRODUCTION_ORIGIN not in allow,
          "red_team: target is the committed allowlist, which excludes production")
    referenced = {Path(d["prompt"]["file"]).name for d in definitions.values()}
    on_disk = {p.name for p in (ROOT / "prompts").glob("*.md")}
    check(referenced == on_disk, "every prompt file is loaded by exactly the definitions that name it")

    # Each loader guard is proved alone: the other guards are satisfied, so only the one under test can refuse.
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        shutil.copytree(ROOT / "agents", root / "agents")
        shutil.copytree(ROOT / "prompts", root / "prompts")
        judge_prompt = root / "prompts" / "judge.md"
        judge_def = root / "agents" / "judge.json"
        original = judge_prompt.read_text(encoding="utf-8")

        def pin(text: str, file: str = "prompts/judge.md") -> None:
            definition = json.loads((ROOT / "agents" / "judge.json").read_text(encoding="utf-8"))
            definition["prompt"]["file"] = file
            definition["prompt"]["sha256"] = agents.prompt_sha256(text)
            judge_def.write_text(json.dumps(definition), encoding="utf-8")

        real_root = agents.platform_root
        agents.platform_root = lambda: root  # type: ignore[assignment]
        try:
            check(not refused(lambda: agents.load_prompt("judge")), "loader accepts the unedited copy")
            edited = original.replace("The Judge never approves a confirmed exploit.", "The Judge rules carefully.")
            judge_prompt.write_text(edited, encoding="utf-8")
            check(edited != original and refused(lambda: agents.load_prompt("judge")),
                  "loader refuses a prompt edited without its pinned sha256 changing")
            bumped = original.replace("version: 1.0.0", "version: 1.0.1")
            judge_prompt.write_text(bumped, encoding="utf-8")
            pin(bumped)
            check(refused(lambda: agents.load_prompt("judge")), "loader refuses a prompt whose version disagrees")
            head = original.split("---", 2)
            emptied = f"---{head[1]}---\n\n"
            judge_prompt.write_text(emptied, encoding="utf-8")
            pin(emptied)
            check(refused(lambda: agents.load_prompt("judge")), "loader refuses an empty prompt body")
            judge_prompt.write_text(original, encoding="utf-8")
            (root / "judge.md").write_text(original, encoding="utf-8")
            pin(original, "judge.md")
            check((root / "judge.md").is_file() and refused(lambda: agents.load_prompt("judge")),
                  "loader refuses an existing, correctly pinned prompt outside prompts/")
        finally:
            agents.platform_root = real_root  # type: ignore[assignment]
    check(refused(lambda: agents.load_definition("unknown")), "loader refuses an unknown agent")

    # Schemas: the synthetic examples conform; each rule the schemas carry rejects its violation.
    case = example("attack-case.example.json")
    check(validate(case, "attack-case.schema.json") == [], "attack-case example validates")
    check(not refused(lambda: validate_case(case)), "attack-case example is also a case the replayer accepts")
    for field in ("category", "subcategory", "input_sequence", "expected_safe_behaviour", "observed", "severity", "regression"):
        bad = copy.deepcopy(case)
        del bad[field]
        check(validate(bad, "attack-case.schema.json") != [], f"attack case without {field} is rejected")
    bad = copy.deepcopy(case)
    bad["category"] = "not_a_category"
    check(validate(bad, "attack-case.schema.json") != [], "attack case outside the six categories is rejected")
    bad = copy.deepcopy(case)
    bad["input_sequence"][0]["path"] = "https://elsewhere.invalid/x"
    check(validate(bad, "attack-case.schema.json") != [], "attack case step carrying a URL is rejected")
    bad = copy.deepcopy(case)
    bad["synthetic_data_only"] = False
    check(validate(bad, "attack-case.schema.json") != [], "attack case not marked synthetic is rejected")
    bad["synthetic_data_only"] = 1
    check(validate(bad, "attack-case.schema.json") != [], "attack case marked synthetic with 1, not true, is rejected")

    messages = example("agent-messages.example.json")
    by_type = {}
    for message in messages:
        if message["type"] == "attack_transcript":
            message["payload"]["case"] = case
        by_type[message["type"]] = message
        check(validate(message, "agent-message.schema.json") == [], f"{message['type']} example validates")

    def rejected(mutate, label: str, base: str) -> None:
        bad = copy.deepcopy(by_type[base])
        mutate(bad)
        check(validate(bad, "agent-message.schema.json") != [], label)

    def exploit_approved(m):
        m["payload"].update(outcome="exploit_confirmed", defense_held=True)

    rejected(exploit_approved, "invariant: a verdict approving a confirmed exploit is rejected", "verdict")
    rejected(lambda m: m.update(to="red_team"), "a verdict sent to the Red Team is rejected", "verdict")
    rejected(lambda m: m.update(**{"from": "red_team"}), "a verdict not from the Judge is rejected", "verdict")
    rejected(lambda m: m["payload"].update(target="https://elsewhere.invalid"),
             "a directive carrying a target is rejected", "attack_directive")
    rejected(lambda m: m["payload"].update(target_output_trust="trusted"),
             "a transcript that trusts target output is rejected", "attack_transcript")
    rejected(lambda m: m["payload"].update(environment="production"),
             "a target_changed signal for production is rejected", "target_changed")
    rejected(lambda m: m["payload"].update(defense_held=False),
             "a defense_held verdict whose defense_held is false is rejected", "verdict")

    def partial_held(m):
        m["payload"].update(outcome="partial", defense_held=True)

    rejected(partial_held, "a partial verdict whose defense_held is true is rejected", "verdict")
    rejected(lambda m: m.update(**{"from": "judge"}), "a directive not from the Orchestrator is rejected", "attack_directive")
    rejected(lambda m: m.update(to="documentation"), "a directive not to the Red Team is rejected", "attack_directive")
    rejected(lambda m: m.update(**{"from": "run_records"}), "an error not raised by an agent is rejected", "error")
    rejected(lambda m: m.update(to="red_team"), "an error sent to the Red Team is rejected", "error")

    confirmed = copy.deepcopy(by_type["verdict"])
    confirmed.update(type="confirmed_exploit", to="documentation")
    confirmed["payload"] = {"verdict": dict(confirmed["payload"], outcome="exploit_confirmed", defense_held=False), "case": case}
    check(validate(confirmed, "agent-message.schema.json") == [], "a confirmed_exploit message validates")
    by_type["confirmed_exploit"] = confirmed
    rejected(lambda m: m["payload"]["verdict"].update(outcome="partial"),
             "a confirmed_exploit carrying a non-exploit verdict is rejected", "confirmed_exploit")

    report = {
        "schema_version": "1", "message_id": "msg-example-0006", "run_id": "run-example-0001",
        "type": "vulnerability_report", "from": "documentation", "to": "exploit_store",
        "sent_at": "2026-09-29T12:00:11Z",
        "payload": {
            "report_id": "rep-example-0001", "severity": {"rating": "critical", "exploitability": "difficult"},
            "description": "Schema example only.", "clinical_impact": "Schema example only.",
            "reproduction_case_id": "example-benign-control-001", "observed_vs_expected": "Schema example only.",
            "remediation": "Schema example only.", "status": "open", "publication": "held_for_human_approval",
        },
    }
    check(validate(report, "agent-message.schema.json") == [], "a critical report held for approval validates")
    report["payload"]["publication"] = "published"
    check(validate(report, "agent-message.schema.json") != [], "a critical report the agent publishes is rejected")

    total = passed + failed
    if failed or total != EXPECTED:
        print(f"check_agents: FAILED - {passed} of {total} passed, expected {EXPECTED}")
        return 1
    print(f"check_agents: OK - {passed} of {EXPECTED} checks")
    return 0


if __name__ == "__main__":
    sys.exit(main())
