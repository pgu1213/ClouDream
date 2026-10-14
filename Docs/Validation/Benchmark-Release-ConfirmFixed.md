# Release 고정 경로 독립 재확인 (2026-10-05)

이 결과는 2026-10-05에 별도 실행한 ReleaseConfirmFixed이다. 이전 ReleaseFixed 결과와 같은 빌드·카메라 경로지만 날짜와 실행 세션이 다르므로 합산하지 않았다. 각 조건은 540프레임씩 2회, 총 1,080프레임. 실행 순서 baseline→performance→performance→baseline.

RTX 4080 / i7-13700K / Unity 6000.6.2f1 / D3D12 / 1920×1080. Build GUID `3a635c8a2136459587223dd8a19317dd`.

| 프리셋 | wall 평균 / 중앙 / p95 ms | Frame GPU 평균 / 중앙 / p95 ms | FPS | 유효 wall / Frame GPU |
|---|---:|---:|---:|---:|
| baseline | 35.508068 / 34.464400 / 54.189800 | 35.498239 / 34.416640 / 54.278144 | 28.162614 | 1080 / 1080 |
| performance | 14.840547 / 14.606650 / 21.648500 | 14.832489 / 14.600192 / 21.511168 | 67.382962 | 1080 / 1080 |

관측 wall 변화 -58.205141%, FPS 변화 139.263878%. Cloud GPU 단독 시간은 Release 마커 표본이 없으므로 N/A.

| 실행 | wall 평균 ms | Frame GPU 평균 ms | FPS | 평균 GPU MHz / °C / W |
|---|---:|---:|---:|---:|
| redesign-20261005-115623-0-baseline | 42.493034 | 42.490194 | 23.533269 | 2744.35 / 63.91 / 192.93 |
| redesign-20261005-115623-1-performance | 14.698859 | 14.692045 | 68.032494 | 2737.50 / 67.00 / 218.08 |
| redesign-20261005-115623-2-performance | 14.982235 | 14.972934 | 66.745715 | 2736.67 / 66.67 / 216.63 |
| redesign-20261005-115623-3-baseline | 28.523102 | 28.506285 | 35.059300 | 2739.38 / 68.06 / 234.50 |

반복 평균의 범위/평균: baseline 39.342977%, performance 1.909476%. 기준선 반복 편차가 계속 존재한다. 각 실행의 GPU·wall 값이 함께 변했고 표본 누락이 원인은 아니다. 원인을 확정하지 않았으므로 58.21%를 보편적이고 안정적인 개선율로 표현하지 않는다.

모든 실행 completed, 읽기 오류 0. 실제 포즈 SHA-256 동일: `ece77496bdc4f86f921f323c37b3a566941099160c2a114ff2a5ee62ea1ad861`. 빌드/환경/style/sky/ocean/시간/화면/경로 중심 모두 동일. 프리셋 내 정규화 설정 동일. 프리셋 간 비리소스 설정 차이는 resolutionScale 0.75→0.5 및 distanceQuality.density/lighting false→true뿐이다. densityStepScale/lightingSpacingScale은 1.5, 깊이 기반 업샘플링은 둘 다 켜져 있다. 기본 performance에서는 새 캐시/temporal/traversal/adaptive가 모두 꺼진 상태다.

GPU 어댑터 사용률은 두 조건 모두 거의 100%이나 NVML은 전체 어댑터 값이다. 다른 GPU 프로세스 부하나 GPU 내부 병목을 분리하지 못한다. Nsight 캡처 없이 occupancy/register spill 차이로 원인을 단정하지 않는다.

FPS는 총 프레임/총 wall 초, 중앙값은 ordinary median, p95는 nearest-rank이다. FrameTiming은 지연 스트림으로 개별 포즈와 일대일 대응하지 않는다. 구름 단독 2ms, 전체 240FPS 달성을 뜻하지 않는다.
