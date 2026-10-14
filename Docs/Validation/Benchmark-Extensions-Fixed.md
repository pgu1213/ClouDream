# 확장 기술 고정 경로 Development 벤치마크

전체 10회 측정 완료 뒤 원시 프레임과 GPU 스트림을 재계산했다. 이전 날짜·빌드와 합산하지 않는다.

RTX 4080 / i7-13700K / Direct3D12 / Unity 6000.6.2f1 / FHD 1920×1080. 5개 프리셋을 순방향·역방향으로 각 2회 실행했다. 실행당 3구간×180=540프레임, 프리셋당 1,080프레임, 전체 5,400프레임이다.

빌드 UTC: 2026-10-05T13:12:39.9785722Z. Build GUID: fd7fe20c323c47068467657a4bf7b8a6. 현재 셰이더 hash: c11b76415364624e3b611555c87c8113. Reference hash: b905121bc380295741d634a4b9e163f6. 별도 extensionOnly Development 실행파일이며 GPU Profiler 계측 비용이 포함된다.

모든 조건은 동일한 보존 원본 GPU 노이즈 Texture3D를 공유한다. baseline은 보존 셰이더·해상도 0.75·거리 품질 OFF, reference-performance는 보존 셰이더·해상도 0.5·거리 품질 ON이다. performance는 동일 0.5/거리 품질에서 현재 셰이더를 사용한다. normal-cache와 surface-hybrid는 performance에 해당 실험 한 가지만 켠다. 영상·형상 허용 여부는 별도 검증 결과로 판단한다.

| 프리셋 | Cloud.Total 평균 / 중앙 / p95 ms | wall 평균 / 중앙 / p95 ms | Frame GPU 평균 / 중앙 / p95 ms | FPS | Cloud / Frame GPU 유효 수 |
|---|---:|---:|---:|---:|---|
| baseline | 27.155908 / 27.053568 / 38.227968 | 28.320907 / 28.283400 / 39.437400 | 28.300155 / 28.218368 / 39.386112 | 35.309604 | 1080/1080 / 1079/1080 |
| reference-performance | 12.951226 / 12.737536 / 19.808256 | 14.145191 / 13.937150 / 21.082000 | 14.133446 / 13.924352 / 21.210112 | 70.695404 | 1080/1080 / 1079/1080 |
| performance | 14.541760 / 13.928448 / 22.637568 | 15.750024 / 15.126150 / 23.886000 | 15.729181 / 15.124480 / 23.918592 | 63.491969 | 1080/1080 / 1079/1080 |
| normal-cache | 15.436247 / 15.178240 / 23.599104 | 16.636375 / 16.393700 / 24.766100 | 16.629351 / 16.365568 / 24.960000 | 60.109250 | 1080/1080 / 1080/1080 |
| surface-hybrid | 19.684317 / 18.752512 / 31.620096 | 20.908858 / 20.000850 / 33.107500 | 20.846467 / 19.826688 / 33.125376 | 47.826620 | 1080/1080 / 1079/1080 |

평균·일반 중앙값·nearest-rank p95는 유효 원시 표본을 합산한다. FPS는 총 프레임/총 wall 시간이다. Cloud.Total과 하위 GPU 마커는 중복되므로 더하지 않는다. 빈 마커는 N/A이다.

| 비교 | Cloud.Total 변화 | wall 변화 | FPS 변화 | 의미 |
|---|---:|---:|---:|---|
| baseline → reference-performance | -52.307889% | -50.053890% | 100.215793% | 보존 셰이더에서 해상도·거리 품질 변경 |
| reference-performance → performance | 12.280958% | 11.345427% | -10.189396% | 동일 품질에서 현재 셰이더로 변경 |
| baseline → performance | -46.450841% | -44.387291% | 79.815013% | 기준 품질에서 performance로 전체 변경 |
| performance → normal-cache | 6.151159% | 5.627617% | -5.327790% | 법선 결과 캐시 추가 |
| performance → surface-hybrid | 35.364057% | 32.754455% | -24.672961% | 원경 surface/volume 추가 |

시간 변화 음수는 단축, 양수는 증가이다. baseline→performance에는 해상도·거리 품질 변경이 포함된다. 새 두 기술의 단독 추가 효과는 performance를 분모로 비교한다.

| 실행 순서 / 프리셋 | Cloud.Total 평균 ms | wall 평균 ms | Frame GPU 평균 ms | FPS | Cloud / Frame GPU 유효 수 |
|---|---:|---:|---:|---:|---|
| 0 / baseline | 27.253337 | 28.404041 | 28.400432 | 35.206259 | 540/540 / 539/540 |
| 1 / reference-performance | 12.934538 | 14.117920 | 14.092273 | 70.831962 | 540/540 / 540/540 |
| 2 / performance | 15.295577 | 16.526534 | 16.507089 | 60.508757 | 540/540 / 539/540 |
| 3 / normal-cache | 15.657208 | 16.878952 | 16.872064 | 59.245383 | 540/540 / 540/540 |
| 4 / surface-hybrid | 23.994370 | 25.242064 | 25.149523 | 39.616412 | 540/540 / 539/540 |
| 5 / surface-hybrid | 15.374264 | 16.575652 | 16.551379 | 60.329453 | 540/540 / 540/540 |
| 6 / normal-cache | 15.215286 | 16.393797 | 16.386638 | 60.998683 | 540/540 / 540/540 |
| 7 / performance | 13.787944 | 14.973514 | 14.952713 | 66.784593 | 540/540 / 540/540 |
| 8 / reference-performance | 12.967913 | 14.172462 | 14.174696 | 70.559371 | 540/540 / 539/540 |
| 9 / baseline | 27.058479 | 28.237773 | 28.200064 | 35.413558 | 540/540 / 540/540 |

| 프리셋 | 반복 Cloud 평균 범위/평균 | 반복 wall 평균 범위/평균 |
|---|---:|---:|
| baseline | 0.717552% | 0.587084% |
| reference-performance | 0.257696% | 0.385585% |
| performance | 10.367612% | 9.860430% |
| normal-cache | 2.862887% | 2.916235% |
| surface-hybrid | 43.791745% | 41.448521% |

반복 편차는 두 실행 평균의 (최대−최소)/평균이다. 두 번의 반복으로 넓은 조건의 재현성을 확정하지 않는다. performance의 Cloud 반복 편차는 10.367612%, surface-hybrid는 43.791745%이다. 따라서 이 두 조건의 pooled 변화율은 해당 묶음의 관측값이며 안정된 재현 효과로 주장하지 않는다. 이번 경로에서는 두 신규 실험 모두 performance보다 평균 GPU 비용이 증가했다.

| 프리셋 / 추가 GPU 마커 | 관측 수 | 관측 마커 프레임 평균 / 중앙 / p95 ms | 관측 합/전체 경로 프레임 ms |
|---|---:|---:|---:|
| baseline / Cloud.NormalFieldBuild | 0 | N/A / N/A / N/A | N/A |
| baseline / Cloud.FarSurfaceBuild | 0 | N/A / N/A / N/A | N/A |
| reference-performance / Cloud.NormalFieldBuild | 0 | N/A / N/A / N/A | N/A |
| reference-performance / Cloud.FarSurfaceBuild | 0 | N/A / N/A / N/A | N/A |
| performance / Cloud.NormalFieldBuild | 0 | N/A / N/A / N/A | N/A |
| performance / Cloud.FarSurfaceBuild | 0 | N/A / N/A / N/A | N/A |
| normal-cache / Cloud.NormalFieldBuild | 192 | 0.064956 / 0.033792 / 0.331776 | 0.011548 |
| normal-cache / Cloud.FarSurfaceBuild | 0 | N/A / N/A / N/A | N/A |
| surface-hybrid / Cloud.NormalFieldBuild | 0 | N/A / N/A / N/A | N/A |
| surface-hybrid / Cloud.FarSurfaceBuild | 1080 | 2.116173 / 2.220032 / 3.324928 | 2.116173 |

추가 마커 평균은 마커가 관측된 프레임의 평균이다. 한 프레임의 여러 cascade는 합계로 수집한다. 마지막 열은 관측된 합/전체 기록 프레임이며 누락을 0으로 가정한 확정 상각 비용이 아니다. 전체 비용은 Cloud.Total/wall로 판단한다.

| 프리셋 | 법선 필드 bytes 최소..최대 | 원경 표면 bytes 최소..최대 | 표면 상태 |
|---|---:|---:|---|
| baseline | 0..0 | 0..0 | Off |
| reference-performance | 0..0 | 0..0 | Off |
| performance | 0..0 | 0..0 | Off |
| normal-cache | 786432..786432 | 0..0 | Off |
| surface-hybrid | 0..0 | 6266964..6266964 | Rebuilding current-frame surface |

검증: suite completed, 10/10 실행 completed, 읽기 오류 0개. 실제 포즈 일치: True, 서명: ece77496bdc4f86f921f323c37b3a566941099160c2a114ff2a5ee62ea1ad861. 빌드·환경·style·sky·ocean·원점·FOV·정지 시각·노이즈·Profiler 메타데이터 일치: True. 프리셋 내부 설정 반복 일치: True. 전체 Cloud GPU 표본 확보: True.

각 조건은 경로 540프레임+첫 포즈 32프레임을 워밍업한 뒤 측정하고 32프레임 동안 지연 GPU 결과를 배출한다. 확장 전용 Player는 최초 45프레임 준비부터 performance 설정을 사용한다. 진단 GPU readback 스냅샷은 0개이다. GPU/FrameTiming 스트림은 지연되므로 개별 포즈와 일대일 대응하지 않는다.

passSettings 차이(일시적 Unity instanceID 제외):

- baseline → reference-performance: distanceQuality.density False→True, distanceQuality.lighting False→True, resolutionScale 0.75→0.5.
- reference-performance → performance: 품질 설정 차이 없음; 셰이더 역할 변경.
- baseline → performance: distanceQuality.density False→True, distanceQuality.lighting False→True, resolutionScale 0.75→0.5.
- performance → normal-cache: useNormalFieldCache False→True.
- performance → surface-hybrid: useFarSurface False→True.

원자료: D:\Dev\ClouDream\Benchmarks\Redesign\DevelopmentExtensionsFixed. 개별 경로, 구간 wall 통계, build 횟수와 설정 감사 서명은 같은 이름의 JSON에 보존했다.
