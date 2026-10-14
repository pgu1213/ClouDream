# 고정 경로 벤치마크 수치 (초기 Release + Development 확인)

이 문서는 UTC 2026-10-02의 초기 측정 묶음이다. 2026-10-05 독립 Release 재확인은 [Benchmark-Release-ConfirmFixed.md](Benchmark-Release-ConfirmFixed.md), 같은 날 실제 시간 비행은 [Benchmark-Release-Flight.md](Benchmark-Release-Flight.md)에 별도로 보존했다. 날짜가 다른 결과를 합산하지 않는다.

원자료 기준: RTX 4080 / i7-13700K / Unity 6000.6.2f1 / Direct3D12 / FHD 1920×1080. 각 프리셋은 3구간 × 180프레임 × 2회 = 1,080프레임이다. 전체 경로 워밍업 후 측정했고 고정 시간·동일 포즈를 사용했다. Development와 Release는 서로 다른 빌드이므로 합치지 않는다.

baseline은 보존한 기존 GPU 셰이더에 0.75 해상도, performance는 재설계 GPU 셰이더에 0.5 해상도 및 density/lighting 거리 품질(배수 1.5)을 적용한다. 두 조건 모두 깊이 기반 업샘플링을 사용한다. 새 필드·차폐 캐시·temporal·traversal·adaptive는 performance에서 꺼져 있다.

baseline은 프로젝트 기준 셰이더를 보존한 비교 경로이며, Player용 노이즈 리소스와 공통 C# 기반은 두 조건에서 공유한다. 원래 전체 Player 실행파일과의 비교는 아니다.

## Release

Build GUID: `3a635c8a2136459587223dd8a19317dd`. 경로 포즈 SHA-256: `ece77496bdc4f86f921f323c37b3a566941099160c2a114ff2a5ee62ea1ad861`.

| 프리셋 | wall 평균 / 중앙 / p95 ms | Frame GPU 평균 / 중앙 / p95 ms | Cloud GPU 평균 / 중앙 / p95 ms | aggregate FPS |
|---|---:|---:|---:|---:|
| baseline | 31.765100 / 32.895900 / 46.929800 | 31.775704 / 32.899072 / 46.969856 | N/A | 31.481091 |
| performance | 14.056443 / 13.858100 / 20.306900 | 14.047213 / 13.843968 / 20.357120 | N/A | 71.141754 |

관측 wall 변화: -55.748785%. FPS 변화: 125.982495%.
Cloud.Total/Cloud.Ray GPU 마커는 0개 유효 표본으로 N/A이다. Release의 Frame GPU를 구름 단독 GPU 시간이라고 표현하면 안 된다. 원자료 summary.frameGpuDurationSanity=false는 Cloud 마커 부재로 교차 검사가 불가능한 상태이며, FrameTiming 전체 GPU 자체가 0이라는 뜻이 아니다.

| 실행 순서 / 프리셋 | wall 평균 ms | Frame GPU 평균 ms | Cloud GPU 평균 ms | FPS | wall / Frame GPU / Cloud GPU 유효 수 |
|---|---:|---:|---:|---:|---|
| redesign-20261002-194104-0-baseline | 38.310424 | 38.313800 | N/A | 26.102557 | 540 / 540 / 0 |
| redesign-20261002-194104-1-performance | 14.124241 | 14.113832 | N/A | 70.800263 | 540 / 540 / 0 |
| redesign-20261002-194104-2-performance | 13.988644 | 13.980594 | N/A | 71.486556 | 540 / 540 / 0 |
| redesign-20261002-194104-3-baseline | 25.219776 | 25.225478 | N/A | 39.651422 | 540 / 539 / 0 |

반복 평균의 범위/평균: baseline 41.210785%, performance 0.964661%.
Release baseline 반복 차이가 크다. 두 baseline의 카메라·프로필·환경·설정·빌드 메타데이터는 동일하며 GPU 클록 평균은 2,737.14→2,727.86MHz로 큰 성능 차이를 설명하지 못한다. adapter-wide GPU 사용률은 거의 100%이나 다른 GPU 작업의 기여를 분리하지 못한다. 따라서 -55.75%는 이 실행 묶음의 관측 결과이고 확정된 재현성 있는 개선율로 주장하지 않는다. 개별 반복 기준 wall 감소는 약 44.53~63.13%이다.

검증: 모든 실행 completed, 읽기 오류 0, 프리셋 내부 정규화 passSettings 동일. 네 실행 모두 실제 포즈 서명 동일. buildGuid/buildManifest/환경/style/sky/ocean/routeCenter/시각/해상도/프레임 수 모두 동일하다. 프리셋 간 비리소스 passSettings 차이는 resolutionScale 0.75→0.5와 distanceQuality.density/lighting false→true뿐이다. GPU 셰이더 역할은 의도대로 preserved-reference-shader→current-scene-shader이다.

## Development

Build GUID: `2618b9db2fc84014956b4b69141f8fde`. 경로 포즈 SHA-256: `ece77496bdc4f86f921f323c37b3a566941099160c2a114ff2a5ee62ea1ad861`.

| 프리셋 | wall 평균 / 중앙 / p95 ms | Frame GPU 평균 / 중앙 / p95 ms | Cloud GPU 평균 / 중앙 / p95 ms | aggregate FPS |
|---|---:|---:|---:|---:|
| baseline | 28.597050 / 28.312050 / 39.917600 | 28.587167 / 28.261376 / 40.002560 | 27.398737 / 27.073024 / 38.816768 | 34.968642 |
| performance | 22.646717 / 22.882800 / 32.277400 | 22.622481 / 22.858752 / 32.142336 | 21.477158 / 21.676544 / 31.002624 | 44.156510 |

관측 wall 변화: -20.807505%. FPS 변화: 26.274592%.
Cloud.Total GPU 변화: -21.612598%.

| 실행 순서 / 프리셋 | wall 평균 ms | Frame GPU 평균 ms | Cloud GPU 평균 ms | FPS | wall / Frame GPU / Cloud GPU 유효 수 |
|---|---:|---:|---:|---:|---|
| redesign-20261002-192421-0-baseline | 28.552777 | 28.549582 | 27.359693 | 35.022863 | 540 / 540 / 540 |
| redesign-20261002-192421-1-baseline | 28.641324 | 28.624753 | 27.437780 | 34.914588 | 540 / 540 / 540 |
| redesign-20261002-193522-0-performance | 22.577328 | 22.563285 | 21.409563 | 44.292221 | 540 / 539 / 540 |
| redesign-20261002-193522-1-performance | 22.716107 | 22.681567 | 21.544753 | 44.021627 | 540 / 540 / 540 |

반복 평균의 범위/평균: baseline 0.309636%, performance 0.612800%.

검증: 모든 실행 completed, 읽기 오류 0, 프리셋 내부 정규화 passSettings 동일. 네 실행 모두 실제 포즈 서명 동일. buildGuid/buildManifest/환경/style/sky/ocean/routeCenter/시각/해상도/프레임 수 모두 동일하다. 프리셋 간 비리소스 passSettings 차이는 resolutionScale 0.75→0.5와 distanceQuality.density/lighting false→true뿐이다. GPU 셰이더 역할은 의도대로 preserved-reference-shader→current-scene-shader이다.

## 해석 제한

- GPU 마커와 FrameTiming 표본은 지연 스트림이므로 개별 카메라 포즈와 일대일 대응한다고 해석하지 않는다.
- 평균/중앙/p95는 원시 유효 표본을 합산해 계산했다. 원자료 C# summary의 중앙값은 하단 중앙 요소여서 짝수 표본에서 Python ordinary median과 약간 다를 수 있다.
- aggregate FPS는 전체 측정 프레임 수 / 전체 wall 시간이며 순간 FPS 평균이 아니다.
- Release와 Development 간 차이를 최적화 효과로 섞지 않는다. GPU 누락 표본은 0으로 채우지 않았다.
- 240FPS(전체 프레임 4.167ms)나 구름 2ms 목표를 달성했다고 판단할 근거는 없다.
