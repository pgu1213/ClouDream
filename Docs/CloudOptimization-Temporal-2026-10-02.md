# 시간 재투영과 분할 갱신 도입 결과

2026년 10월 2일. 승인받은 두 번째 기능인 **시간 재투영과 분할 갱신**을 Unity HDRP 구름에 구현했다. F8에서 OFF·2분할·4분할을 비교할 수 있다. **기본값은 OFF, 선택 단계는 2**다. RTX 4080의 FHD 정지 시점에서 4분할은 구름 GPU 시간을 약 41~70% 줄였지만, 이동 중 같은 절감률이나 GTX 1660의 60 FPS를 확인한 결과는 아니다.

## 구현과 재사용 조건

구름 출력의 8×8 픽셀 타일을 2단계 또는 4단계로 순환 갱신한다. 선택되지 않은 타일은 이전 프레임의 대표 구름 깊이로 현재 광선을 이전 카메라에 투영한다. 이동 중에는 깊이를 반복 보정하고 네 이웃을 보간한다. 색, 투과율, opacity가 곱해진 깊이 모멘트를 같은 계약으로 취급하며 첫 구름 교차 거리는 새 카메라 기준으로 변환한다.

이전 결과가 화면 밖이거나 깊이·전경 가림·구름 경계 조건에 맞지 않으면 해당 픽셀을 **같은 프레임에 새로 적분**한다. 보간한 이력의 나이는 네 이웃 중 최댓값으로 잡는다. 재사용은 2분할에서 최대 1프레임, 4분할에서 최대 3프레임으로 제한한다. 새로 드러난 영역을 오래된 색이나 공간 보간만으로 채우지 않는다.

다음 조건에서는 전체 광선을 다시 계산한다.

- 최초 활성화, 출력 크기·형식 변경, 갱신 단계 변경
- 구름 형태 프로필 및 렌더 설정, 날씨 밀도, 입사광·팔레트 변경
- 월드 원점, 카메라 또는 투영 설정 변경
- 한 번에 128m 초과 이동 또는 8도 초과 회전
- 바람 입력의 급변, 렌더 간격 0.5초 초과

조명과 날씨 입력은 변경 여부를 엄격하게 비교한다. 따라서 시간 자동 재생이나 날씨 전환 중에는 매 프레임 전체 갱신할 수 있다. 현재 바람은 전체 구름의 강체 이동이 아니라 일부 세부 노이즈 변화이므로, 일괄 이동 보정 대신 짧은 이력 수명과 급변 시 무효화를 적용했다.

`CloudFormationProfile`의 지원 여부와 상태 키 계약을 통해 형태 확장을 구분한다. 현재 재투영은 Concept V2의 깊이 모멘트 계약에만 연결했다. Play의 Game 카메라에서 사용하며 Scene 뷰와 스테레오에서는 전체 계산으로 돌아간다. 카메라별 이력을 분리하고 기존 최대 4개 카메라 캐시의 퇴출과 함께 해제한다.

핵심 구현은 [CloudTemporal.compute](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudTemporal.compute), [CloudTemporalHistory.cs](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudTemporalHistory.cs), [CloudRaymarch.compute](D:/Dev/ClouDream/Assets/LostSkiesClouds/Runtime/CloudRaymarch.compute)에 있다. 시간상 갱신 비용을 나누는 원리를 ClouDream의 화면 깊이·투과율 계약에 맞게 새로 설계했다. FluidNinja의 Unreal 코드나 수작업 복원본을 Unity 원본 구현으로 취급하지 않았다. 구름 생성식과 날씨 전환 로직은 변경하지 않았다.

## FHD GPU 측정

RTX 4080, D3D12, Unity 6000.6.2f1 Editor Play. Game 뷰 1920×1080, 구름 75%(1440×816), 깊이 기반 업샘플링 ON, 정오·바람 0·FOV 68이다. 셀 캐시·지원 영역 제거·Compact는 ON, 팔레트 생략·구간 제한·그림자 종료·공간 조명은 OFF다. 카메라 위치와 회전을 고정하고 OFF/2/4/OFF 및 OFF/4/2/OFF 순서로 비교했다. 경우마다 워밍업 20개와 유효 표본 48개를 수집했다.

아래는 **Cloud.Total GPU p50 / p95(ms)**다. 재투영, 이력 저장, 깊이 복사, 구름 적분과 합성을 포함한다. 전체 HDRP 프레임 시간이나 Player FPS는 아니다.

| 순서 | 시점 | OFF 시작 | 2분할 | 4분할 | OFF 끝 |
|---|---|---:|---:|---:|---:|
| 정순 | home | 35.46 / 39.75 | 40.82 / 48.10 | 18.56 / 21.19 | 27.96 / 46.86 |
| 정순 | side | 39.00 / 40.62 | 26.58 / 28.15 | 11.88 / 15.60 | 38.93 / 48.80 |
| 정순 | inside | 6.70 / 9.51 | 4.81 / 5.10 | 3.21 / 4.77 | 6.66 / 9.48 |
| 역순 | home | 31.77 / 54.40 | 23.38 / 40.82 | 13.47 / 24.47 | 33.09 / 41.84 |
| 역순 | side | 38.70 / 40.75 | 20.63 / 36.04 | 12.00 / 16.86 | 38.93 / 52.34 |
| 역순 | inside | 6.69 / 10.68 | 6.06 / 11.38 | 4.46 / 6.42 | 8.35 / 12.35 |

시작·끝 OFF 중앙값의 산술 평균을 기준으로 4분할의 감소율은 home 41.5~58.5%, side 69.1~69.5%, inside 40.7~51.9%다. 표본을 합쳐 새 중앙값을 만든 것은 아니다. 2분할은 home 정순에서 약 28.7% 느렸고 역순에서는 약 27.9% 빨랐다. 이 편차를 숨기거나 고정된 향상률로 표현하지 않는다.

이동 중 GPU 성능은 별도로 확정하지 않았다. 아래의 320×184 이동 정확도 검사에서는 평균적으로 약 13.5%의 픽셀만 재사용했고 나머지는 다시 적분했다. 파동 실행 단위와 추가 재투영 비용도 있으므로 재사용 픽셀 비율을 GPU 절감률로 환산할 수 없다. 빠른 비행·구름 진입·전환 중에는 이득이 줄거나 더 느려질 수 있다.

[정순 원시 표본](D:/Dev/ClouDream/Screenshots/Temporal-SceneFrames.json), [역순 원시 표본](D:/Dev/ClouDream/Screenshots/Temporal-SceneFrames-Reverse.json). FrameTimingManager와 Editor Whole frame 값으로 GTX 1660 FPS를 추정하지 않는다.

## 화질과 이력 수명 검증

변경 전 셰이더를 Unity AssetDatabase로 복사해 독립 기준으로 사용했다. Regular/Compact × 2/4분할에서 **184개 GPU 비교가 통과**했다. 해상도는 320×184다.

- 정지, OFF, 전경 생성·제거, 카메라 급이동, 투영·원점·바람 급변, 조명·밀도 변경과 복귀, 프로필 값, 단계 전환, 프레임 중단, 카메라 분리, 해상도 변경 등 **104개 일치 조건**에서 RGB·투과율·깊이 모멘트·첫 교차 거리의 최대 오차가 모두 0이었다.
- 이동 48개와 연속 바람 32개는 근사 조건이다. 사전에 둔 검사 기준은 프레임 평균 RGB 절대 오차 0.01 미만, 픽셀 RGB 오차 p95 0.04 미만, 평균 투과율 오차 0.01 미만이다.
- 모든 출력의 유한성과 이력 나이 상한, 고정 설정에서의 할당 안정성, OFF 시 전체 카메라 이력 해제를 확인했다.

| 근사 조건 | 프레임 평균 RGB 오차의 최댓값 | 픽셀 RGB p95의 최댓값 | 단일 픽셀 최대 RGB 오차 | 평균 투과율 오차의 최댓값 |
|---|---:|---:|---:|---:|
| 이동 | 0.001162 | 0.006836 | 0.467286 | 0.000002844 |
| 연속 바람 | 0.000639 | 0.002930 | 0.246094 | 0.000001095 |

낮은 평균 오차가 모든 픽셀의 동일 화질을 뜻하지 않는다. 일부 픽셀의 순간 차이가 남아 있으며 이 검사는 모든 속도·시점에 대한 오차 상한 보장이 아니다. 구름 깊이를 하나의 대표 거리로 표현하는 재투영의 한계도 남는다. 그래서 기본 ON으로 승격하지 않았다.

최초 최근접 표본 방식은 4분할 이동에서 선언한 p95 기준을 넘겨 채택하지 않았다. [초기 실패 기록](D:/Dev/ClouDream/Screenshots/Temporal-Validation-PointSampling-Rejected.json)을 보존했다. 최종 보간 구현에서는 해당 검사가 통과했다.

[최종 GPU 검사 결과](D:/Dev/ClouDream/Screenshots/Temporal-Validation.json), [검사 도구](D:/Dev/ClouDream/Assets/LostSkiesClouds/Editor/CloudTemporalValidation.cs). 단색 배경에 원시 색·투과율을 합성한 [기준 이동 이미지](D:/Dev/ClouDream/Screenshots/Temporal-Motion-Reference.png)와 [재투영 이미지](D:/Dev/ClouDream/Screenshots/Temporal-Motion-Reprojected.png)를 함께 확인했다. 이는 최종 HDRP 화면이나 동영상의 육안 평가를 대신하지 않는다.

변경 전 셰이더 보관본은 [CloudTemporalBaseline.compute.txt](D:/Dev/ClouDream/Screenshots/CloudTemporalBaseline.compute.txt)이며 SHA-256은 `AF91C80AB53AFB1EF5F9809E4186B79CDE1120D41B14A9E818454D5D167DCE52`다. 상대 include는 원래 Runtime 폴더 기준이다. 임시 Unity 자산은 검사 후 제거했다. 이후 메뉴 실행은 해당 임시 자산이 없으면 현재 셰이더의 OFF를 기준으로 비교한다.

## 메모리와 Play 설정

이력 색·투과율 두 RT와 장면 깊이·나이 두 RGFloat RT를 카메라마다 추가한다. Compact에서는 픽셀당 28바이트, Regular에서는 32바이트다. FHD의 구름 75%에서 Compact 이력은 **32,901,120바이트, 약 31.4 MiB**다. 기존 카메라 버퍼 약 17.9 MiB와 별도이며 다른 카메라의 이력도 합산된다. 옵션을 끄거나 카메라를 퇴출하면 GPU RT와 계측 자원을 해제한다.

F8 → `Temporal reprojection / split updates (experimental)` → `2 phases` 또는 `4 phases`로 비교한다. History에는 재투영 여부 또는 전체 갱신 원인이 표시된다. 설정이 늘어나 창 안에 세로 스크롤을 추가했다. `Optimizations OFF / compare`, `Restore session defaults`, F8/Esc 닫기 동작을 유지했다.

Unity MCP의 실제 Play 검사에서 **56회 설정 전환**, 입력 중단·복원, 단계 변경 시 재할당 없음, OFF 시 메모리 해제와 시작값 복구가 통과했다. 기존 공간 조명·팔레트·구간 옵션과 함께 켜는 경로도 검사했다. 마지막 자원 정리 보완 후 ON/OFF/ON 재활성화 검사에서도 이력을 두 번 생성하고 OFF 잔여 메모리 0을 확인했다. 컴파일 및 검증 후 Console 오류·경고는 0개였다.

[Play 검사 결과](D:/Dev/ClouDream/Screenshots/Optimization-PlayValidation.json), [F8 화면](D:/Dev/ClouDream/Screenshots/Optimization/Temporal-F8-FHD.png), [스크롤 하단](D:/Dev/ClouDream/Screenshots/Optimization/Temporal-F8-Bottom.png).

이번 단계는 시간 재투영과 분할 갱신의 도입까지다. 거리별 LOD, 추가 빈 공간 건너뛰기, 유체 반응은 이 변경에 포함하지 않았다.
