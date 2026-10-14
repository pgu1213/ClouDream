"""Report completed current Release cohorts without turning absent Cloud GPU markers into zero."""

import argparse
import json
from collections import Counter, defaultdict
from pathlib import Path

import analyze_cloud_benchmarks as core
import analyze_extension_benchmarks as shared
from analyze_extension_flight import route_audit


def analyze_cohort(directory):
    """Validate one ABBA suite and compute pooled plus repeat statistics."""
    suites = sorted(directory.glob("suite-*.json"))
    if len(suites) != 1:
        raise ValueError("Expected one Release suite manifest.")
    suite = json.loads(suites[0].read_text(encoding="utf-8-sig"))
    if suite.get("status") != "completed" or len(suite.get("paths", [])) != 4:
        raise ValueError(f"Incomplete Release suite: {directory}")
    reports, errors = core.discover([directory])
    reports.sort(key=lambda r: r["startedUtc"])
    analysis = core.analyze(reports)
    if len(reports) != 4 or errors or analysis["rejected_runs"] or len(analysis["contexts"]) != 1:
        raise ValueError("Wrong run count, errors, or mixed contexts.")
    if [core.preset_name(r["label"]) for r in reports] != ["baseline", "performance", "performance", "baseline"]:
        raise ValueError("Expected baseline/performance ABBA.")
    first = reports[0]
    fixed = first["routeMode"] == "fixed-pose"
    for report in reports:
        if report.get("developmentBuild") or report.get("editor") or report.get("diagnosticRun") or report.get("diagnosticFrames"):
            raise ValueError("Release performance data must not contain Development/Editor or diagnostic runs.")
        if (report["width"], report["height"]) != (1920, 1080):
            raise ValueError("Expected FHD.")
        settings = shared.normalized(report["passSettings"])
        if settings["useNormalFieldCache"] or settings["useFarSurface"]:
            raise ValueError("Release comparison must keep independent new techniques OFF.")
        if fixed:
            if len(report["frames"]) != 540 or Counter(f["leg"] for f in report["frames"]) != Counter({0: 180, 1: 180, 2: 180}):
                raise ValueError("Fixed route frame count mismatch.")
        elif (report["routeSecondsPerLeg"], report["routeDaySeconds"], report["flightMetresPerSecond"], report["flightRotationDegreesPerSecond"]) != (12, 1200, 720, 45):
            raise ValueError("Flight route parameters mismatch.")
    fields = ("gpu", "cpu", "graphicsApi", "unityVersion", "buildGuid", "buildManifest", "environmentSettings",
              "styleSettings", "skySettings", "oceanSettings", "routeCenter", "fieldOfView", "frozenGameTime",
              "hour", "noiseSource", "profilerEnabled", "gpuProfilerEnabled")
    signatures = {field: sorted({shared.signature(r.get(field)) for r in reports}) for field in fields}
    metadata_match = all(len(values) == 1 for values in signatures.values())
    groups = defaultdict(list)
    repeats = []
    for index, report in enumerate(reports):
        preset = core.preset_name(report["label"])
        groups[preset].append(report)
        summary = core.summarize_group([report])
        audit = None
        if not fixed:
            audit = route_audit(report)
        repeats.append({
            "order": index, "preset": preset, "path": report["_path"], "label": report["label"],
            "frames": len(report["frames"]), "metrics": summary["metrics"], "fps": summary["average_fps"],
            "route_audit": audit, "frame_gpu_duration_sanity": summary["frame_gpu_duration_sanity"],
        })
    cases = {c["preset"]: c for c in analysis["contexts"][0]["cases"]}
    for preset, case in cases.items():
        expected_role = "preserved-reference-shader"
        if preset == "performance":
            expected_role = "current-scene-shader"
        if case["shader_source_roles"] != [expected_role]:
            raise ValueError("Unexpected shader source role.")
        case["wall_repeat_range_percent"] = shared.repeat_span(case["repeat_wall_means_ms"])
        case["pass_settings_repeat_match"] = len({shared.signature(r["passSettings"]) for r in groups[preset]}) == 1
        case["frame_gpu_mean_div_wall_mean"] = None
        if case["metrics"]["frame_gpu"]["mean_ms"] is not None:
            case["frame_gpu_mean_div_wall_mean"] = case["metrics"]["frame_gpu"]["mean_ms"] / case["metrics"]["wall"]["mean_ms"]
    poses = []
    route_matches = True
    if fixed:
        poses = sorted({core.pose_signature(r) for r in reports})
        route_matches = len(poses) == 1
    else:
        route_matches = all(r["route_audit"]["passed"] for r in repeats)
    before, after = cases["baseline"], cases["performance"]
    matched = metadata_match and route_matches
    difference = shared.differences(shared.normalized(groups["baseline"][0]["passSettings"]), shared.normalized(groups["performance"][0]["passSettings"]))
    comparison = {
        "wall_change_percent": shared.change(before["metrics"]["wall"]["mean_ms"], after["metrics"]["wall"]["mean_ms"]) if matched else None,
        "frame_gpu_change_percent": shared.change(before["metrics"]["frame_gpu"]["mean_ms"], after["metrics"]["frame_gpu"]["mean_ms"]) if matched else None,
        "fps_change_percent": shared.change(before["average_fps"], after["average_fps"]) if matched else None,
        "cloud_change_percent": None, "pass_differences": difference,
        "interpretation": "Resolution .75 to .5 and distance density/lighting quality are included; independent normal/far techniques remain OFF.",
    }
    build = json.loads(first["buildManifest"])
    if not build.get("extensionOnly") or build.get("development"):
        raise ValueError("Unexpected Release build policy.")
    return {
        "route_mode": first["routeMode"], "raw_directory": str(directory.resolve()), "suite": suite,
        "suite_path": str(suites[0].resolve()), "build_manifest": build, "build_guid": first["buildGuid"],
        "analysis": analysis, "read_errors": errors, "cases": cases, "repeat_runs": repeats,
        "comparison": comparison,
        "validation": {"completed_runs": 4, "metadata_matches": metadata_match, "metadata_signatures": signatures,
                       "route_matches": route_matches, "fixed_pose_signatures": poses,
                       "pass_settings_repeat_match": all(c["pass_settings_repeat_match"] for c in cases.values()),
                       "cloud_gpu_markers_available": any(c["metrics"]["cloud_gpu"]["samples"] > 0 for c in cases.values())},
    }


def markdown(cohorts):
    """Render separate fixed and elapsed-time Release results and missing-marker limits."""
    fmt, three = shared.fmt, shared.three
    first = cohorts[0]
    build = first["build_manifest"]
    lines = ["# 최신 확장 Release 빌드 성능", "",
             "최신 ExtensionRelease 실행파일에서 완료한 baseline/performance ABBA를 원자료로 재계산했다. 이전 Release·Development와 합산하지 않는다.", "",
             "RTX 4080 / i7-13700K / Unity " + build["unityVersion"] + " / Direct3D12 / FHD 1920×1080. 빌드 UTC: "
             + build["builtUtc"] + ". Build GUID: " + first["build_guid"] + ". Shader hash: " + build["shaderDependencyHash"]
             + ". Reference hash: " + build["referenceShaderDependencyHash"] + ".", "",
             "baseline은 공통 C#/보존 노이즈 위에 보존 셰이더·0.75 해상도·거리 품질 OFF를 적용하고, performance는 현재 셰이더·0.5 해상도·거리 품질 ON을 적용한다. "
             "따라서 아래 개선율에는 품질 설정 변경이 포함된다. 독립 법선 캐시와 원경 표면 옵션은 두 조건 모두 OFF이다. 원래 전체 Player 실행파일과의 비교는 아니다.", "",
             "**Release에는 유효 Cloud.Total/Cloud.Raymarch GPU 마커가 없으므로 N/A다.** FrameTiming 전체 GPU 또는 wall 시간을 구름 단독 GPU 시간으로 표현하지 않는다. "
             "원시 summary의 Frame GPU duration sanity가 false인 것은 Cloud 마커와의 교차검사가 불가능한 상태이며 Frame GPU가 0이라는 뜻이 아니다.", ""]
    for cohort in cohorts:
        fixed = cohort["route_mode"] == "fixed-pose"
        title = "고정 경로" if fixed else "실제 시간 비행"
        lines += ["## " + title, ""]
        if fixed:
            lines += ["각 실행 3구간×180=540프레임, 조건별 2회=1,080프레임이다. 전체 경로+32프레임 워밍업 뒤 측정했다. 캡처는 계측이 모두 끝난 뒤 별도 단계에서 실행됐다.", ""]
        else:
            lines += ["각 실행 12초×3구간=36초, 조건별 2회다. 초기 포즈 4초 워밍업 후 720m/s·첫 구간45도/s·바람5·낮 주기1,200초로 이동한다. "
                      "날씨는 6초에 변경, 9.6초에 중단·재전환, 21.6초에 원래 상태 복귀를 시작한다. FPS가 달라 포즈 표본은 1:1 같지 않으며 동일 시간 함수로 생성됐는지 검사했다.", ""]
        lines += ["| 프리셋 | 프레임 | wall 평균 / 중앙 / p95 ms | Frame GPU 평균 / 중앙 / p95 ms | Cloud GPU | aggregate FPS | Frame GPU 유효 수 |",
                  "|---|---:|---:|---:|---:|---:|---|"]
        for preset in ("baseline", "performance"):
            case = cohort["cases"][preset]
            m = case["metrics"]
            lines.append(f"| {preset} | {case['frames']} | {three(m['wall'])} | {three(m['frame_gpu'])} | {fmt(m['cloud_gpu']['mean_ms'])} | {fmt(case['average_fps'])} | {m['frame_gpu']['samples']}/{case['frames']} |")
        c = cohort["comparison"]
        lines += ["", f"baseline→performance 관측 wall 변화 **{fmt(c['wall_change_percent'])}%**, 전체 Frame GPU 변화 **{fmt(c['frame_gpu_change_percent'])}%**, "
                  f"FPS 변화 **{fmt(c['fps_change_percent'])}%**. Cloud GPU 개선율은 N/A다.", "",
                  "| 실행 순서 / 프리셋 | 프레임 | wall 평균 ms | Frame GPU 평균 ms | FPS | Frame GPU 유효 수 |", "|---|---:|---:|---:|---:|---|"]
        for row in cohort["repeat_runs"]:
            m = row["metrics"]
            lines.append(f"| {row['order']} / {row['preset']} | {row['frames']} | {fmt(m['wall']['mean_ms'])} | {fmt(m['frame_gpu']['mean_ms'])} | {fmt(row['fps'])} | {m['frame_gpu']['samples']}/{row['frames']} |")
        ranges = ", ".join(p + " " + fmt(cohort["cases"][p]["wall_repeat_range_percent"]) + "%" for p in ("baseline", "performance"))
        lines += ["", "반복 wall 평균의 (최대−최소)/평균: " + ranges + ". 두 반복에서 관측한 편차이며 다른 조건의 재현성을 확정하지 않는다.", ""]
        if any(cohort["cases"][p]["wall_repeat_range_percent"] > 5 for p in ("baseline", "performance")):
            lines += ["이 묶음은 반복 편차가 크므로 합산 개선율을 안정된 재현 효과로 주장하지 않는다. 개별 반복 수치를 함께 보존한다.", ""]
        v = cohort["validation"]
        lines += [f"검증: suite completed, 4/4 실행 completed, 읽기 오류 {len(cohort['read_errors'])}개. 공통 빌드·환경·프로필·노이즈·시각 메타데이터 일치: "
                  f"{v['metadata_matches']}; 프리셋 내부 설정 반복 일치: {v['pass_settings_repeat_match']}; 경로 검증: {v['route_matches']}.", ""]
        if fixed:
            lines += ["실제 포즈 SHA-256: " + v["fixed_pose_signatures"][0] + ".", ""]
        fields = [f"{d['field']} {d['before']}→{d['after']}" for d in c["pass_differences"]]
        lines += ["설정 차이(Unity instanceID 제외): " + ", ".join(fields) + ". 셰이더 역할은 preserved-reference→current로 변경된다.", "",
                  "원자료: " + cohort["raw_directory"] + ".", ""]
    lines += ["GPU·FrameTiming 스트림은 지연되므로 개별 포즈와 일대일 대응하지 않는다. N/A를 0으로 바꾸지 않았다. 평균·일반 중앙값·nearest-rank p95를 유효 원시 표본에서 계산하고 FPS는 총 프레임/총 wall 시간으로 계산했다.", ""]
    return "\n".join(lines)


def main():
    """Write only completed cohorts; multiple inputs remain separated in one report."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directories", nargs="+", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    cohorts = [analyze_cohort(directory) for directory in args.directories]
    if len({c["build_guid"] for c in cohorts}) != 1:
        raise ValueError("Latest Release report must use one build.")
    result = {"cohorts": cohorts, "notes": "No pooling across route modes or historical builds. Cloud GPU markers unavailable remain N/A."}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.with_suffix(".json").write_text(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")
    args.output.with_suffix(".md").write_text(markdown(cohorts), encoding="utf-8")
    print("Wrote", args.output.with_suffix(".md"))
    for cohort in cohorts:
        print(cohort["route_mode"], json.dumps(cohort["comparison"], ensure_ascii=False))
        for preset, case in cohort["cases"].items():
            print(preset, "wall", shared.fmt(case["metrics"]["wall"]["mean_ms"]), "FPS", shared.fmt(case["average_fps"]),
                  "repeat span %", shared.fmt(case["wall_repeat_range_percent"]))


if __name__ == "__main__":
    main()

