# 같은 기준 셰이더에서 거리 품질과 해상도 전환

ReleaseReferenceSwitch, 2026-10-05 독립 Player. 측정 순서는 baseline → reference-performance → baseline, 각540프레임이다. 모든 측정 case는 보존한 reference shader를 사용한다.

| case | wall 평균 / 중앙 / p95 ms | Frame GPU 평균 / 중앙 / p95 ms | FPS | GPU 유효 표본 | RT 할당 누적 |
|---|---:|---:|---:|---:|---:|
| redesign-20261005-122249-0-baseline | 28.607543 / 28.474750 / 39.881200 | 28.628523 / 28.399616 / 39.983104 | 34.955816 | 539/540 | 1 |
| redesign-20261005-122249-1-reference-performance | 21.205603 / 21.603450 / 30.648200 | 21.175010 / 21.569024 / 30.787584 | 47.157348 | 540/540 | 2 |
| redesign-20261005-122249-2-baseline | 32.542591 / 32.096800 / 48.215000 | 32.497281 / 32.086528 / 47.935488 | 30.728960 | 540/540 | 3 |

마지막 baseline은 첫 baseline 대비 wall +13.755284%, Frame GPU +13.513649%이다. 이번에는 빨라지지 않았다.

첫 baseline부터28.6075ms여서 이전 baseline-only 약43ms와 다르다. ABBA 측정 사이의 current shader 전환이 없어도 독립 프로세스 첫 baseline에서 약28ms가 관측됐으므로, shader 교체 자체를 기존 편차의 확정 원인이라고 설명할 수 없다. 이 결과는 원인 분리용이며 일반적인 개선율을 확정하는 자료가 아니다.

검증: 두 baseline의 passSettings가 문자열 수준에서 동일하다. 모든 case의 실제 포즈 서명, 환경/style/sky/ocean, 빌드, FHD, 시각도 동일하다. shapeBuild는14→26,41→53,68→80으로 증가하며 renderer가 유지됨을 확인한다. 내부 구름 크기는1440×816 → 960×544 → 1440×816, RT 할당 누적은1→2→3이다.

| case | 평균 GPU MHz | 평균 °C | 평균 W |
|---|---:|---:|---:|
| redesign-20261005-122249-0-baseline | 2734.69 | 67.44 | 231.75 |
| redesign-20261005-122249-1-reference-performance | 2745.00 | 59.67 | 181.71 |
| redesign-20261005-122249-2-baseline | 2744.17 | 64.50 | 216.01 |

중요한 초기 경로: Automation은 Configure를 호출하기 전45프레임 동안 원래 scene shader로 렌더링할 수 있다. baseline-only도 프로세스 초기에는 current shader를 사용할 수 있으므로, 여기서 "같은 reference만 사용"했다는 말은 측정 case 사이에 한정된다.

GPU 전체 상태, 외부 GPU 부하, 실제 출력의 변화 여부는 아직 분리하지 못했다. 다음 FHD 픽셀 비교는 렌더 결과가 동일한데 성능만 바뀌는지 확인하는 데 유용하다. GPU 클록과 adapter-wide 사용률만으로 동일한 실행량을 증명하지 않는다.
