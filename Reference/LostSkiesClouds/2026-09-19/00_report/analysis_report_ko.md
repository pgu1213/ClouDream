# 구름 시스템 분석 보고서

> 추가 분석: `03_isil_evidence`를 함수 단위로 다시 판독해 상세 수작업 복원본을 작성했다. 원본 소스가 아니라는 고지와 메서드별 신뢰도는 `MANUAL_RECONSTRUCTION_NOTICE_KO.md`, `isil_manual_reconstruction_notes_ko.md`를 참조한다.

## 1. 기술 스택

원본 빌드에서 Cpp2IL이 판별한 정보는 다음과 같다.

- Unity: `6000.0.41f1`
- IL2CPP metadata: `31.1`
- 플랫폼: Windows x64 PE
- 렌더 파이프라인: HDRP
- 구름/대기 패키지: Expanse
- 원본 메서드 매핑 수: 약 335,744개

기존 `ExportedProject/ProjectSettings/ProjectVersion.txt`의 `6000.5.10f1`은 원본과 불일치한다. 기존 프로젝트의 `Packages/manifest.json`, GraphicsSettings, ProjectSettings, VFXManager 등도 Git에서 수정 상태였다. 따라서 기존 프로젝트는 실행 가능한 원본 재현물로 보지 않고, 직렬화 에셋을 찾기 위한 색인으로만 사용했다.

## 2. 전체 구조

```text
SkyBossService / WindwallService
             |
             v
     SkyChangeController
       |        |        |
       |        |        +--> HDRP ColorAdjustments.postExposure
       |        +-----------> TimeOfDayController boss-fight lighting
       +--------------------> Expanse CreativeCloudVolume
                                  | coverage, raininess
                                  v
                         ProceduralCloudVolume
                                  | coverage curve + 6 noise layers
                                  v
                   UniversalCloudLayer / CloudGenerator
                                  v
                            CloudRenderer (GPU)
```

별도로 `CloudRemapping`은 floating-world-origin 이동 이벤트를 받아 Expanse 행성 원점을 같은 양만큼 역이동시킨다.

## 3. 게임 고유 상태 전환

`SkyChangeController.SkyProfileType`:

- `Default = 0`
- `HeraldBattle = 1`
- `Windwall = 10`

이벤트 매핑:

- 보스 전투 진입 → HeraldBattle
- 보스 전투 이탈 → Default
- 플레이어가 Windwall 진입 → Windwall
- Windwall 이탈 → Default
- 디버그 버튼 `SetHeraldProfile`, `SetDefaultProfile`도 같은 진입점을 사용한다.

전환 시작 시 현재 화면 값을 임시 프로필에 캡처한다. 그래서 전환 도중 다른 상태로 바뀌어도 새 전환이 현재 보이는 상태에서 이어진다.

캡처되는 값:

- `CreativeCloudVolume.m_coverage`
- `CreativeCloudVolume.m_raininess`
- `TimeOfDayController.BossFightValue`
- `ProceduralCloudVolume.m_coverageCurve` 복사본
- 현재 post-exposure 곡선 복사본

이후 `Time.deltaTime / LerpTime`으로 0..1 보간하고 매 프레임 끝에서 계속한다. 이전 전환은 `CancellationTokenSource`로 취소한다.

## 4. Expanse 구름 표현

프리셋 `UniversalCloudLayer`에는 다음 데이터가 있다.

- 높이 방향 density curve: 16 샘플
- 높이 방향 coverage curve: 16 샘플
- 편집용 AnimationCurve 두 개
- 6개 노이즈 계층
- 3D 구름 볼륨의 공간 범위
- 광학 계수, 다중 산란, 실버 라이닝, 자기 그림자
- ray-march step/LOD/temporal denoise 설정

6개 노이즈 계층:

1. Coverage
2. Base
3. Structure
4. Detail
5. BaseWarp
6. DetailWarp

각 계층은 procedural 여부, noise type, scale, octave 수, octave scale/multiplier, tile 값을 가진다. 현재 프리셋은 여섯 계층 모두 procedural이다.

`CloudGenerator`는 품질과 2D/3D 차원에 맞춰 RTHandle을 만들고, 프리셋 해시가 달라질 때 필요한 노이즈 텍스처를 다시 생성한다. `CloudRenderer`는 fullscreen/reflection/shadow/gameplay query 커널을 선택하고, 높이 곡선을 텍스처로 올려 ray-march 컴퓨트 셰이더에 전달한다.

## 5. Normal 대 Herald 프리셋

두 프리셋의 geometry, noise layer 구성, density curve, 대부분의 광학/성능 설정은 같다. 차이는 의도적으로 좁다.

- Herald는 구름 상부 coverage가 더 높다.
- `coverageIntensity`: `0.248` → `0.253`
- `scatteringCoefficients`: `4.0e-6` → `3.3232e-6`
- 마지막 coverage sample: `0.7604588` → `0.9092015`

즉 Herald 상태는 완전히 다른 구름 생성기를 쓰는 것이 아니라, 같은 노이즈 구조를 유지하면서 coverage 분포와 산란을 조정해 더 무겁고 닫힌 실루엣을 만든다.

## 6. Floating origin 처리

`CloudRemapping`은 시작 시 `GlobalSettings` 참조가 없으면 씬에서 찾고, `FloatingWorldOriginService.FloatingWorldOriginShifted`에 구독한다.

원점이 `delta`만큼 이동하면:

```text
GlobalSettings.m_planetOriginOffset -= delta
CloudManager.planetOriginOffset    -= delta
```

대규모 월드 좌표를 주기적으로 원점 근처로 옮겨도 구름 샘플링 좌표와 행성 대기 좌표가 시각적으로 고정되게 하는 장치다.

## 7. 기존 디컴파일이 비어 보인 이유

- IL2CPP는 배포 시 C# IL을 `GameAssembly.dll` 네이티브 코드로 변환한다.
- AssetRipper가 만든 Unity 프로젝트는 에셋 복원에는 유용하지만 원본 C# 메서드 본문을 만들지 못한다.
- 기존 `DiffableCs`는 필드와 메서드 시그니처만 있고 본문은 `{ }`다.
- 기존 `ILRecovery/*.dll`도 본문이 `throw null`이라 ILSpy만 열면 정상 코드처럼 보이지 않는다.
- 이번 결과는 최신 Cpp2IL로 원본에서 ISIL을 새로 뽑아 실제 분기, 오프셋, 호출을 확인했다.

## 8. 남은 한계

- 원본 C#의 변수명, 주석, 편집기 전용 코드는 복원할 수 없다.
- IL2CPP/LTO가 메서드를 인라인하거나 동일 본문을 합쳐 `Update`와 `UpdatePostExposure`가 같은 네이티브 주소로 보이는 부분이 있다.
- 기존 `Island.unity`의 `SkyChangeController` MonoBehaviour 블록은 필드가 비어 있다. 스크립트 타입 트리를 연결하지 못한 기존 AssetRipper 출력의 한계다.
- Expanse 셰이더 원문은 배포 빌드에 컴파일된 형태로 들어 있으므로 원래 HLSL 파일과 주석을 그대로 복원할 수 없다.
