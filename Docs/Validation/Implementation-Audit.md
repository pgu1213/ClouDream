# 구름 최적화 재설계 구현·검증 감사

감사일: 2026-10-05. 첨부 논의(`pasted-text-1.txt`) 전체, 현재 소스, 유효 입력으로 재실행한 법선·표면 GPU 결과, 확장 Development 및 최종 Release·캡처·Play 증거를 대조했다. 이 문서는 새 Unity 실행 결과가 아니라 저장된 실행 증거와 구현을 대조한 감사다. 감사 중 Assets/GPU 상태는 변경하지 않았다. 최종 보고서의 링크와 완료 상태도 대조했다.

**핵심 구조 변경과 후순위로 제안된 독립 법선 저장·원경 표면 혼합까지 실행 가능한 구현과 비교 자료가 있다. 그러나 구현 완료와 기본 채택은 다르다.** 필드·차폐·법선·표면 캐시는 품질 기준을 통과하지 못했고, 시간 재사용·적응 적분은 평균 오차 검사에 통과해도 큰 국소 오차가 남았다. 현재 저장된 `LostSkiesCloudSea.unity`는 해상도 50%, 깊이 업샘플링, 거리별 밀도·조명 간격 1.5배를 채택하고 기존 셀 캐시·지원 범위 검사·압축 타깃을 유지한다. 새 실험 옵션과 진단 옵션은 모두 OFF다. Nsight 내부 병목 조사는 미완료이며, 모든 제안의 성능·품질 채택 조건을 달성했다고 표현하지 않는다.

## 제안과 구현의 대응

| 제안 | 현재 구현과 범위 | 기본 채택 |
|---|---|---|
| 50% 해상도 + 깊이 업샘플링 + 거리 밀도·조명 1.5배 | Scene에 저장됨. 기존 셀 캐시·지원 범위 검사·압축 타깃 유지 | ON |
| 절차식 생성 규칙과 렌더링 결과 데이터 분리 | `CloudFormationProfile` 확장 계약을 통해 지원 여부와 운해 필드 파라미터/경계 제공. `CloudEnvironmentSource` 환경 공급 계약 유지 | 계약 구현 |
| 상층의 필요한 공간만 형태 필드화 | `CloudShapeFieldCache`: 셀당 40³, 최대 128슬롯, RGHalf atlas. 상세/큰 형태를 별도 성분에 보존. 카메라 주변 후보를 선택하고 기존 슬롯은 유지하며 신규 슬롯만 생성. 가까운 영역·범위 밖·미지원 프로필은 원래 계산 | OFF |
| 무한 운해를 필요한 구역만 3D 필드화 | `CloudOceanShapeFieldCache`: 96×48×96 격자 4단계, 64/192/576/1728m 간격, RGBAHalf. 날씨 적용 전 몸체 거리·특성 크기·마스크 저장, 현재 날씨/노이즈 변위를 조회 시 적용. 상층 필드 키워드가 함께 필요 | OFF |
| 법선용 조회부터 연결하고 주 밀도는 별도 비교 | 필드 조회 경로와 `useShapeFieldDensity`를 분리. `CloudBoundaryNormal`의 상세 ±25m 6회 차분, 큰 형태 6회 차분, Concept V2 최대 간격 150m와 혼합 0.68 유지 | OFF |
| 원문 §3.3의 후순위 독립 법선 벡터 저장 | `CloudNormalFieldCache`: 최종 법선 RGB와 유효성 A를 32³×3 ARGBHalf에 저장. 기본 256/1024/4096m 간격. 형태·날씨·바람·노이즈 입력 변경 시 정확 생산 함수로 재생성. 2500m 이내·범위 밖·코너 불일치는 원식. 다른 근사 형태 캐시 활성화가 필수 조건이 아님 | OFF |
| 차폐 캐시 범위와 호출 기준 계측 재설계 | `CloudSpatialLightCache`: 64³×4, 기본 16/80/400/2000m 간격. 기존 태양 7개+하늘 3개 밀도 표본의 차폐를 공유하고 최종 팔레트·시선·법선 색은 현재 프레임에서 계산. 화면 밖은 원래 밀도 계산으로 복귀. 현재는 매 렌더 갱신 | OFF |
| 실제 조회의 캐시 사용·복귀 계측 | 상층/차폐/운해 각각 query/full/blend/exact의 12개 카운터. 1/64 픽셀 표본, 마지막 렌더 결과. blend는 원래 계산도 수행하므로 full과 합쳐 완전 적중률로 표현하면 안 됨 | 진단 때만 ON |
| 시간 이력의 형상·조명 무효화 분리 | 형상 해시와 `CloudTemporalLightingState` 분리. 작은 광원 입력 차이는 제한적으로 재사용, 큰 변화는 RGB를 새로 추적하면서 형상 상태를 별도로 유지. 입력 변화 한계 0.005, 변화 중 최대 이력 나이 2. 완전한 볼륨 재조명은 아님 | OFF |
| 실제 바람·카메라 이동 대응 | 모든 구름을 강체 평행이동시키지 않음. 각 후보 프레임과 누적·반전된 정규화 노이즈 입력을 비교. `0.13 × billowPeriod × 입력차`로 환산한 노이즈 좌표 변화 0.5m 이내, 변화 중 나이 2 이하. 이 수치는 구름 자체의 이동 거리나 출력 오차 상한이 아님 | OFF |
| 재사용 실패 사유·깊이 분산 | 13개 분류(재사용, 예약 갱신, 형상, 조명, 화면 이탈, 깊이, 전경, 경계, 나이, 분산, 무효 값, 빈 광선 이동, 변형). 실제 불투명도 가중 1·2차 깊이 모멘트에서 분산 계산 | OFF |
| 필요한 8×8 타일만 실행 | 갱신이 필요한 타일 목록과 indirect dispatch 구현. 같은 유효성 판정에서 직접/압축 실행의 출력 동등성 검사 | OFF |
| 보수적 점유 격자 이동 | `CloudTraverseEmptyBlocks`가 생산 함수의 지원 범위와 셀 경계로 다음 유효 구간을 찾음. 근사 타원체 필드를 안전한 거리장으로 간주하지 않음. 최대 변형까지 포함하는 기존 지원 범위를 사용 | OFF |
| 빈 공간의 실제 주 루프 도약 | `CloudAdvanceEmptySamples`로 주 적분·밀도·점유 조회는 건너뜀. 다만 원래 float 표본 격자와 마지막 빈 표본을 보존하기 위해 저렴한 스칼라 거리 덧셈은 반복함. O(1) 거리 이동이나 모든 반복 제거로 표현하면 안 됨 | OFF |
| 원경 내부 적분 완화 | 안정된 원경 조밀 내부에서 최대 기본 간격 2배. 추가 확대분에 광학 깊이 0.25 제약. 기존 간격 자체를 항상 0.25 이하로 만드는 규칙은 아님. 가까운 구간·진입 탐색 보존 | OFF |
| 원문 §8의 후순위 원경 표면/볼륨 혼합 | `CloudFarSurface`: 현재 밀도에서 각 축 절반 해상도의 화면 광선 표면을 매 렌더 추출. 기본 첫 진입 12km 이상·연속 불투명 두께 120~1600m만 후보. 소비 광선에서도 기존 밀도 표본·진입·전경·연속성·잔여 반복 예산을 검사. 근접·얇은 층·관통은 볼륨으로 복귀. 내부 밀도 적분은 남기고 반복 조명을 대표 차폐로 근사하며 전체 메시 교체는 아님 | OFF |
| 고정 자세와 실제 시간 비행 분리 | 고정 CloudRoute와 초당 이동·회전 기반 비행, 바람·시간대·날씨 중단/재전환, Development/Release Player 측정·CSV/JSON·캡처 도구 구현 | 측정 도구 |

관련 구현: [CloudRaymarch.compute](../../Assets/LostSkiesClouds/Runtime/CloudRaymarch.compute), [CloudRayIntervals.hlsl](../../Assets/LostSkiesClouds/Runtime/CloudRayIntervals.hlsl), [CloudShapeFieldCache.cs](../../Assets/LostSkiesClouds/Runtime/CloudShapeFieldCache.cs), [CloudOceanShapeFieldCache.cs](../../Assets/LostSkiesClouds/Runtime/CloudOceanShapeFieldCache.cs), [CloudSpatialLightCache.cs](../../Assets/LostSkiesClouds/Runtime/CloudSpatialLightCache.cs), [CloudTemporalHistory.cs](../../Assets/LostSkiesClouds/Runtime/CloudTemporalHistory.cs), [CloudTemporal.compute](../../Assets/LostSkiesClouds/Runtime/CloudTemporal.compute).

추가 구현: [CloudNormalFieldCache.cs](../../Assets/LostSkiesClouds/Runtime/CloudNormalFieldCache.cs), [CloudNormalFieldCache.hlsl](../../Assets/LostSkiesClouds/Runtime/CloudNormalFieldCache.hlsl), [CloudFarSurface.cs](../../Assets/LostSkiesClouds/Runtime/CloudFarSurface.cs), [CloudFarSurface.hlsl](../../Assets/LostSkiesClouds/Runtime/CloudFarSurface.hlsl). 독립 법선은 별도 query/full/blend/exact 4개 계수, 표면은 생성·소비·복귀 20개 계수와 사용 mask를 갖는다. 표면 계수는 해당 렌더 전체 호출이며 1/64 표본 계수와 분모가 다르다.

## 원문의 필수 방향·조건부 대안·남은 범위

| 원문 위치 | 요구 또는 제안 | 현재 판정 |
|---|---|---|
| §2, §7.1 | 기존 유효 최적화 유지, 50%+거리 1.5배 조합 비교 | 구현·저장·Player 비교 완료. 새 캐시를 무조건 기본 ON으로 하라는 제안은 아님 |
| §3.2, §4.2 | 형태 필드의 렌더/조명 분리, 날씨·바람 적용, 차폐 7+3 표본 공유, 화면 밖 차폐 보존 | 구현. 공간 후보와 차폐 조회 여유를 준비하고 저장되지 않은 위치는 원식으로 복귀. 생성·조회·복귀를 포함한 채택 비용·품질은 별도 판정 |
| §3.3 | 우선 차분 입력 공유, 필요하면 최종 법선 저장으로 확장 | 두 경로 모두 구현·검사. 원래 차분·혼합 표현 규칙 유지 |
| §5.1 | 형상과 조명 무효화 분리, 재조명 보조 정보 또는 변화량에 따른 제한 재사용·갱신 | 후자를 구현. 단일 깊이의 정확 볼륨 재조명은 구현하거나 주장하지 않음 |
| §5.2 | 거절 원인 계측 후 근·원경 이력 분리 또는 깊이 분산 검토 | 13개 거절 사유와 실제 깊이 분산 구현. 근·원경 전용의 별도 이력 저장소는 구현하지 않은 조건부 대안 |
| §5.3, §6 | 갱신 타일 압축, 보수적 공간 이동, 얇은 구름/진입 보존 | 구현·선언된 검사 완료. 빈 거리의 스칼라 진행까지 O(1)로 바꾼 것은 아님 |
| §7.2 | Nsight로 레지스터·실행 스레드·메모리 대기·명령 병목 조사 | **미측정인 조사 요구**. 도구 미설치를 실측 근거로 바꾸거나 Unity 마커로 원인을 확정하지 않음 |
| §7.4 | 고정 경로와 실제 시간 비행, Development와 배포 조건 구분 | 도구·Development 고정/비행·최신 Release baseline/performance ABBA 완료. [최신 배포 결과](Benchmark-Release-Extensions.md). 두 실험 옵션 자체의 비용은 별도 Development 자료이며 Release 채택 설정에서는 OFF |
| §8 | 원경 미세 윤곽·내부 적분·느린 차폐 갱신 빈도를 절충 후보로 설정 | 해상도·내부 적분 실험 구현. 차폐 시간적 부분 갱신은 미구현 절충 후보; 현재 매 렌더 갱신. 원문은 특정 부분 갱신 알고리즘을 필수로 지정하지 않음 |
| §8 | 캐시 이후에도 부족하면 먼 불투명 표면과 볼륨 혼합 | 후순위였지만 구현·실제 사용·출력/수명 검사 및 Development 비용 측정까지 진행 |
| §8 시각 자료 | FHD 낮/석양/내부/원경, 급회전·관통·시간 변화 영상, 빌드·설정 대응 | 최신 Release 양쪽 221개 포즈/시각·빌드 대응, 원본 442장 FHD/알파, 게시 정지 PNG 10장 RGB 보존 검사 완료. 216프레임 비행과 GIF 보존. [최신 FHD 증거](Redesign-FHD-Images.json) |

핵심 기술의 실행 경로가 없어서 남은 항목과, 원문에 제시된 대안 중 채택하지 않은 항목을 혼동하지 않는다. 최종 Release·시각·Play 증거는 완료됐다. 남은 실질적 성과 공백은 Nsight 조사와 품질·성능 채택 조건의 미달이다. 2ms·240FPS 달성은 구현 목록의 완성과 별개다.

## 품질 검사와 해석 한계

| 원시 결과 | 확인된 결과 | 통과가 보장하지 않는 것 |
|---|---|---|
| [Redesign-ShapeField.json](Redesign-ShapeField.json) | 계약·안정 재생성·날씨에 의한 불필요한 재생성 방지·환경 복원·원점·근접/범위 밖 fallback 통과. 61셀, 기록 메모리 32,774,688바이트 | `withinSuggestedImageBudget=false`, `lightingOnlyGeometryStrictPassed=false`. 법선/조명 전용 최대 평균 RGB 0.002663, 최대 RGB 0.574829, 최대 투과율 차이 0.002441. 주 밀도 연결 시 최대 투과율 차이 0.901001, 가중 깊이 차이 34,976m. 조명 전용 FP32 검사도 투과율 0.002369/깊이 34.792m 차이가 있어 모두 half 양자화 때문이라고 단정할 수 없음 |
| [Redesign-OceanShapeField.json](Redesign-OceanShapeField.json) | 계약·원점·복원·near/outside fallback 통과. 하층 1,089개 점 모두 점유 유지, 누락 0. snap 경계 밀도 오차 0.000001967. 메모리 14,155,776바이트 | 6개 시점 전부 평균 RGB 예산 0.005 실패(0.006664~0.035921). 최대 투과율 차이 1.0, 가중 깊이 차이 38,272~41,632m. 점유 연속성 통과는 원래 실루엣/밀도/색 보존과 다름 |
| [Redesign-SpatialLight.json](Redesign-SpatialLight.json) | 계약·할당·원점·환경 복원·미지원 fallback 통과. 밀도·범위 밖 차폐·window/snap 차이 0. 격자 정점 최대 차폐 오차 0.00012207. 메모리 8,388,608바이트 | 색 기준은 45조건 중 3조건만 통과. bright-rim, cellSize64에서 HDR RGB 최대 차이 163.3441, cloudy P95 8.3884. 기본 cellSize16 home도 평균 RGB 0.24555로 실패. 차폐 캐시가 형태를 보존해도 명암을 보존했다는 뜻은 아님 |
| [Redesign-Temporal.json](Redesign-Temporal.json) | 264 표본, suite `passed=true`. 안정 할당·해제·카메라 분리·직접/압축 parity·조명 변경 제어·유한 깊이 분산 통과 | 이동은 주로 평균/P95 예산으로 판정. 전체 최대 평균 RGB 0.001177, 최대 P95 0.007080이어도 최대 RGB 0.600586, 투과율 0.897339, 가중 깊이 33,920m, `hitDepth` 차이 11,702m가 남음. 모든 픽셀의 깊이/가림 정확성을 인증한 결과가 아님 |
| [Redesign-Temporal-Wind.json](Redesign-Temporal-Wind.json) | 50 표본, suite `passed=true`. 제한된 바람 재사용·누적 변화 거절·반전·유한 분산 통과 | 최대 평균 RGB 0.001052, P95 0.006348이지만 최대 RGB 0.438324, 투과율 0.683594, 가중 깊이 20,396.5m, `hitDepth` 차이 38,860m. 이 wind 전용 실행의 다른 suite 플래그 false는 미실행 항목이며 실패로 합산하면 안 됨 |
| [Redesign-Traversal.json](Redesign-Traversal.json) | 320×184, 45/45 표본 통과. macro-only의 RGB·투과율·가중 깊이·hitDepth 차이는 전부 0. 세 변형 모두 기존 점유 픽셀 누락 0. 근접 제한·진입 깊이 조건 통과 | adaptive/combined는 평균/P95 예산 통과이며 최악 RGB 6.25, 투과율 0.222290, 가중 깊이 7,424m 차이가 남음. 이미지 동일성으로 표현하면 안 됨 |
| [Redesign-CombinedBindings.json](Redesign-CombinedBindings.json) | 160×96의 조합 실행 결과 RGB·투과율·깊이 차이 0 | JSON에는 비교 결과만 있고 설정/프레임 수/독립 passed 플래그가 없음. 실행 당시 기록된 height+spatial+compact+temporal 조합의 회귀 증거이며 모든 조합의 포괄 검증은 아님 |
| [Redesign-NormalField.json](Redesign-NormalField.json) | 검증된 6종 보존 노이즈, 준비 6/생성기 dispatch 0. 계약 통과, 실제 호출 표본 977=full 34+blend 19+exact 924. 유효 격자 표본 누적 3,919, 최대 정점 성분 오차 0.000488162, near/outside·고정 위치 밀도 오차 0 | 평균 RGB 6/6, 최대 RGB 1/6, 가중 깊이 3/6만 통과. 최대 HDR RGB 0.519043·가중 깊이 32m·법선 각도 8.139334°. `imageBudgetPassed=false`, `geometryStrictPassed=false`. 32m가 half 1 ULP라는 검사는 없음. 표본 full+blend 5.425%는 원식 전체 절감률이 아님 |
| [Redesign-FarSurface.json](Redesign-FarSurface.json) | 검증된 6종 보존 노이즈, 26표본(320×192 25개, 160×96 1개). 유한 값·계수 분할·표면 사용 픽셀의 선언된 깊이 계약 26/26. 생성/소비 모두 0 초과. 총 1,551,360픽셀 중 표면 340,901, 볼륨 복귀 1,210,459. 입력 복원·카메라 격리·OFF/비호환/resize/Dispose 해제 통과 | 엄격한 `fallbackExact` 2/26, 색 예산 6/26. `contractsPassed=false`, 별도 `contractsNumericalBudgetPassed=true`, 최종 두 passed 모두 false. 전체 최대 평균 RGB 0.0421855, P95 0.215698, 최대 RGB 0.782471. fallback의 픽셀별 half 1단계·T/hit 정확 일치·float32 모멘트 예산은 26/26 통과했지만 비트 동일성 실패를 통과로 바꾸지 않음 |

시간 검사에서 **정확성 요구 시나리오**의 lighting alpha는 ARGBHalf이고, 픽셀별 인접한 유한 비음수 half 인코딩 1단계까지 허용한다. 이는 일률적인 8m 허용 오차가 아니다. 15km 부근에서는 1단계가 8m가 될 수 있으며, 해당 검사에서는 RGB·투과율·hitDepth의 원래 엄격 조건도 적용했다. 반면 움직임·바람의 근사 허용 시나리오 전체에 이 1단계 보장이 적용되는 것은 아니다. 위 수만 m의 최악 오차를 half 정밀도만으로 설명하면 안 된다.

출력 이름 `hitDepth`는 원래 구현의 **투과율이 0.99보다 큰 동안 기록한 점유 표본 위치**다. 첫 기하학적 교차점을 엄밀하게 나타내는 값이 아니다. `lighting.a`는 불투명도 가중 깊이다. 검사 보고서의 `maximumFirstHit`/`maximumDepthMoment`를 각각 이 출력 계약에 맞게 해석해야 한다. 큰 최악 깊이 차이는 특정 픽셀의 재투영·업샘플링 위험을 의미하며, 평균 색 오차가 작다는 이유로 숨길 수 없다.

필드 보간 오차에는 현재 격자 간격이 큰 영향 요인으로 의심된다. 상층 40³와 운해의 수백~천 m 격자는 원래 25m 차분·얇은 경계보다 거칠다. 다만 감사만으로 모든 오차의 원인을 하나로 확정하지 않는다. 현재 판정은 원인 가설과 관계없이 품질 실패·기본 OFF다.

법선은 최대 RGB와 가중 깊이의 실패를 분리하며, 표면 혼합은 strict 실패와 별도 수치 예산 통과를 분리한다. 표면의 16m 가중 깊이 차이는 해당 검사에서 픽셀별 half 인접 간격 1개 이내임을 확인했지만, 법선의 32m에 이 결론을 옮기면 안 된다. 표면 fallback의 깊이 분산 최대 차이 1,024.908203125는 m²이며 깊이 오차 m가 아니다. 일부 표면 검사 시나리오는 의도적으로 사용 픽셀 0인 복귀 검사이므로 “26개 모두 표면 사용”이라고 쓰지 않는다. 상세 기준은 [법선 분석](Redesign-NormalField-Summary.md)과 [표면 분석](Redesign-FarSurface-Summary.md)을 따른다.

## 작업량과 안전 검사

[Redesign-Traversal-WorkCounts.json](Redesign-Traversal-WorkCounts.json)은 18개 고정 자세×4변형, 160×96의 72표본이다. GPU 계측 작업 수이며 ms가 아니다.

| 변형 | 주 루프 횟수 | 주 밀도 조회 | 빈 구간 도약 | 점유 적분 표본 |
|---|---:|---:|---:|---:|
| baseline | 123,743,772 | 123,743,772 | 0 | 7,777,618 |
| macro-traversal | 62,890,012 | 62,681,485 | 208,527 | 7,777,618 |
| adaptive-integration | 122,042,453 | 122,042,453 | 0 | 6,076,912 |
| combined | 61,188,838 | 60,980,311 | 208,527 | 6,076,912 |

이 계수는 빈 구간의 스칼라 거리 진행, 법선·차폐·경계 이분 탐색의 밀도 조회를 포함하지 않는다. 주 밀도 조회 감소율을 전체 GPU 시간 절감률로 바꿀 수 없다.

- [Redesign-Sky.json](Redesign-Sky.json): 65,536점, 점유 셀 10개. 시드별 분포, 빈 셀, 경계, 원점 이동, 날씨 중단 전환 검사 통과.
- [Redesign-Gpu.json](Redesign-Gpu.json): 반복 4프레임 최대 차이 0, 노이즈 비유한 값 0, `passed=true`. 저장된 MCP 반환값이며 장시간 GPU 스트레스 인증은 아님.
- [Redesign-Lighting.json](Redesign-Lighting.json): 유한 GPU 출력, 형상 보존, 색/주변광 반응, 날씨 및 시간 전환 중단·재전환, 상태 소유권/복원 통과.
- [Redesign-Night.json](Redesign-Night.json): 낮밤 상태와 달빛 방향, 중단/재전환, 기존 프로필 보존, 외부 광원 복원, 날씨 밀도 보존 통과.
- [CloudNoise-Bake.json](CloudNoise-Bake.json): 기존 직렬화 GPU 생성기의 출력에서 만든 6개 텍스처의 모든 RGBAHalf mip를 보존하고 GPU 왕복/해시 동일성 통과. 수작업으로 원본 HLSL을 복원했다는 의미가 아님.
- [CloudNoise-PreservedValidation.json](CloudNoise-PreservedValidation.json): 현재 Resources 입력 6종·43 mip·45,837,744바이트를 GPU에서 읽어 원래 베이크 명세의 모든 mip SHA 일치, 비유한 half 채널 0, 각 기본 체적의 공간 변화 확인. Concept sculpt 입력도 포함한다. 생성기 dispatch·재베이크 없이 검사했으며 성능 실행에서 제외한다.
- [Redesign-Extensions-Sky.json](Redesign-Extensions-Sky.json), [Redesign-Extensions-Lighting.json](Redesign-Extensions-Lighting.json), [Redesign-Extensions-Gpu.json](Redesign-Extensions-Gpu.json): 입력 경로 변경 후 상층 65,536점·시드·경계·원점, 날씨/조명 중단·재전환·복원, 반복 4프레임 GPU 검사 통과. 마지막 파일의 기본 5종 노이즈 검사를 6종 전체 입력 검증으로 대신 사용하지 않는다.

초기 노이즈 생성 오류나 입력 유효성이 불확실했던 Normal/Far 실행은 제외하고 보존 입력을 사용한 재실행만 채택했다. 앞선 형태·시간·탐색 검사 자료는 해당 실행의 증거로 유지하며, 새 공통 회귀 검사만으로 모든 이전 실험 조합을 같은 빌드에서 재실행했다고 표현하지 않는다.

## 추가 구현의 완료된 Development 비용

[고정 경로](Benchmark-Extensions-Fixed.md)와 [실제 시간 비행](Benchmark-Extensions-Flight.md)은 같은 확장 전용 Development 빌드 `fd7fe20c323c47068467657a4bf7b8a6`의 완료 자료다. RTX 4080 / i7-13700K / FHD / D3D12이며 GPU Profiler 비용을 포함한다. 이전 날짜나 다른 빌드와 합산하지 않는다. 다음은 `Cloud.Total` 표본 평균이며 하위 생성 마커를 다시 더하지 않는다.

| 비교 조건 | 고정 경로 평균 ms | 실제 시간 비행 평균 ms |
|---|---:|---:|
| baseline: 동결 셰이더, 75%, 거리 품질 OFF | 27.155908 | 이 묶음에서 미측정 |
| reference-performance: 동결 셰이더, 50%, 거리 품질 ON | 12.951226 | 이 묶음에서 미측정 |
| performance: 현재 셰이더, 같은 50%/거리 품질 | 14.541760 | 14.619505 |
| normal-cache: performance+독립 법선 | 15.436247 | 22.396062 |
| surface-hybrid: performance+원경 표면 | 19.684317 | 19.323824 |

고정 경로는 각 조건 540프레임×2회이며 포즈 서명이 같고 전체 Cloud GPU 표본이 확보됐다. 두 실험을 각각 추가하면 관측 Cloud 비용은 +6.15%/+35.36%였다. 다만 performance 반복 평균의 범위/평균은 10.37%, surface-hybrid는 43.79%여서 안정된 증가율로 일반화하지 않는다. 동일 품질의 동결 셰이더 대비 현재 셰이더도 이 묶음에서 평균 +12.28%였으므로, baseline→performance의 총 절감을 신규 구조만의 성과로 표현하면 안 된다.

비행은 각 조건 36초×2회이며 720m/s 이동·바람·시간대·날씨 중단/재전환이 있는 동일 **시간 함수 경로**다. FPS가 달라 실제 포즈 표본은 일대일 같지 않다. performance에 비해 법선 +53.19%, 표면 +32.18%의 Cloud 비용 증가가 관측됐다. 두 추가 기술 모두 이 실행에서 성능 이득을 입증하지 못했으며 품질 실패와 함께 기본 OFF 판단을 지지한다. 두 번의 반복을 더 넓은 조건의 재현성 보증으로 사용하지 않는다.

고정 경로 법선 생성 마커는 192/1,080프레임에서 관측됐고 관측 프레임 평균 0.064956ms다. 이를 모든 프레임의 생성 비용이나 새 기술 전체 비용으로 표현하지 않는다. 법선 0.75MiB와 진단 OFF 표면 6,266,964바이트도 각각의 추가 자원 값이며 렌더러 전체 VRAM은 아니다. GPU 품질 readback 검사와 별도 완료된 Player 비용 측정을 구분하며 성능 수치는 위 두 보고서로 확인한다.

[확장 비행 진단](Diagnostic-Extensions-Summary.md)은 성능 실행과 분리된 readback 자료다. 법선은 관측 query 366,405회 중 full 11,481회(3.1334%), blend 2,461회(0.6717%), exact 352,463회(96.1949%)였다. 표면 승인은 최초 진입 시도 20,847,317회 중 1,194,474회(5.7296%)이며, 관측 출력 픽셀 36,556,800개 대비로는 3.2674%다. 생성 성공률·소비 승인률·전체 화면 적용률의 분모를 섞지 않는다. 기록 프레임 간 법선 생성 누계는 매번 +3 cascade, 표면은 매번 +1 build였다. 이는 생성이 계속된다는 근거지만 개별 GPU 비용이나 바람·날씨 각각의 무효화 기여율을 직접 설명하지 않는다.

Nsight 레지스터·occupancy·메모리 stall·spill·명령 병목은 계속 미측정이다. Unity GPU marker, FrameTiming, 작업 카운터나 생성 ms만으로 캐시 손해의 GPU 내부 원인을 확정하지 않는다.

## 최종 Release·시각·Play 증거

[최신 Release](Benchmark-Release-Extensions.md)는 빌드 `af89e978f96b4ea68810fbf9683d316b`에서 baseline→performance→performance→baseline 순서로 완료했다. 고정 경로의 전체 프레임 평균은 28.775653→14.915812ms(관측 −48.165167%), 처리율은 34.751600→67.042948FPS다. 두 반복 평균의 범위/평균은 0.021520%/2.011164%였다. 실제 시간 비행은 43.326885→25.078920ms, 23.080358→39.874125FPS였지만 반복 범위/평균이 18.726618%/8.711660%여서 효과 크기를 일반화하지 않는다. Release Cloud GPU 마커는 N/A이며 전체 프레임 비용을 구름 단독 ms로 바꾸지 않는다. 해상도·거리 품질 변경을 포함하고 새 실험 옵션은 OFF다.

[FHD 검사](Redesign-FHD-Images.json)는 같은 최신 빌드의 두 묶음에서 221개 캡처 메타데이터가 일치하고, 원본 442장이 1920×1080·알파 255이며, 게시 정지 PNG 열 장의 RGB 바이트가 원본과 같음을 기록한다. 각 묶음은 정지 5장과 18초 고정 시뮬레이션 시각의 12fps 비행 216프레임이다. 게시 GIF도 파일을 읽어 640×360, 104프레임, 18,000ms임을 확인했다. 이는 성능 측정 FPS가 아니다. 캡처 manifest는 포즈·시각·기록 설정을 대조하며 프레임별 날씨/환경 내부 상태까지 독립적으로 직렬화·검증하지는 않는다. RGB 차이 결과에도 자동 육안 품질 통과 임계값은 없다.

[최종 입력 검사](Redesign-FinalPlay-Input.json)는 키보드 전진 6,903.18017578125m와 초기 위치 복귀를 통과했다. 이 거리는 고정 시간 이동 속도나 성능 지표가 아니다. [최종 Editor 상태](Redesign-Final-Editor-State.json)는 Play 종료·timeScale 1·renderInEditor ON·장면 dirty false·해상도 0.5·법선/표면/시간 재투영 OFF·콘솔 오류 0건을 기록한다. 최신 Player 이후의 소스 변경은 렌더러 설명 주석 두 곳이며 실행 동작은 바뀌지 않았다. 저장된 [최종 Play 화면](../CloudRedesignEvidence/Final-Editor-Play.png)과 함께 완료 증거로 연결했다.

## 코드 계약·수명 정적 검토

변경 줄과 새 C#/HLSL/compute 51개 파일을 검색하고 주요 캐시·이력·렌더러의 할당/해제 경로를 읽었다. 새 삼항 연산자나 람다, 명백한 새 함수 설명 주석 누락은 찾지 못했다. 동결한 비교용 셰이더는 신규 설계 소스와 구분했다. 이 정적 검토를 새 컴파일/Play 검증이라고 표현하지 않는다.

`CloudFormationProfile` 기본 구현은 미지원 계약을 false로 반환하며 Concept V2 지원과 운해 경계/파라미터를 실제 override로 제공한다. 렌더러가 지원 여부를 확인하여 필드를 활성화한다. 상층 필드/운해 체적은 OFF 시 큰 리소스를 반환하고, 렌더러 Dispose가 두 캐시와 기존 높이/차폐 캐시 및 카메라 타깃을 해제한다. 시간 이력 Dispose는 두 프레임의 깊이 모멘트와 타일/indirect/진단 버퍼를 포함한다. 비활성 바인딩용 작은 운해 1³ fallback 텍스처와 캐시 진단 버퍼는 렌더러 수명까지 유지될 수 있으므로 OFF가 모든 GPU 바이트 0이라는 뜻은 아니다. 즉시 수정해야 할 명백한 수명/프로필 계약 누락은 이 검토에서 찾지 못했다.

독립 법선의 capability는 기본 false·Concept V2 override이며 형태 필드 ON에 의존하지 않는다. 생산 커널은 원래 차분 규칙을 유지하면서 다른 근사 필드를 우회한다. 생성 커널의 정확 생산 모드, 일반 법선 OFF 경로, probe/ON 경로를 컴파일 분기로 구분해 일반 OFF 변형에 mutable 정확 생산 모드를 강제하지 않는다. 이 구조 확인을 레지스터 감소나 셰이더 컴파일 시간 개선의 실측으로 바꾸지 않는다. 큰 텍스처는 OFF 해제, 작은 fallback·계수는 renderer Dispose 해제다.

표면 자원은 카메라별 `CloudRenderTargets`가 소유하며 OFF/비호환 시 비활성 카메라를 포함해 해제한다. 현재 지원 조합은 Concept V2이고 상층/운해 형태 필드·공간 차폐·높이 캐시·적응 적분·독립 법선과 동시 활성화하지 않는다. 이 경우 볼륨으로 복귀한다. 따라서 UI에서 모든 체크를 켠 것을 “모든 근사를 동시에 실행한 최종 렌더러”라고 보고하면 안 된다. 이는 원래 밀도와 깊이 계약을 분리해 비교하기 위한 명시적 지원 범위다.

노이즈는 `CloudNoiseLibrary`의 공용 Resources 자산을 요청하며 개별 렌더러가 파괴하지 않는다. 기존 생성기의 GPU 출력을 보존한 자산이므로 수작업 복원본을 원본 HLSL이라고 부르지 않는다. 전용 벤치마크의 `CloudBenchmarkComputeStripper`는 명시적인 Enabled/ExtensionOnly 모드에서만 조합을 제한한다. 확장 전용 실행파일을 모든 UI 옵션이 포함된 일반 배포 빌드라고 표현하지 않는다.

## 성능·시각 자료를 보고할 때의 제한

- 기준선은 `05cf3ff`의 프로젝트 GPU 셰이더를 동결하고 새 Player 호환 노이즈 공급 및 공통 C# 기반 위에서 실행한 비교다. 완전히 수정 전인 전체 Player라고 표현하지 않는다. 처음 생성된 비정상/빈 노이즈 Player는 유효 기준선으로 사용하지 않는다.
- 프레임 번호 기반 고정 자세와 실제 시간 기반 비행을 분리하고, Development/Release 및 프레임 수·시간대 진행 조건을 섞지 않는다. 비행은 동일 초당 경로여도 빠른 설정이 다른 시각에 더 많은 프레임을 표본화하므로 픽셀별 같은 프레임 쌍은 아니다.
- 시간대는 프로필의 시작/끝으로 제한된다. 현재 12~22시 범위를 24시간 주기로 표기하면 안 된다. 이전 캡처의 `Night`는 요청 0시가 12시로 제한된 오류가 있었으므로 유효 야간 증거에서 제외하고, 수정된 22시 캡처만 채택한다.
- 진단 카운터의 동기 GPU readback 비용을 포함한 실행은 성능 판정 자료와 분리한다. 형태·차폐·법선 캐시 계수는 1/64 픽셀의 마지막 렌더 표본이며 경로 전체의 절대 호출 수가 아니다. 표면 계수는 해당 스냅샷 렌더의 전체 생성/소비 호출이다. 둘 다 15프레임 간격의 스냅샷을 경로 전체의 호출 수로 바꾸지 않는다.
- 확장 진단 normal-cache/surface-hybrid는 시간 재투영 OFF다. 시간 계수 0을 활성화된 재사용률 0%라고 해석하지 않는다. 생성 누계에는 warmup과 이전 조건이 포함될 수 있다. 누계가 남아 있으나 증가하지 않는 것을 현재 조건의 재생성으로 세지 않는다.
- 메모리 bytes와 주 출력 allocation이 안정적이라는 것만으로 개별 캐시의 무재할당을 입증하지 않는다. 개별 수명 검사의 계약 결과와 Player 진단에서 실제 기록한 필드를 구별한다.
- 낮은 해상도의 자동 품질 검사는 FHD 전체 시퀀스 육안 평가를 대체하지 못한다. FHD 캡처와 비행 시퀀스의 존재·설정·빌드 대응을 최종 보고서에서 별도로 확인해야 한다.
- 반복 측정의 큰 편차와 계측 누락을 숨기지 않는다. GPU marker가 없는 Release 결과는 전체 프레임/FrameTiming GPU로 보고하며 Cloud.Total 0ms로 채우지 않는다.
- 240FPS 전체 4.17ms와 구름 2ms는 목표이며, 이번 검사의 passed나 작업량 감소만으로 달성을 선언할 수 없다.

