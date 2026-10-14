# 기준선 반복 편차의 코드 검토 (2026-10-05)

이 검토는 동결된 ReleaseFixed, ReleaseConfirmFixed 원자료 및 현재 벤치마크 소스를 읽어 수행했다. 렌더링/계측/MCP/소스 변경은 하지 않았다. 코드에서 기준선 두 실행에 다른 구름 파라미터를 전달하는 확정된 원인은 발견하지 못했다.

## 확인한 경로

- CloudBenchmarkAutomation.Configure: 각 실행마다 resolutionScale, distanceQuality, 주요 기존/신규 bool을 다시 지정한다. baseline은 같은 Resources reference shader, 0.75 해상도, density/lighting 거리 품질 OFF. 시각은 12시, wind 0이다. 옵션에 없는 새 기능이 추가되면 반드시 기본 OFF 초기화 목록에도 포함해야 한다.
- LostSkiesCloudPass.Execute/CreateRenderer: 셰이더가 바뀌면 이전 렌더러를 Dispose하고 ComputeShader를 Instantiate한 새 렌더러를 만든다. baseline→performance→performance→baseline 중 두 baseline은 모두 새 렌더러다. 가운데 performance 연속 실행만 렌더러를 유지한다.
- LostSkiesCloudRenderer: Uniform/텍스처를 매 Render마다 다시 연결한다. base settings는 UniversalCloudLayerRenderSettings struct에서 복사한다. wind는 매 프레임 source에서 계산하므로 누적 드리프트가 없다. Player의 노이즈 텍스처는 동일한 검증된 Resources를 공유하고 렌더러에서 파괴하지 않는다.
- 고정 경로는 각 실행에서 전체 540포즈 워밍업 후 첫 포즈 32프레임을 더 렌더링하고 측정한다. 첫 실행만 워밍업을 건너뛰는 분기를 발견하지 못했다. 측정 경로의 실제 좌표와 회전은 네 실행에서 일치한다.
- 측정마다 VSync 0, targetFrameRate=-1, gameTime 0, runInBackground를 적용한다. 프레임 타이밍 recorder와 timestamp를 새로 시작하고 종료시 복원한다. Release는 GPU profiler를 켜지 않는다.
- 씬의 Camera.allowDynamicResolution 및 HDAdditionalCameraData.allowDynamicResolution은 모두0이며 관련 런타임 품질 변경 스크립트를 찾지 못했다. 다만 보고서는 실제 HDRP 내부 크기/quality index/렌더 파이프라인 설정 해시를 매 프레임 저장하지 않는다.

## 원자료로 제외한 단순 설명

두 날짜 모두 첫 baseline이 전체 경로에서 지속적으로 느리다. 앞 10프레임을 제외하면10/02의 528/530, 10/05의 511/530프레임에서 첫 실행이 더 느리다. 초기 몇 프레임 spike만 제거해 해결되는 현상은 아니다.

| 10/05 baseline | 첫 구간 평균ms | 둘째 구간 평균ms | 셋째 구간 평균ms |
|---|---:|---:|---:|
| 첫 실행 |38.611145|43.192119|45.675836|
| 마지막 실행 |27.578094|28.648835|29.342378|

두 baseline의 targetAllocations는 측정 내내 1이고 shapeBuilds는 프레임 0/179/180/359/360/539에서 모두 [14,22,22,24,25,26]이다. Cloud RT 크기 1440×816, cache/temporal 메모리 0도 동일하다. 실제 wall과 FrameTiming GPU가 함께 바뀌고 FrameTiming 표본도10/05는 모두 540/540 유효여서 누락표본 평균 문제는 아니다. GPU 클록 평균 2744→2739MHz 차이로 약 1/3의 시간 차이를 설명할 수 없다. NVML 사용률은 다른 앱을 포함하므로 높은GPU사용률만으로 동일한 실행량과 스케줄링을 증명하지 못한다.

## 남은 원인 후보와 계측 공백

1. 프로세스 공통의 HDRP 상태, 드라이버 파이프라인 캐시, 공유Resources, GPU 스케줄링 상태는 렌더러 Dispose로 초기화하지 않는다. 코드상 첫baseline과 마지막baseline의 실제 차이다. 이것이 원인이라는 증거는 아직 없다.
2. 워밍업은 고정 프레임 수이며 안정화 여부나 워밍업 자체의 GPU 시간을 기록하지 않는다. 경로 한 바퀴를 돌았다는 사실만으로 드라이버와 동시 프로세스 상태가 안정화됐다고 증명하지 못한다.
3. 같은 shader의 연속 조건은 renderer가 유지되고 shader 교체 조건은 재생성된다. 현재 baseline 편차를 직접 설명하진 않지만, 새 후보와 performance의 준비 상태를 비교할 때 통제하거나 기록해야 하는 차이다.
4. 현재 metadata에는 원래 VSync/targetFrameRate만 있고 실제 quality index, active HDRP hash, compute 활성 keyword, 렌더러 generation, 실제 GPU constant hash, 매 프레임 내부 render 크기, foreground 상태가 없다. 초기 passSettings 동일성과 셰이더 코드 확인은 상태 누수가 없다는 완전한 GPU 캡처 증거는 아니다.
5. buildManifest.runtimeSourceHash는 LostSkiesCloudRenderer.cs 한 파일의 AssetDependencyHash이다. 전체 런타임 및 벤치마크 코드의 내용 해시로 해석하면 안 된다. buildGUID로 실행 빌드는 분리되지만 새 측정의 재현성에는 관련 source 전체 해시가 더 유용하다.
6. Flight의 passSettings는 CloudBenchmarkFlight 생성 전에 기록되므로 wind 0으로 남고 실제 wind 5는 constructor에서 적용된다. 현재 비행 설명은 소스로 확인됐지만 차기 보고서에는 적용 후 설정도 기록하는 편이 명확하다. 고정 baseline 편차와는 무관하다.

## 재측정 우선순위

Root가 실행하는 동일 baseline × 4 독립 프로세스에서 첫 실행만 느린지 먼저 본다. 연속 baseline에서도 감소하면 중간 performance shader 전환 없이 프로세스 공통 상태가 변하는 것이다. baseline × 4가 안정하면 shader 교체와 실행 순서에 관련된 상태를 우선 추적한다. 이 실험만으로 드라이버 원인을 확정할 수는 없다.

다음 분리 실험은 `baseline,reference-performance,baseline`이 유용하다. 같은 보존 셰이더와 renderer를 유지하면서 해상도 0.75→0.5→0.75, 거리 키워드 OFF→ON→OFF만 바뀐다. 여기서도 마지막 baseline이 빨라지면 current shader 고유 구현보다 해상도·키워드·GPU 공통 상태를 우선 본다. 여기서는 유지되고 current shader를 거친 ABBA에서만 감소하면 shader 또는 renderer 교체 조건을 더 조사한다.

모든 추가 실행은 기존 raw를 덮어쓰지 않고 별도 suite 디렉터리로 저장한다. 불안정한 데이터를 삭제하거나 좋은 반복만 선택하지 않는다.

## 법선 결과 캐시와 원경 surface-volume hybrid 통합 후 체크

- Configure는 모든 프리셋 시작 시 새 bool 두 개를 OFF로 초기화하고, 개별 후보와 결합 프리셋에서 ON을 명시한다. final의 의미가 변하므로 새 buildGUID와 알고리즘 revision을 기록한다. preserved reference는 수정하지 않는다.
- reference shader에는 새 kernel과 keyword가 없으므로 Renderer에서 지원 여부를 검사하고 정확 경로로 복귀해야 한다. 새 기능을 요청했는데 지원되지 않으면 벤치마크는 침묵하는 OFF 대신 명확히 실패해야 한다.
- ComputeStripper는 CloudRaymarch라는 이름의 reference와 현재 shader를 모두 처리한다. 현재 제거 조건은 compact OFF, ocean-height ON, palette ON, density≠lighting이다. 새 키워드 두 개는 이 규칙으로 직접 제거되지 않지만 조합 수가 최대 4배로 늘어난다. 새 build/probe kernel에도 같은 전역 multi_compile이 적용됨을 확인하고, 지원하는 모든 조합을 남긴 상태에서 빌드 시간과 누락 variant를 확인한다.
- 새 texture/buffer 바인딩은 Raymarch, RaymarchTemporal, RaymarchTemporalCompact, ProbeDensity, BuildSpatialLighting 및 새 build/probe kernel을 모두 검토한다. 새 옵션 OFF도 공통 shader 컴파일 변경 때문에 느려질 수 있으므로 reference-performance와 새 performance를 같은 0.5 해상도 및 거리 배수 1.5 조건으로 비교한다.
- 새 GPU marker는 Cloud.Total의 자식으로 보고한다. 부모와 자식을 더해 이중 계산하지 않는다. cache/hybrid bytes, build count, query/full/blend/fallback 및 hybrid 전환/정확 복귀 이유를 개별 기록한다.
- OFF 검증 후 최소 행렬은 reference-performance, performance, normal-result-cache, far-hybrid, 두 기능 결합, all-final이다. 고정 포즈 180×3과 실제 시간 비행 36초를 분리하고 기존 Dev/Release 수치와 합산하지 않는다. 구름 GPU marker는 Development, 최종 전체 frame은 Release로 확인한다.
- 이미지 검증에는 법선 캐시의 근거리·실루엣·가림 경계·태양 변화를 포함한다. hybrid는 불투명 조건, 원경 전환, 빈 공간, 상층 구름 뒤 운해, 카메라 관통을 확인한다. 기존 운해 연속성·상층 분포·GPU 유한성·날씨 중단/재전환도 다시 검증한다. 성능 향상과 이미지 오차 판정은 분리한다.

## 후속 ReferenceSwitch 결과

[Benchmark-Reference-Switch.md](Benchmark-Reference-Switch.md)에서 독립 프로세스 첫 baseline28.61ms, reference-performance21.21ms, 마지막 baseline32.54ms를 관측했다. 따라서 측정 case 사이의 current shader 교체를 편차 원인으로 확정할 수 없다. 또한 모든 suite는 Configure 전45프레임에 원래 scene shader를 렌더할 수 있으므로, baseline-only가 프로세스 전체에서 current shader를 실행하지 않았다고 가정하면 안 된다.
