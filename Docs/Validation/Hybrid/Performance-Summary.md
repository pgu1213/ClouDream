# 하이브리드 구름 Player 비교

RTX 4080 / D3D12 / 1920×1080. 동일 실행 파일에서 R/H/H/R/R/H, 각 36초 실제 시간 비행이다. 생성·갱신·기존 경로 복귀 프레임을 제외하지 않았다.

전체 프레임은 Release wall 시간으로 판정하고, 구름 GPU는 Development의 `Cloud.ComparisonTotal` 스트림으로 판정한다. GPU 스트림은 지연되어 개별 포즈와 대응하지 않는다. 반복별 원시 보고서와 빌드 매니페스트를 함께 보존한다.

| 빌드 / 경로 | 실행 | 프레임 | wall 평균 / P95 / P99 / 최대 ms | 구름 GPU 평균 / P95 ms | GPU 유효 / 누락 | 복귀 프레임 |
|---|---:|---:|---|---|---|---:|
| Development-Flight / reference | 3 | 5473 | 19.736 / 30.323 / 35.258 / 66.363 | 9.334 / 19.544 | 5479 / 0 | 0 |
| Development-Flight / hybrid | 3 | 9263 | 11.661 / 19.102 / 23.927 / 41.711 | 1.356 / 8.691 | 9272 / 0 | 965 |
| Release-Flight / reference | 3 | 5513 | 19.593 / 30.081 / 34.947 / 49.844 | N/A / N/A | 0 / 5513 | 0 |
| Release-Flight / hybrid | 3 | 9295 | 11.621 / 18.809 / 23.615 / 54.429 | N/A / N/A | 0 / 9295 | 965 |

## 반복별 결과

| 원시 보고서 | 완료 | wall 평균 / P95 ms | 구름 GPU 평균 / P95 ms | 복귀 |
|---|---|---|---|---:|
| [Development-Flight/20261015-014706-0-reference.json](Development-Flight/20261015-014706-0-reference.json) | True | 19.851 / 31.279 | 9.471 / 20.698 | 0 |
| [Development-Flight/20261015-014706-1-hybrid.json](Development-Flight/20261015-014706-1-hybrid.json) | True | 11.680 / 19.339 | 1.353 / 8.831 | 322 |
| [Development-Flight/20261015-014706-2-hybrid.json](Development-Flight/20261015-014706-2-hybrid.json) | True | 11.667 / 19.334 | 1.358 / 8.691 | 322 |
| [Development-Flight/20261015-014706-3-reference.json](Development-Flight/20261015-014706-3-reference.json) | True | 19.705 / 29.557 | 9.269 / 18.933 | 0 |
| [Development-Flight/20261015-014706-4-reference.json](Development-Flight/20261015-014706-4-reference.json) | True | 19.652 / 30.054 | 9.262 / 19.151 | 0 |
| [Development-Flight/20261015-014706-5-hybrid.json](Development-Flight/20261015-014706-5-hybrid.json) | True | 11.637 / 18.816 | 1.358 / 8.485 | 321 |
| [Release-Flight/20261015-015414-0-reference.json](Release-Flight/20261015-015414-0-reference.json) | True | 19.737 / 31.055 | N/A / N/A | 0 |
| [Release-Flight/20261015-015414-1-hybrid.json](Release-Flight/20261015-015414-1-hybrid.json) | True | 11.670 / 18.792 | N/A / N/A | 322 |
| [Release-Flight/20261015-015414-2-hybrid.json](Release-Flight/20261015-015414-2-hybrid.json) | True | 11.623 / 18.804 | N/A / N/A | 322 |
| [Release-Flight/20261015-015414-3-reference.json](Release-Flight/20261015-015414-3-reference.json) | True | 19.548 / 29.867 | N/A / N/A | 0 |
| [Release-Flight/20261015-015414-4-reference.json](Release-Flight/20261015-015414-4-reference.json) | True | 19.496 / 29.546 | N/A / N/A | 0 |
| [Release-Flight/20261015-015414-5-hybrid.json](Release-Flight/20261015-015414-5-hybrid.json) | True | 11.570 / 19.008 | N/A / N/A | 321 |

## 판정과 남은 작업

기본 전환은 보류한다. 전체 프레임 P95 ≤ 6.944ms, 구름 GPU 평균 ≤ 2ms·P95 ≤ 3ms, 전환 팝·겹침 없음의 모든 조건을 만족해야 한다. Release에서 GPU 마커가 없으면 구름 GPU를 0ms로 판단하지 않는다.

하이브리드가 실제 적용된 프레임의 wall 분포와 복귀 프레임의 분포도 JSON에 분리했다. 복귀 비용이 꼬리 지연을 키우지만, 실제 적용 프레임만으로도 전체 144FPS 통과를 입증하지 못했다. 구름 외 HDRP·CPU 비용의 원인을 이 측정만으로 단정하지 않는다.

셀 중심 순서 합성은 교차하는 구름의 픽셀별 깊이 정렬을 보장하지 않는다. 급격한 카메라 이동·컷에서 아직 준비되지 않은 셀은 기준 경로로 복귀하므로 스타일 팝과 비용 꼬리가 남을 수 있다. LOD 교체 시 전역적인 팝 없음은 아직 입증하지 않았다. 이 항목들이 해소되기 전에는 기본 씬을 전환하지 않는다.

패스별 프레임 값은 지연된 ProfilingSampler 값이므로 포즈별 비용으로 해석하지 않는다. 양수 표본의 분포와 0 또는 누락된 값의 수는 Performance-Summary.json의 subpasses에 기록했다. 전체 구름 판정은 독립 GPU 스트림을 사용한다.

실시간 비행은 FPS에 따라 표본 포즈와 개수가 다르므로 픽셀별 쌍 비교가 아니다. 고정 360포즈 모드는 실행 도구에 제공하지만 이 최종 표의 실행과 합산하지 않는다.
