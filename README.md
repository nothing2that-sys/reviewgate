# ReviewGate

**Headless CLI를 중심으로 원문 근거를 추적하는 범용 AI 검수 도구.**

OpenAI, Together AI, Google AI, LG EXAONE의 제공자와 모델을 선택하여 소설 원고, 코드, 기획서·설계서 등을 검수하고, 필요할 때 다른 모델로 독립 검수하거나 특정 지적을 추가 검증하는 것을 목표로 합니다.

기본 사용 경험은 Codex CLI를 Headless로 호출하는 방식과 유사하게 설계합니다. 검수 목적·입력·모델·옵션을 전달하면 GUI 없이 실행하고, 사람이 읽는 보고서와 기계가 처리할 수 있는 결과·종료 상태를 반환합니다. 특정 Codex CLI 명령이나 동작의 완전한 복제를 뜻하지는 않습니다.

## 현재 상태

- 프로젝트명: **ReviewGate**
- 저장소: [nothing2that-sys/reviewgate](https://github.com/nothing2that-sys/reviewgate)
- 단계: **v0.3 데스크톱 앱** — API 키 등록·모델 선택·간이 테스트·실행 기록 화면([안내](docs/DESKTOP.md)). v0.2 실연결은 OpenAI·Together·Google 실서버에서 검증했습니다([설계](docs/LIVE_CONNECTION_V0_2.md))
- 이전 단계: v0.1 오프라인 검증판 한정 종결(2026-11-04). 받아들인 잔여는 [검토 기록](validation/offline-v0.1/IMPLEMENTATION_REVIEW_20261104.md)에 있습니다
- Windows .NET 10 CLI: `plan`, `exec`, `status`, `cancel`, `resume`, `export`. 실행은 고정 synthetic fixture만 사용합니다.
- 실제 모델 API 호출·키 조회/발급/입력/저장/연결: **없음**. 구현은 새 맥락 Claude Code 검토 2회(1차 지적, 1차 수정본 독립 검토)를 거쳤고, 2차 수정은 자체 검증만 거쳤습니다.

이 저장소의 문서는 합의 사항과 검토 중인 제안을 함께 기록합니다. 문서 커밋·푸시는 구현이나 외부 전송의 승인을 뜻하지 않습니다.

## 기본 방향

- 단일 모델 검수, 서로의 결과를 보지 않는 독립 검수, 특정 지적의 선택적 추가 검증을 구분합니다.
- 지적에는 위치, 원문·코드 근거, 이유, 영향, 수정 제안, 확신 및 검증 상태를 남깁니다.
- 원문 처리 범위를 추적하고, 요약·검색만 수행한 범위를 원문 전체 검수로 표시하지 않습니다.
- 실행 전 모델·전송 대상·예상 비용을 확인하고, 승인된 범위와 예산 안에서 실행합니다.
- Headless에서 숨은 입력 대기를 만들지 않고 승인·credential·예산 부족을 구조화된 상태로 반환합니다.
- HGLOS가 CLI를 호출하고 결과를 받아 검증 흐름에 활용할 수 있게 설계합니다.
- 보고서와 수정 제안을 분리하고 원본을 자동 수정하지 않습니다.
- 첫 버전은 Windows .NET 10 Headless CLI·텍스트/코드·HGLOS export·오프라인 검증판으로 범위를 확정했습니다. WPF 등 GUI와 실제 모델 연결은 후속입니다.

## 문서

| 문서 | 내용 |
|---|---|
| [요구사항](docs/REQUIREMENTS.md) | 검수 유형, 사용 흐름, 근거·처리 범위·비용·보안·원본 보호 |
| [Headless CLI 및 HGLOS 설계](docs/HEADLESS_DESIGN.md) | 엔진 책임, CLI 계약 초안, 상태·재개·승인, HGLOS 연결 |
| [결정 및 미결 사항](docs/DECISIONS.md) | 합의된 방향, 제안, 미확인, 단계별 범위와 검증 기준 |
| [제공자 조사 기록](docs/PROVIDERS.md) | 공식 자료 출처, 대화 당시 모델·가격 확인 기록과 한계 |
| [v0.1 구현 계획](docs/IMPLEMENTATION_PLAN_V0_1.md) | 확정 범위, Headless·HGLOS 우선 단계, API 없는 완료 기준 |
| [CLI·실행·예산 상세 계약](docs/CLI_AND_RUN_CONTRACT.md) | 명령·종료 코드·승인 신뢰 경계·상태 전이·월 예산·crash 복구 |
| [원문·근거·HGLOS 상세 계약](docs/SOURCE_EVIDENCE_AND_HGLOS.md) | 원문 좌표·diff·독립성·처리 범위·findings projection·보존 정책 |
| [독립 설계 검토 종결 기록](validation/design-review-20261104/DESIGN_REVIEW.md) | Claude Code 공식 사용법 적용, 독립 검토 원문·지적 반영·조건 해소·한계 |

## 설치 (현재 사용자)

```powershell
.\scripts\install.ps1
```

CLI와 데스크톱 앱을 `%LOCALAPPDATA%\Programs\ReviewGate`에 설치하고, 사용자 PATH와 환경 변수 `REVIEWGATE_CLI`를 등록합니다. 새 터미널 어디서든 `reviewgate ...`로 부릅니다. 다시 실행하면 갱신, `scripts\uninstall.ps1`로 제거합니다. 자세한 사용법은 [에이전트에서 쓰기](docs/AGENT_USAGE.md)에 있습니다.

## 오프라인 실행

필요 환경은 Windows의 .NET 10 SDK입니다. HGLOS 계약 테스트에만 Python 3가 필요합니다. 런타임·도구 설치나 키 설정은 자동 수행하지 않습니다.

```powershell
dotnet restore ReviewGate.sln
dotnet publish src/ReviewGate.Cli/ReviewGate.Cli.csproj -c Release --no-restore -o .work/publish
./scripts/offline-demo.ps1
```

데모는 동봉된 합성 파일과 임의 단가를 사용하고 **simulation grant**를 만듭니다. 실제 모델의 검수·가격·청구·정확도를 측정하지 않습니다. `--execution-mode live`는 exit 14로 차단합니다.

전체 검증은 `./scripts/verify-offline.ps1 -Python '<확인한 python.exe 절대 경로>'`로 실행합니다. 빈 suite·누락·skip을 통과로 세지 않습니다. [실행 안내](docs/OFFLINE_USAGE.md), [구현·검증 보고서](validation/offline-v0.1/IMPLEMENTATION_REPORT.md)에 사용법과 제한을 기록합니다.

실제 모델 연결과 HGLOS의 기존 회차 실행기 교체는 후속 승인 범위입니다. 현재 구현은 사용자 요청에 따른 로컬 checkpoint 커밋에 포함하며, 선행 설계 문서 커밋만 원격에 푸시했습니다.
