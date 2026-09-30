"""Manage one private official Emby acceptance backend with synthetic media only."""

from __future__ import annotations

import argparse
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import signal
import socket
import subprocess
import sys
import time
import urllib.parse
import xml.etree.ElementTree as ET


HELPER_DIRECTORY = Path(__file__).resolve().parent
CI_DIRECTORY = HELPER_DIRECTORY.parent / "EmbyClient.ServerValidation" / "Ci"
if CI_DIRECTORY.is_dir():
    sys.path.insert(0, str(CI_DIRECTORY))

from protocol_ci import Failure, IMAGE, MEDIA_PATH, OWNER_LABEL, Runner, SERVER_URL, SERVER_VERSION
from protocol_ci import read_json, sha256, write_json
from loopback_relay import LoopbackRelay


MOVIES = (
    ("Northern Lights", "Science Fiction", "A small observatory follows a signal across the night sky.", "#245a67"),
    ("The Quiet Signal", "Science Fiction", "Two engineers trace a gentle transmission beyond the horizon.", "#672e4a"),
    ("Open Water", "Adventure", "A research crew maps an unfamiliar coastline together.", "#1d6370"),
    ("Orbit Station", "Science Fiction", "An orbital team prepares its first shared expedition.", "#52578b"),
    ("After the Rain", "Drama", "Old friends meet to finish a long-delayed community project.", "#407064"),
    ("Paper Cities", "Drama", "An archivist discovers an unfinished story in a quiet city.", "#765244"),
    ("Wild Frequencies", "Documentary", "A fictional field team records the rhythms of a new landscape.", "#446446"),
    ("The Long Weekend", "Comedy", "A group of friends turns a simple trip into a small adventure.", "#744655"),
)


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def safe_work(path: Path) -> Path:
    work = path.absolute()
    if work.is_symlink() or work.resolve().parent != Path("/tmp") or not re.fullmatch(r"lumen-official-[0-9a-f]{32}", work.name):
        raise Failure("OwnedDirectoryRequired")
    return work


def process_start_ticks(pid: int) -> str:
    text = Path(f"/proc/{pid}/stat").read_text(encoding="ascii")
    return text[text.rfind(")") + 2:].split()[19]


def add_text(parent: ET.Element, name: str, value) -> None:
    ET.SubElement(parent, name).text = str(value)


def write_nfo(path: Path, kind: str, title: str, overview: str, genre: str, *, season=None, episode=None) -> None:
    root = ET.Element(kind)
    for name, value in {
        "title": title, "originaltitle": title, "plot": overview, "year": 2026,
        "mpaa": "TV-PG", "rating": "8.1", "runtime": "2", "studio": "Lumen Synthetic Studio",
        "director": "Alex Vale", "tagline": "An entirely synthetic acceptance fixture.",
    }.items():
        add_text(root, name, value)
    for value in (genre, "Drama"):
        add_text(root, "genre", value)
    for index, (name, role) in enumerate((("Maya Stone", "Researcher"), ("Noah Quinn", "Engineer"), ("Eli Hart", "Navigator"))):
        actor = ET.SubElement(root, "actor")
        for key, value in {"name": name, "role": role, "order": index}.items():
            add_text(actor, key, value)
    add_text(root, "tag", "LumenSyntheticAcceptance")
    if kind == "movie":
        add_text(root, "set", "Lumen Horizons")
    if season is not None:
        add_text(root, "season", season)
    if episode is not None:
        add_text(root, "episode", episode)
        add_text(root, "showtitle", "Lumen Field Notes")
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


class Backend(Runner):
    """Reuse the reviewed Docker isolation and loopback relay contract without CI assumptions."""

    def __init__(self, work: Path, *, load: bool = False, import_receipt: Path | None = None):
        if sys.platform != "linux":
            raise Failure("RemoteLinuxEnvironmentRequired")
        self.work = safe_work(work)
        self.temporary = Path("/tmp")
        self.repository = self.work
        self.run_id = "20260930"
        self.attempt = "1"
        self.state_path = self.work / "owned-resources.json"
        self.summary_path = self.work / "summary.json"
        self.environment = os.environ.copy()
        for name in ("DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH"):
            self.environment.pop(name, None)
        import urllib.request
        from protocol_ci import NoRedirect
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        self.deadline = time.monotonic() + 20 * 60
        self.relay = None
        self.relay_target_ipv4 = None
        self.state = read_json(self.state_path, 8192) if load else None
        self.summary = read_json(self.summary_path) if load else {
            "SchemaVersion": 1, "Scope": "OwnedOfficialEmbySyntheticAcceptance", "Outcome": "Running",
            "OfficialServer": True, "SyntheticDataOnly": True, "ServerVersion": SERVER_VERSION,
            "OfficialImage": IMAGE, "CreatedUtc": utc_now(), "CleanupCompleted": False,
            "NativeUi": "NotRun", "NativeDecoderAndFirstFrame": "NotRun",
        }
        self.quit_requested = False
        self.import_receipt = import_receipt

    def prepare(self) -> None:
        if self.work.exists():
            raise Failure("PrivateDirectoryAlreadyExists")
        self.work.mkdir(mode=0o700)
        owner = self.work.name.removeprefix("lumen-official-")
        self.state = {
            "Owner": owner, "WorkflowRunId": self.run_id, "WorkflowRunAttempt": self.attempt,
            "ContainerName": "emby-protocol-" + owner, "NetworkName": "emby-protocol-net-" + owner,
            "ServerName": "Lumen Official Acceptance " + owner,
        }
        self.save_state()
        self.stage("Prerequisites")
        for tool in ("docker", "ffmpeg", "ffprobe", "apt-get", "dpkg-deb"):
            if shutil.which(tool) is None:
                raise Failure("RequiredRemoteToolMissing")
        engine = self.docker_json("version", "--format", "{{json .Server}}")
        version = re.fullmatch(r"(\d+)\.(\d+)\.(\d+)(?:[-+][0-9A-Za-z.-]+)?", engine.get("Version", ""))
        if engine.get("Os") != "linux" or version is None or tuple(map(int, version.groups())) < (28, 0, 0):
            raise Failure("UnsupportedDockerEngine")
        self.summary["DockerEngineVersion"] = engine["Version"]
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as reservation:
            try:
                reservation.bind(("127.0.0.1", 19096))
            except OSError:
                raise Failure("LoopbackPortUnavailable") from None
        self.stage("OfficialImagePull")
        cached = self.docker("image", "inspect", IMAGE, accept_failure=True)
        imported = self.import_receipt is not None
        if imported:
            path = self.import_receipt.absolute()
            if (path.is_symlink() or path.name != "import-receipt.json" or path.parent.resolve().parent != Path("/tmp")
                    or not re.fullmatch(r"lumen-official-image-[0-9a-f]{32}", path.parent.name)):
                raise Failure("OwnedImageImportReceiptRequired")
            proof = read_json(path)
            expected_config = "sha256:fa0dadda51e7ef3834d49a80a5708992fe4cdd5561c3ed2e2a4e87c01a447e6b"
            if (proof.get("SourceRegistry") != "registry-1.docker.io" or proof.get("OfficialPublisher") != "emby/embyserver"
                    or proof.get("PinnedIndexDigest") != IMAGE.split("@", 1)[1] or proof.get("ConfigDigest") != expected_config
                    or proof.get("Outcome") != "Imported" or proof.get("TlsCertificateVerification") is not True
                    or proof.get("SystemDnsOrDockerConfigurationChanged") is not False
                    or proof.get("UncompressedLayerDigestsVerified") is not True or proof.get("VerifiedTag") != "emby/embyserver:4.9.5.0"):
                raise Failure("OfficialImageImportProofMismatch")
            image = self.docker_json("image", "inspect", proof["VerifiedTag"])[0]
            if image.get("Id") != expected_config:
                raise Failure("ImportedOfficialImageConfigMismatch")
            self.state["ResolvedImageReference"] = expected_config
            self.summary["OfficialImageImport"] = proof
        elif cached.returncode != 0:
            self.docker("pull", "--platform", "linux/amd64", IMAGE, timeout=240)
        if not imported:
            image = self.docker_json("image", "inspect", IMAGE)[0]
        if image.get("Architecture") != "amd64" or image.get("Os") != "linux" or (not imported and IMAGE not in image.get("RepoDigests", [])):
            raise Failure("OfficialImageIdentityMismatch")
        self.state["ImageId"] = image["Id"]
        self.summary["ImageId"] = image["Id"]
        self.save_state()
        self.create_fixture()
        self.create_server()

    def create_server(self) -> None:
        import protocol_ci
        original = protocol_ci.IMAGE
        try:
            # Classic Docker imports retain tags rather than registry digests; bind creation to
            # the original pinned configuration digest after verifying the complete official chain.
            protocol_ci.IMAGE = self.state.get("ResolvedImageReference", original)
            super().create_server()
        finally:
            protocol_ci.IMAGE = original

    def create_speech(self) -> tuple[Path, Path]:
        self.stage("SyntheticBilingualSpeech")
        packages = ("espeak-ng", "espeak-ng-data", "libespeak-ng1", "libpcaudio0", "libsonic0")
        self.command(["apt-get", "download", *packages], 180, "SpeechPackageDownloadFailed")
        extracted = self.work / "speech"
        extracted.mkdir(mode=0o700)
        hashes = {}
        for package in packages:
            matches = list(self.work.glob(package + "_*.deb"))
            if len(matches) != 1:
                raise Failure("SpeechPackageIdentityMismatch")
            hashes[package] = sha256(matches[0])
            self.command(["dpkg-deb", "--extract", str(matches[0]), str(extracted)], 60, "SpeechPackageExtractionFailed")
        library_path = extracted / "usr/lib/x86_64-linux-gnu"
        self.environment["LD_LIBRARY_PATH"] = str(library_path)
        sentences = {
            "eng": ("en-us", "This is the English audio track for the Lumen synthetic test film. All characters and images are fictional. "),
            "fra": ("fr-fr", "Bonjour. Ceci est la piste audio francaise du film de test Lumen. Les personnages et les images sont fictifs. "),
        }
        paths = []
        for language, (voice, sentence) in sentences.items():
            text_path = self.work / f"speech-{language}.txt"
            text_path.write_text(sentence * 14, encoding="ascii")
            wave = self.work / f"speech-{language}.wav"
            self.command([str(extracted / "usr/bin/espeak-ng"), "--path=" + str(library_path), "-v", voice,
                "-s", "145", "-f", str(text_path), "-w", str(wave)], 60, "SyntheticSpeechFailed")
            paths.append(wave)
        self.summary["SpeechPackages"] = hashes
        self.summary["SpeechPackageInstallation"] = "ExtractedUnderOwnedDirectoryOnly"
        return paths[0], paths[1]

    def create_fixture(self) -> None:
        english, french = self.create_speech()
        self.stage("SyntheticFixture")
        media = self.work / "media"
        media.mkdir(mode=0o700)
        movies = media / "movies"
        series = media / "series"
        movies.mkdir(mode=0o700)
        series.mkdir(mode=0o700)
        master = self.work / "master.mp4"
        self.command(["ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-n",
            "-f", "lavfi", "-i", "testsrc2=size=960x540:rate=24", "-stream_loop", "-1", "-i", str(english),
            "-stream_loop", "-1", "-i", str(french), "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0", "-t", "120",
            "-c:v", "libx264", "-preset", "ultrafast", "-profile:v", "baseline", "-level:v", "3.1",
            "-pix_fmt", "yuv420p", "-threads", "2", "-g", "72", "-keyint_min", "72", "-sc_threshold", "0",
            "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-ar", "48000", "-disposition:a:0", "default",
            "-disposition:a:1", "0", "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=fra",
            "-metadata:s:a:0", "title=English", "-metadata:s:a:1", "title=French",
            "-movflags", "+faststart", str(master)], 180, "FixtureGenerationFailed")
        metadata = json.loads(self.command(["ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", str(master)],
            30, "FixtureInspectionFailed").stdout)
        streams = metadata.get("streams", [])
        video = [item for item in streams if item.get("codec_type") == "video"]
        audio = [item for item in streams if item.get("codec_type") == "audio"]
        if (len(video) != 1 or len(audio) != 2 or video[0].get("codec_name") != "h264"
                or any(item.get("codec_name") != "aac" or item.get("channels") != 2 for item in audio)
                or [item.get("tags", {}).get("language") for item in audio] != ["eng", "fra"]
                or not 119 <= float(metadata.get("format", {}).get("duration", "0")) <= 121):
            raise Failure("FixtureFormatMismatch")
        subtitle_texts = {
            "eng": "1\n00:00:02,000 --> 00:00:10,000\nLumen synthetic acceptance film.\n\n2\n00:00:20,000 --> 00:00:32,000\nThe English audio track is available.\n\n3\n00:01:00,000 --> 00:01:12,000\nAll characters and locations are fictional.\n",
            "fra": "1\n00:00:02,000 --> 00:00:10,000\nFilm de test Lumen.\n\n2\n00:00:20,000 --> 00:00:32,000\nLa piste audio francaise est disponible.\n\n3\n00:01:00,000 --> 00:01:12,000\nTous les personnages sont fictifs.\n",
        }
        for title, genre, overview, color in MOVIES:
            folder = movies / f"{title} (2026)"
            folder.mkdir(mode=0o700)
            movie = folder / f"{title} (2026).mp4"
            os.link(master, movie)
            for language, text in subtitle_texts.items():
                movie.with_suffix(f".{language}.srt").write_text(text, encoding="ascii")
            write_nfo(folder / "movie.nfo", "movie", title, overview, genre)
            self.create_images(folder, title, color)
        trailer_folder = movies / "Northern Lights (2026)" / "trailers"
        trailer_folder.mkdir(mode=0o700)
        self.command(["ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-i", str(master),
            "-t", "20", "-map", "0", "-c", "copy", "-movflags", "+faststart", str(trailer_folder / "Lumen Preview.mp4")],
            60, "TrailerGenerationFailed")
        show = series / "Lumen Field Notes (2026)"
        show.mkdir(mode=0o700)
        write_nfo(show / "tvshow.nfo", "tvshow", "Lumen Field Notes", "A fictional field team studies unfamiliar landscapes.", "Science Fiction")
        self.create_images(show, "Lumen Field Notes", "#324862")
        for season in (1, 2):
            folder = show / f"Season {season:02d}"
            folder.mkdir(mode=0o700)
            self.create_images(folder, f"Field Notes - Season {season}", "#475165")
            for episode in (1, 2):
                number = f"S{season:02d}E{episode:02d}"
                movie = folder / f"Lumen Field Notes {number}.mp4"
                os.link(master, movie)
                for language, text in subtitle_texts.items():
                    movie.with_suffix(f".{language}.srt").write_text(text, encoding="ascii")
                write_nfo(movie.with_suffix(".nfo"), "episodedetails", f"Field Report {season}.{episode}",
                    "The fictional team completes another observation.", "Science Fiction", season=season, episode=episode)
        self.summary["Fixture"] = {"Synthetic": True, "Video": "H264BaselineYuv420p", "Audio": "AacStereo",
            "AudioLanguages": ["eng", "fra"], "SpokenLanguages": ["English", "French"], "AudioStreamCount": 2,
            "SubtitleLanguages": ["eng", "fra"], "Subtitle": "GeneratedExternalSrt", "Width": 960, "Height": 540,
            "DurationSeconds": 120, "MovieCount": len(MOVIES), "SeriesCount": 1, "SeasonCount": 2,
            "EpisodeCount": 4, "LocalTrailerCount": 1, "MasterSha256": sha256(master)}
        self.save_summary()

    def create_images(self, folder: Path, title: str, color: str) -> None:
        font = Path("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf")
        if not font.is_file():
            raise Failure("SyntheticImageFontUnavailable")
        for name, size, text_size in (("poster.jpg", "640x960", 36), ("backdrop.jpg", "1280x720", 54)):
            safe_title = title.replace("'", "")
            filters = f"drawbox=x=0:y=ih*0.62:w=iw:h=ih*0.38:color=black@0.65:t=fill,drawtext=fontfile={font}:text='{safe_title}':fontsize={text_size}:fontcolor=white:x=40:y=h*0.72,drawtext=fontfile={font}:text='LUMEN TEST FILM':fontsize=22:fontcolor=white@0.7:x=40:y=h*0.87"
            self.command(["ffmpeg", "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "lavfi", "-i",
                f"color=c={color}:s={size}", "-vf", filters, "-frames:v", "1", "-threads", "1", str(folder / name)],
                30, "SyntheticImageGenerationFailed")

    def initialize(self) -> None:
        self.stage("FreshServerInitialization")
        admin_password, user_password = secrets.token_urlsafe(32), secrets.token_urlsafe(32)
        self.api("POST", "Startup/Configuration", body={"UICulture": "en-US"})
        self.api("POST", "Startup/User", body={"Name": "lumen-owned-admin", "Password": admin_password})
        self.api("POST", "Startup/RemoteAccess", body={"EnableRemoteAccess": False, "EnableAutomaticPortMapping": False})
        self.api("POST", "Startup/Complete")
        authentication = self.api("POST", "Users/AuthenticateByName", body={"Username": "lumen-owned-admin", "Pw": admin_password})
        admin_token = authentication["AccessToken"]
        admin_id = authentication["User"]["Id"]
        public = self.api("GET", "System/Info/Public")
        server_id = public["Id"]
        self.summary["ServerId"] = server_id
        try:
            configuration = self.api("GET", "System/Configuration", token=admin_token)
            configuration.update({"EnableUPnP": False, "EnableRemoteAccess": False, "EnableAutoUpdate": False})
            self.api("POST", "System/Configuration", body=configuration, token=admin_token)
            user = self.api("POST", "Users/New", body={"Name": "lumen-test"}, token=admin_token)
            user_id = user["Id"]
            self.api("POST", f"Users/{user_id}/Password", body={"Id": user_id, "NewPw": user_password, "ResetPassword": False}, token=admin_token)
            for name, content_type, path, types in (
                    ("Lumen Movies", "movies", MEDIA_PATH + "/movies", ["Movie"]),
                    ("Lumen Series", "tvshows", MEDIA_PATH + "/series", ["Series", "Season", "Episode"])):
                options = {"PathInfos": [{"Path": path}], "ContentType": content_type, "EnableRealtimeMonitor": False,
                    "EnableChapterImageExtraction": False, "ExtractChapterImagesDuringLibraryScan": False,
                    "EnableMarkerDetectionDuringLibraryScan": False, "EnableInternetProviders": False,
                    "DownloadImagesInAdvance": False, "SaveLocalMetadata": False, "MetadataSavers": [],
                    "SubtitleDownloadLanguages": [], "TypeOptions": [{"Type": kind, "MetadataFetchers": ["Nfo"],
                        "MetadataFetcherOrder": ["Nfo"], "ImageFetchers": [], "ImageFetcherOrder": []} for kind in types]}
                route = "Library/VirtualFolders?" + urllib.parse.urlencode({"name": name, "collectionType": content_type, "refreshLibrary": "true"})
                self.api("POST", route, body={"LibraryOptions": options}, token=admin_token)
            self.api("POST", "Library/Refresh", token=admin_token)
            self.stage("SyntheticLibraryScan")
            expires = time.monotonic() + 240
            items = []
            while time.monotonic() < expires:
                items = self.api("GET", f"Users/{user_id}/Items?Recursive=true&Limit=100&Fields=Overview,Genres,People,MediaSources,MediaStreams&EnableUserData=true",
                    token=admin_token).get("Items", [])
                if (sum(item.get("Type") == "Movie" for item in items) == len(MOVIES)
                        and sum(item.get("Type") == "Series" for item in items) == 1
                        and sum(item.get("Type") == "Season" for item in items) == 2
                        and sum(item.get("Type") == "Episode" for item in items) == 4
                        and all(item.get("Genres") for item in items if item.get("Type") == "Movie")):
                    break
                time.sleep(2)
            else:
                raise Failure("SyntheticLibraryScanDeadline")
            movie_items = [item for item in items if item.get("Type") == "Movie"]
            collection = self.api("POST", "Collections?" + urllib.parse.urlencode({"Name": "Lumen Horizons",
                "Ids": ",".join(item["Id"] for item in movie_items[:4]), "IsLocked": "false"}), token=admin_token)
            self.summary["CollectionId"] = collection["Id"]
            self.summary["PlaybackUserId"] = user_id
            self.summary["AdminUserId"] = admin_id
            catalog = {"SchemaVersion": 1, "SyntheticDataOnly": True, "ServerId": server_id, "CollectionId": collection["Id"],
                "Items": [{key: item[key] for key in ("Id", "Name", "Type", "Genres", "SeriesId", "SeasonId", "ParentIndexNumber", "IndexNumber")
                    if key in item} for item in items]}
            write_json(self.work / "catalog.json", catalog)
            write_json(self.work / "admin-credentials.json", {"ServerUrl": SERVER_URL, "Username": "lumen-owned-admin",
                "Password": admin_password, "ServerId": server_id, "ExpectedServerVersion": SERVER_VERSION})
            write_json(self.work / "credentials.json", {"ServerUrl": SERVER_URL, "Username": "lumen-test", "Password": user_password,
                "ServerId": server_id, "ExpectedServerVersion": SERVER_VERSION})
        finally:
            self.api("POST", "Sessions/Logout", token=admin_token)
        authentication = self.api("POST", "Users/AuthenticateByName", body={"Username": "lumen-test", "Pw": user_password}, role="fixture-seed")
        token = authentication["AccessToken"]
        try:
            user = self.api("GET", f"Users/{user_id}", token=token, role="fixture-seed")
            if user.get("Policy", {}).get("IsAdministrator") is not False or user.get("Policy", {}).get("IsDisabled") is not False:
                raise Failure("NonAdminPlaybackUserRequired")
            northern = next(item for item in movie_items if item.get("Name") == "Northern Lights")
            quiet = next(item for item in movie_items if item.get("Name") == "The Quiet Signal")
            open_water = next(item for item in movie_items if item.get("Name") == "Open Water")
            self.api("POST", f"Users/{user_id}/FavoriteItems/{northern['Id']}", token=token, role="fixture-seed")
            self.api("POST", f"Users/{user_id}/PlayedItems/{quiet['Id']}", token=token, role="fixture-seed")
            self.seed_resume(user_id, open_water["Id"], token)
            first_episode = next(item for item in items if item.get("Type") == "Episode" and item.get("ParentIndexNumber") == 1 and item.get("IndexNumber") == 1)
            self.api("POST", f"Users/{user_id}/PlayedItems/{first_episode['Id']}", token=token, role="fixture-seed")
            detail = self.api("GET", f"Users/{user_id}/Items/{northern['Id']}", token=token, role="fixture-seed")
            streams = detail.get("MediaStreams", []) or detail.get("MediaSources", [{}])[0].get("MediaStreams", [])
            audios = [item for item in streams if item.get("Type") == "Audio"]
            subtitles = [item for item in streams if item.get("Type") == "Subtitle" and item.get("IsExternal") is True]
            trailers = self.api("GET", f"Users/{user_id}/Items/{northern['Id']}/LocalTrailers", token=token, role="fixture-seed")
            similar = self.api("GET", f"Items/{northern['Id']}/Similar?UserId={user_id}&Limit=16", token=token, role="fixture-seed")
            if len(audios) != 2 or {item.get("Language") for item in audios} != {"eng", "fra"} or len(subtitles) < 2 or not trailers:
                raise Failure("OfficialFixtureStreamsOrTrailerMismatch")
            self.summary["LibraryReadiness"] = {"PlaybackUserIsAdministrator": False, "MovieCount": len(movie_items),
                "SeriesCount": 1, "SeasonCount": 2, "EpisodeCount": 4, "AudioStreamCount": len(audios),
                "ExternalSubtitleCount": len(subtitles), "LocalTrailerCount": len(trailers),
                "SimilarItemCount": len(similar.get("Items", [])), "GenreCount": len(detail.get("Genres", [])),
                "CastCount": len([person for person in detail.get("People", []) if person.get("Type") == "Actor"])}
            self.summary["SeedMovieId"] = northern["Id"]
            self.summary["ResumeMovieId"] = open_water["Id"]
        finally:
            self.api("POST", "Sessions/Logout", token=token, role="fixture-seed")
        self.save_summary()

    def seed_resume(self, user_id: str, item_id: str, token: str, *, role: str = "fixture-seed") -> None:
        info = self.api("POST", f"Items/{item_id}/PlaybackInfo", body={"UserId": user_id, "IsPlayback": False,
            "MaxStreamingBitrate": 50000000, "DeviceProfile": {"Name": "Lumen Synthetic Seed", "MaxStreamingBitrate": 50000000,
                "DirectPlayProfiles": [{"Container": "mp4,mkv", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac"}]}},
            token=token, role=role)
        common = {"ItemId": item_id, "MediaSourceId": info["MediaSources"][0]["Id"], "PlaySessionId": info["PlaySessionId"],
            "PositionTicks": 350000000, "PlayMethod": "DirectPlay", "CanSeek": True, "AudioStreamIndex": 1}
        self.api("POST", "Sessions/Playing", body={**common, "PositionTicks": 0}, token=token, role=role)
        self.api("POST", "Sessions/Playing/Progress", body=common, token=token, role=role)
        self.api("POST", "Sessions/Playing/Stopped", body=common, token=token, role=role)

    def receipt(self) -> dict:
        receipt = {key: self.summary.get(key) for key in ("SchemaVersion", "Scope", "OfficialServer", "SyntheticDataOnly",
            "ServerVersion", "OfficialImage", "ImageId", "ServerId", "ServerIdentitySha256", "CreatedUtc", "ExpiresUtc")}
        receipt.update({key: self.state[key] for key in ("Owner", "ContainerId", "ContainerName", "NetworkId", "NetworkName")})
        receipt.update({"OwnerLabel": OWNER_LABEL, "OwnerLabelValue": self.state["Owner"], "WorkDirectory": str(self.work),
            "ConfigDirectory": str(self.work / "config"), "MediaDirectory": str(self.work / "media"),
            "InternalBridge": True, "DockerPublishedPorts": False, "RemoteRelayLoopbackOnly": True,
            "RemoteServerUrl": SERVER_URL, "RemoteRelayPort": 19096, "RemoteRelayPid": os.getpid(),
            "RemoteRelayStartTicks": process_start_ticks(os.getpid()), "RelayHelperSource": "ExistingProtocolCiLoopbackRelay"})
        if "OfficialImageImport" in self.summary:
            receipt["OfficialImageImport"] = self.summary["OfficialImageImport"]
        return receipt

    def serve(self, lifetime_hours: int) -> None:
        self.deadline = time.monotonic() + lifetime_hours * 60 * 60
        self.summary["ExpiresUtc"] = (datetime.now(timezone.utc) + timedelta(hours=lifetime_hours)).isoformat()
        target = self.inspect_isolation()
        if target is None:
            raise Failure("ContainerEndpointUnavailable")
        self.relay_target_ipv4 = target
        self.relay = LoopbackRelay(target, 8096, listen_port=19096).start()
        self.inspect_isolation()
        self.check_server_identity(self.api("GET", "System/Info/Public"))
        self.summary["Outcome"] = "Ready"
        self.summary["LastStage"] = "RetainedForAcceptance"
        self.save_summary()
        write_json(self.work / "owned-backend.json", self.receipt())
        write_json(self.work / "ready.json", {"Ready": True, "RelayPid": os.getpid(), "RelayStartTicks": process_start_ticks(os.getpid())})
        signal.signal(signal.SIGTERM, lambda *_: setattr(self, "quit_requested", True))
        signal.signal(signal.SIGINT, lambda *_: setattr(self, "quit_requested", True))
        while time.monotonic() < self.deadline and not self.quit_requested and not (self.work / "stop-request.json").exists():
            if not self.relay.is_running:
                raise Failure("OwnedRelayStopped")
            time.sleep(1)
        if not self.cleanup():
            raise Failure("OwnedCleanupIncomplete")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("create", "serve", "status", "stop", "cleanup"))
    parser.add_argument("--work", type=Path)
    parser.add_argument("--import-receipt", type=Path)
    parser.add_argument("--lifetime-hours", type=int, default=8, choices=range(1, 25))
    args = parser.parse_args()
    work = args.work or Path("/tmp") / ("lumen-official-" + secrets.token_hex(16))
    backend = Backend(work, load=args.action != "create", import_receipt=args.import_receipt)
    try:
        if args.action == "create":
            backend.prepare()
            backend.initialize()
            backend.relay.close()
            backend.relay = None
            output = (backend.work / "relay-process.log").open("ab")
            os.chmod(backend.work / "relay-process.log", 0o600)
            process = subprocess.Popen([sys.executable, str(Path(__file__).resolve()), "serve", "--work", str(backend.work),
                "--lifetime-hours", str(args.lifetime_hours)], cwd=HELPER_DIRECTORY, stdin=subprocess.DEVNULL,
                stdout=output, stderr=output, start_new_session=True)
            output.close()
            expires = time.monotonic() + 30
            while time.monotonic() < expires:
                if process.poll() is not None:
                    raise Failure("RetainedRelayStartFailed")
                if (backend.work / "ready.json").exists():
                    print(json.dumps({"Outcome": "Ready", "WorkDirectory": str(backend.work), "ReceiptPath": str(backend.work / "owned-backend.json"),
                        "CredentialsPath": str(backend.work / "credentials.json"), "ServerVersion": SERVER_VERSION, "RemoteServerUrl": SERVER_URL}))
                    return 0
                time.sleep(0.2)
            raise Failure("RetainedRelayReadinessDeadline")
        if args.action == "serve":
            backend.serve(args.lifetime_hours)
        elif args.action == "status":
            backend.inspect_isolation()
            info = backend.api("GET", "System/Info/Public")
            backend.check_server_identity(info)
            print(json.dumps({"Outcome": "Ready", "ServerVersion": info["Version"], "WorkDirectory": str(backend.work),
                "ContainerRunning": True, "IdentityMatched": True}))
        elif args.action == "stop":
            ready = read_json(backend.work / "ready.json", 8192)
            pid = ready.get("RelayPid")
            if type(pid) is not int or pid < 2 or process_start_ticks(pid) != ready.get("RelayStartTicks"):
                raise Failure("RelayProcessIdentityMismatch")
            write_json(backend.work / "stop-request.json", {"Owner": backend.state["Owner"], "RequestedUtc": utc_now()})
            expires = time.monotonic() + 50
            while backend.work.exists() and time.monotonic() < expires:
                time.sleep(0.5)
            if backend.work.exists():
                raise Failure("OwnedCleanupDeadline")
            print(json.dumps({"Outcome": "Stopped", "CleanupCompleted": True, "WorkDirectory": str(backend.work)}))
        elif args.action == "cleanup":
            ready_path = backend.work / "ready.json"
            if ready_path.exists():
                ready = read_json(ready_path, 8192)
                pid = ready.get("RelayPid")
                if type(pid) is int and Path(f"/proc/{pid}/stat").exists() and process_start_ticks(pid) == ready.get("RelayStartTicks"):
                    raise Failure("UseStopForActiveOwnedRelay")
            if not backend.cleanup():
                raise Failure("OwnedCleanupIncomplete")
            print(json.dumps({"Outcome": "Cleaned", "CleanupCompleted": True, "WorkDirectory": str(backend.work)}))
        return 0
    except Failure as error:
        if args.action == "create" and backend.work.exists():
            backend.summary["Outcome"] = "Failed"
            backend.summary["FailureCode"] = error.code
            backend.save_summary()
        print(json.dumps({"Outcome": "Failed", "FailureCode": error.code, "WorkDirectory": str(backend.work)}))
        return 1
    except Exception as error:
        print(json.dumps({"Outcome": "Failed", "FailureCode": "UnexpectedBackendFailure", "ExceptionType": type(error).__name__,
            "WorkDirectory": str(backend.work)}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
