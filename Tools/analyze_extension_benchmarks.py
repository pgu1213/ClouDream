"""Analyze one completed extension suite, preserving missing GPU data and repeat variability."""

import argparse
import hashlib
import json
import math
import statistics
from collections import Counter, defaultdict
from pathlib import Path

import analyze_cloud_benchmarks as core


PRESETS = ("baseline", "reference-performance", "performance", "normal-cache", "surface-hybrid")
PAIRS = (
    ("baseline", "reference-performance", "보존 셰이더에서 해상도·거리 품질 변경"),
    ("reference-performance", "performance", "동일 품질에서 현재 셰이더로 변경"),
    ("baseline", "performance", "기준 품질에서 performance로 전체 변경"),
    ("performance", "normal-cache", "법선 결과 캐시 추가"),
    ("performance", "surface-hybrid", "원경 surface/volume 추가"),
)


def normalized(value):
    """Decode metadata and remove only transient Unity object IDs."""
    if isinstance(value, str):
        try:
            value = json.loads(value)
        except ValueError:
            return value
    if isinstance(value, dict):
        if set(value) == {"instanceID"}:
            return "<Unity object reference>"
        return {key: normalized(item) for key, item in value.items()}
    if isinstance(value, list):
        return [normalized(item) for item in value]
    return value


def signature(value):
    """Create a reproducible comparison signature."""
    return hashlib.sha256(json.dumps(normalized(value), sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def differences(before, after, prefix=""):
    """List nested setting differences without hiding quality changes."""
    result = []
    if isinstance(before, dict) and isinstance(after, dict):
        for key in sorted(set(before) | set(after)):
            path = key
            if prefix:
                path = prefix + "." + key
            result.extend(differences(before.get(key), after.get(key), path))
    elif before != after:
        result.append({"field": prefix, "before": before, "after": after})
    return result


def change(before, after):
    """Return percentage change only for available, valid inputs."""
    if before is None or after is None or before <= 0:
        return None
    return (after / before - 1) * 100


def repeat_span(values):
    """Compute range/mean of available repeat means."""
    values = [v for v in values if v is not None and math.isfinite(v)]
    if len(values) < 2:
        return None
    return (max(values) - min(values)) / statistics.fmean(values) * 100


def fmt(value):
    """Format missing values as N/A."""
    if value is None:
        return "N/A"
    return f"{value:.6f}"


def three(metric):
    """Format mean, ordinary median and nearest-rank p95."""
    return " / ".join(fmt(metric[k]) for k in ("mean_ms", "median_ms", "p95_ms"))


def analyze(directory):
    """Require suite completion before producing final comparisons and audit metadata."""
    manifests = sorted(directory.glob("suite-*.json"))
    if len(manifests) != 1:
        raise ValueError("Expected exactly one suite manifest.")
    suite = json.loads(manifests[0].read_text(encoding="utf-8-sig"))
    if suite.get("status") != "completed" or len(suite.get("paths", [])) != 10:
        raise ValueError(f"Suite not complete: {suite.get('status')}, paths={len(suite.get('paths', []))}.")
    reports, errors = core.discover([directory])
    reports.sort(key=lambda r: r["startedUtc"])
    result = core.analyze(reports)
    result["read_errors"] = errors
    if errors or result["rejected_runs"] or len(reports) != 10 or len(result["contexts"]) != 1:
        raise ValueError("Wrong run count, read errors, rejected runs, or mixed contexts.")
    order = [core.preset_name(r["label"]) for r in reports]
    if order != list(PRESETS) + list(reversed(PRESETS)):
        raise ValueError(f"Unexpected case order: {order}")
    groups = defaultdict(list)
    for report in reports:
        if report["status"] != "completed" or len(report["frames"]) != 540:
            raise ValueError("Each run must complete with 540 frames.")
        if Counter(f["leg"] for f in report["frames"]) != Counter({0: 180, 1: 180, 2: 180}):
            raise ValueError("Each leg must contain exactly 180 frames.")
        if [f["index"] for f in report["frames"]] != list(range(540)):
            raise ValueError("Frame indices are incomplete.")
        if report.get("diagnosticFrames") or report.get("diagnosticRun"):
            raise ValueError("Diagnostic readbacks cannot enter timing comparisons.")
        groups[core.preset_name(report["label"])].append(report)
    first = reports[0]
    if first.get("editor") or not first.get("developmentBuild"):
        raise ValueError("Expected a Development Player.")
    if (first["width"], first["height"], first["framesPerLeg"]) != (1920, 1080, 180):
        raise ValueError("Expected FHD and 180 frames per leg.")
    build = json.loads(first["buildManifest"])
    if build.get("extensionOnly") is not True:
        raise ValueError("Expected extension-only build metadata.")
    fields = ("gpu", "cpu", "graphicsApi", "unityVersion", "buildGuid", "buildManifest", "environmentSettings",
              "styleSettings", "skySettings", "oceanSettings", "routeCenter", "fieldOfView", "frozenGameTime",
              "hour", "noiseSource", "profilerEnabled", "gpuProfilerEnabled")
    signatures = {k: sorted({signature(r.get(k)) for r in reports}) for k in fields}
    same_metadata = all(len(v) == 1 for v in signatures.values())
    poses = sorted({core.pose_signature(r) for r in reports})
    cases = {c["preset"]: c for c in result["contexts"][0]["cases"]}
    settings = {}
    resources = {}
    markers = []
    for preset in PRESETS:
        runs = groups[preset]
        case = cases[preset]
        expected_role = "current-scene-shader"
        if preset in ("baseline", "reference-performance"):
            expected_role = "preserved-reference-shader"
        if case["shader_source_roles"] != [expected_role]:
            raise ValueError(f"Unexpected shader role for {preset}: {case['shader_source_roles']}")
        settings[preset] = normalized(runs[0]["passSettings"])
        case["wall_repeat_range_percent"] = repeat_span(case["repeat_wall_means_ms"])
        case["pass_settings_match_between_repeats"] = len({signature(r["passSettings"]) for r in runs}) == 1
        frames = [f for r in runs for f in r["frames"]]
        case["wall_by_leg"] = {str(leg): core.describe([f["wallMs"] for f in frames if f["leg"] == leg]) for leg in range(3)}
        resources[preset] = {}
        for field in ("normalFieldBytes", "farSurfaceBytes", "activeCloudBytes"):
            values = [f[field] for f in frames if field in f]
            resources[preset][field] = {"min": min(values), "max": max(values)} if values else None
        resources[preset]["farSurfaceStatuses"] = sorted({f.get("farSurfaceStatus", "unrecorded") for f in frames})
        for name in ("Cloud.NormalFieldBuild", "Cloud.FarSurfaceBuild"):
            values = []
            for run in runs:
                for marker in run.get("additionalGpuMarkers", []):
                    if marker["name"] == name:
                        values.extend(v for v in marker.get("milliseconds", []) if v > 0 and math.isfinite(v))
            markers.append({
                "preset": preset, "marker": name, "per_observed_marker_frame": core.describe(values),
                "route_frames": case["frames"],
                "observed_sum_div_route_frames_ms": sum(values) / case["frames"] if values else None,
                "note": "Observed mean is not amortized. Sum/route frames reports only observed contribution; omitted events are not assumed zero. Child scopes overlap Cloud.Total.",
            })
    comparisons = []
    for before_name, after_name, reason in PAIRS:
        before, after = cases[before_name], cases[after_name]
        matched = same_metadata and len(poses) == 1
        cloud = matched and before["cloud_gpu_full_coverage"] and after["cloud_gpu_full_coverage"]
        comparisons.append({
            "before": before_name, "after": after_name, "meaning": reason, "metadata_and_poses_match": matched,
            "pass_differences": differences(settings[before_name], settings[after_name]),
            "shader_roles": [before["shader_source_roles"], after["shader_source_roles"]],
            "cloud_change_percent": change(before["metrics"]["cloud_gpu"]["mean_ms"], after["metrics"]["cloud_gpu"]["mean_ms"]) if cloud else None,
            "wall_change_percent": change(before["metrics"]["wall"]["mean_ms"], after["metrics"]["wall"]["mean_ms"]) if matched else None,
            "fps_change_percent": change(before["average_fps"], after["average_fps"]) if matched else None,
        })
    repeats = []
    for index, report in enumerate(reports):
        single = core.summarize_group([report])
        repeats.append({
            "order": index, "preset": single["preset"], "label": report["label"], "path": report["_path"],
            "startedUtc": report["startedUtc"], "metrics": single["metrics"], "fps": single["average_fps"],
            "frames": len(report["frames"]), "frame_gpu_duration_sanity": single["frame_gpu_duration_sanity"],
            "normal_builds_first_last": [report["frames"][0]["normalFieldBuilds"], report["frames"][-1]["normalFieldBuilds"]],
            "surface_builds_first_last": [report["frames"][0]["farSurfaceBuilds"], report["frames"][-1]["farSurfaceBuilds"]],
        })
    result.update({
        "suite": suite, "suite_path": str(manifests[0].resolve()), "raw_directory": str(directory.resolve()),
        "build_manifest": build, "comparisons": comparisons, "repeat_runs": repeats,
        "resource_observations": resources, "build_markers": markers,
        "validation": {
            "suite_completed": True, "completed_runs": 10, "total_frames": 5400, "frames_per_run": 540,
            "pose_signatures": poses, "poses_match": len(poses) == 1, "metadata_signatures": signatures,
            "metadata_matches": same_metadata,
            "repeat_pass_settings_match": all(c["pass_settings_match_between_repeats"] for c in cases.values()),
            "cloud_coverage_complete": all(c["cloud_gpu_full_coverage"] for c in cases.values()),
            "full_route_warmup_frames_per_case": 572, "gpu_drain_frames_per_case": 32,
            "diagnostic_snapshots": 0,
            "startup": "Extension-only startup applies performance before initial 45 frames. Each case then performs full-route warmup.",
        },
        "notes": [
            "Independent extension-only Development build. Historical Development/Release measurements are not pooled.",
            "Missing GPU data is N/A/null, never zero. GPU and FrameTiming streams are delayed, not aligned one-to-one with poses.",
            "Cloud.Total contains child scopes. Do not add optional build times to total.",
            "Timing does not establish image/geometry acceptance. Read independent normal/far validation reports.",
            "Two repeats show observed variation, not broad reproducibility.",
        ],
    })
    return result, cases


def markdown(result, cases):
    """Write concise measured facts with raw-data coverage, repeats and optional-marker caveats."""
    build = result["build_manifest"]
    check = result["validation"]
    lines = [
        "# 확장 기술 고정 경로 Development 벤치마크", "",
        "전체 10회 측정 완료 뒤 원시 프레임과 GPU 스트림을 재계산했다. 이전 날짜·빌드와 합산하지 않는다.", "",
        "RTX 4080 / i7-13700K / Direct3D12 / Unity " + build["unityVersion"] + " / FHD 1920×1080. "
        "5개 프리셋을 순방향·역방향으로 각 2회 실행했다. 실행당 3구간×180=540프레임, 프리셋당 1,080프레임, 전체 5,400프레임이다.", "",
        "빌드 UTC: " + build["builtUtc"] + ". Build GUID: " + result["contexts"][0]["context"][5] + ". "
        "현재 셰이더 hash: " + build["shaderDependencyHash"] + ". Reference hash: " + build["referenceShaderDependencyHash"] + ". "
        "별도 extensionOnly Development 실행파일이며 GPU Profiler 계측 비용이 포함된다.", "",
        "모든 조건은 동일한 보존 원본 GPU 노이즈 Texture3D를 공유한다. baseline은 보존 셰이더·해상도 0.75·거리 품질 OFF, "
        "reference-performance는 보존 셰이더·해상도 0.5·거리 품질 ON이다. performance는 동일 0.5/거리 품질에서 현재 셰이더를 사용한다. "
        "normal-cache와 surface-hybrid는 performance에 해당 실험 한 가지만 켠다. 영상·형상 허용 여부는 별도 검증 결과로 판단한다.", "",
        "| 프리셋 | Cloud.Total 평균 / 중앙 / p95 ms | wall 평균 / 중앙 / p95 ms | Frame GPU 평균 / 중앙 / p95 ms | FPS | Cloud / Frame GPU 유효 수 |",
        "|---|---:|---:|---:|---:|---|",
    ]
    for preset in PRESETS:
        case = cases[preset]
        m = case["metrics"]
        lines.append(f"| {preset} | {three(m['cloud_gpu'])} | {three(m['wall'])} | {three(m['frame_gpu'])} | {fmt(case['average_fps'])} | {m['cloud_gpu']['samples']}/{case['frames']} / {m['frame_gpu']['samples']}/{case['frames']} |")
    lines += ["", "평균·일반 중앙값·nearest-rank p95는 유효 원시 표본을 합산한다. FPS는 총 프레임/총 wall 시간이다. "
              "Cloud.Total과 하위 GPU 마커는 중복되므로 더하지 않는다. 빈 마커는 N/A이다.", "",
              "| 비교 | Cloud.Total 변화 | wall 변화 | FPS 변화 | 의미 |", "|---|---:|---:|---:|---|"]
    for row in result["comparisons"]:
        lines.append(f"| {row['before']} → {row['after']} | {fmt(row['cloud_change_percent'])}% | {fmt(row['wall_change_percent'])}% | {fmt(row['fps_change_percent'])}% | {row['meaning']} |")
    lines += ["", "시간 변화 음수는 단축, 양수는 증가이다. baseline→performance에는 해상도·거리 품질 변경이 포함된다. "
              "새 두 기술의 단독 추가 효과는 performance를 분모로 비교한다.", "",
              "| 실행 순서 / 프리셋 | Cloud.Total 평균 ms | wall 평균 ms | Frame GPU 평균 ms | FPS | Cloud / Frame GPU 유효 수 |",
              "|---|---:|---:|---:|---:|---|"]
    for row in result["repeat_runs"]:
        m = row["metrics"]
        lines.append(f"| {row['order']} / {row['preset']} | {fmt(m['cloud_gpu']['mean_ms'])} | {fmt(m['wall']['mean_ms'])} | {fmt(m['frame_gpu']['mean_ms'])} | {fmt(row['fps'])} | {m['cloud_gpu']['samples']}/540 / {m['frame_gpu']['samples']}/540 |")
    lines += ["", "| 프리셋 | 반복 Cloud 평균 범위/평균 | 반복 wall 평균 범위/평균 |", "|---|---:|---:|"]
    for preset in PRESETS:
        case = cases[preset]
        lines.append(f"| {preset} | {fmt(case['cloud_repeat_range_percent'])}% | {fmt(case['wall_repeat_range_percent'])}% |")
    lines += ["", "반복 편차는 두 실행 평균의 (최대−최소)/평균이다. 두 번의 반복으로 넓은 조건의 재현성을 확정하지 않는다. "
              f"performance의 Cloud 반복 편차는 {fmt(cases['performance']['cloud_repeat_range_percent'])}%, "
              f"surface-hybrid는 {fmt(cases['surface-hybrid']['cloud_repeat_range_percent'])}%이다. "
              "따라서 이 두 조건의 pooled 변화율은 해당 묶음의 관측값이며 안정된 재현 효과로 주장하지 않는다. "
              "이번 경로에서는 두 신규 실험 모두 performance보다 평균 GPU 비용이 증가했다.", "",
              "| 프리셋 / 추가 GPU 마커 | 관측 수 | 관측 마커 프레임 평균 / 중앙 / p95 ms | 관측 합/전체 경로 프레임 ms |",
              "|---|---:|---:|---:|"]
    for row in result["build_markers"]:
        metric = row["per_observed_marker_frame"]
        lines.append(f"| {row['preset']} / {row['marker']} | {metric['samples']} | {three(metric)} | {fmt(row['observed_sum_div_route_frames_ms'])} |")
    lines += ["", "추가 마커 평균은 마커가 관측된 프레임의 평균이다. 한 프레임의 여러 cascade는 합계로 수집한다. "
              "마지막 열은 관측된 합/전체 기록 프레임이며 누락을 0으로 가정한 확정 상각 비용이 아니다. 전체 비용은 Cloud.Total/wall로 판단한다.", "",
              "| 프리셋 | 법선 필드 bytes 최소..최대 | 원경 표면 bytes 최소..최대 | 표면 상태 |", "|---|---:|---:|---|"]
    for preset in PRESETS:
        resource = result["resource_observations"][preset]
        normal, surface = resource["normalFieldBytes"], resource["farSurfaceBytes"]
        states = "; ".join(resource["farSurfaceStatuses"])
        lines.append(f"| {preset} | {normal['min']}..{normal['max']} | {surface['min']}..{surface['max']} | {states} |")
    lines += ["", f"검증: suite completed, 10/10 실행 completed, 읽기 오류 {len(result['read_errors'])}개. "
              f"실제 포즈 일치: {check['poses_match']}, 서명: {check['pose_signatures'][0]}. "
              f"빌드·환경·style·sky·ocean·원점·FOV·정지 시각·노이즈·Profiler 메타데이터 일치: {check['metadata_matches']}. "
              f"프리셋 내부 설정 반복 일치: {check['repeat_pass_settings_match']}. 전체 Cloud GPU 표본 확보: {check['cloud_coverage_complete']}.", "",
              "각 조건은 경로 540프레임+첫 포즈 32프레임을 워밍업한 뒤 측정하고 32프레임 동안 지연 GPU 결과를 배출한다. "
              "확장 전용 Player는 최초 45프레임 준비부터 performance 설정을 사용한다. 진단 GPU readback 스냅샷은 0개이다. "
              "GPU/FrameTiming 스트림은 지연되므로 개별 포즈와 일대일 대응하지 않는다.", "",
              "passSettings 차이(일시적 Unity instanceID 제외):", ""]
    for row in result["comparisons"]:
        fields = [f"{d['field']} {d['before']}→{d['after']}" for d in row["pass_differences"]]
        if not fields:
            fields = ["품질 설정 차이 없음; 셰이더 역할 변경"]
        lines.append(f"- {row['before']} → {row['after']}: " + ", ".join(fields) + ".")
    lines += ["", "원자료: " + result["raw_directory"] + ". 개별 경로, 구간 wall 통계, build 횟수와 설정 감사 서명은 같은 이름의 JSON에 보존했다.", ""]
    return "\n".join(lines)


def main():
    """Save final artifacts only after all expected raw data is complete."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result, cases = analyze(args.directory)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.with_suffix(".json").write_text(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")
    args.output.with_suffix(".md").write_text(markdown(result, cases), encoding="utf-8")
    print("Wrote", args.output.with_suffix(".md"))
    for preset in PRESETS:
        c = cases[preset]
        print(preset, "Cloud", fmt(c["metrics"]["cloud_gpu"]["mean_ms"]), "wall", fmt(c["metrics"]["wall"]["mean_ms"]),
              "FPS", fmt(c["average_fps"]), "repeat Cloud span %", fmt(c["cloud_repeat_range_percent"]))
    for row in result["comparisons"]:
        print(row["before"], "->", row["after"], "Cloud %", fmt(row["cloud_change_percent"]), "wall %", fmt(row["wall_change_percent"]))


if __name__ == "__main__":
    main()

