"""Exercise new UI endpoints on an ownership-checked official server, without desktop input."""

from __future__ import annotations

import argparse
import copy
import json
from pathlib import Path
import secrets
import time
import urllib.parse
import urllib.request

import backend
from backend import Backend, Failure, SERVER_URL, read_json, sha256, utc_now, write_json
import loopback_relay
import protocol_ci


EXPECTED_CHECKS = frozenset("""
Library.Views Items.Latest.BareArray Items.Search Items.Genres Items.Persons
Items.Similar.QueryResult Items.LocalTrailers.BareArray Shows.Seasons
Shows.Episodes.Season1 Shows.Episodes.Season2 NonAdmin.Favorite.ToggleRestore
NonAdmin.Played.ToggleRestore NonAdmin.HideFromResume.UnchangedPlayed
NonAdmin.UserConfiguration.MergeRestore Media.AuthenticatedRange
Admin.Collection.CreateAddReadback Admin.Metadata.FullMergeRestore Admin.Metadata.Refresh
NonAdmin.Metadata.Authorization NonAdmin.MetadataRefresh.Authorization NonAdmin.Collection.Authorization
""".split())


class Verifier:
    def __init__(self, work: Path):
        self.backend = Backend(work, load=True)
        self.backend.inspect_isolation()
        self.backend.check_server_identity(self.backend.api("GET", "System/Info/Public"))
        self.catalog = read_json(work / "catalog.json")
        self.admin_credentials = read_json(work / "admin-credentials.json")
        self.admin_token = None
        self.user_token = None
        self.user_id = None
        self.collection_ids = []
        self.collection_names = {}
        self.run_nonce = secrets.token_hex(4)
        suffix = self.backend.state["Owner"][:8] + "-" + self.run_nonce
        self.user_name = "lumen-api-check-" + suffix
        self.non_admin_collection_name = "Lumen NonAdmin API Check " + suffix
        self.summary = {"SchemaVersion": 1, "Scope": "OfficialEmbyNewUiApiCapabilities", "Outcome": "Running",
            "OfficialServer": True, "SyntheticDataOnly": True, "ServerVersion": self.backend.summary["ServerVersion"],
            "ServerIdentityMatched": True, "StartedUtc": utc_now(), "ExecutionEnvironment": "ssh test-env",
            "SyntheticPlaybackReports": True,
            "NativeUi": "NotRun", "NativeDecoderAndFirstFrame": "NotRun", "Checks": [], "CleanupCompleted": False}
        sources = {"verify_api.py": Path(__file__), "backend.py": Path(backend.__file__),
            "protocol_ci.py": Path(protocol_ci.__file__), "loopback_relay.py": Path(loopback_relay.__file__)}
        self.summary["SourceSha256"] = {name: sha256(path) for name, path in sources.items()}

    def admin(self, method: str, route: str, body=None):
        return self.backend.api(method, route, body=body, token=self.admin_token, role="api-admin")

    def user(self, method: str, route: str, body=None):
        return self.backend.api(method, route, body=body, token=self.user_token, role="api-user")

    def check(self, name: str, function) -> None:
        try:
            evidence = function() or {}
            self.summary["Checks"].append({"Name": name, "Status": "Passed", **evidence})
        except Failure as error:
            evidence = {"FailureCode": error.code}
            if isinstance(error.diagnostic, dict) and type(error.diagnostic.get("HttpStatus")) is int:
                evidence["HttpStatus"] = error.diagnostic["HttpStatus"]
            self.summary["Checks"].append({"Name": name, "Status": "Failed", **evidence})
        except Exception as error:
            self.summary["Checks"].append({"Name": name, "Status": "Failed", "ExceptionType": type(error).__name__})

    def query(self, route: str, *, minimum=1):
        result = self.user("GET", route)
        if not isinstance(result, dict) or not isinstance(result.get("Items"), list) or len(result["Items"]) < minimum:
            raise Failure("QueryResultShapeMismatch")
        return {"ItemCount": len(result["Items"]), "HasTotalRecordCount": type(result.get("TotalRecordCount")) is int}

    def array(self, route: str):
        result = self.user("GET", route)
        if not isinstance(result, list) or not result:
            raise Failure("BareArrayShapeMismatch")
        return {"ItemCount": len(result), "BareArray": True}

    def state_toggle(self, movie_id: str, field: str, endpoint: str):
        before = self.user("GET", f"Users/{self.user_id}/Items/{movie_id}")["UserData"][field]
        changed = self.user("POST" if not before else "DELETE", f"Users/{self.user_id}/{endpoint}/{movie_id}")
        observed = self.user("GET", f"Users/{self.user_id}/Items/{movie_id}")["UserData"][field]
        self.user("POST" if before else "DELETE", f"Users/{self.user_id}/{endpoint}/{movie_id}")
        restored = self.user("GET", f"Users/{self.user_id}/Items/{movie_id}")["UserData"][field]
        if changed.get(field) != (not before) or observed != (not before) or restored != before:
            raise Failure("UserStateToggleOrRestoreMismatch")
        return {"Mutated": True, "ReadBack": True, "Restored": True}

    def resume_hide(self, movie_id: str):
        self.backend.seed_resume(self.user_id, movie_id, self.user_token, role="api-user")
        expires = time.monotonic() + 10
        while time.monotonic() < expires:
            before = self.user("GET", f"Users/{self.user_id}/Items/{movie_id}")["UserData"]
            resume = self.user("GET", f"Users/{self.user_id}/Items/Resume?Limit=20&MediaTypes=Video")["Items"]
            if movie_id in [item["Id"] for item in resume]:
                break
            time.sleep(0.2)
        else:
            self.summary["ResumeSeedDiagnostic"] = {"PlaybackPositionTicks": before["PlaybackPositionTicks"],
                "Played": before["Played"], "PlayCount": before.get("PlayCount"), "ResumeItemCount": len(resume)}
            raise Failure("SeededResumeMissing")
        hidden = self.user("POST", f"Users/{self.user_id}/Items/{movie_id}/HideFromResume?Hide=true")
        after = self.user("GET", f"Users/{self.user_id}/Items/{movie_id}")["UserData"]
        resume = self.user("GET", f"Users/{self.user_id}/Items/Resume?Limit=20&MediaTypes=Video")["Items"]
        if not isinstance(hidden, dict) or after["Played"] != before["Played"] or movie_id in [item["Id"] for item in resume]:
            raise Failure("HideFromResumeStateMismatch")
        self.user("POST", f"Users/{self.user_id}/Items/{movie_id}/HideFromResume?Hide=false")
        restored = self.user("GET", f"Users/{self.user_id}/Items/{movie_id}")["UserData"]
        resume = self.user("GET", f"Users/{self.user_id}/Items/Resume?Limit=20&MediaTypes=Video")["Items"]
        if (movie_id not in [item["Id"] for item in resume] or restored["Played"] != before["Played"]
                or restored["PlaybackPositionTicks"] != before["PlaybackPositionTicks"]):
            raise Failure("HideFromResumeVisibilityRestoreMismatch")
        return {"BareUserItemData": True, "HiddenFromResume": True, "PlayedStateUnchanged": True,
            "VisibilityRestored": True,
            "PositionBeforeTicks": before["PlaybackPositionTicks"], "PositionAfterTicks": after["PlaybackPositionTicks"]}

    def configuration(self):
        original = self.user("GET", f"Users/{self.user_id}")["Configuration"]
        changed = copy.deepcopy(original)
        changed["EnableNextEpisodeAutoPlay"] = not original.get("EnableNextEpisodeAutoPlay", False)
        try:
            self.user("POST", f"Users/{self.user_id}/Configuration", changed)
            observed = self.user("GET", f"Users/{self.user_id}")["Configuration"]
            if observed.get("EnableNextEpisodeAutoPlay") != changed["EnableNextEpisodeAutoPlay"]:
                raise Failure("ConfigurationMutationReadbackMismatch")
            preserved = all(observed.get(key) == value for key, value in original.items() if key != "EnableNextEpisodeAutoPlay")
            if not preserved:
                raise Failure("UnrelatedConfigurationChanged")
        finally:
            self.user("POST", f"Users/{self.user_id}/Configuration", original)
        restored = self.user("GET", f"Users/{self.user_id}")["Configuration"] == original
        if not restored:
            raise Failure("ConfigurationRestoreMismatch")
        return {"Mutated": True, "UnknownFieldsPreserved": True, "Restored": True, "DedicatedUser": True}

    def metadata(self, movie_id: str):
        original = self.admin("GET", f"Users/{self.admin_id}/Items/{movie_id}")
        changed = copy.deepcopy(original)
        changed["Overview"] = original.get("Overview", "") + " Official API mutation fixture."
        try:
            self.admin("POST", f"Items/{movie_id}", changed)
            observed = self.admin("GET", f"Users/{self.admin_id}/Items/{movie_id}")
            if observed.get("Overview") != changed["Overview"]:
                raise Failure("MetadataMutationReadbackMismatch")
            if any(observed.get(key) != original.get(key) for key in ("Genres", "People", "ProviderIds")):
                raise Failure("UnrelatedNestedMetadataChanged")
        finally:
            self.admin("POST", f"Items/{movie_id}", original)
        restored = self.admin("GET", f"Users/{self.admin_id}/Items/{movie_id}")
        if restored.get("Overview") != original.get("Overview"):
            raise Failure("MetadataRestoreMismatch")
        return {"FullJsonMerge": True, "UnknownNestedFieldsPreserved": True, "Mutated": True, "ReadBack": True, "Restored": True}

    def authorization(self, name: str, function):
        try:
            result = function()
            if name == "CollectionCreation" and isinstance(result, dict) and result.get("Id"):
                self.collection_ids.append(result["Id"])
                self.collection_names[result["Id"]] = self.non_admin_collection_name
            return {"ObservedAuthorization": "Allowed", "ResponseShape": "Empty" if result is None else "Object"}
        except Failure as error:
            status = error.diagnostic.get("HttpStatus") if isinstance(error.diagnostic, dict) else None
            if status not in (401, 403):
                raise
            return {"ObservedAuthorization": "Rejected", "HttpStatus": status}

    def collection(self, movies: list[dict]):
        name = "Lumen API Mutation " + self.backend.state["Owner"][:8] + "-" + self.run_nonce
        route = "Collections?" + urllib.parse.urlencode({"Name": name, "Ids": ",".join(item["Id"] for item in movies[:2]), "IsLocked": "false"})
        result = self.admin("POST", route)
        collection_id = result["Id"]
        self.collection_ids.append(collection_id)
        self.collection_names[collection_id] = name
        if not isinstance(result, dict) or not collection_id:
            raise Failure("CollectionCreationShapeMismatch")
        self.admin("POST", f"Collections/{collection_id}/Items?Ids={movies[2]['Id']}")
        observed = self.admin("GET", f"Users/{self.admin_id}/Items?ParentId={collection_id}&Recursive=false&Limit=100")["Items"]
        if not {item["Id"] for item in movies[:3]}.issubset({item["Id"] for item in observed}):
            raise Failure("CollectionMemberReadbackMismatch")
        return {"CreationObjectHasId": True, "AddedMember": True, "MemberCount": len(observed)}

    def media_range(self, movie_id: str):
        headers = self.backend.headers("api-user", self.user_token)
        headers["Range"] = "bytes=0-4095"
        request = urllib.request.Request(SERVER_URL + f"/emby/Videos/{movie_id}/stream.mp4?Static=true", headers=headers)
        with self.backend.opener.open(request, timeout=15) as response:
            payload = response.read(4097)
            status = response.status
        if status != 206 or len(payload) != 4096:
            raise Failure("AuthenticatedMediaRangeMismatch")
        return {"HttpStatus": status, "Bytes": len(payload), "Authenticated": True}

    def run(self) -> dict:
        credentials = self.admin_credentials
        authentication = self.backend.api("POST", "Users/AuthenticateByName", body={"Username": credentials["Username"],
            "Pw": credentials["Password"]}, role="api-admin")
        self.admin_token = authentication["AccessToken"]
        self.admin_id = authentication["User"]["Id"]
        created = self.admin("POST", "Users/New", {"Name": self.user_name})
        self.user_id = created["Id"]
        password = secrets.token_urlsafe(32)
        self.admin("POST", f"Users/{self.user_id}/Password", {"Id": self.user_id, "NewPw": password, "ResetPassword": False})
        authentication = self.backend.api("POST", "Users/AuthenticateByName", body={"Username": created["Name"], "Pw": password}, role="api-user")
        self.user_token = authentication["AccessToken"]
        movies = [item for item in self.catalog["Items"] if item.get("Type") == "Movie"]
        movie = next(item for item in movies if item["Name"] == "Northern Lights")
        mutation_movie = next(item for item in movies if item["Name"] == "Wild Frequencies")
        show = next(item for item in self.catalog["Items"] if item.get("Type") == "Series")
        user = self.user("GET", f"Users/{self.user_id}")
        if user.get("Policy", {}).get("IsAdministrator") is not False:
            raise Failure("DedicatedNonAdminUserRequired")
        self.summary["DedicatedNonAdminUser"] = True
        detail = self.user("GET", f"Users/{self.user_id}/Items/{movie['Id']}")
        admin_detail = self.admin("GET", f"Users/{self.admin_id}/Items/{movie['Id']}")
        self.summary["CanEditItems"] = {"NonAdminPresent": "CanEditItems" in detail, "NonAdminValue": detail.get("CanEditItems"),
            "AdminPresent": "CanEditItems" in admin_detail, "AdminValue": admin_detail.get("CanEditItems")}
        self.check("Library.Views", lambda: self.query(f"Users/{self.user_id}/Views", minimum=2))
        self.check("Items.Latest.BareArray", lambda: self.array(f"Users/{self.user_id}/Items/Latest?Limit=20&IncludeItemTypes=Movie"))
        self.check("Items.Search", lambda: self.query(f"Users/{self.user_id}/Items?SearchTerm=Northern&Recursive=true&IncludeItemTypes=Movie"))
        self.check("Items.Genres", lambda: self.query(f"Genres?UserId={self.user_id}&IncludeItemTypes=Movie&Limit=500", minimum=3))
        self.check("Items.Persons", lambda: self.query(f"Persons?UserId={self.user_id}&IncludeItemTypes=Movie&Limit=500", minimum=3))
        self.check("Items.Similar.QueryResult", lambda: self.query(f"Items/{movie['Id']}/Similar?UserId={self.user_id}&Limit=16&Fields=Overview,Genres,MediaStreams,PrimaryImageAspectRatio"))
        self.check("Items.LocalTrailers.BareArray", lambda: self.array(f"Users/{self.user_id}/Items/{movie['Id']}/LocalTrailers"))
        self.check("Shows.Seasons", lambda: self.query(f"Shows/{show['Id']}/Seasons?UserId={self.user_id}", minimum=2))
        for season in [item for item in self.catalog["Items"] if item.get("Type") == "Season"]:
            self.check("Shows.Episodes.Season" + str(season.get("IndexNumber")), lambda season=season:
                self.query(f"Shows/{show['Id']}/Episodes?UserId={self.user_id}&SeasonId={season['Id']}", minimum=2))
        self.check("NonAdmin.Favorite.ToggleRestore", lambda: self.state_toggle(movie["Id"], "IsFavorite", "FavoriteItems"))
        self.check("NonAdmin.Played.ToggleRestore", lambda: self.state_toggle(movie["Id"], "Played", "PlayedItems"))
        self.check("NonAdmin.HideFromResume.UnchangedPlayed", lambda: self.resume_hide(movie["Id"]))
        self.check("NonAdmin.UserConfiguration.MergeRestore", self.configuration)
        self.check("Media.AuthenticatedRange", lambda: self.media_range(movie["Id"]))
        self.check("Admin.Collection.CreateAddReadback", lambda: self.collection(movies))
        self.check("Admin.Metadata.FullMergeRestore", lambda: self.metadata(mutation_movie["Id"]))
        refresh = f"Items/{mutation_movie['Id']}/Refresh?Recursive=false&MetadataRefreshMode=Default&ImageRefreshMode=Default&ReplaceAllMetadata=false&ReplaceAllImages=false"
        self.check("Admin.Metadata.Refresh", lambda: self.admin("POST", refresh, {"ReplaceThumbnailImages": False}))
        non_admin_metadata = copy.deepcopy(detail)
        self.check("NonAdmin.Metadata.Authorization", lambda: self.authorization("Metadata", lambda:
            self.user("POST", f"Items/{movie['Id']}", non_admin_metadata)))
        self.check("NonAdmin.MetadataRefresh.Authorization", lambda: self.authorization("MetadataRefresh", lambda:
            self.user("POST", refresh, {"ReplaceThumbnailImages": False})))
        self.check("NonAdmin.Collection.Authorization", lambda: self.authorization("CollectionCreation", lambda:
            self.user("POST", "Collections?" + urllib.parse.urlencode({"Name": self.non_admin_collection_name,
                "Ids": movie["Id"], "IsLocked": "false"}))))
        return self.summary

    def cleanup(self):
        complete = True
        for collection_id in self.collection_ids:
            try:
                detail = self.admin("GET", f"Users/{self.admin_id}/Items/{collection_id}")
                if detail.get("Type") != "BoxSet" or detail.get("Name") != self.collection_names.get(collection_id):
                    raise Failure("OwnedMutationCollectionIdentityMismatch")
                self.admin("DELETE", f"Items/{collection_id}")
                remaining = self.admin("GET", f"Users/{self.admin_id}/Items?IncludeItemTypes=BoxSet&Recursive=true&Limit=500")["Items"]
                if collection_id in [item["Id"] for item in remaining]:
                    raise Failure("OwnedMutationCollectionCleanupIncomplete")
            except Exception:
                complete = False
        if self.user_token:
            try:
                self.user("POST", "Sessions/Logout")
            except Exception:
                complete = False
        if self.user_id:
            try:
                detail = self.admin("GET", f"Users/{self.user_id}")
                if detail.get("Name") != self.user_name:
                    raise Failure("OwnedTemporaryUserIdentityMismatch")
                self.admin("DELETE", f"Users/{self.user_id}")
                if self.user_id in [item["Id"] for item in self.admin("GET", "Users")]:
                    raise Failure("OwnedTemporaryUserCleanupIncomplete")
            except Exception:
                complete = False
        if self.admin_token:
            try:
                self.admin("POST", "Sessions/Logout")
            except Exception:
                complete = False
        self.summary["CleanupCompleted"] = complete


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work", type=Path, required=True)
    args = parser.parse_args()
    verifier = Verifier(args.work)
    try:
        verifier.run()
    except Failure as error:
        verifier.summary["FailureCode"] = error.code
    except Exception as error:
        verifier.summary["ExceptionType"] = type(error).__name__
    finally:
        verifier.cleanup()
    checks = verifier.summary["Checks"]
    verifier.summary["PassedChecks"] = sum(item["Status"] == "Passed" for item in checks)
    verifier.summary["TotalChecks"] = len(checks)
    verifier.summary["CompletedUtc"] = utc_now()
    names = [item["Name"] for item in checks]
    verifier.summary["ExpectedChecks"] = len(EXPECTED_CHECKS)
    verifier.summary["UnknownOrDuplicateCheckCount"] = len(names) - len(set(names) & EXPECTED_CHECKS)
    passed = (set(names) == EXPECTED_CHECKS and len(names) == len(EXPECTED_CHECKS) and all(item["Status"] == "Passed" for item in checks)
        and verifier.summary["CleanupCompleted"] and "FailureCode" not in verifier.summary and "ExceptionType" not in verifier.summary)
    verifier.summary["Outcome"] = "Passed" if passed else "Failed"
    write_json(args.work / "new-ui-api-summary.json", verifier.summary)
    print(json.dumps(verifier.summary))
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
