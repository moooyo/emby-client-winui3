"""Export fixed resume-policy and synthetic-user state fields from an owned server."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from backend import Backend, read_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work", type=Path, required=True)
    args = parser.parse_args()
    backend = Backend(args.work, load=True)
    backend.inspect_isolation()
    backend.check_server_identity(backend.api("GET", "System/Info/Public"))
    credentials = read_json(args.work / "admin-credentials.json")
    authentication = backend.api("POST", "Users/AuthenticateByName", role="resume-inspection",
        body={"Username": credentials["Username"], "Pw": credentials["Password"]})
    token = authentication["AccessToken"]
    try:
        config = backend.api("GET", "System/Configuration", token=token, role="resume-inspection")
        movie_id = backend.summary["ResumeMovieId"]
        user_id = backend.summary["PlaybackUserId"]
        detail = backend.api("GET", f"Users/{user_id}/Items/{movie_id}", token=token, role="resume-inspection")
        state = detail["UserData"]
        resume = backend.api("GET", f"Users/{user_id}/Items/Resume?Limit=20&MediaTypes=Video", token=token, role="resume-inspection")
        print(json.dumps({"Configuration": {key: value for key, value in config.items() if "Resume" in key},
            "SyntheticMovieRuntimeTicks": detail.get("RunTimeTicks"),
            "SyntheticUserData": {key: state.get(key) for key in ("PlaybackPositionTicks", "Played", "PlayCount")},
            "ResumeItemCount": len(resume["Items"]), "SeededMoviePresent": movie_id in [item["Id"] for item in resume["Items"]]}))
    finally:
        backend.api("POST", "Sessions/Logout", token=token, role="resume-inspection")


if __name__ == "__main__":
    main()
