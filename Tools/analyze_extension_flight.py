"""Analyze a completed six-run extension flight suite separately from fixed-pose data."""

import argparse
import json
import math
from collections import Counter, defaultdict
from pathlib import Path

import analyze_cloud_benchmarks as core
import analyze_extension_benchmarks as shared


PRESETS = ("performance", "normal-cache", "surface-hybrid")


def route_audit(report):
    """Check recorded poses and explicit simulation clocks against the continuous route formula."""
    center = report["routeCenter"]
    maximum_position_error = 0.0
    maximum_quaternion_error = 0.0
    maximum_hour_error = 0.0
    maximum_simulation_error = 0.0
    times = []
    legs = Counter()
    for frame in report["frames"]:
        seconds = frame["routeSeconds"]
        times.append(seconds)
        leg = int(seconds // 12.0)
        local = seconds - leg * 12
        if leg == 0:
            expected = [center["x"] - 4320 + 720 * local, center["y"] + 1000, center["z"] - 6800]
            pitch, yaw = 8, 45 * local
        elif leg == 1:
            expected = [center["x"], center["y"], center["z"] - 4320 + 720 * local]
            pitch, yaw = -6 + local, -15 + 1.5 * local
        else:
            length = math.sqrt(5000 ** 2 + 11000 ** 2)
            expected = [1222 + 5000 / length * 720 * local, 3300, -6800 + 11000 / length * 720 * local]
            pitch, yaw = 15, 15 + 12 * local
        actual = [frame["densityPosition"][key] for key in ("x", "y", "z")]
        maximum_position_error = max(maximum_position_error, max(abs(a - b) for a, b in zip(actual, expected)))
        pitch, yaw = math.radians(pitch) / 2, math.radians(yaw) / 2
        expected_q = [math.sin(pitch) * math.cos(yaw), math.cos(pitch) * math.sin(yaw),
                      -math.sin(pitch) * math.sin(yaw), math.cos(pitch) * math.cos(yaw)]
        actual_q = [frame["rotation"][key] for key in ("x", "y", "z", "w")]
        direct = max(abs(a - b) for a, b in zip(actual_q, expected_q))
        flipped = max(abs(a + b) for a, b in zip(actual_q, expected_q))
        maximum_quaternion_error = max(maximum_quaternion_error, min(direct, flipped))
        maximum_hour_error = max(maximum_hour_error, abs(frame["hour"] - (12 + seconds / 1200 * 24)))
        maximum_simulation_error = max(maximum_simulation_error, abs(frame["simulationTime"] - seconds))
        if frame["leg"] != leg:
            raise ValueError("Recorded leg differs from elapsed-time route.")
        legs[leg] += 1
    wall_seconds = sum(frame["wallMs"] for frame in report["frames"]) / 1000
    valid = (set(legs) == {0, 1, 2} and times == sorted(times) and 0 <= times[0] < 0.1
             and 35.8 <= times[-1] < 36 and 36 <= wall_seconds < 36.2
             and maximum_position_error < 0.02 and maximum_quaternion_error < 0.00001
             and maximum_hour_error < 0.00001 and maximum_simulation_error < 0.00001)
    return {
        "passed": valid, "first_route_seconds": times[0], "last_route_seconds": times[-1], "wall_seconds": wall_seconds,
        "frames_by_leg": dict(legs), "maximum_position_component_error_metres": maximum_position_error,
        "maximum_quaternion_component_error": maximum_quaternion_error,
        "maximum_hour_error": maximum_hour_error, "maximum_simulation_time_error_seconds": maximum_simulation_error,
        "note": "Different FPS produces different samples of the same time-parametrized route; exact pose-sequence equality is neither expected nor claimed.",
    }


def analyze(directory):
    """Require completed ordered runs and preserve per-run route validation and timing coverage."""
    paths = sorted(directory.glob("suite-*.json"))
    if len(paths) != 1:
        raise ValueError("Expected one flight suite.")
    suite = json.loads(paths[0].read_text(encoding="utf-8-sig"))
    if suite.get("status") != "completed" or len(suite.get("paths", [])) != 6:
        raise ValueError("Flight suite is not complete.")
    reports, errors = core.discover([directory])
    reports.sort(key=lambda r: r["startedUtc"])
    result = core.analyze(reports)
    result["read_errors"] = errors
    if errors or result["rejected_runs"] or len(reports) != 6 or len(result["contexts"]) != 1:
        raise ValueError("Flight data has missing runs, errors, or mixed contexts.")
    if [core.preset_name(r["label"]) for r in reports] != list(PRESETS) + list(reversed(PRESETS)):
        raise ValueError("Unexpected flight order.")
    first = reports[0]
    expected = {"editor": False, "developmentBuild": True, "diagnosticRun": False, "routeMode": "elapsed-time",
                "width": 1920, "height": 1080, "routeSecondsPerLeg": 12.0, "routeDaySeconds": 1200.0,
                "flightMetresPerSecond": 720.0, "flightRotationDegreesPerSecond": 45.0}
    for report in reports:
        for field, value in expected.items():
            if report.get(field) != value:
                raise ValueError(f"Flight metadata differs: {field}")
        if report.get("diagnosticFrames"):
            raise ValueError("Flight includes diagnostic GPU readback snapshots.")
    fields = ("gpu", "cpu", "graphicsApi", "unityVersion", "buildGuid", "buildManifest", "environmentSettings",
              "styleSettings", "skySettings", "oceanSettings", "routeCenter", "fieldOfView", "frozenGameTime",
              "hour", "noiseSource", "profilerEnabled", "gpuProfilerEnabled", "shaderSourceRole", "routeLightingMode")
    signatures = {field: sorted({shared.signature(r.get(field)) for r in reports}) for field in fields}
    metadata_match = all(len(values) == 1 for values in signatures.values())
    groups = defaultdict(list)
    repeats = []
    for index, report in enumerate(reports):
        preset = core.preset_name(report["label"])
        groups[preset].append(report)
        summary = core.summarize_group([report])
        audit = route_audit(report)
        repeats.append({
            "order": index, "preset": preset, "path": report["_path"], "label": report["label"],
            "frames": len(report["frames"]), "metrics": summary["metrics"], "fps": summary["average_fps"],
            "route_audit": audit, "frame_gpu_duration_sanity": summary["frame_gpu_duration_sanity"],
            "normal_builds_first_last": [report["frames"][0]["normalFieldBuilds"], report["frames"][-1]["normalFieldBuilds"]],
            "surface_builds_first_last": [report["frames"][0]["farSurfaceBuilds"], report["frames"][-1]["farSurfaceBuilds"]],
        })
    cases = {case["preset"]: case for case in result["contexts"][0]["cases"]}
    marker_rows = []
    resources = {}
    settings = {}
    for preset in PRESETS:
        runs = groups[preset]
        case = cases[preset]
        settings[preset] = shared.normalized(runs[0]["passSettings"])
        case["wall_repeat_range_percent"] = shared.repeat_span(case["repeat_wall_means_ms"])
        case["pass_settings_match_between_repeats"] = len({shared.signature(r["passSettings"]) for r in runs}) == 1
        frames = [frame for run in runs for frame in run["frames"]]
        resources[preset] = {}
        for field in ("normalFieldBytes", "farSurfaceBytes"):
            values = [frame[field] for frame in frames]
            resources[preset][field] = {"min": min(values), "max": max(values)}
        resources[preset]["farSurfaceStatuses"] = sorted({frame["farSurfaceStatus"] for frame in frames})
        for name in ("Cloud.NormalFieldBuild", "Cloud.FarSurfaceBuild"):
            values = []
            for run in runs:
                for marker in run.get("additionalGpuMarkers", []):
                    if marker["name"] == name:
                        values.extend(v for v in marker.get("milliseconds", []) if v > 0 and math.isfinite(v))
            marker_rows.append({
                "preset": preset, "marker": name, "per_observed_marker_frame": core.describe(values),
                "route_frames": case["frames"], "observed_sum_div_route_frames_ms": sum(values) / case["frames"] if values else None,
            })
    valid_route = all(row["route_audit"]["passed"] for row in repeats)
    comparisons = []
    before = cases["performance"]
    for preset in PRESETS[1:]:
        after = cases[preset]
        matched = metadata_match and valid_route
        complete_cloud = matched and before["cloud_gpu_full_coverage"] and after["cloud_gpu_full_coverage"]
        comparisons.append({
            "before": "performance", "after": preset,
            "cloud_change_percent": shared.change(before["metrics"]["cloud_gpu"]["mean_ms"], after["metrics"]["cloud_gpu"]["mean_ms"]) if complete_cloud else None,
            "wall_change_percent": shared.change(before["metrics"]["wall"]["mean_ms"], after["metrics"]["wall"]["mean_ms"]) if matched else None,
            "fps_change_percent": shared.change(before["average_fps"], after["average_fps"]) if matched else None,
            "pass_differences": shared.differences(settings["performance"], settings[preset]),
            "note": "Means describe rendered frames on the same elapsed-time schedule; different frame rates create different sampling densities along the path.",
        })
    result.update({
        "suite": suite, "suite_path": str(paths[0].resolve()), "raw_directory": str(directory.resolve()),
        "build_manifest": json.loads(first["buildManifest"]), "repeat_runs": repeats, "comparisons": comparisons,
        "build_markers": marker_rows, "resource_observations": resources,
        "validation": {"metadata_signatures": signatures, "metadata_matches": metadata_match,
                       "route_formula_audit_passed": valid_route, "expected_route_metadata": expected,
                       "pass_settings_repeat_match": all(c["pass_settings_match_between_repeats"] for c in cases.values()),
                       "cloud_coverage_complete": all(c["cloud_gpu_full_coverage"] for c in cases.values())},
        "notes": ["Flight has no baseline by design. Compare incremental technologies only against performance.",
                  "Separate Development flight suite; do not pool fixed-route, other builds, or historical runs.",
                  "GPU streams are delayed and not aligned to individual pose rows. Missing GPU data remains null/N/A.",
                  "Optional marker mean is per observed marker frame, not amortized. Observed sum/route frames does not prove missing events are zero.",
                  "Cloud.Total contains child scopes. Timing does not establish image or geometry acceptance."],
    })
    return result, cases


def markdown(result, cases):
    """Render flight effects and frame-rate-dependent sampling limits."""
    fmt, three = shared.fmt, shared.three
    build = result["build_manifest"]
    validation = result["validation"]
    lines = ["# 확장 기술 실시간 비행 Development 벤치마크", "",
             "performance, normal-cache, surface-hybrid를 순방향·역방향 각 2회 측정한 완료 자료이다. baseline은 이 묶음에 없으며 고정 경로·다른 빌드와 합산하지 않는다.", "",
             "RTX 4080 / i7-13700K / Unity " + build["unityVersion"] + " / Direct3D12 / FHD. Build GUID: "
             + result["contexts"][0]["context"][5] + ". 빌드 UTC: " + build["builtUtc"] + ". Shader hash: " + build["shaderDependencyHash"] + ".", "",
             "각 실행은 실제 시간 12초×3구간=36초이며 처음 4초는 별도 워밍업이다. 이동 720m/s, 첫 구간 회전 45도/s, "
             "바람 5, 낮 주기 1,200초(12시→약 12.72시)를 사용한다. 날씨 변경은 6초, 중단·재전환은 9.6초, 원래 상태 복귀는 21.6초에 시작한다.", "",
             "| 프리셋 | 프레임 | Cloud.Total 평균 / 중앙 / p95 ms | wall 평균 / 중앙 / p95 ms | Frame GPU 평균 / 중앙 / p95 ms | FPS | Cloud / Frame GPU 유효 수 |",
             "|---|---:|---:|---:|---:|---:|---|"]
    for preset in PRESETS:
        c = cases[preset]
        m = c["metrics"]
        lines.append(f"| {preset} | {c['frames']} | {three(m['cloud_gpu'])} | {three(m['wall'])} | {three(m['frame_gpu'])} | {fmt(c['average_fps'])} | {m['cloud_gpu']['samples']}/{c['frames']} / {m['frame_gpu']['samples']}/{c['frames']} |")
    lines += ["", "FPS는 총 프레임/총 wall 시간이다. Cloud.Total은 구름 GPU 범위이며 전체 wall·Frame GPU와 동일하지 않다. "
              "프레임 속도가 다르므로 실제 포즈 표본은 같지 않다. 동일한 시간 함수의 경로를 검증했으며, 평균 GPU 값은 각 실행에서 렌더된 프레임의 표본 평균이다. "
              "같은 포즈 1:1 비교 또는 균일 시간 재표본화 결과로 표현하지 않는다.", "",
              "| performance 대비 | Cloud.Total 변화 | wall 변화 | FPS 변화 |", "|---|---:|---:|---:|"]
    for row in result["comparisons"]:
        lines.append(f"| {row['after']} | {fmt(row['cloud_change_percent'])}% | {fmt(row['wall_change_percent'])}% | {fmt(row['fps_change_percent'])}% |")
    lines += ["", "| 실행 순서 / 프리셋 | 프레임 | 측정 wall 초 | Cloud.Total 평균 ms | wall 평균 ms | FPS | Cloud / Frame GPU 유효 수 |",
              "|---|---:|---:|---:|---:|---:|---|"]
    for row in result["repeat_runs"]:
        m = row["metrics"]
        lines.append(f"| {row['order']} / {row['preset']} | {row['frames']} | {fmt(row['route_audit']['wall_seconds'])} | {fmt(m['cloud_gpu']['mean_ms'])} | {fmt(m['wall']['mean_ms'])} | {fmt(row['fps'])} | {m['cloud_gpu']['samples']}/{row['frames']} / {m['frame_gpu']['samples']}/{row['frames']} |")
    lines += ["", "| 프리셋 | 반복 Cloud 평균 범위/평균 | 반복 wall 평균 범위/평균 |", "|---|---:|---:|"]
    for preset in PRESETS:
        c = cases[preset]
        lines.append(f"| {preset} | {fmt(c['cloud_repeat_range_percent'])}% | {fmt(c['wall_repeat_range_percent'])}% |")
    lines += ["", "| 프리셋 / 마커 | 관측 수 | 관측 마커 프레임 평균 / 중앙 / p95 ms | 관측 합/전체 경로 프레임 ms |", "|---|---:|---:|---:|"]
    for row in result["build_markers"]:
        m = row["per_observed_marker_frame"]
        lines.append(f"| {row['preset']} / {row['marker']} | {m['samples']} | {three(m)} | {fmt(row['observed_sum_div_route_frames_ms'])} |")
    lines += ["", "추가 마커 평균은 관측된 마커 프레임의 평균이며 상각 평균이 아니다. 마지막 열은 관측 합을 기록 프레임 수로 나눈 값이고 누락 이벤트를 0으로 가정하지 않는다. "
              "Cloud.Total과 하위 마커를 더하지 않는다. N/A는 유효 표본 없음이다.", "",
              f"검증: 6/6 실행 completed, 읽기 오류 {len(result['read_errors'])}개. 공통 메타데이터 일치: {validation['metadata_matches']}; "
              f"프리셋 내부 설정 반복 일치: {validation['pass_settings_repeat_match']}; 전체 Cloud GPU 표본 확보: {validation['cloud_coverage_complete']}; "
              f"실제 위치·회전·hour·simulationTime의 경과 시간 경로식 감사: {validation['route_formula_audit_passed']}. "
              "세 구간 모두 존재하고 시간은 단조 증가했다. 개별 최대 오차와 구간별 프레임 수는 JSON에 기록했다.", "",
              "프리셋 차이:", ""]
    for row in result["comparisons"]:
        fields = [f"{d['field']} {d['before']}→{d['after']}" for d in row["pass_differences"]]
        lines.append("- performance → " + row["after"] + ": " + ", ".join(fields) + ".")
    lines += ["", "영상·형상 품질 통과 여부는 별도 Normal/Far GPU 검증 자료를 따른다. 두 반복의 평균 변화율은 이 실행 묶음의 관측값이며 다른 조건으로 일반화하지 않는다.", "",
              "원자료: " + result["raw_directory"] + ".", ""]
    return "\n".join(lines)


def main():
    """Write final flight artifacts only when the completed suite is available."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result, cases = analyze(args.directory)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.with_suffix(".json").write_text(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")
    args.output.with_suffix(".md").write_text(markdown(result, cases), encoding="utf-8")
    print("Wrote", args.output.with_suffix(".md"))
    print(json.dumps(result["validation"], ensure_ascii=False))
    for preset in PRESETS:
        c = cases[preset]
        print(preset, "Cloud", shared.fmt(c["metrics"]["cloud_gpu"]["mean_ms"]),
              "wall", shared.fmt(c["metrics"]["wall"]["mean_ms"]), "FPS", shared.fmt(c["average_fps"]))
    print(json.dumps(result["comparisons"], ensure_ascii=False))


if __name__ == "__main__":
    main()

