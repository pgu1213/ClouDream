# 확장 기술 실시간 비행 Development 벤치마크

performance, normal-cache, surface-hybrid를 순방향·역방향 각 2회 측정한 완료 자료이다. baseline은 이 묶음에 없으며 고정 경로·다른 빌드와 합산하지 않는다.

RTX 4080 / i7-13700K / Unity 6000.6.2f1 / Direct3D12 / FHD. Build GUID: fd7fe20c323c47068467657a4bf7b8a6. 빌드 UTC: 2026-10-05T13:12:39.9785722Z. Shader hash: c11b76415364624e3b611555c87c8113.

각 실행은 실제 시간 12초×3구간=36초이며 처음 4초는 별도 워밍업이다. 이동 720m/s, 첫 구간 회전 45도/s, 바람 5, 낮 주기 1,200초(12시→약 12.72시)를 사용한다. 날씨 변경은 6초, 중단·재전환은 9.6초, 원래 상태 복귀는 21.6초에 시작한다.

| 프리셋 | 프레임 | Cloud.Total 평균 / 중앙 / p95 ms | wall 평균 / 중앙 / p95 ms | Frame GPU 평균 / 중앙 / p95 ms | FPS | Cloud / Frame GPU 유효 수 |
|---|---:|---:|---:|---:|---:|---|
| performance | 2933 | 14.619505 / 14.955520 / 23.386112 | 24.560456 / 24.915100 / 33.369500 | 24.499845 / 24.871936 / 33.261568 | 40.715856 | 2933/2933 / 2925/2933 |
| normal-cache | 2228 | 22.396062 / 24.343040 / 32.763904 | 32.328976 / 34.426050 / 42.728200 | 32.291532 / 34.369536 / 42.711296 | 30.932003 | 2228/2228 / 2225/2228 |
| surface-hybrid | 2458 | 19.323824 / 19.973632 / 30.444544 | 29.306415 / 29.874950 / 40.389800 | 29.254256 / 29.831168 / 40.338176 | 34.122222 | 2458/2458 / 2453/2458 |

FPS는 총 프레임/총 wall 시간이다. Cloud.Total은 구름 GPU 범위이며 전체 wall·Frame GPU와 동일하지 않다. 프레임 속도가 다르므로 실제 포즈 표본은 같지 않다. 동일한 시간 함수의 경로를 검증했으며, 평균 GPU 값은 각 실행에서 렌더된 프레임의 표본 평균이다. 같은 포즈 1:1 비교 또는 균일 시간 재표본화 결과로 표현하지 않는다.

| performance 대비 | Cloud.Total 변화 | wall 변화 | FPS 변화 |
|---|---:|---:|---:|
| normal-cache | 53.193024% | 31.630195% | -24.029589% |
| surface-hybrid | 32.178378% | 19.323579% | -16.194267% |

| 실행 순서 / 프리셋 | 프레임 | 측정 wall 초 | Cloud.Total 평균 ms | wall 평균 ms | FPS | Cloud / Frame GPU 유효 수 |
|---|---:|---:|---:|---:|---:|---|
| 0 / performance | 1448 | 36.018714 | 14.917940 | 24.874803 | 40.201324 | 1448/1448 / 1442/1448 |
| 1 / normal-cache | 1111 | 36.027992 | 22.472781 | 32.428436 | 30.837133 | 1111/1111 / 1111/1111 |
| 2 / surface-hybrid | 1192 | 36.014912 | 20.235120 | 30.213852 | 33.097402 | 1192/1192 / 1191/1192 |
| 3 / surface-hybrid | 1266 | 36.020257 | 18.465796 | 28.452020 | 35.146890 | 1266/1266 / 1262/1266 |
| 4 / normal-cache | 1117 | 36.000966 | 22.319754 | 32.230050 | 31.026945 | 1117/1117 / 1114/1117 |
| 5 / performance | 1485 | 36.017103 | 14.328505 | 24.253941 | 41.230412 | 1485/1485 / 1483/1485 |

| 프리셋 | 반복 Cloud 평균 범위/평균 | 반복 wall 평균 범위/평균 |
|---|---:|---:|
| performance | 4.030816% | 2.527487% |
| normal-cache | 0.683272% | 0.613641% |
| surface-hybrid | 9.143575% | 6.006329% |

| 프리셋 / 마커 | 관측 수 | 관측 마커 프레임 평균 / 중앙 / p95 ms | 관측 합/전체 경로 프레임 ms |
|---|---:|---:|---:|
| performance / Cloud.NormalFieldBuild | 0 | N/A / N/A / N/A | N/A |
| performance / Cloud.FarSurfaceBuild | 0 | N/A / N/A / N/A | N/A |
| normal-cache / Cloud.NormalFieldBuild | 2226 | 0.129321 / 0.116736 / 0.297984 | 0.129205 |
| normal-cache / Cloud.FarSurfaceBuild | 0 | N/A / N/A / N/A | N/A |
| surface-hybrid / Cloud.NormalFieldBuild | 0 | N/A / N/A / N/A | N/A |
| surface-hybrid / Cloud.FarSurfaceBuild | 2458 | 1.788386 / 2.036736 / 2.891776 | 1.788386 |

추가 마커 평균은 관측된 마커 프레임의 평균이며 상각 평균이 아니다. 마지막 열은 관측 합을 기록 프레임 수로 나눈 값이고 누락 이벤트를 0으로 가정하지 않는다. Cloud.Total과 하위 마커를 더하지 않는다. N/A는 유효 표본 없음이다.

검증: 6/6 실행 completed, 읽기 오류 0개. 공통 메타데이터 일치: True; 프리셋 내부 설정 반복 일치: True; 전체 Cloud GPU 표본 확보: True; 실제 위치·회전·hour·simulationTime의 경과 시간 경로식 감사: True. 세 구간 모두 존재하고 시간은 단조 증가했다. 개별 최대 오차와 구간별 프레임 수는 JSON에 기록했다.

프리셋 차이:

- performance → normal-cache: useNormalFieldCache False→True.
- performance → surface-hybrid: useFarSurface False→True.

영상·형상 품질 통과 여부는 별도 Normal/Far GPU 검증 자료를 따른다. 두 반복의 평균 변화율은 이 실행 묶음의 관측값이며 다른 조건으로 일반화하지 않는다.

원자료: D:\Dev\ClouDream\Benchmarks\Redesign\DevelopmentExtensionsFlight.
