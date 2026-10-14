"""Validate same-build cloud captures without modifying any source image.

Run after all Player performance/capture processes have exited. Example:
  python Tools/validate_cloud_evidence.py --candidate <capture-directory> \
    --reference <reference-directory> --output <report.json>
Add --published Docs/CloudRedesignEvidence after packaging to verify both sets
of published static RGB pixels and capture manifests against their sources.
"""

import argparse
import hashlib
import json
import math
from pathlib import Path


# Only rendering quality choices may differ; density, weather, wind and colors
# must match. Runtime instance IDs identify process-local Unity objects, not
# asset identity; the identical build GUID/manifest is required separately.
QUALITY_ROOTS = {
    "resolutionScale", "useDepthAwareUpsampling", "useShapeCellCache",
    "useShapeFieldCache", "useNormalFieldCache", "normalFieldCellSize",
    "normalFieldExactNearDistance", "normalFieldMinimumCoherence",
    "useNormalFieldDiagnostics", "useShapeFieldDensity",
    "useOceanShapeFieldCache", "shapeFieldExactNearDistance",
    "useMacroTraversal", "useAdaptiveIntegration", "useFarSurface",
    "useFarSurfaceDiagnostics", "farSurfaceNearDistance",
    "useTemporalTileCompaction", "useCacheDiagnostics",
    "useTemporalDiagnostics", "useSupportRejection", "useCompactTargets",
    "useRayIntervals", "useShadowTermination", "usePaletteLightingFastPath",
    "useTemporalReprojection", "distanceQuality", "temporalUpdatePhases",
    "useSpatialLightCache", "spatialLightCellSize",
}

CAPTURE_FIELDS = (
    "path", "label", "width", "height", "simulationSeconds", "hour",
    "position", "rotation",
)


def sha256(path):
    """Hash an input file while keeping it open for reading only."""
    digest = hashlib.sha256()
    with path.open("rb") as source:
        while True:
            block = source.read(1024 * 1024)
            if not block:
                break
            digest.update(block)
    return digest.hexdigest()


def load_manifest(directory):
    """Read the completed capture manifest and retain its provenance."""
    path = directory / "capture-manifest.json"
    return json.loads(path.read_text(encoding="utf-8-sig")), {
        "directory": str(directory),
        "manifestSha256": sha256(path),
    }


def flatten(value, prefix=""):
    """Flatten nested settings while preserving missing-versus-present keys."""
    result = {}
    if isinstance(value, dict):
        for key, child in value.items():
            name = key
            if prefix:
                name = prefix + "." + key
            result.update(flatten(child, name))
    else:
        result[prefix] = value
    return result


def finite_tree(value):
    """Reject NaN/Infinity in capture transforms and simulation parameters."""
    if isinstance(value, dict):
        return all(finite_tree(child) for child in value.values())
    if isinstance(value, (int, float)):
        return math.isfinite(value)
    return True


def inspect_manifest(manifest, directory, name, errors):
    """Require the five fixed images and every one of 216 ordered flight frames."""
    captures = manifest.get("captures", [])
    if len(captures) != 221:
        errors.append(f"{name}: expected 221 captures, got {len(captures)}")
        return []
    night = captures[2].get("label")
    if night not in ("Night", "Twilight"):
        errors.append(f"{name}: third image must be Night or Twilight")
    labels = ["Day", "Sunset", night, "Inside", "Far"]
    paths = [label + ".png" for label in labels]
    paths.extend(f"Flight/frame-{index:04d}.png" for index in range(216))
    if [capture.get("path") for capture in captures] != paths:
        errors.append(f"{name}: capture ordering, unique paths or frame coverage differs")
    if manifest.get("flightFramesPerSecond") != 12:
        errors.append(f"{name}: expected 12 fixed simulation frames per second")
    for index, capture in enumerate(captures):
        if not all(field in capture for field in CAPTURE_FIELDS):
            errors.append(f"{name}: missing metadata at index {index}")
            continue
        if not finite_tree(capture):
            errors.append(f"{name}: non-finite capture metadata at index {index}")
        for field, components in [("position", {"x", "y", "z"}),
                                  ("rotation", {"x", "y", "z", "w"})]:
            vector = capture[field]
            if not isinstance(vector, dict) or set(vector) != components:
                errors.append(f"{name}: invalid {field} components at index {index}")
        if (capture["width"], capture["height"]) != (1920, 1080):
            errors.append(f"{name}: non-FHD metadata at index {index}")
        expected_label = "flight"
        expected_seconds = (index - 5) / 12.0
        if index < 5:
            expected_label = labels[index]
            expected_seconds = 0.0
        if capture["label"] != expected_label:
            errors.append(f"{name}: incorrect label at index {index}")
        # Unity stores these timestamps as float32; 1 microsecond covers their
        # rounding through 17.916667 seconds. Cross-source equality stays exact.
        if abs(capture["simulationSeconds"] - expected_seconds) > 0.000001:
            errors.append(f"{name}: unexpected fixed simulation time at index {index}")
        path = (directory / capture["path"]).resolve()
        if not path.is_relative_to(directory) or not path.is_file():
            errors.append(f"{name}: missing or unsafe image path at index {index}")
    return captures


def compare_settings(candidate, reference):
    """List allowed quality changes and reject other configuration differences."""
    left = flatten(json.loads(candidate["settings"]))
    right = flatten(json.loads(reference["settings"]))
    quality = []
    identities = []
    unexpected = []
    for key in sorted(left.keys() | right.keys()):
        if key in left and key in right and left[key] == right[key]:
            continue
        change = {"field": key, "candidatePresent": key in left,
                  "referencePresent": key in right,
                  "candidate": left.get(key), "reference": right.get(key)}
        if key.endswith(".instanceID") and key in left and key in right:
            identities.append(change)
        elif key.split(".", 1)[0] in QUALITY_ROOTS:
            quality.append(change)
        else:
            unexpected.append(change)
    return {"qualityDifferences": quality, "runtimeObjectIdDifferences": identities,
            "unexpectedDifferences": unexpected,
            "sameExceptQualityAndRuntimeObjectIds": not unexpected}


def inspect_images(directory, captures, name, errors):
    """Decode each raw PNG to verify FHD size and actual alpha=255 in every pixel."""
    from PIL import Image

    records = []
    for capture in captures:
        path = directory / capture["path"]
        with Image.open(path) as image:
            is_fhd = image.size == (1920, 1080)
            has_alpha = "A" in image.getbands()
            alpha_range = None
            if has_alpha:
                alpha_range = list(image.getchannel("A").getextrema())
            alpha_255 = alpha_range == [255, 255]
            if image.format != "PNG" or not is_fhd or not alpha_255:
                errors.append(f"{name}: PNG/FHD/explicit alpha=255 failed: {capture['path']}")
            records.append({"path": capture["path"], "sha256": sha256(path),
                            "size": list(image.size), "mode": image.mode,
                            "explicitAlpha255": alpha_255, "alphaRange": alpha_range})
    return records


def read_rgb(path):
    """Decode RGB bytes into memory without rewriting the image or its alpha."""
    import numpy as np
    from PIL import Image

    with Image.open(path) as image:
        return np.asarray(image.convert("RGB"), dtype=np.uint8).copy()


def image_metrics(candidate, reference):
    """Measure normalized RGB channel errors and nearest-rank pixel-maximum P95."""
    import numpy as np

    difference = np.abs(candidate.astype(np.int16) - reference.astype(np.int16))
    pixel_maximum = difference.max(axis=2).ravel()
    rank = math.ceil(0.95 * len(pixel_maximum)) - 1
    p95 = np.partition(pixel_maximum, rank)[rank]
    return {
        "meanRgbAbsolute": float(difference.mean(dtype=np.float64) / 255.0),
        "meanPixelMaximum": float(pixel_maximum.mean(dtype=np.float64) / 255.0),
        "maximumChannelDifference": int(difference.max()) / 255.0,
        "p95PixelMaximum": int(p95) / 255.0,
        "p95Method": "nearest-rank: ceil(0.95 * pixels)-1, no interpolation",
    }


def inspect_published(published, candidate, reference, captures, errors):
    """Check both published manifests and five static RGB exports byte-for-byte."""
    import numpy as np

    result = {"directory": str(published), "candidateManifestExact": False,
              "referenceManifestExact": False, "images": []}
    for label, source, destination in [
        ("candidate", candidate, published),
        ("reference", reference, published / "Reference"),
    ]:
        source_manifest = source / "capture-manifest.json"
        target_manifest = destination / "capture-manifest.json"
        matched = target_manifest.is_file() and sha256(source_manifest) == sha256(target_manifest)
        result[label + "ManifestExact"] = matched
        if not matched:
            errors.append(f"published {label}: manifest does not match capture source")
        for capture in captures:
            path = destination / capture["path"]
            matched = False
            if path.is_file():
                matched = bool(np.array_equal(read_rgb(source / capture["path"]), read_rgb(path)))
            result["images"].append({"source": label, "path": capture["path"],
                                     "rgbExportExact": matched})
            if not matched:
                errors.append(f"published {label}: missing/different RGB export: {capture['path']}")
    result["allRgbExportsExact"] = all(item["rgbExportExact"] for item in result["images"])
    return result


def validate(candidate_directory, reference_directory, published_directory):
    """Validate metadata first, then image contents, and return a serializable report."""
    errors = []
    candidate, candidate_source = load_manifest(candidate_directory)
    reference, reference_source = load_manifest(reference_directory)
    same_build = bool(candidate.get("buildGuid")) and candidate.get("buildGuid") == reference.get("buildGuid")
    same_build_manifest = bool(candidate.get("buildManifest")) and candidate.get("buildManifest") == reference.get("buildManifest")
    report = {
        "schema": "CloudFhdEvidenceValidation-v2", "sameBuild": same_build,
        "buildGuid": candidate.get("buildGuid"), "sameBuildManifest": same_build_manifest,
        "notes": "Read-only validation of tone-mapped 8-bit FHD screenshots, not linear HDR or geometric accuracy. RGB differences are measurements without an automatic visual quality acceptance threshold. Run outside Player timings. Runtime Unity object IDs may differ between processes; asset identity is constrained by the identical build GUID and embedded build manifest. Configuration equality covers recorded manifest settings and each capture pose/hour/simulation timestamp. Per-frame weather/environment internals are not serialized by this manifest, so this script cannot independently prove their equality.",
        "candidateSource": candidate_source, "referenceSource": reference_source,
        "images": [], "errors": errors,
    }
    if not same_build or not same_build_manifest:
        errors.append("Candidate/reference build GUID or embedded build manifest differs")
    candidate_captures = inspect_manifest(candidate, candidate_directory, "candidate", errors)
    reference_captures = inspect_manifest(reference, reference_directory, "reference", errors)
    mismatches = []
    for index, (left, right) in enumerate(zip(candidate_captures, reference_captures)):
        fields = [field for field in CAPTURE_FIELDS if left.get(field) != right.get(field)]
        if fields:
            mismatches.append({"index": index, "path": left.get("path"), "fields": fields})
    report["captureCounts"] = {"candidate": len(candidate.get("captures", [])),
                               "reference": len(reference.get("captures", []))}
    report["matchingAllCaptureMetadata"] = not mismatches and len(candidate_captures) == len(reference_captures) == 221
    report["captureMetadataMismatches"] = mismatches
    if not report["matchingAllCaptureMetadata"]:
        errors.append("All 221 capture poses/hours/simulation times/dimensions must match exactly")
    report["settingsComparison"] = compare_settings(candidate, reference)
    if not report["settingsComparison"]["sameExceptQualityAndRuntimeObjectIds"]:
        errors.append("Non-quality configuration differences found")
    report["metadataPassed"] = not errors
    if errors:
        report["passed"] = False
        return report

    report["candidateImageChecks"] = inspect_images(candidate_directory, candidate_captures, "candidate", errors)
    report["referenceImageChecks"] = inspect_images(reference_directory, reference_captures, "reference", errors)
    report["allRawImagesFhdAndAlpha255"] = all(
        row["size"] == [1920, 1080] and row["explicitAlpha255"]
        for row in report["candidateImageChecks"] + report["referenceImageChecks"])
    if errors:
        report["passed"] = False
        return report
    for capture in candidate_captures[:5]:
        left = read_rgb(candidate_directory / capture["path"])
        right = read_rgb(reference_directory / capture["path"])
        report["images"].append({"label": capture["label"], "path": capture["path"],
                                 "matchingPose": True, "width": 1920, "height": 1080,
                                 "simulationSeconds": capture["simulationSeconds"],
                                 "hour": capture["hour"], **image_metrics(left, right),
                                 "rgbExportExact": None})
    report["publishedChecked"] = published_directory is not None
    if published_directory is not None:
        report["published"] = inspect_published(published_directory, candidate_directory,
                                                 reference_directory, candidate_captures[:5], errors)
        for row in report["images"]:
            matching = [item for item in report["published"]["images"] if item["path"] == row["path"]]
            row["rgbExportExact"] = all(item["rgbExportExact"] for item in matching)
    report["passed"] = not errors
    return report


def main():
    """Parse explicit source/output paths and return failure for any evidence mismatch."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", required=True, type=Path)
    parser.add_argument("--reference", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--published", type=Path,
                        help="Optional packaged directory containing static PNGs and Reference/")
    arguments = parser.parse_args()
    candidate = arguments.candidate.resolve()
    reference = arguments.reference.resolve()
    output = arguments.output.resolve()
    published = None
    if arguments.published is not None:
        published = arguments.published.resolve()
    if output.suffix.lower() != ".json" or output.is_relative_to(candidate) or output.is_relative_to(reference):
        parser.error("Output must be a .json outside both read-only capture directories")
    try:
        report = validate(candidate, reference, published)
    except (OSError, ValueError, KeyError, TypeError) as exception:
        report = {"schema": "CloudFhdEvidenceValidation-v2", "passed": False,
                  "errors": [f"{type(exception).__name__}: {exception}"]}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"passed": report["passed"], "output": str(output),
                      "errors": report["errors"]}, ensure_ascii=False))
    if not report["passed"]:
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
