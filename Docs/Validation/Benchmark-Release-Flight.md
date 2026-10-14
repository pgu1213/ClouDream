# Release 실제 시간 비행 벤치마크

4개 실행 모두 completed. Release / RTX 4080 / i7-13700K / Unity 6000.6.2f1 / D3D12 / 1920×1080. Build GUID `3a635c8a2136459587223dd8a19317dd`. 3구간 × 12초 × 2회, baseline→performance→performance→baseline 순서.

| 프리셋 | 측정 프레임 | wall 평균 / 중앙 / p95 ms | Frame GPU 평균 / 중앙 / p95 ms | FPS | Frame GPU 유효 표본 |
|---|---:|---:|---:|---:|---:|
| baseline | 1596 | 45.147377 / 46.494150 / 64.907600 | 45.138668 / 46.452736 / 64.933888 | 22.149681 | 1595/1596 |
| performance | 2351 | 30.633150 / 32.127500 / 40.501600 | 30.595407 / 32.107520 / 40.414208 | 32.644373 | 2342/2351 |

관측 wall 감소 -32.148550%, FPS 증가 47.380785%. Cloud GPU 단독 시간은 유효 표본 0개로 N/A.

| 실행 | frames | wall 평균 ms | Frame GPU 평균 ms | FPS |
|---|---:|---:|---:|---:|
| redesign-20261005-115148-0-baseline-flight | 756 | 47.658684 | 47.667816 | 20.982535 |
| redesign-20261005-115148-1-performance-flight | 1173 | 30.690927 | 30.619374 | 32.582920 |
| redesign-20261005-115148-2-performance-flight | 1178 | 30.575619 | 30.571563 | 32.705797 |
| redesign-20261005-115148-3-baseline-flight | 840 | 42.887202 | 42.865446 | 23.316980 |

반복 평균 범위/평균: baseline 10.539368%, performance 0.376414%. baseline 편차가 존재하므로 pooled 변화율을 확정된 하드웨어 일반 성능으로 확대하지 않는다.

시간 기반 경로 검증: 카메라는 720m/s로 이동하며 첫 구간은 45도/s로 회전한다. 세 구간 사이에는 카메라 컷이 있다. 각 표본의 기록된 simulationTime으로 경로식을 다시 계산했을 때 최대 위치 오차는 0.0017m 이하, quaternion 성분 벡터 오차는 0.000001 이하이다. 실제 routeSeconds와 float simulationTime 차이는 0.000002초 이하, 시각식 오차는 0.000001시간 이하이다. FPS가 다른 조건은 표본 개수와 포즈 시각이 다르므로 고정 포즈별 쌍 비교는 아니다.

dayCycleSeconds=1200이며 측정 시각은 12.00→약12.72시이다. 낮·밤 전체 주기를 측정한 것으로 표현하지 않는다. Wind=5 및 날씨 전환 6초, 중단·재전환 9.6초, 복귀 21.6초는 CloudBenchmarkFlight.Apply/AdvanceWeather의 동일 경과 시간으로 진행한다.

빌드/프로필/style/sky/ocean/초기 환경/경로 중심/시간 주기/화면 해상도/Profiler 설정은 네 실행에서 동일하다. GPU profiler와 일반 Profiler는 꺼져 있다. FrameTiming은 지연 스트림으로 개별 포즈별 GPU 비용을 나타내지 않으며 누락 표본을 0으로 계산하지 않았다. NVML은 전체 어댑터 측정이므로 다른 프로세스 GPU 부하를 분리하지 못한다.

Release 고정 경로 결과와 실제 시간 비행 결과는 다른 작업량이므로 합산하지 않는다. 240FPS 또는 구름 2ms 목표는 달성하지 못했다.
