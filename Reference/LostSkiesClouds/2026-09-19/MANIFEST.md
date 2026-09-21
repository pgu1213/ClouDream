# 분석 입력과 도구

## 입력

- 원본 게임: `D:\GAME\Lost-Skies_Raw\Lost Skies`
- 기존 에셋 추출: `D:\GAME\Lost-Skies_Decom\ExportedProject`
- 기존 타입 스켈레톤: `D:\Decompile\DiffableCs`
- 기존 dummy/ILRecovery DLL: `D:\Decompile\ILRecovery`

## 새 분석

- Cpp2IL: `2022.1.0-pre-release.21+58fc404ac503f4e512055cafc48c03088fc6e224`
- 처리기: `attributeanalyzer, attributeinjector, callanalyzer`
- 출력 형식: `isil`
- Cpp2IL 판별 Unity 버전: `6000.0.41f1`
- 실제 IL2CPP metadata version: `31.1`
- 새 전체 ISIL 작업 출력: 약 30,051개 파일 / 813,776,221 bytes
- 전달 폴더에는 구름 시스템 관련 파일만 선별 수록

## 추가 수작업 복원

- `00_report/MANUAL_RECONSTRUCTION_NOTICE_KO.md`: 원본이 아님을 명시한 별도 고지와 신뢰도 기준
- `00_report/isil_manual_reconstruction_notes_ko.md`: 함수별 ISIL 판독 결과와 미확정 부분
- `01_reconstructed/SkyChangeController.reconstructed.cs`: 이벤트·초기화·전환 상태 머신·노출 처리
- `01_reconstructed/CloudLayerInterpolator.reconstructed.cs`: 6레이어 배열·generator 설정·texture 보간 전체 흐름
- `01_reconstructed/SkyParticlesController.reconstructed.cs`: 보스 체력·섬·Windwall VFX
- `01_reconstructed/TimeOfDayCloudHooks.reconstructed.cs`: 밀도 query 정규화·보간
- `01_reconstructed/UniversalCloudLayerInterpolation.notes.cs`: UniversalCloudLayer.lerp의 검증된 구조

## SHA-256

- `GameAssembly.dll`: `EA8362904BA2F0C711DC937B4F10C18563045C48C4AD3FE7F9C266374491A04E`
- `global-metadata.dat`: `302DDC4B8E2A74D72535254D197A78F66A219B4C5EA2CE1BB1554719D29E4B06`
- Cpp2IL Windows exe: `663FB432433B4371FD1EE0EBC321A8FFF2A9AAC5AC4230C843F9E03DDEE4E04C`
- CFG plugin zip: `911631082E59C889273262768F7AC2239F7D09E2C5E6BF87CA879C3452ED52DA`

## 외부 소스

Cpp2IL 공식 릴리스와 공식 CFG 플러그인만 다운로드했다. 게임 파일의 DRM, 라이선스, 실행 보호를 우회하거나 수정하지 않았다.
