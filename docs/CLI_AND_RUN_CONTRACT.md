# v0.1 CLI·실행·예산 상세 계약안

상태: 2026-11-04 v0.1 오프라인 설계 기준본. 독립 검토·지적 반영·조건 해소를 완료했습니다. 구현·명령·schema·DB는 아직 없습니다. 제품 요구사항은 [REQUIREMENTS.md](REQUIREMENTS.md), 단계 범위는 [구현 계획](IMPLEMENTATION_PLAN_V0_1.md)을 따릅니다. 구현·사용자 최종 계획 확인·실연결 권한을 만들지 않습니다.

## 1. 기본 선택과 경계

- Windows 우선 .NET 10 CLI·공통 엔진, 전경 실행, GUI·daemon 없음.
- v0.1은 `synthetic`만 실행하며 세 제공자의 serializer/parser를 테스트 대체 transport에 연결합니다. 실제 HTTP transport·OS vault 구현을 CLI 실행 경로에 등록하지 않습니다.
- Profile은 데이터이며 프로그램·스크립트·임의 plugin assembly·shell 명령을 실행하지 않습니다.
- stdout은 기본 단일 JSON envelope, 진행·진단은 stderr. 성공·오류 모두 같은 envelope 버전을 사용합니다. `--help`와 `--version`만 설명용 출력 예외입니다.
- stdout·stderr·JSON·Markdown 파일은 UTF-8 BOM 없음으로 고정하며 Windows 콘솔 코드페이지와 무관하게 pipe byte 계약을 지킵니다.
- source·Profile·prompt·모델 설정·fixture를 고정하여 같은 계획을 복구합니다. 실제 모델의 결정성을 보장한다는 의미가 아닙니다.
- 스냅샷과 fixture의 내용은 프로젝트의 데이터입니다. API 키나 인증값은 어느 계획·결과·정책에도 넣지 않습니다.

## 2. CLI 이름·인수 계약

아래는 제안한 구문입니다. 실행 예제나 존재하는 명령이 아닙니다.

| 명령 | 필수/주요 입력 | 동작 |
|---|---|---|
| `plan` | 하나 이상의 `--input`, `--profile`, 검수 지시 | 스냅샷·계획·전송 manifest 생성, 모델·비용·승인 부족 표시 |
| `exec` | `--plan <plan-id>` 또는 지시를 포함한 plan과 같은 단순 입력; 새 synthetic 계획은 fixture 필수 | 정책 확인 후 전경 실행. plan과 새 지시/단순 입력을 함께 쓰면 오류 |
| `status` | `--run <run-id>` | 상태·진행·비용·owner·필요 조치 조회, API·vault 접근 없음 |
| `cancel` | `--run <run-id>` | 중단 제어 요청. 실제 worker 중단과 구분 |
| `resume` | `--run <run-id>`, `--grant-file`; 선택 `--recover-only`, `--resolve-unknown` | 체크포인트 재검증 후 전경 재개. recover-only는 transport 없이 복구만 수행. completed를 다시 실행하지 않음 |
| `export` | `--run <run-id>`, `--format json/markdown/hglos-findings` | 저장된 결과에서 새 내보내기 bundle 생성, 모델 호출 없음 |

검수 모드는 `single`, `independent`, `verify` 후보로 고정합니다. independent는 v0.1에서 두 reviewer만 허용합니다. reviewer는 provider·model·API/capability profile로 식별하며 표시 이름만으로 선택하지 않습니다. verify는 대상 finding ID·기준 실행을 요구하고 독립 전수 검수와 다르게 기록합니다.

### 공통·실행 입력 표

인수 이름과 오류 처리는 P0 계약 동결의 기준안입니다. 모든 경로는 실행 시작 시 절대 canonical 경로로 고정합니다.

| 옵션 | 규칙·기본값 |
|---|---|
| `--state-root <dir>` | 기본 `%USERPROFILE%\.reviewgate`(2026-11-05까지는 `%LOCALAPPDATA%\ReviewGate`; Claude 데스크톱 앱 같은 MSIX 호스트가 AppData를 패키지별로 가상화해 장부가 갈라지므로 옮김). 테스트는 명시 임시 root. 이 root의 synthetic namespace에 하나의 장부를 사용 |
| `--project <id>` | 새 plan/직접 exec에 필수. 1~64자의 영문·숫자·`_`·`-` ID. 같은 namespace의 같은 ID가 worker·프로젝트 예산 identity |
| `--config <file>` | 선택 project 설정 파일. 아래 지시·Profile·reviewer·한도 등의 기본값을 담을 수 있음. 명시 CLI가 우선하며 최종 병합값을 plan에 저장. 키/실행 스크립트 금지 |
| `--source-root <dir>` | 새 plan에 필수. input의 경로 기준. repo root와 같다고 추정하지 않음 |
| `--repository-root <dir>` | HGLOS code export를 계획할 때 필수. source root를 포함하는 명시 저장소 기준; `.git` marker 존재·실제 경로를 로컬 확인. Git 실행·tracked/HEAD 확인 주장은 하지 않음 |
| `--input <path>` | 반복 가능. 새 plan에 하나 이상 필수. source root 안의 파일/폴더 |
| `--input-class <synthetic|nonsensitive>` | 새 plan에 필수. v0.1은 confidential/company/sensitive 입력 미지원; 실제 무해성 자동 판정의 증거는 아님 |
| `--profile <file>` | 기준 파일 필수. 데이터 계약·버전 확인 |
| `[instruction]` / `--prompt-file <file>` / `--prompt-stdin` | 새 plan/직접 exec에 정확히 하나의 비어 있지 않은 지시가 필수. 최종 지시 byte와 hash를 plan에 고정. 누락·중복·빈 지시는 exit 2. `--plan`과 지시를 함께 주면 exit 2 |
| `--mode <single|independent|verify>` | 기본 single. verify는 `--base-run`과 반복 `--finding` ID 필수 |
| `--reviewer <file>` | 반복 reviewer 설정 파일. single/verify는 하나, independent는 서로 다른 canonical_model_key 두 개. 개발사 unknown도 명시 |
| `--budget-policy <file>` | 새 plan에 필수. USD·run/project/month 한도·요청/재시도 한도. 테스트용 값만 사용하며 운영값 승인 아님 |
| `--prices <file>` | 비용 상한 검사에 쓸 가격 snapshot. strict 계획에는 필수; 누락/상한 불명은 exit 12 |
| `--execution-mode <synthetic|live>` | 기본 synthetic. live는 fixture 유무와 무관하게 exit 14로 즉시 거절; credential을 확인하지 않음 |
| `--fixture <file>` | synthetic의 새 plan/직접 exec에 필수. 없으면 exit 2. bundle은 request별 응답·모의 usage·오류·시간을 가진 데이터이며 코드 실행 금지 |
| `--plan <plan-id>` | exec의 기존 계획 선택. plan 생성 옵션과 함께 지정 불가. 저장한 입력·fixture·설정을 검증하고 그대로 사용 |
| `--grant-file <file>` | exec/resume의 simulation_grant. 누락·다른 plan/run·만료·폐기는 exit 10. plan은 grant 없이 생성 가능 |
| `--allow-partial` | export에만 허용. 검수·projection 불완전 결과를 명시 partial bundle로 내보내며 exit 20 |
| `--recover-only` | resume에만 허용. owner 획득 후 무전송 예약·저장 응답·체크포인트 복구. 새 dispatch 금지 |
| `--resolve-unknown <file>` | resume --recover-only에서만 허용. synthetic resolution fixture의 계획/attempt·결과·근거 검사 |

config로 단순 실행 인수를 줄일 수 있지만 기본 credential 환경변수나 숨은 프로젝트 설정을 읽어 누락을 채우지 않습니다. `--plan` 사용 시 새 Profile/reviewer/fixture/budget/config를 덧씌우지 않습니다. 설정 변경은 새 plan입니다. 지원하지 않는 옵션 조합·ID·입력 선언은 exit 2입니다.

지시는 positional 문자열, `--prompt-file`, 명시적 `--prompt-stdin` 중 하나로 받습니다. stdin은 EOF까지 유한 입력으로 읽을 뿐 승인·키 prompt를 추가로 띄우지 않습니다. 지시 입력은 1MiB 상한과 읽기 timeout을 둡니다. host는 EOF를 닫아야 하며 timeout은 명시 오류입니다. 구체적인 timeout 초기값은 30초 후보이며 실제 모델 timeout과 다릅니다.

입력 경로는 각각 별도 인수로 받고 문자열을 shell 명령으로 조립하지 않습니다. 알 수 없는 옵션·지원하지 않는 Profile 버전·모델 조합은 오류로 반환합니다. fixture 없이 exec/resume의 live 경로를 요구하면 source 전체 처리·credential 접근·네트워크 이전에 `live_not_available`로 거절합니다.

`--output <file>`은 envelope 파일 지정입니다. `--output-last-message` 별칭은 도입하지 않습니다. 이 파일은 모델의 마지막 응답 원문이 아니라 ReviewGate 결과이며 top-level `findings`를 금지합니다. 모델 최종 텍스트·HGLOS raw findings는 별도 artifact입니다. envelope을 기존 HGLOS raw 출력으로 오인하는 계약 테스트를 둡니다.

## 3. 출력·종료 코드

| 필드 | 의미 |
|---|---|
| schema_version | 계약 major/minor, 미지원 major는 소비 거절 |
| command / run_id / plan_id | 명령과 대상 ID. 준비 전 오류면 ID는 null |
| execution_mode | synthetic. 미래 live와 저장 공간·완료 주장까지 분리 |
| status / error_code | 실행 상태와 기계용 오류. 지적 심각도와 별개 |
| required_action | 필요한 로컬 조치·미결 사항. 토큰·키·원문 예외는 포함하지 않음 |
| artifacts | 상대 경로·종류·길이·hash·bundle ID, 허용된 실행 저장 영역 안만 참조 |
| coverage / cost | 요약과 상세 artifact 참조. null을 0으로 바꾸지 않음 |
| limitations | synthetic·부분 범위·미확인·HGLOS 소비 제한 등 |

| 종료 코드 | 의미 |
|---:|---|
| 0 | 명령 처리 성공. exec/일반 resume는 모든 필수 작업과 완료 bundle 저장 충족. recover-only/status/cancel은 제어·조회 성공이며 검수 완료 아님 |
| 2 | 인수·입력·schema·Profile 계약 오류 |
| 10 | 승인 또는 모의 정책 grant 필요 |
| 11 | credential 필요 — v0.1에서는 상태 계약의 fixture로만 재현 |
| 12 | 예산·허용 요청 수 부족 또는 상한 계산 불가 |
| 13 | owner·상태·계획·snapshot 충돌 |
| 14 | live/API/capability 미지원 |
| 20 | 일부 작업/내보내기만 유효. 누락 범위 명시 |
| 21 | 실행·저장·출력 실패 |
| 130 | Ctrl+C 또는 완료된 취소 처리 |

`cancel`의 0은 중단 요청 접수이지 worker 종료가 아닙니다. `status`의 0도 실행 완료가 아닌 조회 성공입니다. 실행 완료는 envelope의 command·status·bundle을 함께 검사합니다.

### 상태·원인과 종료 매핑

| 명령/원인 | run 상태 | exit / error_code / 다음 조치 |
|---|---|---|
| exec/resume의 정상 검수 | completed | 0 / null / 결과 열람 |
| 일부 유효 결과와 실패·truncated·미보고 기준 | partial | 20 / partial_review / 실패 범위 재검사 |
| 실행 전 예산 부족 | blocked | 12 / budget_exhausted / 정책 확인 |
| 실행 중 예산 소진 | 유효 단위 있으면 partial, 없으면 blocked | 12 / budget_exhausted / 한도 확인 후 resume |
| 실행 전·다음 dispatch 전 grant 만료/폐기 | 유효 단위 있으면 partial, 없으면 blocked | 10 / grant_expired 또는 grant_revoked / 동일 run용 grant 재검사 |
| unknown 또는 원인 불명 dispatch | needs_reconciliation | 13 / reconciliation_required / resume --recover-only, 해소 전 일반 resume 거절 |
| 취소 후 unknown이 남음 | needs_reconciliation | 13 / reconciliation_required / cancel 사유도 보존; 130으로 불확실성을 숨기지 않음 |
| 취소 정리 완료, unknown 없음 | cancelled | 130 / cancelled / 필요 시 resume |
| status 정상 조회 | 저장 상태 유지 | 0 / null / owner_alive와 필요 조치 확인 |
| cancel 정상·반복·terminal run | 상태 유지 또는 제어 접수 | 0 / null / cancel_requested·already_terminal 명시; terminal을 cancelled로 덮지 않음 |
| recover-only가 복구 완료 | pending 있으면 partial/blocked, unknown이면 needs_reconciliation | 복구 가능한 범위만 정리됐으면 0, unknown 잔존은 13; 새 요청 없음 |
| HGLOS strict export 완료 조건 충족 | completed 유지 | 0 / null / 새 bundle 발행 |
| HGLOS 기본 export에 partial/failed/cancelled | 저장 상태 유지 | 20 / export_incomplete_run / raw 파일 미발행; 유효 단위가 있으면 allow-partial 선택 가능 |
| HGLOS 기본 export에 needs_reconciliation | 저장 상태 유지 | 13 / reconciliation_required / raw 파일 미발행; 조정 또는 명시 partial 선택 |
| export 시 active owner 또는 run의 planned/blocked/ready 상태 | 저장 상태 유지 | 13 / owner_conflict 또는 export_not_ready / raw 파일 미발행 |
| HGLOS completed run에서 projection 불완전 | completed 유지 | 20 / projection_incomplete / raw 파일 미발행; allow-partial 선택 가능 |
| HGLOS allow-partial로 subset 발행 | needs_reconciliation을 포함한 저장 상태 유지 | 20 / partial_export / raw·provenance·제외/미완료 목록. unknown을 해소했다는 뜻이 아님. 유효 단위가 전혀 없으면 진단만 발행하고 raw 없음 |
| owner lock 충돌·DB busy | 상태 변경 없음 | 13 / owner_conflict 또는 state_store_busy / 자동 반복 없이 재조회 |
| artifact/DB/출력 I/O 실패 | failed 또는 마지막 durable 상태 | 21 / storage_failure 또는 output_failure / status로 저장 결과 확인 |

상태 판정 우선순위는 저장 실패 > unknown > grant/예산 차단 > 취소 > 일반 partial > completed입니다. status/cancel 같은 제어 명령의 처리 결과와 대상 run 상태를 함께 반환합니다. lookup 실패는 exit 2 / unknown_run입니다.

stdout와 파일 결과에 완료 상태·bundle ID가 같아야 합니다. broken pipe로 stdout 전달만 실패하면 저장된 bundle은 보존하고 exit 21을 반환합니다. host는 status로 결과를 조회하며 모델 작업을 반복하지 않습니다. 종료 signal·프로세스 강제 종료에서는 stdout 완료 envelope를 보장하지 않고 체크포인트로 복구합니다.

## 4. ID·snapshot·계획 고정

- opaque ID는 앱이 생성하고 경로 문자를 허용하지 않습니다. 사용자 입력 ID를 저장 경로에 직접 결합하지 않습니다.
- snapshot은 경로 목록·원본 byte hash·추출 규칙·정규화된 텍스트 hash·위치 mapping을 고정합니다.
- plan은 snapshot ID, Profile/prompt 버전, reviewer/API/capability, 단계, 분할, 출력/추론 한도, 가격표 버전, 전송 manifest를 고정합니다.
- 전송 manifest는 렌더링된 요청별 body hash와 각 근거 원문 범위를 기록합니다. 인증 헤더는 hash 대상·preview·저장 대상에서 제외합니다.
- 직렬화 전후 hash를 대조합니다. 재개 시 serializer 버전이 바뀌어 요청이 달라지면 새 계획이 필요합니다.
- dynamic 추가 검증은 기존 실행의 입력을 조용히 확대하지 않습니다. finding·해당 원문·추가 수신자를 고정한 자식 계획을 생성하고 정책/승인 범위를 다시 검사합니다.

## 5. 승인 주체의 신뢰 경계

v0.1은 실제 승인을 수집하지 않습니다. `simulation_grant`는 계획·예산·대상 hash를 묶어 정상/변조 테스트에 사용하는 데이터이며 미래 live 승인이 아닙니다.

실연결 시에는 다음 두 경로를 별도로 설계해야 합니다.

1. **직접 사용자 CLI:** 사람이 전송 계획을 보고 별도 승인 단계에서 발급한 grant를 Headless exec에 전달합니다.
2. **HGLOS/host:** host의 신뢰 가능한 사용자 승인 이벤트를 broker가 확인하고 grant를 발급합니다. 모델이 만든 파일을 승인 이벤트로 취급하지 않습니다.

같은 OS 계정으로 임의 프로세스를 실행할 수 있는 agent와 사람을 일반 CLI·파일만으로 식별할 수는 없습니다. 별도 `approve` 명령·로컬 서명 키·해시만 추가하여 인간 승인 증명이라고 주장하지 않습니다. host 승인 API 또는 OS가 보호하는 별도 broker 신뢰 경계가 확정되기 전에는 live를 해제하지 않는 것을 추천합니다.

grant에 필요한 항목은 grant ID, 발급 주체·채널, plan/manifest hash, execution mode, 허용 reviewer/endpoint, 비용·요청·재시도 한도, 유효 기간, 폐기/소비 상태입니다. grant 검증과 요청 직전 정책 확인은 둘 다 필요합니다. 사용자의 실제 승인 발화를 모델이 만들어 채우지 않습니다.

simulation_grant는 하나의 plan과 하나의 run에 묶습니다. 첫 실행 준비 transaction에서 run에 bind하며 같은 run의 resume에만 재사용합니다. 새 run에 재사용하면 exit 10입니다. grant의 누적 금액·요청·재시도 소비는 resume/crash로 초기화하지 않으며 예산 예약과 같은 transaction에서 검사합니다. 상태는 unbound → bound(run) → closed 또는 expired/revoked이며 폐기/만료 이력은 삭제하지 않습니다. mid-run 만료는 다음 dispatch를 차단하고 이미 전송 의도를 기록한 요청을 소급 무승인으로 만들지 않습니다.

## 6. 저장·동시성

기본 root는 `%USERPROFILE%\.reviewgate`입니다(AppData 가상화를 피함). synthetic와 미래 live를 별도 namespace로 분리하고 초기 테스트에는 임시 root만 사용합니다. 원문 트리 안이나 동기화·네트워크 폴더는 상태 root로 허용하지 않는 방향입니다.

namespace별 단일 `state.db` SQLite 파일에 projects, plans, runs, requests/attempts, budget entries, artifact metadata, policy/grant metadata를 저장합니다. 프로젝트별 DB와 공통 장부 DB를 나누지 않습니다. WAL, foreign_keys=ON, busy_timeout=5000ms를 초기 기준으로 두고 SQLITE_BUSY는 exit 13입니다. 긴 원문·보고서는 immutable 파일로 분리합니다. 이것은 논리 데이터 목록이며 DB DDL이 아닙니다.

request/attempt 생성, grant bind·소비 예약, budget 예약, reserved 체크포인트는 같은 DB transaction에서 commit합니다. budget entry는 존재하는 attempt를 참조해야 하며 고아 entry는 FK/unique 제약으로 거절합니다. 복구에서 FK/ledger 검증 실패가 발견되면 장부를 자동 환불하지 않고 state_store_corrupt로 차단합니다.

- 동일 프로젝트의 worker는 하나만 허용하고 OS가 프로세스 종료 시 해제하는 lock을 사용합니다. PID·시간만 보고 owner lock을 훔치지 않습니다.
- status/cancel은 동일 owner lock을 try-acquire 후 즉시 해제하는 probe로 owner_alive를 반환합니다. 이는 관측 순간 정보이며 이후의 실행 시작까지 막는 보장은 아닙니다. owner 부재의 stale run은 resume --recover-only로 복구 worker가 소유하여 정리합니다. cancel만 호출했다고 장부가 정리되지는 않습니다.
- 다른 프로젝트가 같은 월 예산을 사용할 수 있으므로 전체 namespace에 공통 budget ledger를 둡니다. 프로젝트마다 장부를 새로 만들어 월 한도를 우회하지 않습니다.
- 요청 전 예산 검사·예약은 짧은 DB transaction 안에서 수행하고 API 대기 중 transaction을 잡지 않습니다.
- cancel은 run별 고정 제어 파일의 원자적 교체로 전달하는 후보입니다. worker만 run/예산 상태를 변경합니다. 취소 세대 번호와 owner generation을 대조하고 제어 경로의 reparse point·경로 이탈을 거절합니다.
- cancel 접수와 dispatch가 경쟁하면 이미 의도를 기록했거나 transport에 진입한 요청은 취소 전 진행 요청으로 남을 수 있습니다. worker가 cancelling을 확인한 뒤에는 새 dispatch를 금지합니다. API 대기 중에도 제어 파일을 확인하는 취소 watcher와 linked cancellation token을 사용하며, 모의 테스트에서는 인지 지연 상한을 고정하여 검사합니다.
- 다른 root·장부 복제·같은 OS 계정의 악의적 DB 수정까지 강제 차단하는 보안 금고는 아닙니다.

## 7. 상태 전이

| run 상태 | 진입 조건 | 다음 상태/제약 |
|---|---|---|
| planned | 입력·계획 저장 | blocked / ready |
| blocked | 승인·정책·예산 부족 | 사유 해소 후 동일 plan 재검사. 자료 변경은 새 plan |
| ready | 필요한 사전 조건 충족 | owner 획득 후 running |
| running | worker가 작업 소유 | completed / partial / failed / cancelling |
| cancelling | cancel 요청 확인 | 새 요청 금지, 실행 중 작업 정리 후 cancelled 또는 needs_reconciliation |
| needs_reconciliation | 전송·처리 여부 미확정 요청 존재 | 자동 재호출 금지, 재개 전 조정 필요 |
| completed | 필수 작업·근거 검사·필수 기준 처리·bundle commit 충족; unresolved 없음 | 읽기·export만. 재실행은 새 run |
| partial | 일부 결과만 유효 | 실패 범위를 명시하여 resume 가능 |
| failed / cancelled | 실패·중단 결과 보존 | resume 가능성 검사; 무조건 재시작하지 않음 |

run 상태와 owner 생존 상태는 별개입니다. 프로세스가 죽은 running 상태는 lock 재획득 후 request checkpoint를 검사하여 partial 또는 needs_reconciliation로 복구합니다. unresolved 요청이 있으면 completed를 허용하지 않습니다.

| request checkpoint | 저장 순서·복구 규칙 |
|---|---|
| pending | 아직 예산 예약·전송 없음 |
| reserved | transaction으로 한도 검사·예약, 아직 dispatch 의도 없음. 복구 시 무전송을 확인하고 해제/재개 가능 |
| dispatch_intent | body/attempt ID와 의도를 durable 기록한 뒤 transport 진입. crash 시 응답 증거 없으면 unknown |
| response_saved | 검증 가능한 응답 파일·hash·모의/실제 usage 상태 저장. 재개는 이 파일에서 후처리 |
| validated / rejected | schema·근거·잘림 검사 결과. invalid·거부도 사용량·비용과 별도 보존 |
| settled | 요청 비용 상태·처리 범위를 transaction으로 반영. 완료 bundle은 이후 생성 |
| unknown | 전송됐을 가능성이나 응답 수신/청구 불명. 예약 해제·자동 재전송 금지 |

unknown이 발생하면 v0.1은 조정 필요 상태를 반환합니다. 해소는 resume --recover-only --resolve-unknown의 모의 resolution fixture로만 검사합니다. fixture는 attempt ID·snapshot/plan·outcome·금액·basis·actor·시각을 갖고 합치 여부를 검증합니다. `unknown_resolved` 사건으로 원래 unknown과 연결하며 원래 이력은 삭제하지 않습니다.

outcome은 not_sent(무전송 증거 필요), usage_confirmed(모의 usage와 단가), conservative_full_reservation(모의 상한 전액을 가정 비용으로 전환)입니다. 근거 불명·금액 누락은 해소하지 않습니다. 가정 비용은 실제 usage/청구로 표시하지 않습니다. 장부 정산과 carry hold 해제는 한 transaction입니다. 한도를 초과하는 비용이 확인되면 실제 계산 상태를 기록하고 추가 dispatch를 차단합니다. 누적 unknown으로 한도가 잠기면 required_action=reconcile_unknown_attempts와 해당 ID를 반환합니다.

일반 resume는 unknown을 먼저 해소하라고 거절합니다. 미래 live의 사용자 선언·청구 증거 입력·신뢰 채널은 후속 slice이며 이 fixture 경로를 live 우회 수단으로 재사용하지 않습니다.

## 8. 비용 계산·월 경계

금액은 USD decimal로 계산하고 토큰 단가는 별도 가격 snapshot에 저장합니다. 반올림은 표시에서만 수행합니다. 입력·캐시·출력·reasoning은 Adapter가 중복되지 않는 청구 항목으로 정규화하며 포함 관계를 확정하지 못하면 `cost_unknown`입니다.

가격 snapshot에는 ID/hash, currency, source URL·문서 hash, retrieved_at, effective_from/to, provider/model/API, service tier, region, token 항목별 요율·포함 관계·context 구간을 둡니다. 범위가 맞는 snapshot을 plan에 고정하고 dispatch_intent 시각에 유효 기간을 다시 검사합니다. 만료/요금 조건 불일치는 새 plan 전까지 차단합니다. 과거 dispatch의 요율을 resume 시 현재 가격으로 바꾸지 않습니다.

예상 평균 비용과 보수적 요청 예약 상한은 다릅니다. 모델별 입력 상한·출력/추론 한도·요율을 증명할 수 없으면 엄격 모드에서 요청을 거절합니다. 글자 수 추정이나 safety factor만으로 hard cap을 보장하지 않습니다. v0.1의 계산 검증은 고정 fixture 값에 한정됩니다.

요청 허용 조건은 grant 누적·실행·프로젝트·공통 월 장부의 모든 scope에서 `계산된 소비 + 활성 예약 + 미확정 보류 + 새 예약 <= 한도`이고 요청·재시도 수 상한도 만족하는 것입니다. grant 소비와 장부를 같은 transaction에서 검사합니다. 무전송이 증명된 reserved만 예약·요청 수를 해제할 수 있으며 dispatch_intent 이후는 근거 없이 해제하지 않습니다. 동일 attempt의 reservation/settlement는 한 번만 반영합니다.

월 경계는 최초 예산 설정의 Asia/Seoul 의미인 UTC+09:00을 고정 offset으로 저장하여 Windows IANA 조회에 의존하지 않습니다. 변경은 다음 기간부터 적용하며 청구서의 기간과 같다는 주장은 하지 않습니다.

- 비용 귀속 월은 dispatch 의도 시각 기준으로 보존합니다. 프로세스 재시작·resume로 월을 바꾸지 않습니다.
- 이전 달의 활성 예약·unknown은 해소될 때까지 현재 달의 사용 가능액에서도 보수적으로 보류합니다. 새 달이라는 이유로 잊거나 환불하지 않습니다.
- 정산 시 원래 귀속 월에 비용을 반영하고 현재의 carry hold를 해제합니다. 같은 비용을 두 달의 실제 소비로 중복 기록하지 않습니다.
- 모든 차감·예약·보류·정산은 별도 사건으로 추적하고 ledger를 재계산할 수 있어야 합니다.
- usage 기반 계산액은 청구 확인액이 아닙니다. 실제 잔액 조회나 invoice 동기화는 v0.1 범위 밖입니다.

## 9. artifact commit과 복구

응답·보고서는 임시 파일에 기록 → close/flush → hash 확인 → 같은 volume의 새 고유 파일로 확정 → DB metadata commit 순서로 저장합니다. 최종 결과 bundle은 manifest와 파일 집합이 맞을 때 마지막 commit marker로 발행합니다. 여러 파일을 한 번에 원자 교체했다고 주장하지 않습니다.

crash 지점별 규칙은 다음과 같습니다.

- 임시 파일만 있음: 작업 완료 증거 아님. 정리 후보로 표시하며 source는 건드리지 않음.
- 파일 확정 후 DB 반영 전: attempt ID·hash로 채택 가능한지 검사. 불명확하면 재호출하지 않음.
- response 저장 후 보고서 생성 전: 저장 응답에서 후처리 재개.
- final bundle 저장 후 stdout 전달 전: status/export로 조회, 모델 작업 반복 금지.

사용자가 지정한 기존 출력 파일은 덮어쓰지 않습니다. `--output`은 source·snapshot·기존 artifact와 겹치지 않아야 합니다. export도 새 bundle을 생성하며 이전 결과를 바꾸지 않습니다.

## 10. 실패 검증 시나리오

향후 테스트에는 다음 조합을 포함합니다. 지금 테스트를 작성·실행했다는 의미가 아닙니다.

- stdin 미종료·잘못된 인수·빈 output·broken pipe·출력 권한 오류.
- dispatch 직전/직후, 응답 파일 확정 직후, DB 정산 직전, final marker 직후 강제 종료.
- 다른 프로젝트의 동시 예산 예약, 동일 attempt 중복 정산, 월말 unknown의 다음 달 이월.
- cancel 연속 호출·이미 끝난 run cancel·죽은 owner 복구·설정 변경 후 resume.
- 다른 mode/plan의 grant 재사용, 변조된 manifest, serializer 변경, fixture 없는 live 요청.

통과는 해당 상태 전이·출력·보존 자료·fake 호출 횟수를 대조해야 하며 프로세스 exit 0만으로 판정하지 않습니다.
