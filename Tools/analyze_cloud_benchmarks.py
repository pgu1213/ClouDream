"""Summarize raw cloud benchmark reports without treating missing GPU data as zero."""

import argparse
import csv
import hashlib
import json
import math
import statistics
from collections import defaultdict
from pathlib import Path


PRESETS = (
    "baseline", "reference-performance", "performance", "normal-cache", "surface-hybrid", "field-light", "field-density", "ocean-field",
    "occlusion", "traversal", "temporal", "tiles", "adaptive", "steady", "balanced", "final",
)


def describe(values, allow_zero=False):
    """Return pooled mean, ordinary median and nearest-rank p95 for valid values."""
    valid = []
    for value in values:
        if not isinstance(value, (int, float)) or not math.isfinite(value):
            continue
        if value > 0 or (allow_zero and value == 0):
            valid.append(float(value))
    valid.sort()
    if not valid:
        return {"samples": 0, "mean_ms": None, "median_ms": None, "p95_ms": None}
    return {
        "samples": len(valid),
        "mean_ms": statistics.fmean(valid),
        "median_ms": statistics.median(valid),
        "p95_ms": valid[math.ceil(len(valid) * 0.95) - 1],
    }


def preset_name(label):
    """Read the explicit preset suffix while retaining unknown legacy labels."""
    clean = label
    for suffix in ("-flight", "-diagnostic"):
        if clean.endswith(suffix):
            clean = clean[:-len(suffix)]
    for preset in sorted(PRESETS, key=len, reverse=True):
        if clean == preset or clean.endswith("-" + preset):
            return preset
    return clean


def pose_signature(report):
    """Hash the actual fixed-route poses so mismatched cameras cannot produce paired claims."""
    if report.get("routeMode", "fixed-pose") != "fixed-pose":
        return None
    rows = []
    for frame in report.get("frames", []):
        rows.append([frame.get("index"), frame.get("leg"), frame.get("densityPosition"), frame.get("rotation")])
    encoded = json.dumps(rows, sort_keys=True, separators=(",", ":")).encode()
    return hashlib.sha256(encoded).hexdigest()


def read_csv(path):
    """Recover wall intervals only when a matching JSON report is absent."""
    with path.open(encoding="utf-8-sig", newline="") as handle:
        rows = list(csv.DictReader(handle))
    if not rows or "wall_ms" not in rows[0]:
        return None
    frames = [{"wallMs": float(row["wall_ms"])} for row in rows]
    return {
        "label": path.stem, "status": "csv-only", "frames": frames,
        "routeMode": "metadata-unavailable", "_csv_only": True,
    }


def discover(inputs):
    """Accept directories, raw reports and suite manifests, deduplicating copied reports."""
    pending = []
    for value in inputs:
        path = Path(value)
        if path.is_dir():
            pending.extend(path.rglob("*.json"))
            pending.extend(path.rglob("*.csv"))
        else:
            pending.append(path)
    seen_paths = set()
    seen_runs = set()
    reports = []
    errors = []
    while pending:
        path = pending.pop(0)
        resolved = str(path.resolve())
        if resolved in seen_paths:
            continue
        seen_paths.add(resolved)
        try:
            if path.suffix.lower() == ".csv":
                if path.with_suffix(".json").exists():
                    continue
                report = read_csv(path)
            else:
                report = json.loads(path.read_text(encoding="utf-8-sig"))
                if isinstance(report, dict) and "paths" in report and "presets" in report:
                    for entry in report["paths"]:
                        candidate = Path(entry)
                        if not candidate.exists():
                            candidate = path.parent / candidate.name
                        pending.append(candidate)
                    continue
            if not isinstance(report, dict) or "frames" not in report or "label" not in report:
                continue
            identity = (report.get("startedUtc"), report["label"], len(report["frames"]))
            if identity in seen_runs:
                continue
            seen_runs.add(identity)
            report["_path"] = resolved
            reports.append(report)
        except (OSError, ValueError, TypeError) as exception:
            errors.append({"path": resolved, "error": str(exception)})
    return reports, errors


def context_key(report):
    """Separate hardware, build type, route, resolution and diagnostic instrumentation."""
    return (
        report.get("gpu"), report.get("graphicsApi"), report.get("unityVersion"),
        report.get("editor"), report.get("developmentBuild"), report.get("buildGuid"),
        report.get("routeMode", "fixed-pose"), report.get("width"), report.get("height"),
        report.get("framesPerLeg"), report.get("routeSecondsPerLeg"), report.get("routeDaySeconds"), report.get("diagnosticRun", False),
    )


def summarize_group(reports):
    """Pool raw samples and expose coverage plus repeat variation before computing effects."""
    values = defaultdict(list)
    frame_count = 0
    repeat_cloud_means = []
    repeat_wall_means = []
    for report in reports:
        frame_count += len(report["frames"])
        wall = [frame.get("wallMs") for frame in report["frames"]]
        values["wall"].extend(wall)
        values["cloud_gpu"].extend(report.get("cloudGpuMs", []))
        values["ray_gpu"].extend(report.get("rayGpuMs", []))
        repeat_cloud_means.append(describe(report.get("cloudGpuMs", []))["mean_ms"])
        repeat_wall_means.append(describe(wall)["mean_ms"])
        for timing in report.get("timings", []):
            for metric, field in (("frame_gpu", "gpuMs"), ("cpu", "cpuFrameMs"),
                                  ("main_thread", "mainThreadMs"), ("render_thread", "renderThreadMs")):
                values[metric].append(timing.get(field))
        for marker in report.get("additionalGpuMarkers", []):
            values[marker["name"]].extend(marker.get("milliseconds", []))
    # Optional build scopes remain N/A when the feature/build emits no valid GPU samples.
    for required in ("cloud_gpu", "frame_gpu", "cpu", "ray_gpu", "main_thread", "render_thread",
                     "Cloud.NormalFieldBuild", "Cloud.FarSurfaceBuild"):
        values[required]
    metrics = {name: describe(samples) for name, samples in values.items()}
    cloud = metrics["cloud_gpu"]["mean_ms"]
    frame_gpu = metrics["frame_gpu"]["mean_ms"]
    seconds = sum(values["wall"]) / 1000 if values["wall"] else 0
    result = {
        "preset": preset_name(reports[0]["label"]), "runs": len(reports), "frames": frame_count,
        "metrics": metrics, "average_fps": frame_count / seconds if seconds > 0 else None,
        "cloud_gpu_full_coverage": metrics["cloud_gpu"]["samples"] >= frame_count,
        "frame_gpu_full_coverage": metrics["frame_gpu"]["samples"] >= frame_count,
        "frame_gpu_duration_sanity": None if cloud is None or frame_gpu is None else frame_gpu >= cloud,
        "repeat_cloud_means_ms": repeat_cloud_means, "repeat_wall_means_ms": repeat_wall_means,
        "shader_source_roles": sorted({str(report.get("shaderSourceRole", "unrecorded")) for report in reports}),
        "fixed_pose_signatures": sorted({pose_signature(report) for report in reports if pose_signature(report)}),
        "paths": [report["_path"] for report in reports],
        "missing_metrics": [name for name, metric in metrics.items() if metric["samples"] == 0],
    }
    usable = [value for value in repeat_cloud_means if value is not None]
    result["cloud_repeat_range_percent"] = None
    if len(usable) >= 2:
        result["cloud_repeat_range_percent"] = (max(usable) - min(usable)) / statistics.fmean(usable) * 100
    return result


def analyze(reports):
    """Compare only complete runs within a common measured context; keep failures explicit."""
    grouped = defaultdict(lambda: defaultdict(list))
    rejected = []
    for report in reports:
        if report.get("status") not in ("completed", "csv-only") or not report["frames"]:
            rejected.append({"path": report["_path"], "status": report.get("status"), "error": report.get("error")})
            continue
        grouped[context_key(report)][preset_name(report["label"])].append(report)
    contexts = []
    for key, groups in grouped.items():
        cases = {preset: summarize_group(runs) for preset, runs in groups.items()}
        baseline = cases.get("baseline")
        for case in cases.values():
            case["cloud_change_percent_vs_baseline"] = None
            case["wall_change_percent_vs_baseline"] = None
            case["comparison_note"] = "No matching baseline in this context."
            if baseline is None:
                continue
            if key[-1]:
                case["comparison_note"] = "Diagnostic run: timing effects intentionally not reported."
                continue
            if key[6] == "metadata-unavailable":
                case["comparison_note"] = "CSV-only input: paired context cannot be verified."
                continue
            if key[6] == "fixed-pose" and (len(case["fixed_pose_signatures"]) != 1 or case["fixed_pose_signatures"] != baseline["fixed_pose_signatures"]):
                case["comparison_note"] = "Pose mismatch: paired effect rejected."
                continue
            case["comparison_note"] = "Matched context and route. GPU child scopes overlap Cloud.Total; optional marker means are per observed marker frame."
            for metric, field in (("cloud_gpu", "cloud_change_percent_vs_baseline"), ("wall", "wall_change_percent_vs_baseline")):
                before = baseline["metrics"][metric]["mean_ms"]
                after = case["metrics"][metric]["mean_ms"]
                if before is not None and after is not None:
                    if metric == "cloud_gpu" and not (case["cloud_gpu_full_coverage"] and baseline["cloud_gpu_full_coverage"]):
                        case["comparison_note"] += " Cloud GPU coverage is incomplete; cloud effect withheld."
                        continue
                    case[field] = (after / before - 1) * 100
        contexts.append({"context": key, "cases": list(cases.values())})
    return {"contexts": contexts, "rejected_runs": rejected}


def main():
    """Write machine-readable results and print mean/median/p95 with explicit N/A cells."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("inputs", nargs="+")
    parser.add_argument("--output", type=Path)
    arguments = parser.parse_args()
    reports, errors = discover(arguments.inputs)
    result = analyze(reports)
    result["read_errors"] = errors
    result["notes"] = "GPU and frame timing streams are delayed and cannot be assigned to individual poses. No extrapolation to other GPUs. Lower negative changes mean less elapsed time. Optional build marker means are not amortized over all frames."
    if arguments.output:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        arguments.output.write_text(json.dumps(result, indent=2, allow_nan=False), encoding="utf-8")
    for context in result["contexts"]:
        print("Context:", context["context"])
        print("preset | runs | frames | metric | samples | mean ms | median ms | p95 ms")
        for case in context["cases"]:
            for metric in ("wall", "cloud_gpu", "frame_gpu", "cpu", "ray_gpu",
                           "Cloud.NormalFieldBuild", "Cloud.FarSurfaceBuild"):
                stats = case["metrics"][metric]
                numbers = ["N/A" if stats[name] is None else f"{stats[name]:.4f}" for name in ("mean_ms", "median_ms", "p95_ms")]
                print(" | ".join(map(str, [case["preset"], case["runs"], case["frames"], metric, stats["samples"], *numbers])))
            print("  ", case["comparison_note"])
    print("Rejected runs:", len(result["rejected_runs"]), "Read errors:", len(errors))


if __name__ == "__main__":
    main()
