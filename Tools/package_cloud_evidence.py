"""Archive raw cloud measurements and preserve the selected Player evidence in Docs."""

import argparse
import hashlib
import json
import shutil
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

from PIL import Image


def package(root, capture_relative, reference_relative):
    """Keep raw timing streams separate from the visual capture and its preview."""
    source = root / "Benchmarks/Redesign"
    destination = root / "Docs/CloudRedesignEvidence"
    destination.mkdir(parents=True, exist_ok=True)
    capture = source / capture_relative
    manifest = json.loads((capture / "capture-manifest.json").read_text(encoding="utf-8"))
    if len(manifest["captures"]) != 221:
        raise ValueError("Expected five static images and 216 flight frames.")

    for name in ("Day.png", "Sunset.png", "Night.png", "Inside.png", "Far.png"):
        with Image.open(capture / name) as original:
            original.convert("RGB").save(destination / name)
    shutil.copy2(capture / "capture-manifest.json", destination / "capture-manifest.json")

    reference = source / reference_relative
    reference_destination = destination / "Reference"
    reference_destination.mkdir(parents=True, exist_ok=True)
    for name in ("Day.png", "Sunset.png", "Night.png", "Inside.png", "Far.png"):
        with Image.open(reference / name) as original:
            original.convert("RGB").save(reference_destination / name)
    shutil.copy2(reference / "capture-manifest.json", reference_destination / "capture-manifest.json")

    records = []
    archive = destination / "RawMeasurements.zip"
    with ZipFile(archive, "w", ZIP_DEFLATED, compresslevel=6) as output:
        for path in sorted(source.rglob("*")):
            if not path.is_file() or path.suffix not in (".json", ".csv", ".log"):
                continue
            if any(part.startswith("Evidence-") for part in path.parts):
                continue
            if "Excluded" in path.relative_to(source).parts:
                continue
            if path.relative_to(source).parts[0].startswith("ImplementationDrafts-"):
                continue
            if path.relative_to(source).parts[0].startswith("RestoredEditorGeneratedChanges-"):
                continue
            relative = path.relative_to(source).as_posix()
            data = path.read_bytes()
            output.writestr(relative, data)
            records.append({"path": relative, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})

    flight_archive = source / "Flight-FHD.zip"
    frames = sorted((capture / "Flight").glob("frame-*.png"))
    if len(frames) != 216:
        raise ValueError("Flight image sequence is incomplete.")
    with ZipFile(flight_archive, "w", ZIP_DEFLATED, compresslevel=1) as output:
        output.write(capture / "capture-manifest.json", "capture-manifest.json")
        for frame in frames:
            output.write(frame, "Flight/" + frame.name)

    preview_frames = []
    for frame in frames[::2]:
        with Image.open(frame) as original:
            preview_frames.append(original.convert("RGB").resize((640, 360)))
    durations = [170, 170, 160] * 36
    preview_frames[0].save(destination / "Flight-preview.gif", save_all=True,
                           append_images=preview_frames[1:], duration=durations,
                           loop=0, optimize=False)
    index = {
        "notes": "Raw timing archive excludes image captures and invalid runs under Excluded. FHD sequence contains 216 PNG frames at 12 fps, 18 seconds of fixed simulation time. Published static PNGs preserve every RGB byte while exporting as opaque RGB. Older captures had zero alpha; current SaveCapture writes alpha 255. GIF converts to RGB before resizing and is a 640x360, 6 fps preview only, not a gameplay FPS measurement.",
        "buildGuid": manifest["buildGuid"],
        "captureSource": capture_relative,
        "referenceSource": reference_relative,
        "rawFiles": records,
        "rawArchiveSha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
        "flightArchiveSha256": hashlib.sha256(flight_archive.read_bytes()).hexdigest(),
        "flightArchivePath": "Benchmarks/Redesign/Flight-FHD.zip",
        "captureManifestSha256": hashlib.sha256((destination / "capture-manifest.json").read_bytes()).hexdigest(),
    }
    (destination / "evidence-index.json").write_text(json.dumps(index, indent=2), encoding="utf-8")
    print(json.dumps({"rawFiles": len(records), "flightFrames": len(frames), "destination": str(destination)}, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--capture", default="ReleaseFixed/Evidence-20261002-194104")
    parser.add_argument("--reference", default="ReleaseReferenceEvidence/Evidence-20261005-120156")
    arguments = parser.parse_args()
    package(Path(__file__).resolve().parents[1], arguments.capture, arguments.reference)
