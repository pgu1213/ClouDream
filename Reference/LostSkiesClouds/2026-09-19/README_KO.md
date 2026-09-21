# Lost Skies 구름 시스템 분석 결과

이 폴더는 `Lost Skies`의 구름 시스템을 학습 목적으로 정적 분석한 결과다.

## 가장 중요한 결론

- 원본은 **Unity 6000.0.41f1 / IL2CPP / HDRP** 빌드다.
- 기존 디컴파일 프로젝트의 `6000.5.10f1`은 원본 버전이 아니다. 기존 프로젝트가 수정되면서 바뀐 값이다.
- 실제 게임 구름은 Unity HDRP 기본 `VolumetricClouds`만으로 구성되지 않는다. 핵심 렌더링 계층은 유료 Unity 에셋인 **Expanse**의 `CreativeCloudVolume`, `ProceduralCloudVolume`, `UniversalCloudLayer`, `CloudRenderer`, `CloudGenerator`다.
- 게임 고유 제어기는 `SkyChangeController`다. Default, HeraldBattle, Windwall 프로필 사이에서 다음 값을 보간한다.
  - 구름 coverage
  - raininess
  - 시간대 시스템의 boss-fight 조명 값
  - 높이별 coverage curve
  - HDRP `ColorAdjustments.postExposure`
- `CloudLayerInterpolator`는 6개 노이즈 계층(Coverage, Base, Structure, Detail, BaseWarp, DetailWarp)의 프리셋과 생성 텍스처를 전환한다.
- `CloudRemapping`은 floating-world-origin 이동량을 Expanse의 행성 원점과 `CloudManager.planetOriginOffset`에 반영해 대규모 월드에서 구름이 튀는 현상을 막는다.

## 폴더 구성

- `00_report/analysis_report_ko.md`: 구조와 데이터 흐름 설명
- `00_report/MANUAL_RECONSTRUCTION_NOTICE_KO.md`: 원본 소스가 아님을 명시한 필수 고지
- `00_report/isil_manual_reconstruction_notes_ko.md`: 메서드별 신뢰도와 ISIL 판독 기록
- `01_reconstructed/`: 네이티브 ISIL을 바탕으로 다시 쓴 학습용 C# 수작업 복원 코드
- `02_type_skeletons/`: Cpp2IL이 복원한 필드/메서드 시그니처
- `03_isil_evidence/`: 원본 `GameAssembly.dll`에서 새로 추출한 핵심 메서드의 디스어셈블리와 ISIL
- `04_serialized_data/`: 실제 Normal/Herald 프리셋과 차이 설명
- `05_scene_evidence/`: 기존 씬 추출의 한계와 원본 근거
- `MANIFEST.md`: 입력, 도구, 무결성 정보

## 코드 정확도 주의

`01_reconstructed`의 코드는 원본 C# 소스가 아니다. IL2CPP 네이티브 코드, 필드 오프셋, 타입 스켈레톤을 사람이 읽기 좋게 다시 쓴 **수작업 복원 코드**다. 이번 추가 분석에서는 분기·상수·배열 인덱스·가상 호출 슬롯까지 대조했지만, 지역 변수명과 컴파일러가 인라인한 원래 호출 경계는 달라질 수 있다. 반드시 별도 고지 문서를 함께 읽어야 한다.

`03_isil_evidence`가 재구성의 1차 근거다.
