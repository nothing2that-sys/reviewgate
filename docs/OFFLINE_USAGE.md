# ReviewGate v0.1 오프라인 사용

현재 실행 가능한 것은 **합성 fixture의 처리 흐름**입니다. 원문을 실제 모델이 검수했다고 표시하지 않습니다. 실제 API transport·SDK·credential/vault 구현·키 환경변수 조회·외부 도구 실행은 제품에 없습니다. 운영용 AI 검수 서비스는 아직 아닙니다.

## 빌드와 테스트

Windows .NET 10 SDK가 필요합니다. `global.json`은 SDK 10.0.204 및 latestPatch를 지정합니다. 빌드는 NuGet package lock을 생성·사용합니다. 다른 환경에서는 `dotnet restore ReviewGate.sln --locked-mode`를 권장합니다. 이번 검증은 기존 로컬 NuGet cache에서 복원했고 새 런타임·SDK·도구를 설치하지 않았습니다. 일반 restore는 패키지 저장소를 사용할 수 있으므로 네트워크가 필요 없는 모델 실행과 패키지 복원을 구분합니다.

```powershell
dotnet restore ReviewGate.sln --locked-mode
./scripts/verify-offline.ps1 -Python 'C:/path/to/verified/python.exe'
./scripts/offline-demo.ps1
```

포장 결과는 `.work/publish/ReviewGate.Cli.dll`입니다. `dotnet <dll> ...` 또는 동봉된 `.exe`로 실행합니다. framework-dependent 배포이며 실행 PC에 .NET 10 런타임이 필요합니다. 전역 설치·배포·PATH 변경은 하지 않습니다.

## 명령 흐름

```powershell
$cli = '.work/publish/ReviewGate.Cli.dll'
dotnet $cli plan '코드 검토' --config tests/fixtures/demo/config.json
# preview artifact인 plan.json의 hash/manifest_hash와 전송 본문·단가·범위를 확인한다.
dotnet $cli exec --state-root .work/demo-state --plan <plan_id> --grant-file <simulation-grant.json>
dotnet $cli status --state-root .work/demo-state --run <run_id>
dotnet $cli cancel --state-root .work/demo-state --run <run_id>
dotnet $cli resume --state-root .work/demo-state --run <run_id> --grant-file <같은-run의-grant.json>
dotnet $cli resume --state-root .work/demo-state --run <run_id> --recover-only
dotnet $cli export --state-root .work/demo-state --run <run_id> --format hglos-findings
```

config는 `schema_version: 1.0`, `options` 객체이며 CLI 인수가 우선합니다. config의 profile/reviewer/budget/prices/fixture/source/state 경로는 config 디렉터리 기준입니다. input/reference는 source-root 기준입니다. `--input`·`--reference`·`--reviewer`·`--finding`은 반복할 수 있습니다. 지시문은 positional 하나, `--prompt-file`, `--prompt-stdin` 중 하나만 사용합니다. stdin은 닫힘 또는 EOF를 요구하며 30초/1MiB 제한입니다.

plan은 입력·정규화 스냅샷·디렉터리 목록·profile·fixture·단가·request body와 전송 범위를 고정합니다. 새 입력이나 manifest 변조는 기존 plan으로 실행하지 않습니다. 디렉터리 입력 안의 지원하지 않는 형식(`.csproj`, 확장자 없는 파일 등)은 `unsupported_format` 제외로 기록하고, 명시한 단일 파일이 지원하지 않는 형식이면 2로 거절합니다. logical/export 경로는 입력한 대소문자가 아니라 디스크의 실제 항목 이름을 씁니다. JSON stdout에는 단일 envelope만 출력합니다. `--output`은 같은 envelope를 UTF-8 BOM 없이 새 파일에 쓰며 원문·state 내부·기존 파일에는 쓰지 않습니다. 출력 경로가 원문/state 겹침 검사를 통과한 뒤의 오류 envelope도 같은 파일에 씁니다. 그 검사 전에 실패하거나 안전하지 않은 출력 경로이면 stdout 오류만 반환합니다. 정상 status/cancel의 command 오류와 `run_error_code`는 구분됩니다.

Grant 필드는 schema_version, id, plan_hash, manifest_hash, expires_at, max_usd, max_requests, max_retries, execution_mode=synthetic 및 선택 run_id입니다. 첫 예약에서 단일 run에 결합됩니다. 이는 로컬 합성 예산 검사이며 보안 승인 broker가 아닙니다. 상태 DB를 수정할 수 있는 로컬 사용자를 차단하는 보안 경계로 주장하지 않습니다. 폐기는 Core의 장부 기능으로 검증했고 별도 CLI revoke 명령은 제공하지 않습니다. CLI 중단은 cancel을 사용합니다.

## 검수 모드와 범위

- `--mode single`: reviewer 하나.
- `--mode independent`: 서로 다른 reviewer ID와 canonical_model_key를 가진 reviewer 둘. key는 소문자 `[a-z0-9._/:@-]`만 허용합니다. key가 달라도 model_id의 마지막 경로 요소와 model_version이 같으면(대소문자 무시) 같은 모델로 보고 거절합니다. 이 key는 reviewer 파일의 선언값이며 고정 registry로 검증한 identity가 아닙니다(provenance의 `canonical_model_key_source`). 서로의 결과는 입력에 포함하지 않습니다. 결과는 삭제/다수결 확정 없이 비교·연결합니다.
- `--mode verify --base-run <id> --finding <id>`: 선택한 기존 finding과 같은 원문 스냅샷의 관련 구간을 재검증합니다. 기준 run은 같은 project의 completed/partial이어야 합니다. 기준 run과 별도의 plan·grant·예산이 필요합니다.

`profiles/`의 code/novel/document JSON을 복사해 기준을 추가할 수 있습니다. chunk 기준은 UTF-8 byte core 분할+경계 문맥, relation 기준은 해당 요청에 원문 전체와 참조를 넣습니다. 모델 프로필의 요청 byte 상한을 넘으면 12로 거절하며 요약·검색을 원문 전체 독해로 대체하지 않습니다. relation의 대규모 상호 참조 최적화는 후속입니다. 빈 단독 원문은 검토할 내용 없음으로 거절하고 100%로 표시하지 않습니다.

인용은 고정 원문 byte/행과 정확히 대조합니다. 없는 인용·중복 위치·잘못된 행을 거절합니다. 가정이 붙은 observed는 inferred로 낮추고 unconfirmed를 올리지 않습니다. 일치하는 인용만으로 주장 자체의 진실이 검증된 것은 아닙니다. `.diff`/`.patch`(대소문자 무관) 원문의 finding은 `coordinate_system=diff`이고 `line`은 patch 표시 행입니다. before/after 행은 `diff_before_line`/`diff_after_line`에 따로 두며, HGLOS로는 내보내지 않습니다.

coverage에는 전체 합계와 함께 기준×검수자×stage별 `by_criterion_reviewer`, 내용이 없는 target의 `empty_source_ids`가 있습니다. 모델 payload가 계약을 어기면(요약/검색 등 허용되지 않은 status, finding 불일치) usage를 아는 경우 invalid 단위로 정산하며 전체 독해로 세지 않습니다.

## 비용·복구·출력

synthetic 전용 SQLite 장부 하나가 월 한도와 모든 프로젝트를 공유합니다. 프로젝트별 worker 하나만 허용합니다. 요청 예약의 입력 token 상한은 reviewer가 선언한 값과 `요청 body UTF-8 byte + 256` 중 큰 값입니다(byte 단위 tokenizer 가정이며 제공자 보증이 아님). 비용 귀속 월은 dispatch 의도 시각입니다. grant는 run의 첫 실행 준비에서 결합되며 예약이 실패해도 다른 run에 재사용할 수 없습니다. 예약 이후 dispatch 의도 이전 실패는 해제할 수 있고, 의도 이후 응답 증거가 없으면 unknown hold를 유지합니다. 저장된 응답은 재개 시 후처리하며 정상 완료 요청을 반복하지 않습니다. 재시도 한도를 다 쓴 요청은 `retry_exhausted` 미완료로 남기고 나머지 요청은 계속 처리합니다. 중단 시 in-flight가 불명확하면 130 대신 13을 반환합니다.

unknown 해소는 `resume --recover-only --resolve-unknown <fixture.json>`만 제공합니다. recover-only는 원본·profile·fixture 파일이 바뀌어도 실행됩니다(장부와 저장 응답만 다루며 새 요청 없음). 새 요청을 보내는 일반 resume은 바뀐 입력을 계속 거절합니다. plan_hash·attempt_id·outcome·basis·actor·timestamp를 검사합니다. usage_confirmed는 usage와 고정 단가로 amount_usd를 재계산합니다. conservative_full_reservation은 assumed_usd로 분리합니다. not_sent는 모의 transport의 명시 무전송 근거가 있을 때만 허용하며, 증명된 무전송이므로 예약을 released로 돌려 요청·재시도 수를 쓰지 않습니다. 새로운 dispatch는 일반 resume에서만 수행합니다.

JSON/Markdown 보고서와 hash manifest를 새 bundle로 씁니다. report.md의 모델 작성 문자열은 Markdown/HTML로 해석되지 않게 escape하고, 인용은 code fence에 넣습니다. 제공자 오류(HTTP 4xx/5xx) 응답은 usage만 남기고 body를 지운 뒤 저장합니다. HGLOS는 review target(reference 제외)인 code defect 중 원문 저장소 좌표가 있는 subset만 projection합니다. 정상 strict export는 completed·필수 coverage·미확정 0·제외 0이 모두 필요하며 raw 파일은 `hglos.findings.synthetic.json`입니다. 불완전 결과에 `--allow-partial`을 명시하면 20이며 raw subset은 별도 이름 `hglos.findings.partial.synthetic.json`입니다. 유효 단위가 없으면 raw findings 없이 provenance 진단만 내보냅니다. grant 없이 resume해도 유효 단위가 있는 run은 partial로 남아 부분 export가 가능합니다. synthetic 회차를 HGLOS에 등록하거나 runner를 교체하지 않습니다.

로컬 state에는 원문 스냅샷·전송 본문·고정 응답·결과가 평문으로 보존됩니다. 자동 삭제/암호화/회사 자료 운영 승인은 없습니다. 필요 시 사용자가 선택한 state 디렉터리를 보관·정리해야 합니다. API 키를 이 폴더나 config에 넣지 마세요.

종료 코드: 0 정상, 2 입력/계약 오류, 10 grant 필요/만료/폐기, 11 모의 인증 오류(usage가 있는 401), 12 예산/가격/상한/요청·재시도 한도, 13 잠금/복구/미확정/무결성, 14 live/미지원, 20 부분 결과, 21 저장·실행·stdout 출력 실패, 130 불확실성 없이 취소. usage가 없는 401/429/5xx 응답은 비용을 알 수 없으므로 unknown으로 남겨 13을 반환합니다. 상세 우선순위는 승인된 CLI 계약을 따릅니다.
