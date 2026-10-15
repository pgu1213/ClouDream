# A/B 조명 안정화와 진단

2026-10-15. 기준 커밋 `42f8846`. 사용자가 선택한 범위는 **A/B 안정화와 진단 우선**이며 C 경로·통합 메시 전환은 포함하지 않는다. 이번 변경은 커밋하지 않는다.

## 직접 확인하기

`Assets/LostSkiesClouds/Scenes/CloudABComparison.unity`를 Play하고 F6 패널을 연다. A/B 선택, Full/Surface 전환, F7 동일 시점 A/B 촬영은 그대로다.

- **Original 42f8846 lighting**: 기존 법선·2표본 차폐·팔레트·B 디더·6/12 세그먼트 규칙으로 비교한다. 같은 새 빌드의 진단 모드이며 과거 바이너리 전체를 복제한 것은 아니다.
- **법선만 비교**: Original을 누르고 `World-space volume normal`, `Displacement-aware normals` 두 항목만 켠다. 거시 차폐·색 보강과 디더 변경을 섞지 않고 확인한다.
- **Stable lighting**: 월드 볼륨 법선, 변위 법선, 4표본 거시 차폐와 2차 채움, B 디더 OFF, B 12 세그먼트 고정을 적용한다.
- **Start frozen orbit around view focus**: 현재 보는 방향의 초점까지 거리를 지정하고 수평 12°/초 Orbit를 시작한다. 시간·날씨 진행·바람 위상을 고정한다. Orbit 중에도 A/B와 표시 단계를 바꿀 수 있다. Stop은 관찰 시점을 유지하고 원래 시간/바람 입력을 복구한다.
- 초점은 자동 구름 중심 검색이 아니다. 화면 중앙의 구름까지 거리에 맞춰 슬라이더를 조절한다. F7 촬영은 Orbit를 먼저 종료한다.

[실제 패널](Validation/CloudLighting/Controls.png). 독립 Player는 `Builds/CloudAB-Release/ClouDreamHybrid.exe`다.

## 수정한 부분

**B의 국소 볼륨 법선.** 안정화 모드는 첫 진입 밀도를 만든 셀의 변위 스칼라 기울기를 사용한다. 카메라 광선 반대 방향은 Original 진단에서만 사용한다. 내부는 방향성 표면 면을 만들지 않는다. B의 표면과 볼륨은 같은 준비된 로브 파라미터와 밀도 변환을 공유한다.

**변위 법선.** A는 `HybridReadField + HybridDisplacement`, B 상층/띠는 `LobeField + LobeDisplacement`의 기울기를 월드 8m 간격의 정사면체 네 표본으로 구한다. 밀도를 포화시킨 뒤 미분하지 않는다. A 메시 생성 시 저장하는 큰 형태 법선은 유지하며, 조명 시점에서 실제 변위를 반영한다. B 운해는 기존 높이/법선 캐시에 현재 변위의 기울기를 더하는 근사다.

큰 표면의 정점에서만 새 법선을 구했을 때 B 접합부에 삼각형 단위 명암 패치가 드러났다. 최종 구현은 A 표면과 B 상층의 **표면 픽셀 위치에서** 필드 법선을 계산한다. B의 표면용 셀 파라미터를 기존 `PrepareObjects` 커널로 한 번 준비해 픽셀마다 배치 난수를 재계산하지 않는다. 이 추가 버퍼는 카메라당 약 168KiB이며 자원 계측·해제에 포함한다. 512개 준비 한도를 넘는 셀은 직접 필드 조회로 표시한다.

**거시 자기 차폐와 명암.** 원본 140/160m 두 표본은 진단용으로 유지한다. 안정화 모드는 구름의 대표 크기로 600~5000m 구간을 정하고 네 비균등 구간의 중심에서 적분한다. 광원 조회 OFF는 유지한다. 표면 명암은 월드 법선·광원·높이·하늘 채움으로 만들고, 보라/분홍 중간색을 그림자 안에도 남긴다. 시선은 작은 rim에만 사용한다. A/B만 새 응답을 사용하며 Reference의 `ConceptIncidentLight` 원식은 바꾸지 않는다.

B 볼륨의 안정화 자기 차폐는 첫 진입을 만든 셀에 고정한다. 타일 목록에 다른 셀이 추가됐다는 이유로 그 셀의 자기 차폐가 바뀌지 않게 했다. 다른 구름이 드리우는 전역 그림자까지 계산하는 새 시스템은 아니다. 광원 방향의 저해상도 캐시도 이번에는 추가하지 않았다.

**실루엣 진단.** 안정화 모드는 B 본체의 화면 픽셀 hash 디더를 끈다. 8km의 6↔12 세그먼트 전환은 고정 12로 대신한다. 두 옵션은 독립적으로 다시 켤 수 있다. geomorph나 새 soft shell을 구현한 것으로 해석하지 않는다.

## 같은 시점 캡처

카메라 `(1222,6500,-9000)`에서 `(5031,6550,-5390)` 방향, 낮·바람 위상 12 고정이다. A는 미준비 셀/작업 0을 확인한 뒤 촬영했다.

| 경로 | Original | 법선만 | 전체 안정화 |
|---|---|---|---|
| A | [원본](Validation/CloudLighting/A-Legacy-Near.png) | [법선 수정](Validation/CloudLighting/A-Normals-Near.png) | [안정화](Validation/CloudLighting/A-Stable-Near.png) |
| B | [원본](Validation/CloudLighting/B-Legacy-Near.png) | [법선 수정](Validation/CloudLighting/B-Normals-Near.png) | [안정화](Validation/CloudLighting/B-Stable-Near.png) |

법선 수정만으로 기존 팔레트의 어두운 단색 면이 사라지지는 않았다. 거시 차폐와 채움 보강을 적용한 캡처는 그늘 안의 형태와 중간색을 더 보여 준다. 움직임 중 품질 승인은 고정 Orbit와 자유 비행으로 별도 확인한다.

## 검증

- [GPU 65,536점](Validation/CloudLighting/GPU-Normals.json): 유한값·반복·상층 점유·경계 통과. 준비된 밀도와 직접 조회 최대 차이 약 0.000019.
- A 표면 근처 405점, B 571점에서 독립 6표본 중앙차분과 비교했다. 최소 법선 내적은 A 0.8946 / B 0.9961이다. 네 표본 근사이며 비미분 접합부까지 정확한 해석적 법선이라는 뜻은 아니다.
- 생산용 B 볼륨 법선을 같은 월드 점에서 시선 반전으로 검사했다. rim을 제거한 기본 조명의 최대 RGB 성분 차이는 **수정 모드 0 / 기존 view-ray 모드 1.15999**다. 선형 HDR 값이며 화면 전체의 움직임 오차나 정규화한 이미지 지표가 아니다.
- [A Play 검사](Validation/CloudLighting/A/Play-Validation.json), [B Play 검사](Validation/CloudLighting/B/Play-Validation.json): 운해 연속성, 내부·하부, 낮·석양·밤, 가림, 원점, 날씨 중단·재전환, 두 카메라, resize, OFF 자원 해제 통과.
- [초기 진입·순간이동·B 그리기 역순](Validation/CloudLighting/Streaming-And-Depth.json): 전체 Reference 실행 0, 역순 출력 차이 0, 밀도 0 처리 통과.
- [Orbit 상태 복구](Validation/CloudLighting/Orbit-State.json): 고정 중 카메라 이동, 시간/위상 고정, 중단 시 이전 설정 복구 통과.

## 성능

같은 빌드에서 A Legacy / A Normals / A Stable / B Legacy / B Normals / B Stable을 정순·역순으로 측정한다. 기존 12초 비행·날씨 입력과 48 주 표본·내부 해상도 0.5를 유지한다.

```text
-cloudLightingBenchmark -cloudQuit -cloudRepeats 3 -cloudOutput <directory>
```

Development는 GPU 비용, Release는 전체 프레임을 판정한다. 원본 품질 플래그를 각 JSON에 기록하고 `Tools/CloudAB/Summarize.ps1`로 합산한다.

RTX 4080 / 1920×1080 / D3D12, Development 각 1회. 아래는 구름 GPU ms이며 생성·갱신·합성을 포함한다. [모든 프레임과 합산 통계](Validation/CloudLighting/Development-Flight), [빌드 입력](Validation/CloudLighting/Development-Manifest.json).

| 모드 | 평균 | P95 | P99 | 최대 | 유효 표본 / 누락 | 최대 추가 MiB |
|---|---:|---:|---:|---:|---:|---:|
| A Original | 0.617 | 0.901 | 1.271 | 2.043 | 1197 / 0 | 92.40 |
| A 법선만 | 0.871 | 1.223 | 1.702 | 2.591 | 1167 / 0 | 91.47 |
| A 전체 안정화 | 1.054 | 1.545 | 2.041 | 2.554 | 1107 / 0 | 90.16 |
| B Original | 0.732 | 1.618 | 1.958 | 2.764 | 1157 / 0 | 27.04 |
| B 법선만 | 0.880 | 1.800 | 2.196 | 3.114 | 1141 / 0 | 27.05 |
| B 전체 안정화 | 1.108 | 2.132 | 2.314 | 3.439 | 1114 / 0 | 27.05 |

동일 빌드의 Original 대비 전체 안정화 평균 증가는 A 약 0.437ms, B 약 0.376ms다. 두 안정화 경로 모두 구름 평균 ≤2ms·P95 ≤3ms와 추가 자원 128MiB를 통과했다. 최대값이 항상 3ms 이내라는 결과는 아니다. 여섯 실행 모두 Reference 복귀 0회였다.

Release는 정순→역순→정순으로 모드별 3회, 18개 실행을 모두 완료했다. [원본 프레임과 합산 통계](Validation/CloudLighting/Release-Flight). 다음 값은 전체 프레임 ms이며 세 실행을 합친 표본의 백분위수다.

| 모드 | 프레임 수 | 평균 | P95 | P99 | 최대 | 전체 GPU 유효 / 누락 |
|---|---:|---:|---:|---:|---:|---:|
| A Original | 3422 | 10.525 | 12.413 | 14.484 | 18.939 | 3330 / 92 |
| A 법선만 | 3351 | 10.749 | 12.647 | 14.622 | 36.864 | 3271 / 80 |
| A 전체 안정화 | 3296 | 10.929 | 12.916 | 14.470 | 20.080 | 3219 / 77 |
| B Original | 3394 | 10.611 | 12.429 | 13.982 | 33.226 | 3306 / 88 |
| B 법선만 | 3339 | 10.788 | 12.727 | 14.870 | 46.353 | 3258 / 81 |
| B 전체 안정화 | 3267 | 11.022 | 12.971 | 14.673 | 56.815 | 3177 / 90 |

모든 실행이 측정 완료·Reference 복귀 0회였다. 전체 P95 6.944ms 목표는 미달이다. Release의 구름 GPU 마커는 비활성화하므로 누락을 0ms로 채우거나 GPU 예산 검증으로 사용하지 않는다. FrameTiming의 누락도 위처럼 별도로 남겼다.

[Development](Validation/CloudLighting/Development-Manifest.json)와 [Release](Validation/CloudLighting/Release-Manifest.json)는 같은 런타임 소스 SHA-256 `1c02f730deb261d5cdfa0643f91476a4344ec77bfe583c854b8ff7d883d1f69b`를 사용한다. 기존 A/B 보고서의 수치를 이 변경 후 측정값으로 재사용하지 않았다.

## 남겨 둔 구조적 문제

B의 겹친 로브 셸을 하나의 메시로 바꾸지 않았으므로 접합부 깊이 선택 문제는 남는다. A는 캐시 격자와 낮은 메시 LOD의 흔적이 새 명암에서 드러날 수 있다. 이번 작업을 모든 breathing·팝 제거 완료로 보고하지 않는다.

Surface/Volume의 0.75/0.5 해상도 분리, soft fringe, Surface를 외관 기준으로 유지하는 새 내부 전환, 통합 로브 메시, A/B 결합 C 경로와 LOD morph는 다음 구조 실험 범위다. 시간 재투영·외부 패키지·새 HDRP 안개는 추가하지 않았다. 기본 채택과 144FPS 목표도 자동 변경하지 않는다.
