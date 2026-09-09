"""Pure report and ownership tests; no runner, Docker daemon, server, or player is started."""

from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import tempfile
from types import MethodType, SimpleNamespace
import unittest
from unittest import mock


_SCRIPT = Path(__file__).with_name("protocol_ci.py")
_SPEC = importlib.util.spec_from_file_location("protocol_ci_under_test", _SCRIPT)
assert _SPEC is not None and _SPEC.loader is not None
protocol_ci = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(protocol_ci)

EXPECTED_CHECK_NAMES = """
Server.PublicInfo Authentication.ValidCredentials Authentication.WrongPassword User.Current
Server.AuthenticatedInfo Session.Capabilities Library.Views Items.List Items.Latest Items.Resume
Items.NextUp Items.Details Items.Search Items.EmptySearch Items.Paging UserData.FavoriteToggle
UserData.FavoriteRestore UserData.PlayedToggle UserData.PlayedRestore PlaybackInfo.Original
Media.OriginalRange PlaybackInfo.ForcedHls Media.HlsManifest Media.HlsSegment
Subtitles.ExternalNegotiation Media.WebVttSubtitle PlaybackReport.Start Session.StartReadback
PlaybackReport.Progress Session.ProgressReadback PlaybackReport.Stop Session.StopReadback
Cleanup.OriginalEncoding Cleanup.HlsEncoding Cleanup.SubtitleEncoding Cleanup.FavoriteRestore
Cleanup.PlayedRestore Cleanup.UserDataBooleans Session.Logout Authentication.LoggedOutTokenRejected
""".split()


def passing_report() -> dict:
    return {
        "Outcome": "Passed",
        "ExpectedServerVersion": "4.9.5.0",
        "ObservedServerVersion": "4.9.5.0",
        "NativeUi": "NotRun",
        "NativeDecoderAndFirstFrame": "NotRun",
        "Steps": [{"Name": name, "Status": "Passed", "Required": True, "HttpStatus": 200}
                  for name in EXPECTED_CHECK_NAMES],
    }


class SummaryTests(unittest.TestCase):
    def test_exact_40_successful_checks_and_zero_exit_are_accepted(self):
        self.assertEqual(40, len(EXPECTED_CHECK_NAMES))
        self.assertEqual(40, len(set(EXPECTED_CHECK_NAMES)))

        summary, passed = protocol_ci.summarize_report(passing_report(), 0)

        self.assertTrue(passed)
        self.assertEqual(40, summary["PassedChecks"])
        self.assertEqual(40, summary["ExpectedChecks"])
        self.assertEqual(0, summary["UnknownOrDuplicateStepCount"])
        self.assertEqual(sorted(EXPECTED_CHECK_NAMES), [step["Name"] for step in summary["Checks"]])

    def test_a_passed_37_check_report_cannot_omit_subtitle_negotiation_transfer_and_cleanup(self):
        report = passing_report()
        omitted = {"Subtitles.ExternalNegotiation", "Media.WebVttSubtitle", "Cleanup.SubtitleEncoding"}
        report["Steps"] = [step for step in report["Steps"] if step["Name"] not in omitted]
        self.assertEqual(37, len(report["Steps"]))

        summary, passed = protocol_ci.summarize_report(report, 0)

        self.assertFalse(passed)
        self.assertEqual(37, summary["PassedChecks"])
        self.assertEqual(40, summary["ExpectedChecks"])
        self.assertEqual(omitted, {step["Name"] for step in summary["Checks"] if step["Status"] == "Blocked"})

    def test_failed_supplemental_checks_are_required_by_the_ci_lane_even_when_the_probe_calls_them_optional(self):
        for optional in ("Server.AuthenticatedInfo", "Session.StartReadback", "Session.ProgressReadback", "Session.StopReadback"):
            with self.subTest(check=optional):
                report = passing_report()
                step = next(step for step in report["Steps"] if step["Name"] == optional)
                step.update(Status="Failed", Required=False)

                summary, passed = protocol_ci.summarize_report(report, 0)

                self.assertFalse(passed)
                self.assertEqual(39, summary["PassedChecks"])
                self.assertEqual("Failed", next(step["Status"] for step in summary["Checks"] if step["Name"] == optional))

    def test_duplicate_and_unknown_names_cannot_satisfy_the_exact_plan(self):
        for scenario in ("duplicate", "unknown"):
            with self.subTest(scenario=scenario):
                report = passing_report()
                if scenario == "duplicate":
                    report["Steps"].append(dict(report["Steps"][0]))
                else:
                    report["Steps"][0]["Name"] = "https://untrusted.example/api_key=private-token"

                summary, passed = protocol_ci.summarize_report(report, 0)

                self.assertFalse(passed)
                self.assertEqual(1, summary["UnknownOrDuplicateStepCount"])
                self.assertEqual(set(EXPECTED_CHECK_NAMES), {step["Name"] for step in summary["Checks"]})
                self.assertNotIn("private-token", json.dumps(summary))
                self.assertNotIn("untrusted.example", json.dumps(summary))

    def test_nonzero_process_exit_rejects_an_otherwise_passed_report(self):
        summary, passed = protocol_ci.summarize_report(passing_report(), 1)

        self.assertFalse(passed)
        self.assertEqual(1, summary["ProbeExitCode"])
        self.assertEqual(40, summary["PassedChecks"])

    def test_raw_urls_tokens_evidence_and_invalid_http_metadata_never_reach_the_exported_summary(self):
        report = passing_report()
        url = "https://untrusted.example/stream?api_key=private-token"
        report.update(ServerUrl=url, AccessToken="private-token", Evidence={"MediaUrl": url}, Error="private-token")
        for step in report["Steps"]:
            step.update(Evidence={"RequestUrl": url, "Token": "private-token"}, Message=url, ErrorCode=url)
        report["Steps"][0]["HttpStatus"] = True
        report["Steps"][1]["HttpStatus"] = url
        report["Steps"][2]["HttpStatus"] = 600
        report["Steps"][3]["HttpStatus"] = 204

        summary, passed = protocol_ci.summarize_report(report, 0)

        self.assertTrue(passed)
        self.assertEqual({"Checks", "PassedChecks", "ExpectedChecks", "ProbeExitCode", "UnknownOrDuplicateStepCount"}, set(summary))
        by_name = {step["Name"]: step for step in summary["Checks"]}
        for original in report["Steps"][:3]:
            self.assertNotIn("HttpStatus", by_name[original["Name"]])
        self.assertEqual(204, by_name[report["Steps"][3]["Name"]]["HttpStatus"])
        for step in summary["Checks"]:
            self.assertLessEqual(set(step), {"Name", "Status", "HttpStatus"})
        exported = json.dumps(summary, sort_keys=True)
        for forbidden in (url, "private-token", "untrusted.example", "Evidence", "ServerUrl", "AccessToken", "ErrorCode"):
            self.assertNotIn(forbidden, exported)

    def test_native_or_version_claims_do_not_replace_the_expected_protocol_scope(self):
        for field, value in (("ExpectedServerVersion", "different"), ("ObservedServerVersion", "different"),
                             ("NativeUi", "Passed"), ("NativeDecoderAndFirstFrame", "Passed")):
            with self.subTest(field=field):
                report = passing_report()
                report[field] = value
                _, passed = protocol_ci.summarize_report(report, 0)
                self.assertFalse(passed)

    def test_invalid_report_shapes_raise_only_the_fixed_validation_error(self):
        invalid = [(None, 0), ({"Steps": "private-token"}, 0), ({"Steps": ["private-token"]}, 0),
                   ({"Steps": [{"Name": {"Url": "private-token"}}]}, 0), (passing_report(), True)]
        for report, exit_code in invalid:
            with self.subTest(report_type=type(report).__name__, exit_type=type(exit_code).__name__):
                with self.assertRaises(protocol_ci.Failure) as error:
                    protocol_ci.summarize_report(report, exit_code)
                self.assertEqual("InvalidProbeReport", error.exception.code)
                self.assertEqual("InvalidProbeReport", str(error.exception))


class CleanupOwnershipTests(unittest.TestCase):
    def setUp(self):
        repository = Path(__file__).resolve().parents[3]
        artifacts = repository / "artifacts"
        artifacts.mkdir(exist_ok=True)
        self.assertTrue(artifacts.resolve().is_relative_to(repository))
        self.scratch = artifacts / "protocol-ci-unittest-scratch"
        self.scratch.mkdir(exist_ok=True)
        self.assertEqual(artifacts.resolve(), self.scratch.resolve().parent)
        self.temporary = tempfile.TemporaryDirectory(prefix="ownership-", dir=self.scratch)
        self.private_root = Path(self.temporary.name)
        self.assertEqual(self.scratch.resolve(), self.private_root.resolve().parent)

    def tearDown(self):
        # The only real recursive removal in these tests is restricted to this test's unique scratch directory.
        self.assertFalse(self.private_root.is_symlink())
        self.assertEqual(self.scratch.resolve(), self.private_root.resolve().parent)
        self.temporary.cleanup()

    def test_a_foreign_owner_label_never_authorizes_stop_remove_or_private_directory_deletion(self):
        for kind in ("container", "network"):
            with self.subTest(resource=kind):
                self.assert_foreign_resource_is_preserved(kind, wrong_label=True)

    def test_a_foreign_resource_id_never_authorizes_stop_remove_or_private_directory_deletion(self):
        for kind in ("container", "network"):
            with self.subTest(resource=kind):
                self.assert_foreign_resource_is_preserved(kind, wrong_label=False)

    def test_failed_relay_cleanup_preserves_private_ownership_even_when_docker_resources_are_absent(self):
        owner = "a" * 32
        work = self.private_root / "relay-release-failure"
        work.mkdir()
        self.assertEqual(self.private_root.resolve(), work.resolve().parent)
        state = {
            "Owner": owner, "WorkflowRunId": "1001", "WorkflowRunAttempt": "1",
            "ContainerName": "emby-protocol-" + owner, "NetworkName": "emby-protocol-net-" + owner,
        }
        state_path = work / "owned-resources.json"
        state_path.write_text(json.dumps(state), encoding="utf-8")
        relay = mock.Mock()
        relay.close.side_effect = RuntimeError("The synthetic relay did not drain.")
        runner = SimpleNamespace(temporary=self.private_root, work=work, state_path=state_path,
                                 run_id="1001", attempt="1", summary={}, relay=relay)
        runner.docker = mock.Mock(return_value=SimpleNamespace(returncode=0, stdout=b""))
        runner.docker_json = MethodType(protocol_ci.Runner.docker_json, runner)
        runner.find_resource = MethodType(protocol_ci.Runner.find_resource, runner)

        with mock.patch.object(protocol_ci.subprocess, "Popen", side_effect=AssertionError("No command may run.")) as process, \
                mock.patch.object(protocol_ci.shutil, "rmtree") as remove_tree:
            completed = protocol_ci.Runner.cleanup(runner)

            self.assertFalse(completed)
            self.assertFalse(runner.summary["RelayCleanupCompleted"])
            self.assertFalse(runner.summary["CleanupCompleted"])
            self.assertIs(relay, runner.relay)
            relay.close.assert_called_once_with()
            remove_tree.assert_not_called()
            process.assert_not_called()
        self.assertTrue(work.is_dir())
        self.assertEqual(state, json.loads(state_path.read_text(encoding="utf-8")))
        queries = [invocation.args[:2] for invocation in runner.docker.call_args_list]
        self.assertIn(("container", "ls"), queries)
        self.assertIn(("network", "ls"), queries)
        for query in queries:
            self.assertIn(query, (("container", "ls"), ("network", "ls")))

    def assert_foreign_resource_is_preserved(self, kind: str, *, wrong_label: bool):
        owner = "a" * 32
        work = self.private_root / (kind + ("-label" if wrong_label else "-identity"))
        work.mkdir()
        self.assertEqual(self.private_root.resolve(), work.resolve().parent)
        state = {
            "Owner": owner, "WorkflowRunId": "1001", "WorkflowRunAttempt": "1",
            "ContainerName": "emby-protocol-" + owner, "NetworkName": "emby-protocol-net-" + owner,
            "ContainerId": "1" * 64, "NetworkId": "2" * 64,
        }
        state_path = work / "owned-resources.json"
        state_path.write_text(json.dumps(state), encoding="utf-8")
        sentinel = work / "credentials.json"
        sentinel.write_text('{"Token":"synthetic-private-value"}', encoding="utf-8")
        name = state["ContainerName" if kind == "container" else "NetworkName"]
        expected_id = state["ContainerId" if kind == "container" else "NetworkId"]
        actual_id = expected_id if wrong_label else "f" * 64
        labels = {protocol_ci.OWNER_LABEL: "b" * 32 if wrong_label else owner}
        metadata = {"Id": actual_id, "Config": {"Labels": labels}} if kind == "container" else {"Id": actual_id, "Labels": labels}

        def docker_read_only(*arguments, **kwargs):
            if len(arguments) >= 2 and arguments[1] == "ls" and arguments[0] in ("container", "network"):
                listed = {"ID": actual_id, "Names" if kind == "container" else "Name": name}
                data = (json.dumps(listed) + "\n").encode() if arguments[0] == kind else b""
            elif arguments == (kind, "inspect", name):
                data = json.dumps([metadata]).encode()
            else:
                raise AssertionError("The ownership test attempted a mutating Docker operation.")
            return SimpleNamespace(returncode=0, stdout=data)

        # Exercise the production methods against a data-only double, never Runner.__init__ or a real Docker command.
        runner = SimpleNamespace(temporary=self.private_root, work=work, state_path=state_path,
                                 run_id="1001", attempt="1", summary={})
        runner.docker = mock.Mock(side_effect=docker_read_only)
        runner.docker_json = MethodType(protocol_ci.Runner.docker_json, runner)
        runner.find_resource = MethodType(protocol_ci.Runner.find_resource, runner)
        with mock.patch.object(protocol_ci.Runner, "__init__", side_effect=AssertionError("A real runner must not be created.")) as constructor, \
                mock.patch.object(protocol_ci.subprocess, "Popen", side_effect=AssertionError("No command may run.")) as process, \
                mock.patch.object(protocol_ci.shutil, "rmtree") as remove_tree:
            completed = protocol_ci.Runner.cleanup(runner)

            self.assertFalse(completed)
            self.assertFalse(runner.summary["CleanupCompleted"])
            constructor.assert_not_called()
            process.assert_not_called()
            remove_tree.assert_not_called()
        self.assertTrue(work.is_dir())
        self.assertEqual('{"Token":"synthetic-private-value"}', sentinel.read_text(encoding="utf-8"))
        self.assertTrue(runner.docker.called)
        for invocation in runner.docker.call_args_list:
            arguments = invocation.args
            self.assertIn(arguments[0], ("container", "network"))
            self.assertIn(arguments[1], ("ls", "inspect"))
            self.assertNotIn("stop", arguments)
            self.assertNotIn("rm", arguments)


if __name__ == "__main__":
    unittest.main()
