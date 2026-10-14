# 실제 비행 진단 스냅샷 집계

2026-10-05 정리. 실행 원자료는 2026-10-03 KST에 기록된 Development Player 진단 2회다. 아래 비율은 **15프레임마다 읽은 직전 완료 렌더의 스냅샷 안에서 관측된 비율**이다. 전체 비행의 모든 프레임·모든 광선 적중률로 확대 해석하지 않는다.

두 실행 모두 1920×1080 출력, 960×544 구름 버퍼, 구간당 12초, 720m/s 이동, 첫 구간 45도/s 회전, 1200초 설정 낮밤 주기와 바람·날씨 전환을 사용했다. `final`은 상층·운해·차폐 캐시와 이동/시간 기법을 모두 켰고, `balanced`는 해당 세 캐시를 끈 상태다. 캐시 결과 차이와 서로 다른 관측 시각 때문에 동일 자세별 비교가 아니다.

원자 집계와 동기 GPU readback이 켜진 진단 실행이다. 이 문서에서는 실행 시간·FPS·GPU 시간을 성능 대표값으로 사용하지 않는다.

## 집계 범위와 무결성

| 프리셋 | 기록 프레임 | 진단 스냅샷 | 마지막 관측 시각 | 시간 판정 픽셀 관측 합계 |
|---|---:|---:|---:|---:|
| final | 1,524 | 102 | 35.752s | 53,268,480 |
| balanced | 1,201 | 81 | 35.971s | 42,301,440 |

모든 스냅샷에서 캐시 12슬롯·시간 13슬롯 길이, 15프레임 관측 간격, 각 캐시의 `full + blend + exact = queries`, 시간 사유 합계 `960×544 = 522,240`를 검사했다. 모두 일치한다. 첫 관측은 초기 위치 워밍업 후라서 cold start가 아니다.

## 캐시 실제 조회

매 렌더 공유 버퍼를 0으로 초기화한다. 전체 픽셀 격자의 `((x×1973 + y×9277) & 63) == 0`인 1/64 공간 마스크 중 실제로 재적분된 픽셀에서 발생한 함수 조회만 기록한다. 캐시 생성 커널은 제외된다. 한 픽셀에 여러 몸체·법선·조명 조회가 있으므로 **조회 수는 광선 수가 아니다**. 값을 64배 또는 15배로 확장하지 않았다.

`full`은 조회한 필드가 완전히 사용된 경우, `blend`는 필드와 원래 절차 평가를 혼합한 경우, `exact`는 캐시를 쓰지 않고 원래 평가로 복귀한 경우다. `blend`에도 원래 평가 비용이 남는다. full은 계산 경로 분류이며 화면 품질 통과를 뜻하지 않는다.

| 프리셋 | 조회 대상 | 표본 조회 합계 | full | blend | exact |
|---|---|---:|---:|---:|---:|
| final | 상층 몸체 | 44,497,568 | 11,657,436 (26.20%) | 4,773,606 (10.73%) | 28,066,526 (63.07%) |
| final | 차폐 | 4,391,946 | 4,291,400 (97.71%) | 100,546 (2.29%) | 0 (0.00%) |
| final | 운해 몸체 | 127,926,623 | 69,416,174 (54.26%) | 15,077,819 (11.79%) | 43,432,630 (33.95%) |
| balanced | 상층 몸체 | 54,526,588 | 0 (0.00%) | 0 (0.00%) | 54,526,588 (100.00%) |
| balanced | 차폐 | 0 | 0 (해당 없음) | 0 (해당 없음) | 0 (해당 없음) |
| balanced | 운해 몸체 | 118,650,806 | 0 (0.00%) | 0 (0.00%) | 118,650,806 (100.00%) |

balanced의 상층·운해 조회는 모두 exact다. 차폐 캐시 조회 함수 자체는 꺼져 있어서 차폐 query가 0이다. 이것은 절차 차폐 평가가 0회라는 뜻이 아니다. 분모가 0인 차폐 비율은 계산하지 않는다.

## 시간 재사용 판정

매 재투영 렌더에서 13슬롯을 초기화하고 모든 픽셀을 정확히 한 결과에 넣는다. 아래 분모는 관측 스냅샷의 모든 구름 버퍼 픽셀 수 합계다. 사유는 코드의 첫 반환 조건이므로 동시에 실패할 수 있는 모든 원인의 독립 빈도가 아니다. `Scheduled`는 정상적인 4-phase 주기 갱신이다.

| 슬롯 | 판정 | final 표본 픽셀 수 (비율) | balanced 표본 픽셀 수 (비율) |
|---:|---|---:|---:|
| 0 | Reused | 5,185,772 (9.74%) | 4,306,180 (10.18%) |
| 1 | Scheduled | 5,222,400 (9.80%) | 4,308,480 (10.19%) |
| 2 | ShapeInvalid | 32,378,880 (60.78%) | 25,067,520 (59.26%) |
| 3 | LightingChanged | 2,113,026 (3.97%) | 1,724,958 (4.08%) |
| 4 | Offscreen | 136,603 (0.26%) | 255,116 (0.60%) |
| 5 | DepthMismatch | 1,396,139 (2.62%) | 1,083,310 (2.56%) |
| 6 | Foreground | 0 (0.00%) | 0 (0.00%) |
| 7 | CloudBoundary | 367,352 (0.69%) | 312,680 (0.74%) |
| 8 | HistoryAge | 0 (0.00%) | 0 (0.00%) |
| 9 | DepthVariance | 1,588,369 (2.98%) | 1,463,529 (3.46%) |
| 10 | InvalidData | 0 (0.00%) | 0 (0.00%) |
| 11 | EmptyTranslation | 4,754,018 (8.92%) | 3,721,764 (8.80%) |
| 12 | DeformationChanged | 125,921 (0.24%) | 57,903 (0.14%) |

관측된 시간 재사용은 final 9.74%, balanced 10.18%에 그쳤다. `ShapeInvalid`가 각각 60.78%, 59.26%이며, 해당 스냅샷의 상태 문자열도 각각 62회, 48회 `Shape / integration settings changed`로 기록되어 있다. 따라서 이 날씨 전환 경로에서 시간 기법의 낮은 재사용을 바람 변화만의 결과라고 해석할 수 없다. 더 구체적인 설정 필드 원인은 별도 계측이 필요하다.

| 관측 구간 | final 스냅샷 / Reused | balanced 스냅샷 / Reused |
|---|---:|---:|
| 0 (0–12s) | 35 / 13.93% | 27 / 14.58% |
| 1 (12–24s) | 38 / 2.66% | 30 / 3.14% |
| 2 (24–36s) | 29 / 13.95% | 24 / 14.03% |

구간은 readback 호출 시각으로 나눴다. GPU 결과를 특정 자세·날씨 이벤트 프레임과 정확히 동기화한 표가 아니다. `ShapeInvalid`는 첫 렌더/해상도, 형상·적분 설정, 원점, 카메라·투영, 큰 이동·회전, 빠른 바람, 프레임 간격 리셋을 포괄한다. 픽셀 사유만으로 원인별 리셋 횟수를 분리할 수 없다.

`LightingChanged`와 `DeformationChanged`는 조명 입력 변화와 노이즈 좌표 변화의 누적/나이 제한에 따른 재적분이다. `EmptyTranslation`은 빈 이력 픽셀에서 카메라 이동이 생겼을 때 새 구름 출현을 놓치지 않기 위한 재적분이다. 0으로 관측된 사유도 스냅샷 사이 모든 프레임에서 발생하지 않았다는 증거는 아니다.

## 원자료와 코드 근거

전체 합계·구간별 합계·실행 설정·SHA-256·검사 결과는 [Diagnostic-Summary.json](Diagnostic-Summary.json)에 저장했다.

- `Benchmarks/Redesign/DevelopmentDiagnostics/20261002-193750-171-redesign-20261002-193706-0-final-diagnostic-flight.json` — SHA-256 `6e3ab957837bc1d152c0267ed634b7043bdf8b8d38456b2da92a99c18fcfd1b0`
- `Benchmarks/Redesign/DevelopmentDiagnostics/20261002-193830-623-redesign-20261002-193706-1-balanced-diagnostic-flight.json` — SHA-256 `7d8d23d1266aa101848be3cf9f9937abb2584a508a78a165aa6b7b392db9280d`

코드 근거:

- `Assets/LostSkiesClouds/Runtime/CloudBenchmark.cs:642` — 15-frame sampling and both readbacks
- `Assets/LostSkiesClouds/Runtime/CloudBenchmarkReport.cs:73` — 12-slot cache names
- `Assets/LostSkiesClouds/Runtime/CloudShapeFieldCache.cs:149` — per-render zero and diagnostic enable
- `Assets/LostSkiesClouds/Runtime/CloudShapeFieldCache.hlsl:14` — pixel selection and atomics
- `Assets/LostSkiesClouds/Runtime/CloudOceanShapeFieldCache.hlsl:30` — ocean outcomes
- `Assets/LostSkiesClouds/Runtime/CloudSpatialLighting.hlsl:21` — occlusion outcomes
- `Assets/LostSkiesClouds/Runtime/CloudTemporalLightingState.cs:104` — 13-slot enum
- `Assets/LostSkiesClouds/Runtime/CloudTemporalHistory.cs:317` — per-render temporal reset
- `Assets/LostSkiesClouds/Runtime/CloudTemporal.compute:236` — first-return reason priority
- `Assets/LostSkiesClouds/Runtime/CloudTemporal.compute:427` — every-pixel reason partition
