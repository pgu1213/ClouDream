# 최신 확장 Release 빌드 성능

최신 ExtensionRelease 실행파일에서 완료한 baseline/performance ABBA를 원자료로 재계산했다. 이전 Release·Development와 합산하지 않는다.

RTX 4080 / i7-13700K / Unity 6000.6.2f1 / Direct3D12 / FHD 1920×1080. 빌드 UTC: 2026-10-05T13:31:27.9033856Z. Build GUID: af89e978f96b4ea68810fbf9683d316b. Shader hash: c11b76415364624e3b611555c87c8113. Reference hash: b905121bc380295741d634a4b9e163f6.

baseline은 공통 C#/보존 노이즈 위에 보존 셰이더·0.75 해상도·거리 품질 OFF를 적용하고, performance는 현재 셰이더·0.5 해상도·거리 품질 ON을 적용한다. 따라서 아래 개선율에는 품질 설정 변경이 포함된다. 독립 법선 캐시와 원경 표면 옵션은 두 조건 모두 OFF이다. 원래 전체 Player 실행파일과의 비교는 아니다.

**Release에는 유효 Cloud.Total/Cloud.Raymarch GPU 마커가 없으므로 N/A다.** FrameTiming 전체 GPU 또는 wall 시간을 구름 단독 GPU 시간으로 표현하지 않는다. 원시 summary의 Frame GPU duration sanity가 false인 것은 Cloud 마커와의 교차검사가 불가능한 상태이며 Frame GPU가 0이라는 뜻이 아니다.

## 고정 경로

각 실행 3구간×180=540프레임, 조건별 2회=1,080프레임이다. 전체 경로+32프레임 워밍업 뒤 측정했다. 캡처는 계측이 모두 끝난 뒤 별도 단계에서 실행됐다.

| 프리셋 | 프레임 | wall 평균 / 중앙 / p95 ms | Frame GPU 평균 / 중앙 / p95 ms | Cloud GPU | aggregate FPS | Frame GPU 유효 수 |
|---|---:|---:|---:|---:|---:|---|
| baseline | 1080 | 28.775653 / 28.580800 / 40.561300 | 28.779435 / 28.601344 / 40.498176 | N/A | 34.751600 | 1079/1080 |
| performance | 1080 | 14.915812 / 14.590450 / 22.268100 | 14.920784 / 14.560768 / 22.169600 | N/A | 67.042948 | 1080/1080 |

baseline→performance 관측 wall 변화 **-48.165167%**, 전체 Frame GPU 변화 **-48.154702%**, FPS 변화 **92.920463%**. Cloud GPU 개선율은 N/A다.

| 실행 순서 / 프리셋 | 프레임 | wall 평균 ms | Frame GPU 평균 ms | FPS | Frame GPU 유효 수 |
|---|---:|---:|---:|---:|---|
| 0 / baseline | 540 | 28.778749 | 28.802979 | 34.747862 | 539/540 |
| 1 / performance | 540 | 15.065803 | 15.079086 | 66.375488 | 540/540 |
| 2 / performance | 540 | 14.765821 | 14.762481 | 67.723968 | 540/540 |
| 3 / baseline | 540 | 28.772557 | 28.755934 | 34.755340 | 540/540 |

반복 wall 평균의 (최대−최소)/평균: baseline 0.021520%, performance 2.011164%. 두 반복에서 관측한 편차이며 다른 조건의 재현성을 확정하지 않는다.

검증: suite completed, 4/4 실행 completed, 읽기 오류 0개. 공통 빌드·환경·프로필·노이즈·시각 메타데이터 일치: True; 프리셋 내부 설정 반복 일치: True; 경로 검증: True.

실제 포즈 SHA-256: ece77496bdc4f86f921f323c37b3a566941099160c2a114ff2a5ee62ea1ad861.

설정 차이(Unity instanceID 제외): distanceQuality.density False→True, distanceQuality.lighting False→True, resolutionScale 0.75→0.5. 셰이더 역할은 preserved-reference→current로 변경된다.

원자료: D:\Dev\ClouDream\Benchmarks\Redesign\ReleaseExtensionsFixed.

## 실제 시간 비행

각 실행 12초×3구간=36초, 조건별 2회다. 초기 포즈 4초 워밍업 후 720m/s·첫 구간45도/s·바람5·낮 주기1,200초로 이동한다. 날씨는 6초에 변경, 9.6초에 중단·재전환, 21.6초에 원래 상태 복귀를 시작한다. FPS가 달라 포즈 표본은 1:1 같지 않으며 동일 시간 함수로 생성됐는지 검사했다.

| 프리셋 | 프레임 | wall 평균 / 중앙 / p95 ms | Frame GPU 평균 / 중앙 / p95 ms | Cloud GPU | aggregate FPS | Frame GPU 유효 수 |
|---|---:|---:|---:|---:|---:|---|
| baseline | 1663 | 43.326885 / 43.656800 / 64.023600 | 43.285004 / 43.627520 / 63.993856 | N/A | 23.080358 | 1659/1663 |
| performance | 2872 | 25.078920 / 25.299850 / 34.038700 | 25.034609 / 25.234432 / 33.911808 | N/A | 39.874125 | 2863/2872 |

baseline→performance 관측 wall 변화 **-42.116955%**, 전체 Frame GPU 변화 **-42.163321%**, FPS 변화 **72.762163%**. Cloud GPU 개선율은 N/A다.

| 실행 순서 / 프리셋 | 프레임 | wall 평균 ms | Frame GPU 평균 ms | FPS | Frame GPU 유효 수 |
|---|---:|---:|---:|---:|---|
| 0 / baseline | 909 | 39.615785 | 39.560272 | 25.242463 | 905/909 |
| 1 / performance | 1373 | 26.221424 | 26.177084 | 38.136754 | 1367/1373 |
| 2 / performance | 1499 | 24.032451 | 23.990649 | 41.610404 | 1496/1499 |
| 3 / baseline | 754 | 47.800877 | 47.755670 | 20.920118 | 754/754 |

반복 wall 평균의 (최대−최소)/평균: baseline 18.726618%, performance 8.711660%. 두 반복에서 관측한 편차이며 다른 조건의 재현성을 확정하지 않는다.

이 묶음은 반복 편차가 크므로 합산 개선율을 안정된 재현 효과로 주장하지 않는다. 개별 반복 수치를 함께 보존한다.

검증: suite completed, 4/4 실행 completed, 읽기 오류 0개. 공통 빌드·환경·프로필·노이즈·시각 메타데이터 일치: True; 프리셋 내부 설정 반복 일치: True; 경로 검증: True.

설정 차이(Unity instanceID 제외): distanceQuality.density False→True, distanceQuality.lighting False→True, resolutionScale 0.75→0.5. 셰이더 역할은 preserved-reference→current로 변경된다.

원자료: D:\Dev\ClouDream\Benchmarks\Redesign\ReleaseExtensionsFlight.

GPU·FrameTiming 스트림은 지연되므로 개별 포즈와 일대일 대응하지 않는다. N/A를 0으로 바꾸지 않았다. 평균·일반 중앙값·nearest-rank p95를 유효 원시 표본에서 계산하고 FPS는 총 프레임/총 wall 시간으로 계산했다.
