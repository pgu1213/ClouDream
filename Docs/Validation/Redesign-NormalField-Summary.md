# 독립 법선 벡터 캐시 GPU 검사 결과

검토일: 2026-10-05. 저장된 JSON과 현재 소스를 대조한 분석이며, 이 문서를 작성하면서 Unity를 새로 실행하지 않았다.

**검증된 6종 노이즈 입력으로 다시 실행한 GPU 검사에서 계약은 통과했지만 색·가중 깊이 예산은 실패했다.** 캐시 사용은 실제 호출 표본에서 확인됐다. 전체 평균 RGB 오차는 0.000918380으로 작지만 최대 HDR RGB 차이는 0.519042969, 최대 가중 깊이 차이는 32m다. 실험 옵션은 기본 OFF를 유지한다. 이 GPU 검사 자체는 구현과 품질을 확인하며 Player 성능을 측정하지 않는다. 별도로 완료한 Development Player 성능은 아래에 덧붙였다.

## 원자료와 입력 검증 범위

| 자료 | 이 문서가 읽은 상태 | 해석 |
|---|---|---|
| [Redesign-NormalField.json](Redesign-NormalField.json) | SHA-256 `ba07080233f2f73ffd3a6bc64de6bcce97bc74ce845f98816cf049a57993c055` | `completed=true`, `noiseLibraryReady=true`, `contractsPassed=true`, `actualCachedCallsObserved=true`. 첫 렌더 후 보존 노이즈 준비 6회, 생성기 dispatch 0회, 보존 입력 출처 기록 |
| [CloudNoise-PreservedValidation.json](CloudNoise-PreservedValidation.json) | SHA-256 `d81edce11b5ea064ef69bdcd92c6501a14550a9bd43d92686a25d1b153af029f` | 6종 텍스처·43개 mip·45,837,744바이트의 GPU 읽기 검증. 각 mip 해시가 베이크 명세와 일치, 비유한 half 채널 0, 모든 기본 체적에서 공간 변화 확인 |
| [Redesign-Extensions-Gpu.json](Redesign-Extensions-Gpu.json) | SHA-256 `07263904bf15ca1f1de4e10d988946210fa422d3a3f157e1b489f4ba4db8444a` | 보조 기록: 기본 5종 노이즈와 반복 4프레임만 확인. 법선 캐시 전용 인증이나 6번째 Concept sculpt 입력 검사의 근거로 사용하지 않음 |

실행 환경은 Unity 6000.6.2f1 / Direct3D12다. 법선 검사 렌더러와 Player는 기존 직렬화 생성기의 GPU 출력을 보존한 동일 Resources Texture3D RGBAHalf 자산과 원래 mip를 사용한다. 기본 5종뿐 아니라 Concept sculpt용 `WORLEY3D_128_4_1_0.5`도 확인했다. 이는 원본 HLSL을 복원했다는 의미가 아니다. 베이크 명세는 [CloudNoise-Bake.json](CloudNoise-Bake.json)이며, 보존 입력 검사는 다시 베이크하거나 원래 생성기를 dispatch하지 않았다.

이전 생성 오류가 있었던 실행은 유효 자료에서 제외했다. 이전과 수치가 같다는 사실을 근거로 채택한 것이 아니라, 새 실행의 보존 입력 6회 준비·생성기 dispatch 0회 기록과 6종 전 mip GPU 해시·유한성 검증을 근거로 채택했다. 재실행 후 Unity MCP 콘솔 오류 조회 결과도 0건이었다. 아래 수치와 위 해시는 이 유효 재실행 결과를 가리킨다.

## 구현·옵션·입력 계약

| 항목 | 현재 구현 |
|---|---|
| 저장 대상 | 형태 스칼라가 아닌 최종 법선 벡터. ARGBHalf의 RGB=단위 법선, A=유효성 |
| 원래 법선 규칙 | 상세 밀도의 ±25m 6회 차분과 Concept 큰 형태의 6회 차분. 프로필의 간격·혼합 규칙 유지; Concept V2의 최대 간격 150m·혼합 0.68 유지 |
| 생성 입력 | 현재 형태·날씨·바람·노이즈. 생산 단계는 상층/운해/높이/셀 근사 캐시를 우회하여 원래 생산 함수를 사용. 이번 실행은 검증된 보존 노이즈 6종을 준비하고 생성기 dispatch 없이 사용 |
| 독립 옵션 | `useNormalFieldCache=false`. 상층 형태 캐시를 켜야 작동하는 종속 관계가 없음. 프로필의 `SupportsNormalFieldCache`가 지원 여부를 결정 |
| 기본 조절값 | `normalFieldCellSize=256`, `normalFieldExactNearDistance=2500`, `normalFieldMinimumCoherence=0.995`, `useNormalFieldDiagnostics=false` |
| 공간 배치 | 32³ 격자 3개를 Z 방향 atlas에 저장. 기본 간격 256/1024/4096m. 4셀 단위 월드 정렬 창 |
| 조회·복귀 | 2500m 이내 원식, 기본 2500~3750m 전환. 범위 밖·무효 코너·방향 불일치에서는 원식. 8개 코너 방향과 평균 방향의 내적 문턱은 내부 보간 오차의 수학적 상한이 아님 |
| 재생성 | 형태/날씨/노이즈 준비 상태 또는 바람 입력이 달라지면 3개 격자 재생성. 창만 이동하면 해당 격자 재생성. 조명 색 변경은 재생성하지 않음 |
| 시간 이력 | 모드·간격·근접 거리·방향 문턱 변경을 시간 이력 해시에 포함 |
| 메모리 | 필드 텍스처 786,432바이트(0.75MiB). 보고된 `bytes`는 16바이트 계수 버퍼, 1³ half fallback의 명목 8바이트, 플랫폼 할당 부가 비용을 제외 |
| 수명 | OFF/미지원 시 큰 텍스처 반환. 작은 fallback·진단 버퍼는 렌더러 Dispose까지 유지 가능 |

관련 소스: [CloudNormalFieldCache.cs](../../Assets/LostSkiesClouds/Runtime/CloudNormalFieldCache.cs), [CloudNormalFieldCache.hlsl](../../Assets/LostSkiesClouds/Runtime/CloudNormalFieldCache.hlsl), [CloudRaymarch.compute](../../Assets/LostSkiesClouds/Runtime/CloudRaymarch.compute), [LostSkiesCloudPass.cs](../../Assets/LostSkiesClouds/Runtime/LostSkiesCloudPass.cs), [CloudNormalFieldValidation.cs](../../Assets/LostSkiesClouds/Editor/CloudNormalFieldValidation.cs).

## 실제 호출 계수

계수 배열은 `[query, full, blend, exact]`다. 화면 픽셀의 1/64 마스크에서 발생한 **실제 법선 함수 호출**을 세며, 픽셀 수나 모든 광선 호출 수가 아니다. 각 시점의 후보 렌더 직후 값을 합산했다. `query=full+blend+exact`는 6개 시점 모두 성립한다.

| 시점 | query | full | blend | exact | 누적 격자 생성 수 |
|---|---:|---:|---:|---:|---:|
| home | 190 | 14 | 6 | 170 | 3 |
| horizon | 171 | 2 | 2 | 167 | 6 |
| inside | 194 | 8 | 9 | 177 | 9 |
| negative-cell | 175 | 4 | 0 | 171 | 12 |
| max-displacement | 158 | 6 | 2 | 150 | 15 |
| bright-rim | 89 | 0 | 0 | 89 | 18 |
| 합계 | **977** | **34** | **19** | **924** | — |

실측 표본에서 full 3.480%, blend 1.945%, exact 94.575%다. full+blend 5.425%는 캐시가 일부라도 사용된 호출 비율이며, 완전한 원식 계산 절감률이 아니다. blend는 원식도 계산한다. 경로 전체나 모든 픽셀의 적중률로 확대 해석하지 않는다.

다른 필드를 함께 켠 별도 렌더의 `lastCombinedCalls=[181,9,5,167]`은 위 분모 977에 포함하지 않는다. 누적 생성 수는 격자 단위이며 프레임 수나 ms가 아니다. `allocations=6`은 검사에서 시점마다 OFF→ON으로 비교한 결과이며, 그 값만으로 매 프레임 재할당이나 누수를 뜻하지 않는다.

## 색과 깊이 예산

6개 시점 각각 160×96(15,360픽셀), 총 92,160픽셀을 비교했다. HDR RGB 오차는 톤 매핑 후 화면 색 오차와 같지 않다. 색 예산은 평균 RGB ≤0.005와 최대 RGB ≤0.15, 별도 엄격 출력 예산은 최대 투과율 차이 ≤0.0001과 최대 불투명도 가중 깊이 차이 ≤0.1m다.

| 시점 | 평균 RGB | 최대 HDR RGB | 최대 투과율 차이 | 최대 가중 깊이 차이(m) | 색 예산 | 엄격 출력 예산 |
|---|---:|---:|---:|---:|---|---|
| home | 0.001792892 | 0.322265625 | 0 | 0 | 실패 | 통과 |
| horizon | 0.000340196 | 0.213867188 | 0 | 0 | 실패 | 통과 |
| inside | 0.001422071 | 0.519042969 | 0.000015259 | 2 | 실패 | 실패 |
| negative-cell | 0.000508009 | 0.275634766 | 0 | 16 | 실패 | 실패 |
| max-displacement | 0.001429776 | 0.446777344 | 0.000030518 | 32 | 실패 | 실패 |
| bright-rim | 0.000017333 | 0.125000000 | 0.000015259 | 0 | 통과 | 통과 |

전체 픽셀 가중 평균 RGB는 0.000918380이다. 평균 RGB 기준은 6/6이지만 최대 RGB 기준은 1/6이다. 투과율 기준은 6/6, 가중 깊이 기준은 3/6이다. 이에 따라 JSON의 `imageBudgetPassed=false`, `geometryStrictPassed=false`는 별개 실패다. 평균 오차만으로 품질을 통과시킬 수 없다.

유일하게 색 기준을 통과한 bright-rim은 표본 계수에서 full/blend가 0이다. 이는 화면 전체 캐시 사용이 0임을 뜻하지 않지만, 관찰된 캐시 사용과 품질 통과를 함께 입증한 시점으로 셀 수는 없다.

`maximumDepthError`는 `lighting.a`의 **불투명도 가중 깊이**이며 기하학적 표면 이동이나 첫 교차점 오차가 아니다. half 출력 양자화 가능성은 추가 조사할 수 있지만, 이 실행은 픽셀별 half 인코딩 인접 1단계(1 ULP) 여부나 FP32 대조를 확인하지 않았다. 따라서 32m를 1 ULP라고 단정하거나, half 정밀도만의 차이 또는 형태 완전 보존이라고 결론내리지 않는다.

## 계약 검사의 기록과 한계

유효 재실행 JSON에는 안정 입력 재사용, 조명 변경 시 재생성 생략, 날씨·바람·반전 재생성, 원점 보존, 창 이동, OFF 해제, 미지원 복귀, 다른 필드 우회가 모두 true로 기록돼 있다. 계약 통과와 화면 품질 실패는 함께 보고한다.

| 항목 | 기록값 | 해석 한계 |
|---|---:|---|
| 유효 격자 표본 | 3,919 | 검사 중 누적 표본 수. 전체 98,304개 노드의 유일한 유효 노드 수가 아님 |
| 격자 성분 최대 오차 | 0.000488162 | 비교한 정점에서의 값. 격자 내부 법선 보간 정확성 보장은 아님 |
| 근접/범위 밖 법선 오차 | 0 / 0 | 검사한 프로브 위치의 원식 복귀 결과 |
| 고정 위치 밀도 오차 | 0 | 프로브 표본 결과. 최종 영상의 가중 깊이 동일성을 보장하지 않음 |
| 최대 법선 각도 차이 | 8.139334° | 방향 일관성 문턱이 실제 정답과의 각도 상한을 보장하지 않음을 보여주는 관찰 |
| 법선 유효성 불일치 | 0 | 검사 표본 내 결과이며 전체 공간 인증 아님 |

생산 함수는 비유한 법선을 유효 노드로 저장하지 않고 0/invalid로 기록하며, 검사기는 원식의 유한성도 별도로 요구한다. 하지만 잘못된 입력에서 유한한 결과가 나올 수 있으므로 유한성 통과가 정상 노이즈 입력을 대신하지 않는다.

## 이 GPU 품질 검사의 성능 측정 범위

| 측정 항목 | 상태 |
|---|---|
| `Cloud.NormalFieldBuild` GPU 시간 | 이 GPU 검사에 포함하지 않음 |
| 동일 입력 ON/OFF의 `Cloud.Total` 차이 | 이 GPU 검사에 포함하지 않음 |
| 전체 GPU 프레임·FPS 변화 | 이 GPU 검사에 포함하지 않음 |
| 실제 바람·이동 비행의 Development/Release Player 비용 | 이 GPU 검사에 포함하지 않음 |
| Nsight 레지스터·occupancy·stall·spill | 이 GPU 검사에 포함하지 않음 |

전체 3단계 재생성은 98,304개 위치에서 원래 법선을 생산한다. 바람 입력의 매번 다른 값도 재생성을 유발하므로 안정된 고정 자세의 재사용과 실제 바람 실행의 비용이 다를 수 있다. 원래 상세/큰 형태 차분, 격자 생성, 캐시 조회와 exact/blend 복귀를 포함한 총비용을 측정해야 한다. 작은 메모리 사용량이나 full 호출 수를 GPU 절감량으로 환산하지 않는다. 동기 readback 시간과 셰이더 컴파일 대기도 GPU 실행 시간으로 보고하지 않는다.

검증된 6종 입력으로 재실행한 계약·품질 검사는 완료했으며, 현재 품질 예산 실패에 따라 기본 OFF를 유지한다. 동일 빌드의 별도 Development Player에서 ON/OFF 비용과 실제 바람 비행을 측정한 결과는 아래와 같다. 이 기법의 Release 비용은 별도 후속 범위다. 이 160×96 고정 시점 검사는 FHD 이동 영상의 국소 경계·명암·깊이 평가를 대체하지 않는다. 현재 결과로 기본 ON 전환이나 목표 성능 달성을 선언하지 않는다.

## 별도 Development Player 성능 측정

[확장 고정 경로](Benchmark-Extensions-Fixed.md)와 [확장 실시간 비행](Benchmark-Extensions-Flight.md)은 진단 GPU readback을 끈 FHD Development Player에서 performance와 normal-cache를 각 2회 비교했다. 같은 빌드·품질에서 차이는 useNormalFieldCache 하나다. 고정 경로 Cloud.Total 평균은 **14.541760→15.436247ms(+6.151%)**, aggregate FPS는 **63.491969→60.109250**이다. 실제 시간 36초 비행에서는 **14.619505→22.396062ms(+53.193%)**, FPS는 **40.715856→30.932003**이다. 두 경로 모두 이번 측정에서 평균 비용이 증가했다. 고정 경로의 performance 반복 Cloud 평균 편차는 10.37%, normal-cache는 2.86%이므로 변화율은 이 묶음의 관측값으로 보고한다.

Cloud.NormalFieldBuild는 고정 경로에서 관측 192프레임의 평균 **0.064956ms**, 비행에서 관측 2,226프레임의 평균 **0.129321ms**다. 이는 마커가 나타난 프레임의 평균이며 전체 프레임의 상각 평균이나 총 구름 비용이 아니다. 모든 Cloud.Total 표본을 확보했고, 고정 경로는 실제 포즈가 일치했다. 비행은 FPS별 표본 간격이 다르지만 같은 720m/s·바람·날씨·낮 주기 시간 함수를 확인했다. 품질 예산 실패와 이 성능 결과를 함께 고려하여 기본 OFF를 유지한다.
