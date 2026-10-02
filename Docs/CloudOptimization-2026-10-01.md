# 구름 최적화 및 Play 설정창 — 2026-10-01

## 사용자 요구와 계획서의 구분

현재 대화에서 확정된 범위는 기존 구름의 최적화와 Play 설정창이다. 후류·유체 반응은 후속 작업으로 분리한다. 목표는 **GTX 1660, FHD, 60fps**다. 현재 장비는 **RTX 4080**으로, GTX 1660의 성능을 실측하거나 목표 달성을 확인하지 않았다.

검토 자료는 Downloads의 `ChatGPT-구름 시스템 최적화 계획서-20261001-1738.md` 및 `ClouDream_FluidNinja_Implementation_Plan_2026-09-30.md`다. 첫 문서에 인용된 사용자의 과거 요구 중 기존 화풍 유지, FluidNinja 원리 선별, 프로젝트 자체 병목 개선을 현재 요청의 해석에 반영했다. 문서의 AI 제안·명령형 실행 단계 전체를 현재 사용자의 지시로 간주하지 않았다.

두 계획서는 같은 분석을 서술한 자료다. 실제 FluidNinja 매뉴얼·내부 솔버 소스가 이번 작업에 제공된 것은 아니다. 구현은 기존 ClouDream의 생성식을 재사용해 새로 작성했으며, FluidNinja 또는 Lost Skies 원본 소스의 이식·복원이라고 표현하지 않는다.

## 이번에 채택한 변경

| 변경 | 구현 및 경계 |
|---|---|
| 상층 셀 불변 계산 캐시 | `CloudShapeCellCache`와 `CloudConceptCellCache.hlsl`. 32×32 월드 셀, 셀당 float4 32개, 0.5 MiB. 몸체 3개·어깨 2개·렌더 로브 9개의 중심/반경과 성장 계수를 GPU에서 준비한다. |
| 재생성 조건 | 시드·배치·반경·고도 또는 카메라의 정수 셀 창 변경 시 재생성한다. 바람·빛·팔레트 변경에는 몸체 캐시를 재생성하지 않는다. 범위 밖은 기존 절차식을 사용한다. |
| 생성식 공유 | 캐시 생성과 절차식 fallback이 `ConceptBuildBody`와 `ConceptBuildLobe`를 공유한다. `CloudFormationProfile.TryGetSkyCellParameters`가 지원하는 형태만 캐시에 참여한다. |
| 빈 상층 지지 영역의 밀도 계산 생략 | 기존 최종 밀도는 타원체 지지 영역 밖에서 항상 0이다. 이 영역에서 몸체·로브 평가를 생략한다. 포화 전 필드가 필요한 큰 법선에는 적용하지 않는다. 광선 샘플 위치·반복 수·진입 이분 탐색을 바꾸지 않는다. |
| 렌더 버퍼 정리 | Lighting RGBA16F 유지, Transmission RG16F(T, first depth), Scene depth R32F. 지원 여부 확인 후 적용하며 미지원 시 기존 형식을 사용한다. 깊이 복사용 RT의 불필요한 random write를 해제했다. |
| 계측 | `Cloud.DepthCopy`, `Cloud.Raymarch`, `Cloud.Composite` 마커. 기존 GPU 검사 도구에 동일 V2의 최적화 OFF/ON 비교를 추가했다. GPU 기록을 시작하고 종료·취소·재로드 시 이전 Profiler 상태를 복구한다. |
| Play 설정창 | 현재 씬의 `CloudOptimizationPanel`. F8 또는 우측 하단 버튼으로 열고 F8/Esc로 닫는다. 비행·시간 입력을 잠시 비활성화한 뒤 이전 상태로 복원한다. |

운해의 위치 의존 높이장을 셀 중심 높이로 바꾸지 않았다. 점유율·구름 수·반경·날씨·팔레트·자기 그림자·세부 로브를 줄이지 않았다. 렌더와 조명의 `useDetail` 차이도 유지한다.

이는 계획서의 **전체 빈 공간 traversal** 구현이 아니다. 안전한 상층 밀도 조기 거부부터 적용했다. 광선 구간을 크게 건너뛰는 가속, 깊이 인식 업샘플링, 시간 재투영, 차폐 캐시, reactive simulation은 아직 구현하지 않았다.

## 설정창 사용

씬: `Assets/LostSkiesClouds/Scenes/LostSkiesCloudSea.unity`.

- Play → **F8** 또는 **Cloud settings** 버튼.
- 해상도: 50%, 67%, 75%, 100%. 기본은 기존과 같은 **75%**다. 50%는 얇은 형태와 경계를 부드럽게 만들 수 있다.
- 상층 형태 캐시, 지지 영역 밀도 생략, 압축 버퍼를 독립적으로 ON/OFF할 수 있다.
- **Optimizations OFF / compare**는 해상도를 유지한 채 세 최적화만 끈다.
- **Restore session defaults**는 해당 Play 실행을 시작할 때의 설정으로 돌아간다.
- 변경은 현재 Play 실행의 패스에 적용한다. 프로필 에셋이나 PlayerPrefs에 저장하지 않는다.
- 표시 FPS/ms는 최근 120프레임의 전체 실행 간격이다. Editor 지연·VSync가 포함되며 구름 GPU 시간으로 해석하면 안 된다. 설정 변경 시 표본을 초기화한다.
- 메모리 표시에는 현재 카메라의 세 RT만 포함한다. HDRP·공유 노이즈·형태 캐시·다른 카메라의 RT는 제외한다.

FHD×0.75를 8픽셀 정렬한 1440×816에서 세 RT의 저장량은 **26.89 → 17.93 MiB**, 약 **33.3% 감소**한다. 형태 캐시 0.5 MiB는 별도다. 메모리 감소율이 FPS 향상률을 뜻하지 않는다.

## GPU 성능 결과

Unity MCP로 연결한 ClouDream, Unity 6000.6.2f1, Direct3D12, RTX 4080, Editor Play에서 측정했다. 현재 프로필·시점·환경·풍속 오프셋을 고정하고 각 케이스에 워밍업 8개, 유효 GPU 표본 24개를 사용했다. 9개 케이스 총 **216개**의 유효 측정이다.

**다음 수치는 Raymarch DispatchCompute의 GPU 타임스탬프다.** 전체 프레임/FPS가 아니며 깊이 복사·합성·HDRP·초기 노이즈·캐시 재구축 비용을 제외한다. OFF는 같은 수정본의 절차식 fallback과 기존 RT 형식을 사용한다. 변경 전 커밋 전체를 실행한 결과와 혼동하지 않는다.

| 고정 시점 | OFF 75% p50 / p95 (ms) | ON 75% p50 / p95 (ms) | p50 감소 | ON 50% p50 / p95 (ms) |
|---|---:|---:|---:|---:|
| 운해 home | 75.02 / 115.06 | 37.77 / 41.43 | 49.7% | 20.16 / 22.45 |
| 상층 side | 93.32 / 100.37 | 58.16 / 67.34 | 37.7% | 29.31 / 37.47 |
| 관통 경로 내부 후보 | 11.74 / 12.67 | 7.26 / 7.51 | 38.1% | 3.39 / 3.92 |

원시 표본·카메라 좌표·환경·프로필 값은 `Screenshots/Optimization-GPU-Performance.json`에 있다. Screenshots는 git 제외 폴더다. 재현: Play에서 **ClouDream → Lost Skies → Optimization → Measure GPU A-B**. 완료 상태는 `CloudConceptGpuPerformance.Status()`로 확인할 수 있다.

초기에는 GPU 영역만 활성화하고 Profiler 기록 자체가 꺼져 있어 유효 표본이 0개였다. 기록을 시작하자 현재 Graphics Jobs 설정을 변경하지 않고 계측이 성공했다. 따라서 실패 원인을 Graphics Jobs 하나로 단정하지 않는다. [Unity GPU Profiler 문서](https://docs.unity.com/en-us/engine/6000.3/manual/analysis/graphics-performance-profiling/profile-rendering/profiler-gpu)는 환경별 지원 제한과 Editor 오버헤드를 설명하지만, 실제 6000.6 동작은 이번 측정 결과를 기준으로 기록한다.

GPU 계측 복구 전에 사용한 `Screenshots/Optimization/Batch-*.json`은 CPU 제출과 동기 readback 대기를 포함한 보조 진단이다. 위 표나 FPS 주장에 사용하지 않는다.

**GTX 1660 / FHD / 60fps는 미달성·미검증이다.** 현재 RTX 4080에서도 일부 시점의 Raymarch만 16.67ms를 넘는다. Editor 결과를 GTX 1660 Player 성능으로 환산하지 않는다. 낮은 해상도 선택만으로 목표를 달성한다고 약속하지 않는다.

## 검증

- 변경 전후 기존 Concept V2 geometry 검사 통과: 상층 65,536개 표본, 20,800개 경계 표본, 4개 하향 시점, 연속 운해, 원점 이동, 조명 독립성, 노이즈 및 카메라 버퍼 재사용. 상층 점유율은 0.009384155로 같았다.
- 신규 A/B GPU 검사 통과: 8개 조건, 밀도 262,144개 표본. 음수 좌표·캐시 외부·시드/반경 변경·최대 변위·원점 이동·상층 비활성화를 포함한다. 최대 밀도 차이는 0.000005722046. 검사 프레임의 최대 RGB/투과율/가중 깊이 차이는 0이다.
- 기존→압축→기존 형식 전환 시 GPU 출력 및 자원 재할당 검사 통과. 같은 설정에서는 재할당하지 않는다.
- Play 검사 통과: 실제 Input System F8/Esc, 비행 입력 중단·복구, 50/67/75/100/75%와 최적화 설정 10회 전환, 안정 상태에서 버퍼·캐시 재생성 없음.
- HDRP 장면 검사 통과: EV+1 노출 비율 0.5, 전경 팔레트 영향 0, 태양 동기화, 공유 하늘 에셋 불변.
- Unity C# 및 compute shader 컴파일 오류 없음. Play Console 오류 없음.
- 기존/변경 후 1280×720 정지 캡처를 시각 비교했다. 완성 합성 이미지는 HDRP 시간 이력의 영향을 받을 수 있으므로 픽셀 동일성을 주장하지 않는다. 엄격한 동일 입력 비교는 위 GPU 버퍼 검사로 별도 수행했다.

검증 파일: `Screenshots/Optimization-Validation.json`, `Screenshots/Optimization-PlayValidation.json`, `Screenshots/ConceptV2-Geometry-Validation.json`, `Screenshots/CloudLighting-SceneValidation.json`.

## 후속 판단이 필요한 부분

이번 변경은 현재 화풍의 계산 비용을 줄이는 범위다. GTX 1660 목표를 위해서는 추가로 큰 비용 감소가 필요하다. 다음 후보는 깊이 인식 저해상도 재구성, 선택적 시간 재투영, 차폐 조회 재사용이다. 잔상·전경 경계·얇은 띠 손실·조명 지연 같은 시각적 절충이 생길 수 있으므로 사용자에게 별도 단계로 제안한다. 대규모 기능 확대를 이번 요청에 자동 포함하지 않았다.

후속 검증은 실제 GTX 1660의 FHD Player에서 전체 GPU/CPU p50·p95, 급회전·전경·낮/밤, 캐시 창 교체 및 장시간 메모리 수명을 포함해야 한다. 현재의 고정 시점 24개 표본은 출시 성능 인증이나 장시간 스트레스 테스트를 대체하지 않는다.

이후 제공된 FluidNinja 실제 에셋 분석 자료를 바탕으로 한 [추가 검토](D:/Dev/ClouDream/Docs/FluidNinja-Optimization-Review-2026-10-01.md)는 별도 문서에 기록했다. 위의 자료 제공 범위와 성능 수치는 1차 구현 당시 기준이며, 추가 검토 자체에서 런타임 변경이나 성능 재측정을 실행하지 않았다.

사용자 승인 후 진행한 [2차 구현 결과](D:/Dev/ClouDream/Docs/CloudOptimization-Phase2-2026-10-02.md)에는 독립 dispatch와 실제 Game 카메라의 측정 결과를 구분했다. 새 두 옵션은 일부 시점에서 느려져 기본 OFF이며 F8에서 비교할 수 있다. 높이장 캐시는 채택을 보류했다. 위 1차 성능 표와는 측정 입력이 다르다.
